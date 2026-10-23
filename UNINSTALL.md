# Removing the mod (BepInEx)

Installed: BepInEx 6.0.0-be.789 (Unity IL2CPP win-x64), 2026-10-17.
Download kept in `%USERPROFILE%\dev\oth-wheelmod-build\downloads\`.

To remove everything, close the game and delete these from
`C:\Program Files (x86)\Steam\steamapps\common\over the hill\`:

- `BepInEx\` (folder)
- `dotnet\` (folder)
- `winhttp.dll`
- `doorstop_config.ini`
- `.doorstop_version` (hidden file)
- `changelog.txt`

To remove only the wheel mod but keep BepInEx, delete `BepInEx\plugins\OthWheel.dll`.
To switch the wheel off without deleting anything, set `Enabled = false` in
`BepInEx\config\jonkomonk.oth.wheel.cfg` (works even while the game is running).

No original game files were changed. To disable the mod without deleting it,
open `doorstop_config.ini` and set `enabled = false`.
