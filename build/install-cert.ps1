# ============================================================
#  install-cert.ps1  —  安装 LLCL 的 MSIX 签名证书
#
#  必须以【管理员身份】运行。仅需执行一次。
#
#  用法:
#    pwsh -ExecutionPolicy Bypass -File build\install-cert.ps1
# ============================================================

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$cer  = Join-Path $PSScriptRoot 'certs\llcl-signing.cer'

if (-not (Test-Path $cer)) {
    Write-Host "找不到证书文件: $cer" -ForegroundColor Red
    exit 1
}

# 检查是否管理员
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()) `
    .IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "请以【管理员身份】重新运行此脚本。" -ForegroundColor Red
    Write-Host "右键 PowerShell -> 以管理员身份运行，然后执行：" -ForegroundColor Yellow
    Write-Host "  pwsh -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Cyan
    exit 1
}

Write-Host "正在安装 LLCL 代码签名证书..." -ForegroundColor Green

# AppX 部署服务以 SYSTEM 运行，需要机器级信任
# TrustedPeople 用于信任自签名包；Root 用于让签名链验证通过
certutil -addstore TrustedPeople $cer | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "导入 TrustedPeople 失败" -ForegroundColor Red
    exit 1
}

certutil -addstore Root $cer | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "导入 Root 失败" -ForegroundColor Red
    exit 1
}

Write-Host "证书已安装到受信任存储。" -ForegroundColor Green
Write-Host ""
Write-Host "现在可以安装 MSIX 了：" -ForegroundColor Yellow
Write-Host "  Add-AppxPackage -Path dist\LLCL_0.1.0-beta.1_x64.msix" -ForegroundColor Cyan
Write-Host ""
Write-Host "若仍失败，请确认已开启开发者模式（设置 -> 系统 -> 高级 -> 开发人员模式）。" -ForegroundColor DarkGray
