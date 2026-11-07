# OTH Wheel: changes

Version numbers: MINOR (1.X) = new feature, PATCH (1.0.X) = fixes.

## Planned

- 1.1.0 (rest): "VR recenter" wheel-button action (for OTH VR)
- 1.2.0: clutch required (optional: shifting without the clutch grinds)

## 1.1.1 (2026-11-07)

- Known: on the map the selection circle still only stays in the middle after you press A once (the mod
  now switches the game's circle to its Centered mode and needs a real mouse move to let go, but that
  wasn't enough yet).
- Logitech G923 (PlayStation 4 and PC) works right away: built-in setup, thanks to rh5knk. Your own setup
  from the wizard still goes first.
- Removing the mod leaves nothing behind. A player deleted `Mods\OthWheel.Melon.dll` and F3 still opened
  the wheel menu: another copy was still loading (e.g. `BepInEx\plugins\OthWheel.dll` from the installer
  on a BepInEx game, or a renamed copy like `OthWheel.Melon (2).dll`).
  - New F3 > **Remove the mod**: lists every file to delete (also copies in other folders) and opens the
    folder with the mod's file selected. Without a wheel setup: 9 in the setup wizard.
  - At the start, a warning when the mod is installed more than once (the copies fight over the wheel).
    The log says which file the mod was loaded from.
  - Uninstall.bat removes every copy it finds (any name starting with OthWheel / OthCockpit, in Mods\,
    Plugins\ and BepInEx\plugins\ with their subfolders) and says if one couldn't be deleted.
    Install.bat removes older copies, so exactly one is left.
  - README: the manual uninstall also lists the BepInEx files.

## 1.1.0 (2026-11-06)

- Works with the game's 26.10.30 update.
- Known: on the map the selection circle may only stay in the middle after you press A once.
- Menu buttons keep working after the game's 26.10.30 update (one failed read at startup used to switch them
  off until the next launch).
- **Smart camera** (third person): with your hands on the wheel you can't keep turning the camera, so it
  picks a good off-road view by itself from the terrain around the car:
  - slow off-road driving: a bit to the side of and above the car; closer to straight behind at speed;
  - downhill and over a crest: higher, so you see the ground ahead (uphill it stays low: from above you
    only saw the roof);
  - along the side of a mountain: round to the downhill side, looking back up at the car
    (`SideSlope = Downhill | Uphill | Off`); it only changes sides when the ground clearly leans the other
    way for a moment;
  - reversing: no swinging round; it rises a little so you see the ground behind the car;
  - trees, rocks or buildings between the car and the camera: it zooms in until the view is clear
    (treetops from the terrain's tree list: they have no colliders);
  - distance: a little closer when crawling or among trees, further back when fast or downhill;
  - your side: new wheel action `CameraSide` (Moza preset: PL) puts it on the left or right; a
    mountainside borrows it and gives it back after 6 s of flat ground (`SideReturn`), and it stays at
    least 10 s on a side before it swaps by itself (on rolling ground it swapped back and forth);
  - F3 menu: new Camera page with every setting, live while driving;
  - standing still: it leaves the camera alone.
  Your own camera moves (mouse, right stick, D-pad) always win; it eases back after `ReturnAfter` seconds.
  Raising or lowering the camera yourself sets the flat height until the game restarts.
  Settings under `[SmartCamera]`: Enabled, Strength, Speed, ReturnAfter, SideAngle, Height, MaxHeight,
  SideSlope, Reverse, ZoomInWhenBlocked, SideReturn, Distance, DpadSpeed, DpadMaxSpeed, Log. `Enabled = false` = the game's own camera.
- Camera buttons (D-pad): turn the game camera directly and smoothly (no simulated mouse), speeding up
  while held (60 → 240 °/s). With the smart camera off they work as before.
- **Wheel buttons in the game's menus** (the mod now knows which screen has the controls):
  - B = back (Escape) in every menu, photo mode, the map, the music player and the quick action wheel;
    while driving it still releases the winch;
  - A = OK in menus (Enter, no more winch extend), takes the photo in photo mode, sets the map marker,
    play / pause in the music player;
  - quick action wheel (the game's Q): hold the map button (Moza: S1) for half a second, or give the new
    `QuickMenu` action its own button. D-pad left/right steps round the wheel, up/down picks the top /
    bottom item, A = OK, B = close;
  - D-pad moves the map (W/A/S/D) and looks around in photo mode (speeding up while held); zoom buttons
    zoom the map smoothly and the photo camera; on the map the selection circle stays in the middle of
    the screen (until you move the real mouse);
  - winch point picker (on the car and the anchor in the world): the D-pad steps through every point in a
    loop (right/down = next, left/up = back), A picks it;
  - settings: `[MenuKeys]` (OK, Back, PhotoModeOK, MapOK, MusicOK), `[Buttons] QuickMenuOnMapHold`;
    B in the F3 settings menu = back.
- `[General] ShowMessages` (F3 > On-screen messages): turn off every yellow message in the top-left corner
  (only a warning at the start stays, when the wheel isn't found or force feedback isn't connected).
  F3 > Buttons now also has "Show button presses" (the F8 display).
- Smaller logs: the clutch/engine line comes at once when the gear, stall state or clutch pedal changes,
  otherwise at most every 2 seconds (it was up to 5 lines a second while driving).

## 1.0.0 (2026-10-24), first public release

- Steering, pedals (incl. clutch), H-shifter with real stalling, wheel buttons, force feedback from the
  car's tyres (Soft / Normal / Realistic / Heavy), setup wizard + settings menu (F3) for any wheel,
  one-click installer (MelonLoader or BepInEx), co-op.
