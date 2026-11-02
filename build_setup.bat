@echo off
rem Builds Controller-Setup.exe, the installer. Run build.bat first: the installer carries Controller.exe inside.
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /debug- ^
  /out:"Controller-Setup.exe" /win32icon:app.ico ^
  /resource:Controller.exe,Controller.exe /resource:logo.png,logo.png ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll ^
  Setup.cs
