@echo off
rem Builds Deskside.exe with the C# compiler that ships with Windows.
rem No SDK, no NuGet, no project file: .NET Framework 4.x is present on every
rem Windows 10 and 11 install, and so is its compiler.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo C# compiler not found at %CSC%
    echo Deskside needs the .NET Framework 4.x compiler, part of Windows.
    exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
    /win32icon:"%~dp0assets\icon.ico" ^
    /out:"%~dp0Deskside.exe" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0Deskside.exe
