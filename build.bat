@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [错误] 未找到 csc.exe，请确认已安装 .NET Framework 4.x
  exit /b 1
)

echo 使用编译器: %CSC%
if not exist bin mkdir bin
if not exist dist mkdir dist
if not exist dist\portable mkdir dist\portable

if not exist oui.gz (
  echo [提示] 缺少 oui.gz，尝试压缩 oui.txt ...
  powershell -NoProfile -Command "if(Test-Path 'oui.txt'){$b=[IO.File]::ReadAllBytes('oui.txt');$ms=New-Object IO.MemoryStream;$gz=New-Object IO.Compression.GZipStream($ms,[IO.Compression.CompressionMode]::Compress);$gz.Write($b,0,$b.Length);$gz.Close();[IO.File]::WriteAllBytes('oui.gz',$ms.ToArray())}"
)
if not exist oui.gz (
  echo [错误] 需要 oui.gz 或 oui.txt
  exit /b 1
)
if not exist app.ico (
  echo [错误] 缺少 app.ico
  exit /b 1
)

echo 编译最终单文件版（图标 + 厂商库内嵌）...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /win32manifest:app.manifest ^
  /win32icon:app.ico ^
  /resource:oui.gz,LanIpWatcher.oui.gz ^
  /out:bin\LanIpWatcher.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Runtime.Serialization.dll ^
  Program.cs MainForm.cs IpScanner.cs Config.cs AutoStart.cs HeatMapControl.cs OuiLookup.cs UiFonts.cs NameResolver.cs

if errorlevel 1 (
  echo 编译失败
  exit /b 1
)

copy /Y app.config bin\LanIpWatcher.exe.config >nul
copy /Y bin\LanIpWatcher.exe dist\portable\LanIpWatcher.exe >nul
copy /Y app.config dist\portable\LanIpWatcher.exe.config >nul
copy /Y 使用说明.txt dist\portable\使用说明.txt >nul

echo.
echo ========================================
echo  最终单文件: dist\portable\LanIpWatcher.exe
echo ========================================
exit /b 0
