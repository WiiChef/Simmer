@echo off
rem Builds Simmer.exe (and tools\calibrate.exe) with the Roslyn compiler from VS Build Tools, for .NET Framework 4.8.1.
rem AnyCPU without 32-bit preference runs as native ARM64 on this laptop.
setlocal
set CSC="C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set OPTS=/nologo /optimize+ /platform:anycpu /langversion:latest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
cd /d "%~dp0"
if "%1"=="calibrate" (
  %CSC% %OPTS% /target:exe /out:tools\calibrate.exe src\Native.cs src\Power.cs src\Meter.cs tools\Calibrate.cs || exit /b 1
  exit /b 0
)
if "%1"=="selftest" (
  %CSC% %OPTS% /r:System.Web.Extensions.dll /define:SELFTEST /target:exe /out:tools\selftest.exe src\Native.cs src\Power.cs src\Meter.cs src\Governor.cs src\Settings.cs src\Safety.cs src\Engine.cs src\Fan.cs src\Charge.cs src\Drain.cs src\Telemetry.cs src\Pep.cs src\Elevation.cs src\Program.cs || exit /b 1
  exit /b 0
)
%CSC% %OPTS% /r:System.Web.Extensions.dll /target:winexe /win32icon:simmer.ico /out:Simmer.exe src\*.cs || exit /b 1



