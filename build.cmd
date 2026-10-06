@echo off
rem Builds Capsule with the C# compiler that ships with Windows (.NET Framework 4.8.1).
rem Nothing is downloaded. Works from any folder.
rem   build.cmd       builds into bin\, stopping a running Capsule.exe first (it holds bin\Capsule.exe open)
rem   build.cmd dev   builds into bin-dev\ and runs the tests there, leaving a running Capsule alone
setlocal
cd /d "%~dp0"
set "OUT=bin"
if /i "%~1"=="dev" set "OUT=bin-dev"
if not "%~1"=="" if /i not "%~1"=="dev" (
    echo Usage: build.cmd [dev]
    exit /b 2
)
if not "%~2"=="" (
    echo Usage: build.cmd [dev]
    exit /b 2
)
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
set "WPF=%FW%\WPF"
rem The live blur uses Windows' visual layer, described by the WinMD files that ship with Windows; the
rem facades in the GAC let this compiler read them.
set "WINMD=%WINDIR%\System32\WinMetadata"
set "GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL"
set FLAGS=/nologo /langversion:5 /optimize+
set REFS=/r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll
set UIREFS=/r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll
set GLASSREFS=/r:System.Numerics.dll /r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.UI.winmd" /r:"%WINMD%\Windows.Graphics.winmd" /r:"%WINMD%\Windows.System.winmd" /r:"%GAC%\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll" /r:"%GAC%\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll" /r:"%GAC%\System.Runtime.InteropServices.WindowsRuntime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%GAC%\System.Numerics.Vectors\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Numerics.Vectors.dll"

if not exist %OUT% mkdir %OUT%
if "%OUT%"=="bin" taskkill /im Capsule.exe /f >nul 2>&1

echo Building capsule-hook.exe into %OUT%
"%CSC%" %FLAGS% /target:exe /out:%OUT%\capsule-hook.exe %REFS% hook\*.cs src\shared\*.cs || goto :fail

echo Building Capsule.exe
"%CSC%" %FLAGS% /target:winexe /out:%OUT%\Capsule.exe /win32manifest:src\app.manifest %REFS% %UIREFS% %GLASSREFS% src\*.cs src\shared\*.cs || goto :fail
copy /y src\App.config %OUT%\Capsule.exe.config >nul || goto :fail

echo Building Tests.exe
"%CSC%" %FLAGS% /target:exe /main:Capsule.TestRunner /out:%OUT%\Tests.exe %REFS% %UIREFS% %GLASSREFS% tests\*.cs src\*.cs src\shared\*.cs || goto :fail

echo Running tests
%OUT%\Tests.exe || goto :fail
echo Build OK
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
