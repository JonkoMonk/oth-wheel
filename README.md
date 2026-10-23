# OTH Wheel — steering wheel, pedals and force feedback for *over the hill*

Drive **over the hill** (Funselektor, Steam) with a real wheel: steering, throttle, brake, clutch, H-shifter, wheel buttons, and **force feedback calculated from the car's tyres** — you feel grip, bumps, slides and crashes. Single player and co-op.

- **Moza R3** (ES rim, SR-P Lite pedals, HGP shifter): works right away.
- **Any other wheel** (Logitech, Thrustmaster, Fanatec, Moza, Simagic, …): an in-game setup wizard (**F3**) asks you to turn the wheel, press the pedals and pick your buttons. About 2 minutes.
- Optional in the same installer: **OTH Cockpit**, a first-person view with a working interior; its steering wheel turns 1:1 with yours.

## Install

1. Download the zip from the [Releases](../../releases) page (or Nexus Mods) and extract it.
2. Close the game, double-click `Install.bat`, follow the pop-ups. It finds the game, installs MelonLoader if you have no mod loader (or uses your BepInEx), and never changes original game files.
3. Start the game; the first start takes 1–2 minutes. Get in a car.

Uninstall: `Uninstall.bat`. Manual install: see `installer/Manual install.txt`.

## Building from source

Windows, .NET SDK 8 (target net6.0). The projects reference the game's generated interop assemblies from a MelonLoader (and optionally BepInEx) copy of the game; paths are in `src/Directory.Build.props`. `build.ps1` builds and installs, `package.ps1` makes the release zip.

## Licence

MIT — see [LICENSE](LICENSE). MelonLoader (Apache-2.0) and BepInEx (LGPL-2.1) are bundled with their own licences.
