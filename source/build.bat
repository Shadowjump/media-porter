@echo off
setlocal enabledelayedexpansion
rem ===================================================================
rem  MediaPorter - build script
rem
rem  Compiles the app with the C# compiler that ships inside Windows
rem  itself (.NET Framework 4.x), so it can be rebuilt on any Windows
rem  PC with nothing installed. No SDK, no Visual Studio, no internet.
rem
rem  Usage:  build.bat            -> builds APP\MediaPorter.exe
rem          build.bat "out.exe"  -> builds to a specific file
rem ===================================================================

set "SRC=%~dp0"
set "SRC=%SRC:~0,-1%"
for %%I in ("%SRC%\..") do set "APPDIR=%%~fI"

set "OUT=%~1"
if "%OUT%"=="" set "OUT=%APPDIR%\MediaPorter.exe"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo [X] Could not find the .NET Framework C# compiler ^(csc.exe^).
  echo     Expected in %WINDIR%\Microsoft.NET\Framework64\v4.0.30319
  exit /b 2
)

set "WPF=%FW%\WPF"

rem Explorer shows the generic exe icon unless one is embedded at compile
rem time; the runtime-drawn window icon does not help the file itself.
set "ICON="
if exist "%APPDIR%\ui\app.ico" set ICON=/win32icon:"%APPDIR%\ui\app.ico"

echo.
echo  Compiling MediaPorter
echo    compiler : %FW%\csc.exe
echo    output   : %OUT%
echo.

"%FW%\csc.exe" /nologo /nowarn:1701,1702 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%OUT%" ^
  /win32manifest:"%SRC%\app.manifest" ^
  %ICON% ^
  /reference:"%FW%\System.dll" ^
  /reference:"%FW%\System.Core.dll" ^
  /reference:"%FW%\System.Xml.dll" ^
  /reference:"%FW%\System.Drawing.dll" ^
  /reference:"%FW%\System.Windows.Forms.dll" ^
  /reference:"%FW%\System.Management.dll" ^
  /reference:"%FW%\System.Xaml.dll" ^
  /reference:"%FW%\System.IO.Compression.dll" ^
  /reference:"%FW%\System.IO.Compression.FileSystem.dll" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  "%SRC%\*.cs"

if errorlevel 1 (
  echo.
  echo  [X] Build failed.
  exit /b 1
)

copy /y "%SRC%\app.config" "%OUT%.config" >nul 2>&1

echo.
echo  [OK] Build succeeded.
exit /b 0
