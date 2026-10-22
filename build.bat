@echo off
rem Builds NumpadMouse.exe using the C# compiler that ships with Windows (.NET Framework 4).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe. Turn on ".NET Framework 4.8" in "Turn Windows features on or off".
  pause
  exit /b 1
)
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:NumpadMouse.exe NumpadMouse.cs
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Built NumpadMouse.exe - double-click it to start.
pause
