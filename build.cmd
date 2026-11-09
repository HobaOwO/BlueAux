@echo off
rem Builds BlueAux.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install. Output: bin\BlueAux.exe
setlocal
cd /d "%~dp0"

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WPF=%FW%\WPF
set WM=%WINDIR%\System32\WinMetadata

set REFS=/r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Xml.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll"
set REFS=%REFS% /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\WindowsBase.dll"
set REFS=%REFS% /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Devices.winmd"
set REFS=%REFS% /r:"%WM%\Windows.Security.winmd" /r:"%WM%\Windows.Storage.winmd"
set RES=/resource:src\Styles.xaml,BlueAux.Styles.xaml /resource:src\MainView.xaml,BlueAux.MainView.xaml
set ICON=
if exist src\app.ico set ICON=/win32icon:src\app.ico

if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /nowarn:1607 /out:bin\BlueAux.exe /win32manifest:src\app.manifest %ICON% %REFS% %RES% src\*.cs
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

if not exist src\app.ico (
  rem First build: draw the icon, then build again so the .exe carries it.
  start "" /wait bin\BlueAux.exe --make-icon src\app.ico
  if exist src\app.ico call "%~f0" & exit /b
)

echo Built bin\BlueAux.exe
