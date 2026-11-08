# OTH Wheel — steering wheel, pedals and force feedback for *over the hill*

Drive **over the hill** (Funselektor, Steam) with a real wheel: steering, throttle, brake, clutch, H-shifter, wheel buttons, and **force feedback calculated from the car's tyres** — you feel grip, bumps, slides and crashes. Single player and co-op.

Watch it in action: [mod spotlight video](https://youtu.be/rHvPhiTygKQ) and the [November update](https://youtu.be/gRxpiDNqJK0) (smart camera, wheel in the menus, mirrors). Download: [OTH Wheel on Nexus Mods](https://www.nexusmods.com/overthehill/mods/83).

Questions or problems? Ask in the [JonkoMonk Discord](https://discord.gg/h2hXcYgkpE) (#wheel-help).

- **Moza R3** (ES rim, SR-P Lite pedals, HGP shifter): works right away.
- **Any other wheel** (Logitech, Thrustmaster, Fanatec, Moza, Simagic, …): an in-game setup wizard (**F3**) asks you to turn the wheel, press the pedals and pick your buttons. About 2 minutes.
- **Smart camera** (1.1.0): the third-person camera picks a good off-road view by itself (side-on when crawling, higher downhill and over crests, the downhill side along a mountain, no swinging round when reversing, zooms in when trees block the view); your own camera moves always win. `[SmartCamera]` in the settings file.
- **Wheel in the menus** (1.1.0): B = back and A = OK in every menu; the D-pad moves the map, looks around in photo mode and steps through the winch points; hold S1 for the game's quick action wheel. Since 1.1.2 the map opens on your car with the selection circle in the middle, the pedals switch tabs in the car menu, and a wheel button can open the F3 menu.
- **Every feature on or off:** the smart camera, force feedback, stalling, H-shifter, wheel buttons and on-screen messages each have their own switch, in the F3 menu or the settings file (`UserData\OthWheel\jonkomonk.oth.wheel.cfg`).

[**OTH Cockpit**](https://github.com/JonkoMonk/oth-cockpit) (alpha), a first-person view with a working interior whose steering wheel turns 1:1 with yours, is a separate mod that uses the same installer ([OTH Cockpit on Nexus Mods](https://www.nexusmods.com/overthehill/mods/90)).

## Download

The one-click installer is on the [Releases](../../releases) page (Nexus Mods blocks zips with install scripts), pick one:

| Zip | Contains |
|---|---|
| `OTH-Wheel-v….zip` | the wheel mod |
| `OTH-Cockpit-v…-alpha.zip` | the cockpit view |
| `OTH-Wheel-and-Cockpit-v….zip` | both (the installer asks which ones) |

Installing one zip and later another keeps what's already there. [Nexus Mods](https://www.nexusmods.com/overthehill/mods/83) has a version to copy in by hand.

## Install

1. Extract the zip.
2. Close the game, double-click `Install.bat`, follow the pop-ups. It finds the game, installs MelonLoader if you have no mod loader (or uses your BepInEx), and never changes original game files.
3. Start the game; the first start takes 1–2 minutes. Get in a car.

Uninstall: `Uninstall.bat` (asks which mods to remove if you have both). Manual install: see the `Manual install` folder in the zip.

## Building from source

Windows, .NET SDK 8 (target net6.0). The projects reference the game's generated interop assemblies from a MelonLoader (and optionally BepInEx) copy of the game; paths are in `src/Directory.Build.props`. `build.ps1` builds and installs, `package.ps1` makes the release zips (the cockpit zips need the cockpit project next to this one).

## Licence

MIT — see [LICENSE](LICENSE). The installer downloads MelonLoader (Apache-2.0) or BepInEx (LGPL-2.1) from their official sites; they keep their own licences.
