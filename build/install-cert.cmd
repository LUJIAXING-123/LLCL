@echo off
REM ============================================================
REM  install-cert.cmd  -  安装 LLCL 的 MSIX 签名证书
REM
REM  双击运行，或右键「以管理员身份运行」。
REM  使用 certutil 直接导入，绕开 PowerShell 执行策略限制。
REM ============================================================

setlocal
cd /d "%~dp0"

set "CER=%~dp0certs\llcl-signing.cer"

if not exist "%CER%" (
    echo [错误] 找不到证书文件：
    echo        %CER%
    echo.
    pause
    exit /b 1
)

echo 正在安装 LLCL 代码签名证书...
echo 证书：%CER%
echo.

REM AppX 部署服务以 SYSTEM 运行，必须机器级信任
certutil -addstore TrustedPeople "%CER%"
if errorlevel 1 (
    echo.
    echo [失败] 导入 TrustedPeople 失败。
    echo        请确认本窗口已以管理员身份运行。
    pause
    exit /b 1
)

certutil -addstore Root "%CER%"
echo.

echo ============================================================
echo  证书安装完成。
echo.
echo  现在可以安装 MSIX：
echo    Add-AppxPackage -Path dist\LLCL_0.1.0-beta.1_x64.msix
echo.
echo  也可直接双击 dist 目录下的 .msix 文件安装。
echo ============================================================
echo.
pause
