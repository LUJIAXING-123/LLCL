# ============================================================
#  build/build-msi.ps1  —  把发布目录打包成 MSI 安装包
#
#  用法:
#    pwsh -ExecutionPolicy Bypass -File build\build-msi.ps1
#
#  前置条件:
#    dotnet tool install --global wix
#
#  产物: dist\LLCL_<版本>_<架构>.msi
# ============================================================

param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Arch = 'x64',

    [string]$Version = '',

    # 已发布好的目录；为空时使用 artifacts 下的默认位置。
    # bin 目录只放 dev 版本，带 beta 等后缀的产物统一在 artifacts 里构建。
    [string]$PublishDir = '',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------- 读取版本 ----------
if (-not $Version) {
    $raw = Get-Content (Join-Path $PSScriptRoot 'version.json') -Raw
    $jsonOnly = ($raw -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $v = $jsonOnly | ConvertFrom-Json
    $suffix = if ($v.stage -eq 'release') { '' } else { "-$($v.stage).$($v.stageNumber)" }
    $Version = "$($v.major).$($v.minor).$($v.patch)$suffix"
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " 构建 MSI 安装包" -ForegroundColor Cyan
Write-Host " 版本: $Version" -ForegroundColor Cyan
Write-Host " 架构: $Arch" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ---------- 检查 wix ----------
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "未找到 wix，正在安装..." -ForegroundColor Yellow
    dotnet tool install --global wix | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "wix 安装失败" }
}

# ---------- 发布 ----------
$rid = if ($Arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
if (-not $PublishDir) { $PublishDir = Join-Path $root "artifacts/publish-$rid" }

if (-not $SkipBuild) {
    Write-Host "[1/4] 发布应用..." -ForegroundColor Green
    dotnet publish (Join-Path $root 'src/LLCL.App/LLCL.App.csproj') `
        -c Release "-p:Platform=$Arch" -r $rid -o $PublishDir --nologo -v m | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "发布失败" }
} else {
    Write-Host "[1/4] 跳过发布（使用现有产物）" -ForegroundColor DarkGray
}

if (-not (Test-Path (Join-Path $PublishDir 'LLCL.exe'))) {
    throw "发布目录缺少 LLCL.exe，请先执行发布"
}

# ---------- MSI 版本号 ----------
# Windows Installer 只接受纯数字、最多 4 段的版本号，且必须严格递增
# 才会触发 MajorUpgrade 覆盖安装。
#
# 原实现直接把 0.1.2-beta.5 截断成 0.1.2，导致所有 beta 的 MSI 版本号完全相同。
# 用户重新安装时会被判定为「已安装同版本」而直接跳过，
# 实际仍运行旧程序，表现为「重装后问题依旧」。
#
# 这里把预发布阶段与序号编码进第 4 段：
#   dev.N -> 0+N     beta.N -> 100+N     preview.N -> 200+N
#   rc.N  -> 300+N   正式版 -> 保留三段（不再加第 4 段）
$baseVersion = ($Version -split '-')[0]
$segments = @($baseVersion -split '\.' | Select-Object -First 3)
while ($segments.Count -lt 3) { $segments += '0' }
$baseVersion = $segments -join '.'

$msiVersion = $baseVersion
if ($Version -match '-(dev|beta|preview|rc)\.(\d+)$') {
    $stageName = $matches[1]
    $stageNumber = [int]$matches[2]
    $stageBase = switch ($stageName) {
        'dev'     { 0 }
        'beta'    { 100 }
        'preview' { 200 }
        'rc'      { 300 }
        default   { 0 }
    }
    $msiVersion = "$baseVersion.$($stageBase + $stageNumber)"
}

Write-Host "[2/4] MSI 版本号: $msiVersion" -ForegroundColor DarkGray

# ---------- 扫描文件并生成 WiX 元素 ----------
Write-Host "[3/4] 扫描发布目录..." -ForegroundColor Green

$files = Get-ChildItem $PublishDir -File -Recurse |
    Where-Object { $_.Extension -notin @('.pdb', '.ipdb', '.iobj') }

Write-Host "  共 $($files.Count) 个文件" -ForegroundColor DarkGray

# 目录 Id 生成：把相对路径里的特殊字符替换为下划线
function Get-DirId([string]$relDir) {
    if ([string]::IsNullOrEmpty($relDir)) { return 'INSTALLFOLDER' }
    $sanitized = $relDir -replace '[^A-Za-z0-9]', '_'
    return "DIR_$sanitized"
}

# 收集所有需要的目录（含中间层级）
$dirSet = [System.Collections.Generic.HashSet[string]]::new()
foreach ($f in $files) {
    $rel = [System.IO.Path]::GetRelativePath($PublishDir, $f.FullName)
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    while (-not [string]::IsNullOrEmpty($dir)) {
        [void]$dirSet.Add($dir)
        $dir = [System.IO.Path]::GetDirectoryName($dir)
    }
}

# 按深度排序，父目录排在前面

# 简化方案：直接把所有目录以扁平方式放到 INSTALLFOLDER 下，
# 但 WiX 要求 Directory 嵌套才能表达多级路径。
# 这里构建完整嵌套树。
$treeBuilder = [System.Text.StringBuilder]::new()

function Add-DirectoryNodes {
    param(
        [System.Text.StringBuilder]$Builder,
        [string]$ParentRelPath,
        [int]$Depth
    )

    $prefix = ' ' * (12 + $Depth * 4)
    $children = $dirSet | Where-Object {
        $p = [System.IO.Path]::GetDirectoryName($_)
        if ([string]::IsNullOrEmpty($ParentRelPath)) {
            return [string]::IsNullOrEmpty($p)
        }
        return ($p -eq $ParentRelPath)
    } | Sort-Object

    foreach ($child in $children) {
        $name = [System.IO.Path]::GetFileName($child)
        $id = Get-DirId $child
        [void]$Builder.AppendLine("$prefix<Directory Id=`"$id`" Name=`"$name`">")
        Add-DirectoryNodes -Builder $Builder -ParentRelPath $child -Depth ($Depth + 1)
        [void]$Builder.AppendLine("$prefix</Directory>")
    }
}

Add-DirectoryNodes -Builder $treeBuilder -ParentRelPath '' -Depth 0
$directoryTree = $treeBuilder.ToString()

# 生成 File + Component（每个文件一个 Component）
$fileBuilder = [System.Text.StringBuilder]::new()
$index = 0

foreach ($f in $files) {
    $rel = [System.IO.Path]::GetRelativePath($PublishDir, $f.FullName)
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    $dirId = Get-DirId $dir
    $srcPath = $f.FullName.Replace('\', '\\')
    $componentId = "CMP_$index"

    [void]$fileBuilder.AppendLine(
        "            <Component Id=`"$componentId`" Directory=`"$dirId`" Guid=`"*`">")
    [void]$fileBuilder.AppendLine(
        "                <File Id=`"FILE_$index`" Source=`"$srcPath`" KeyPath=`"yes`" />")
    [void]$fileBuilder.AppendLine("            </Component>")

    $index++
}

$fileComponents = $fileBuilder.ToString()

# ---------- 组装并编译 ----------
Write-Host "[4/4] 编译 MSI..." -ForegroundColor Green

$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $distDir | Out-Null
$msiName = "LLCL_${Version}_${Arch}.msi"
$msiPath = Join-Path $distDir $msiName

$template = Get-Content (Join-Path $PSScriptRoot 'msi/LLCL.wxs') -Raw
$wxs = $template.Replace('@@DIRECTORY_TREE@@', $directoryTree)
$wxs = $wxs.Replace('@@FILE_COMPONENTS@@', $fileComponents)

$wxsTemp = Join-Path $env:TEMP "LLCL-$([guid]::NewGuid().ToString('N').Substring(0,8)).wxs"
Set-Content $wxsTemp $wxs -Encoding UTF8
Write-Host "  临时 wxs: $wxsTemp" -ForegroundColor DarkGray

$iconPath = Join-Path $root 'src/LLCL.App/Assets/llcl.ico'

& wix build $wxsTemp `
    -d "Version=$msiVersion" `
    -d "IconPath=$iconPath" `
    -o $msiPath `
    -arch $Arch `
    -pdbtype none 2>&1 | Out-Host

if ($LASTEXITCODE -ne 0) { throw "MSI 编译失败（详见上方错误）" }

$size = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Host ""
Write-Host "完成: $msiName ($size MB)" -ForegroundColor Green
Write-Host "路径: $msiPath" -ForegroundColor Cyan

# 清理临时文件
Remove-Item $wxsTemp -Force -ErrorAction SilentlyContinue




