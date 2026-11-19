# OTH Wheel: changes

Version numbers: MINOR (1.X) = new feature, PATCH (1.0.X) = fixes.

## Planned

- 1.1.0 (rest): "VR recenter" wheel-button action (for OTH VR)

## 1.2.0 (2026-11-19)

- Update notice: when a newer version is out, a message in the game's menu (main menu or pause menu) says so once,
  with a button that opens its Nexus page and a "Don't show this again" box (ticked = not again for that version; a
  newer one is shown once more). With OTH Cockpit 0.4.4 or newer installed too, one message covers both mods. After
  that, F3 shows a line about it until you update. Nothing is downloaded by itself. The mod asks GitHub once per game
  start and sends nothing about you; switch it off with `[Updates] Check = false` in the settings file.
- Installer: running Install.bat over an older version says what it updates ("Updating: wheel 1.1.3 -> 1.2.0,
  cockpit 0.4.3 -> 0.4.4. Your settings and wheel setups are kept") and no longer asks again whether you want the
  parts you already have; it only asks about a part from the zip that isn't installed yet.

- Stutter fix: the game no longer hitches every few seconds when no wheel is set up yet (no wheel plugged in, only a
  pad or another controller, or the setup wizard never run). The mod looked for new controllers every 3 seconds, and
  on some PCs that look takes long enough to drop frames (about 0.17 s on the author's PC). Now it only looks when
  Windows says something was plugged in or out, and a controller it can't open is no longer tried again on every
  look. Plugging in a wheel while the game runs still works. Reported by two players.

- Setup wizard on first start: the yellow start message no longer says "this wheel isn't set up yet. Press F3 for
  the setup wizard" while the wizard is already open. Pressing F3 as told closed the wizard that had just opened by
  itself, and it didn't come back that session (reported by a player with a Moza R21). The message now waits until
  the wizard is closed.

- Clutch needed to shift (H-shifter, optional, off by default): a gear only goes in with the clutch pedal pressed
  (50% by default). Without it the gear grinds, with a short buzz in the wheel, and doesn't go in: the car stays
  in neutral until you press the clutch with the lever still in the gear, or move the lever. Taking a gear out never
  needs the clutch, and with the engine off gears go in freely. Works with the real clutch and stalling. F3 >
  Clutch & shifter > "Clutch to shift", "Clutch needed", "Grind buzz"; Setup tools > 0 (also offered right after
  the shifter setup); settings file `[Shifter] ClutchRequired`, `ClutchToShift`, `GrindBuzz`. Needs a clutch pedal
  (starts once the clutch has been pressed).
- Clutch to shift, two more settings (F3 > Clutch & shifter): "After a grind" = Neutral (default) or Stay in old
  gear (the gear you came from stays in, as if the shift was refused; it doesn't drop to neutral while the lever
  crosses neutral, and moving the lever back to that gear just keeps it), and "Grind buzz length" (0.3 s by default,
  0.1-1 s, or "While in gear" = as long as the lever sits in the gear without the clutch). Settings file
  `[Shifter] AfterGrind`, `GrindBuzzLength`.
- Smart camera: on slopes and mountainsides it now keeps the same "Side angle" as on the flat; before, they swung
  it up to 55 degrees further round. The old swing is still there as an option: F3 > Camera > "Slope side swing"
  (Off by default; settings file `[SmartCamera] SlopeSwing`).
- Smart camera: turning the camera with the D-pad past trees or rocks no longer zooms it in and out quickly (the
  "zoom in when blocked" now waits while you turn it, a block must last a quarter second, and it stays in until
  the view has been clear for 2 s, so rocks no longer pump it in and out).
- F3 number settings are written to the log when you change them (helps reading test logs).
- F3 > Camera: "Cockpit: look into turns" (Off / 10-60 degrees), shown with OTH Cockpit 0.4.4 or newer.
- Quick action wheel: no more yellow "Quick menu: D-pad = choose ..." line top left (only with "Show button
  presses" on).
- Range as gears (2-gear cars, H-shifter): when the game window wasn't in front, the high/low switch gave up with
  "no key for HiLo"; it now waits and switches once the game is in front again. A really missing key says so.
- Quick action wheel, D-pad: the first press jumps to that side's item (left = leftmost, right = rightmost, up = top,
  down = bottom). After that each press steps one item round the ring the way the arrow points from where you are
  (at the top or bottom, left/right step left/right; on the left or right item, up/down step up/down).
- On the first drive after installing, the start message also says "Press F3 for the settings menu" (once).
- Smart camera, uphill: it now goes lower on a climb (the opposite of downhill), so you see the climb ahead
  instead of the car roof and the sky, even below the lowest "Height". A crest ahead hardly raises it while the
  climb is still steep; at the top it rises as before. It still stays above the ground ("Stay above the ground").
  It only goes as low as the ground allows: it stops lowering, and backs off at once, when the camera or the
  line to the car comes within "Stay above the ground" (at least 1 m). F3 > Camera > "Uphill lower" (1.5 m;
  0 = off; settings file `[SmartCamera] UphillDrop`). Downhill is unchanged.

## 1.1.3 (2026-11-08)

- Smart camera, downhill: it no longer sits half in the hill behind the car. The camera, and the line from the car
  to it, now stay at least 1 m above the ground, going higher than "Highest" when they have to (F3 > Camera >
  "Stay above the ground"). Going downhill it also rises sooner and may go above "Highest" by up to 30%, to look
  over the car down the slope (F3 > Camera > "Downhill extra height"; 0 = Highest is the limit everywhere).
- Fix: holding the D-pad (or an arrow key) in the F3 menu could switch force feedback off by accident: a held
  right opened the Force feedback page and kept going, onto the on/off switch. A new page now waits until you let
  go, and on/off switches and choices change once per press. If force feedback is off, the message at the start
  of a drive now says so (also with on-screen messages turned off).
- F3 menu: a "Questions? Discord" line on the main page (opens the JonkoMonk Discord invite in your browser).
- Quieter log: the map line "circle still being moved back" is written once per game session (it came on every
  first map after loading in; harmless, the mod already wins).

## 1.1.2 (2026-11-08)

- B goes back out of the winch and the inventory when you opened them from the quick action wheel (it pressed
  the winch-release key there instead of Escape). On the winch point picker the D-pad steps through the points
  again.
- Map:
  - The selection circle is in the middle as soon as the map opens (the game's "centred" circle mode leaves the
    circle wherever the mouse put it, so the mod now puts it in the middle). A clicks where the circle is.
  - Opening the map while driving or from a menu (also the first map after loading in): it opens centred on
    your car (F3 > Buttons > "Map opens on the car").
  - The D-pad moves the map slowly at first, so you can put the circle on a safe house without overshooting;
    held longer it speeds up (F3 > Buttons > "Map D-pad speed").
  - Only a real mouse move hands the circle to the mouse; a pointer that jumps by itself is put back. After
    the mouse took over, the D-pad brings the circle back to the middle.
- Photo mode: the D-pad looks around faster, and the zoom buttons zoom much faster (speeding up while held).
  Both can be set in F3 > Buttons.
- Smart camera, reversing: when you turn the camera yourself while reversing, it stays that way until you drive
  forward again (also a turn made with reverse gear in before the car moves). The next reverse starts with
  the view you chose last time (F3 > Camera > "Next reverse: last view"; turn it off to start every reverse
  with the normal view).
- Car selection / customisation menu: the pedals switch tabs (throttle = next, brake = previous; F3 > Buttons >
  "Pedals switch tabs"). New wheel-button actions "Menus: previous tab" / "Menus: next tab" (the game's Q / E), in
  the setup wizard and F3 > Buttons, for wheels that want them on buttons.
- New wheel-button action "Wheel mod menu (F3) open / close", so the settings menu works without a keyboard
  (D-pad chooses, B = back). In the setup wizard and F3 > Buttons like every other action.
- The top-left text when you press a wheel button is off by default now (also switched off once for existing
  installs). To check your buttons work, turn it on with F8 or F3 > Buttons > "Show button presses". It
  shows only the devices in your wheel setup now (an Xbox pad next to the wheel showed its buttons there too;
  they never did anything). The cockpit-view, tab and menu-close buttons no longer show a confirmation text either.

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
