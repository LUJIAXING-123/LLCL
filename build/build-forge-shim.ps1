# 重新生成 Forge 安装器外壳（build/forge-shim/src -> src/LLCL.Core/Assets/forge-shim.jar）
# 只有改动了 Java 源码才需要跑；jar 已提交进仓库，普通构建不需要 JDK。
param(
    [string]$JdkHome = ""
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'build\forge-shim\src\com\llcraft\forgeshim\ForgeInstaller.java'
$output = Join-Path $root 'src\LLCL.Core\Assets\forge-shim.jar'

if (-not (Test-Path $source)) {
    throw "找不到源码：$source"
}

function Find-Jdk {
    param([string]$Preferred)

    if ($Preferred) {
        $javac = Join-Path $Preferred 'bin\javac.exe'
        if (Test-Path $javac) { return (Split-Path -Parent $javac) }
        throw "指定的 JDK 不可用：$Preferred"
    }

    foreach ($candidate in @(
        'C:\Program Files\Java\jdk-17',
        'C:\Program Files\Eclipse Adoptium\jdk-17*'
    )) {
        $dirs = Get-ChildItem -Path $candidate -Directory -ErrorAction SilentlyContinue
        if ($candidate -notmatch '\*' -and (Test-Path (Join-Path $candidate 'bin\javac.exe'))) {
            return $candidate
        }
    }

    foreach ($pattern in @(
        'C:\Program Files\Java\jdk-*',
        'C:\Program Files\Eclipse Adoptium\jdk-*'
    )) {
        $dirs = Get-ChildItem -Path $pattern -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($dir in $dirs) {
            if (Test-Path (Join-Path $dir.FullName 'bin\javac.exe')) {
                return $dir.FullName
            }
        }
    }

    $javacCommand = Get-Command javac -ErrorAction SilentlyContinue
    if ($javacCommand) { return (Split-Path -Parent (Split-Path -Parent $javacCommand.Source)) }

    throw '找不到 JDK（需要 javac），请用 -JdkHome 指定 JDK 目录。'
}

$jdk = Find-Jdk -Preferred $JdkHome
Write-Host "使用 JDK: $jdk"

$stage = Join-Path $env:TEMP ('llcl-forge-shim-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    # 必须用 --release 8 编译：
    # 启动器按版本要求挑 Java，探测失败时会退回较低版本（实测退到过 Java 8）。
    # 若字节码版本高于运行时 Java，会直接抛 UnsupportedClassVersionError，
    # 表现为弹「A Java Exception has occurred」错误框，日志里才有真正原因。
    & (Join-Path $jdk 'bin\javac.exe') --release 8 -encoding UTF-8 -d $stage $source
    if ($LASTEXITCODE -ne 0) { throw "javac 失败，退出码 $LASTEXITCODE" }

    $assetsDir = Split-Path -Parent $output
    if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }
    if (Test-Path $output) { Remove-Item $output -Force }

    & (Join-Path $jdk 'bin\jar.exe') --create --file $output -C $stage 'com'
    if ($LASTEXITCODE -ne 0) { throw "jar 打包失败，退出码 $LASTEXITCODE" }

    $size = (Get-Item $output).Length
    Write-Host "已生成 $output（$size 字节）"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

