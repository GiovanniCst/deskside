@echo off
rem Builds and runs the DDC queue test against the monitor that is plugged in.
rem Close Deskside.exe first: two processes on the same bus invalidate the run.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%TEMP%\Deskside-Test-DdcQueue.exe
if not exist "%CSC%" (
    echo C# compiler not found at %CSC%
    exit /b 1
)
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /main:Deskside.TestDdcQueue ^
    /out:"%OUT%" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    "%~dp0..\src\*.cs" "%~dp0Test-DdcQueue.cs"
if errorlevel 1 exit /b 1
"%OUT%"
