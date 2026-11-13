# ============================================================
#  build/publish.ps1  —  LLCL 发布脚本
#
#  用法:
#    pwsh build/publish.ps1                 # 按 version.json 构建 dev
#    pwsh build/publish.ps1 -Stage beta -StageNumber 2
#    pwsh build/publish.ps1 -Stage preview -GitHub
#    pwsh build/publish.ps1 -SkipMsix       # 只出绿色包，不打包 MSIX
# ============================================================

param(
    [ValidateSet('dev', 'beta', 'preview', 'rc', 'release')]
    [string]$Stage = '',

    [int]$StageNumber = 0,

    [switch]$GitHub,

    [switch]$SkipMsix,

    [ValidateSet('x64', 'ARM64')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $PSScriptRoot 'version.json'
$distDir = Join-Path $root 'dist'

# ---------- 读取版本 ----------
# version.json 顶部带有 # 注释，标准 JSON 解析器不接受；
# 这里先剔除注释行再解析，同时保留 MSBuild 的正则可兼容性。
$raw = Get-Content $versionFile -Raw
$jsonOnly = ($raw -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
$versionData = $jsonOnly | ConvertFrom-Json
$major = $versionData.major
$minor = $versionData.minor
$patch = $versionData.patch

if (-not $Stage) { $Stage = $versionData.stage }
if ($StageNumber -le 0) { $StageNumber = $versionData.stageNumber }

# 阶段约束
# 0.x 阶段：可以发 beta（允许提前公开测试），但不产出 preview / rc / release
if ($major -eq 0 -and $Stage -in @('preview', 'rc', 'release')) {
    Write-Host "0.x 期间不产出 $Stage，请使用 dev 或 beta。" -ForegroundColor Yellow
    exit 1
}

# GitHub 发布规则
#   0.x：允许 dev（私有，不建 Release）与 beta（建 Release）
#   1.x 及以后：只有 preview / rc / release 才发布
$gitHubAllowed = if ($major -eq 0) {
    $Stage -eq 'beta'
} else {
    $Stage -in @('preview', 'rc', 'release')
}

if ($GitHub -and -not $gitHubAllowed) {
    Write-Host "当前版本 $major.$minor.$patch-$Stage 不允许发布到 GitHub。" -ForegroundColor Yellow
    Write-Host "0.x 阶段仅 beta 可发布；1.x 及以后仅 preview / rc / release 可发布。" -ForegroundColor Yellow
    exit 1
}

$suffix = if ($Stage -eq 'release') { '' } else { "-$Stage.$StageNumber" }
$fullVersion = "$major.$minor.$patch$suffix"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " LLCL 发布" -ForegroundColor Cyan
Write-Host " 版本: $fullVersion" -ForegroundColor Cyan
Write-Host " 阶段: $Stage" -ForegroundColor Cyan
Write-Host " 架构: $Arch" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ---------- dev 阶段：只编译，不产出安装包 ----------
# dev 为内部测试版，不分发；产物留在 bin 目录直接运行即可。
if ($Stage -eq 'dev') {
    Write-Host "dev 阶段不生成安装包与绿色包，仅编译到 bin 目录。" -ForegroundColor Yellow
    Write-Host ""

    $buildOnly = @(
        'build', (Join-Path $root 'src/LLCL.App/LLCL.App.csproj'),
        '-c', 'Release',
        "-p:Platform=$Arch",
        "-p:LlclStage=$Stage",
        "-p:LlclStageNumber=$StageNumber",
        '--nologo'
    )
    & dotnet @buildOnly | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }

    $binDir = Join-Path $root "src/LLCL.App/bin/$Arch/Release/net10.0-windows10.0.26100.0/win-x64"
    Write-Host ""
    Write-Host "完成。可直接运行:" -ForegroundColor Green
    Write-Host "  $binDir\LLCL.exe" -ForegroundColor Cyan
    exit 0
}

New-Item -ItemType Directory -Force $distDir | Out-Null

# ---------- 1. 生成图标（有源图时） ----------
$logoSource = Join-Path $root 'llcl-logo.png'
if (Test-Path $logoSource) {
    Write-Host "[1/5] 从 llcl-logo.png 生成图标..." -ForegroundColor Green
    & (Join-Path $root 'tools/gen-icon.ps1') -Source $logoSource
} else {
    Write-Host "[1/5] 未找到 llcl-logo.png，沿用现有图标" -ForegroundColor DarkGray
}

# ---------- 2. 构建 ----------
# 注意：写入 bin 目录的产物一律使用 dev 版本号。
# beta/preview/rc 只允许出现在 dist 里的安装包与绿色包中。
Write-Host "[2/5] 编译..." -ForegroundColor Green
$binStage = 'dev'
$buildArgs = @(
    'build', (Join-Path $root 'src/LLCL.App/LLCL.App.csproj'),
    '-c', 'Release',
    "-p:Platform=$Arch",
    "-p:LlclStage=$binStage",
    "-p:LlclStageNumber=$StageNumber",
    '--nologo'
)
& dotnet @buildArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

# ---------- 3. 发布（自包含文件夹） ----------
# 发布目录放在 artifacts 下，避免把带 beta 版本号的文件写进 bin。
Write-Host "[3/5] 发布文件..." -ForegroundColor Green
$rid = if ($Arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$publishDir = Join-Path $root "artifacts/publish-$rid"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
$publishArgs = @(
    'publish', (Join-Path $root 'src/LLCL.App/LLCL.App.csproj'),
    '-c', 'Release',
    "-p:Platform=$Arch",
    "-r", $rid,
    "-p:LlclStage=$Stage",
    "-p:LlclStageNumber=$StageNumber",
    '-o', $publishDir,
    '--nologo',
    '-v', 'm'
)
& dotnet @publishArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

if (-not (Test-Path $publishDir)) { throw "找不到发布目录: $publishDir" }

# ---------- 4. 打包绿色版 zip ----------
Write-Host "[4/5] 打包绿色版 zip..." -ForegroundColor Green
$zipName = "LLCL_${fullVersion}_${Arch}_portable.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  -> $zipName ($zipSize MB)" -ForegroundColor DarkGray

# ---------- 5. MSI 安装包 ----------
if (-not $SkipMsix) {
    Write-Host "[5/5] 打包 MSI 安装包..." -ForegroundColor Green

    $msiArgs = @(
        '-ExecutionPolicy', 'Bypass', '-NoProfile',
        '-File', (Join-Path $root 'build\build-msi.ps1'),
        '-Arch', $Arch,
        '-Version', $fullVersion,
        '-PublishDir', $publishDir,
        '-SkipBuild'
    )
    & pwsh @msiArgs | Out-Host

    $msiName = "LLCL_${fullVersion}_${Arch}.msi"
    $msiPath = Join-Path $distDir $msiName
    if (Test-Path $msiPath) {
        Write-Host "  -> $msiName ($([math]::Round((Get-Item $msiPath).Length/1MB,1)) MB)" -ForegroundColor DarkGray
    } else {
        Write-Host "  未生成 MSI，请检查 WiX 工具链" -ForegroundColor Yellow
    }
} else {
    Write-Host "[5/5] 跳过安装包（-SkipMsix）" -ForegroundColor DarkGray
}

# ---------- 恢复 bin 为 dev 版本 ----------
# dotnet publish 会顺带把 bin 下的程序集重新编译成 beta 版本，
# 而 bin 目录要求只放 dev。打包完成后重新编译一次把它还原。
Write-Host "恢复 bin 目录为 dev 版本..." -ForegroundColor Green
& dotnet build (Join-Path $root 'src/LLCL.App/LLCL.App.csproj') `
    -c Release "-p:Platform=$Arch" "-p:LlclStage=dev" `
    "-p:LlclStageNumber=$StageNumber" --nologo -v q | Out-Host


# ---------- 生成发布信息 ----------
$infoFile = Join-Path $distDir 'release-info.json'
@{
    version    = $fullVersion
    stage      = $Stage
    arch       = $Arch
    buildTime  = (Get-Date).ToUniversalTime().ToString('o')
    portable   = $zipName
    gitHubable = $gitHubAllowed
} | ConvertTo-Json | Set-Content $infoFile -Encoding UTF8

Write-Host ""
Write-Host "完成。产物位于 dist\" -ForegroundColor Green
Get-ChildItem $distDir | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

# ---------- GitHub 发布 ----------
if ($GitHub) {
    Write-Host "正在发布到 GitHub..." -ForegroundColor Green
    $tag = "v$fullVersion"

    & gh release create $tag $zipPath --title "$fullVersion" --generate-notes
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  已发布 tag $tag" -ForegroundColor Green
    } else {
        Write-Host "  GitHub 发布失败，请检查 gh 登录状态" -ForegroundColor Red
    }
}


