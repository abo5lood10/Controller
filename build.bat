@echo off
rem Builds Controller.exe with the C# compiler that ships with Windows (.NET Framework 4.x). Run it from this folder.
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /debug- ^
  /out:"Controller.exe" /win32icon:app.ico ^
  /resource:ViGEmClient.dll,ViGEmClient.dll /resource:app.ico,app.ico /resource:logo.png,logo.png ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  ControllerUnit.cs
