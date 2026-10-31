# Controller

A small native Windows tool that sits between your controller and a game (made for PS4 Remote Play through [chiaki-ng](https://github.com/streetpea/chiaki-ng)). It reads the real controller, shapes the input, and hands the game one clean virtual controller.

## What it does

- **Controllers:** DualSense / DualSense Edge, DualShock 4, Xbox One / Series and GameSir (in Xbox mode).
- **Virtual controller:** a virtual DualShock 4 (or Xbox 360) through ViGEm. A game started from the program sees only this pad, not the physical one.
- **Slots:** per-slot deadzones (left and right stick), default slot at start, as many slots as you want (up to 10).
- **Aim card (optional, per slot):** response curve (Standard / Linear / Dynamic) and ADS sensitivity, with L1 or L2 as the aim button.
- **Scripts (optional, per slot):** your own C# scripts that can change the controller state every report. A blank template with a reference is included.
- **M&K mode:** the right stick and the touchpad move the mouse, Cross / Circle click, the left stick scrolls. The game gets a neutral pad meanwhile.
- **On-screen keyboard (English / Arabic):** opened with a controller combo, driven by the d-pad / left stick, with side buttons that start programs or send key commands such as Ctrl+C.
- **Shortcut panel:** up to 10 pages of 12 big buttons (program or key command, your own text or picture), opened with a combo. Each button can also have its own controller combo.
- **Tray / start with Windows:** runs quietly in the background with its icon next to the clock.
- **Updates:** when a newer release is published here, an update icon appears; one click downloads it and restarts the program.

## Requirements

- Windows 10 / 11, .NET Framework 4.x (already part of Windows).
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver for the virtual controller.
- chiaki-ng is **not** included: install it separately if you want to stream a PS4.

## Install

Download `Controller.exe` from the [latest release](../../releases/latest) and run it. Nothing else to install besides ViGEmBus. Settings are saved next to the program (`settings.ini`).

## Build from source

The program is one C# file and the compiler that ships with Windows is enough:

```
build.bat
```

This writes `Controller.exe` next to the sources (`ControllerUnit.cs`, `app.ico`, `logo.png`, `ViGEmClient.dll`).

## Publishing an update (for the maintainer)

1. Change `AppInfo.Version` in `ControllerUnit.cs`, build, and test.
2. Create a GitHub release whose tag is the same number with a `v` in front (for example `v2.6.0`).
3. Attach the new `Controller.exe` to the release, and optionally `Controller.exe.sha256` (the SHA-256 of the exe, so the program can check the download).

Running copies check the newest release a few seconds after start and every six hours.

## Credits

- [ViGEmClient](https://github.com/nefarius/ViGEmClient) (MIT) is embedded to talk to the ViGEmBus driver.
- Button icons follow the classic PlayStation look.
