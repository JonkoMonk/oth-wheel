=====================================================================
  over the hill  -  wheel mod & cockpit view
=====================================================================

Two mods in one pack. The installer asks which ones you want.

WHEEL MOD: drive over the hill with a steering wheel and pedals:
steering, throttle, brake, clutch, wheel buttons, and real force
feedback calculated from the car's tyres (you feel grip, bumps,
slides and crashes). Works in single player and in co-op.

COCKPIT VIEW: a first-person view from the driver's seat, with a real
interior in the game's style: working dials (speed, revs, fuel,
outside temperature), warning lights, gear display, and a steering
wheel, pedals and levers that move. Painted parts take your car's
livery. Works with keyboard, controller or wheel; with the wheel mod
the cockpit steering wheel turns exactly like your real one. Cars
with a cockpit so far are listed by the installer; more follow in
updates.

  - Moza R3 (with ES rim and SR-P Lite pedals): works right away,
    everything is already set up.
  - Any other wheel (Logitech, Thrustmaster, Fanatec, Moza, Simagic,
    ...): a setup wizard in the game asks you to turn the wheel, press
    the pedals and press the buttons you want. Takes about 2 minutes.
    Separate pedals, handbrakes etc. on their own USB plug work too.

Windows, Steam version of over the hill.


---------------------------------------------------------------------
  INSTALL
---------------------------------------------------------------------

1. Close the game (and MOZA Pit House if you have it).
2. Extract this zip (right-click > "Extract All..."). Don't run it
   from inside the zip.
3. Double-click Install.bat and follow the pop-ups.
   - If Windows shows "Windows protected your PC": click
     "More info" > "Run anyway".
   - If Windows asks for administrator permission: click Yes.

The installer asks which parts you want (wheel mod, cockpit view, or
both), finds the game by itself (any Steam library, any drive) and
installs them into the game folder, with a mod loader:
  - No mod loader yet: it installs MelonLoader (most other over the
    hill mods use it too, so you can add those later).
  - MelonLoader already there: it only adds the wheel mod. Your other
    mods stay as they are.
  - BepInEx already there: it uses BepInEx instead.
  - Both found: it stops, because they don't work together.
For Moza owners it also installs the Pit House profile
"R3 - over the hill (wheel mod)". No original game files are changed.

Another wheel mod (TrailWheel) installed? The installer offers to move
it to Mods\disabled\ (not deleted), because two wheel mods fight over
the wheel.

Prefer to install by hand? See the "Manual install" folder.


---------------------------------------------------------------------
  FIRST START  (important!)
---------------------------------------------------------------------

1. Plug in the wheel and start its driver software (Moza Pit House,
   Logitech G HUB, Thrustmaster / Fanatec control panel). Moza: select
   the profile "R3 - over the hill (wheel mod)".
   Set the rotation to 540 degrees if you can: that feels best here.
2. Start the game from Steam.
   The FIRST start takes 1-2 minutes before anything shows up. The
   mod loader prepares itself (and downloads a few files once, so
   be online). It is NOT stuck. Don't close it.
   MelonLoader: a black text window opens next to the game. That's
   MelonLoader's log; it's normal. MelonLoader also needs Microsoft
   .NET 6: if it isn't on your PC, MelonLoader installs it on the
   first start (click Yes if Windows asks for permission).
3. Keep the game window in front while it starts (don't alt-tab).
4. Get into the car.
   - Moza R3: just drive. After a few seconds the top-left corner
     shows "Wheel mod ... active: ..., force feedback on".
   - Other wheels: the setup wizard opens by itself. Follow it.

Later starts are as fast as normal.


---------------------------------------------------------------------
  THE SETUP WIZARD  (F3)
---------------------------------------------------------------------

Press F3 in the game at any time. With a wheel that is already set
up, you get a menu:

  1  Change button assignments
  2  Full setup from scratch
  3  Force feedback test (direction and strength)
  4  Steering rotation (what's set in your driver software)
  5  Share this setup (saves it as a file)
  6  Save a support report on the desktop

The wizard is controlled with the wheel itself (it detects what you
turn and press) plus the number keys, F9 (skip / keep) and F10 (back).
F3 closes it. Nothing happens in the game while it is open, so it's
safe to use while sitting in the car.

The full setup:
  1. Devices      - shows what was found
  2. Steering     - turn fully left, fully right, back to the middle
  3. Rotation     - pick the rotation set in your driver software
  4. Pedals       - press throttle, brake, clutch (F9 = no clutch)
  5. Force feedback - let go of the wheel: the mod finds out which
                    way your wheel pushes, then a short test where you
                    choose too weak / good / too strong
  6. Buttons      - for each action, press the button you want
                    (F9 = keep / skip, 0 = no button)
Then save. Your setup is used automatically from then on.

Wheels set to more than 540 degrees (e.g. 900): the mod still gives
full steering at 540 degrees and you feel the end stop there.

SHARE YOUR SETUP: if your wheel isn't built in yet, choose "share" when
saving (or menu option 5). It saves the setup as a small file on your
desktop. Send that file to the mod's author (for example through the
mod's page): the next version of the mod can then recognize your
wheel automatically. The file only contains the wheel's name, its USB
id and your axis/button choices. Nothing is ever sent automatically.


---------------------------------------------------------------------
  BUTTONS  (Moza ES rim, built in)
---------------------------------------------------------------------

  Right paddle ....... shift up
  Left paddle ........ shift down
  A .................. winch: extend / interact, OK in menus
  B .................. winch: release
  Y .................. winch: attach / pull
  X .................. high / low range (4x4 transfer case)
  N .................. headlights
  P .................. parking brake
  START .............. engine on / off
  BOX ................ reset car
  WIP ................ push car
  FL ................. horn
  S1 ................. map
  MENU ............... pause menu / back
  RADIO .............. force feedback profile: Soft / Normal / Realistic / Heavy
  D-pad .............. look around (camera); arrows in menus
  S2 / HOME .......... camera zoom in / out (hold)
  CAM ................ cockpit (first person) view on / off
                       (needs the OTH Cockpit mod)
  PL ................. free

Other wheels: you choose in the wizard. Keyboard and gamepad keep
working as normal.


---------------------------------------------------------------------
  COCKPIT VIEW
---------------------------------------------------------------------

Get in:  zoom the camera in as far as it goes, then zoom in once more
         (mouse wheel, gamepad D-pad, or the wheel's zoom buttons).
         Or press F6. Or a wheel button: Moza CAM, other wheels the
         button you pick for "Cockpit view" in the wizard (F3).
Get out: zoom out, or the same key / button again.

Look around: mouse, gamepad right stick, or the wheel's D-pad. The
head turns back to the front by itself, and leans into corners a bit.
Numpad 8/2, 4/6, 9/3 move your seat; 7/1 tilt the view; +/- zoom.
The view hides by itself in menus, the map, camp and photo mode.

Cars without a cockpit yet just show "No cockpit for ... yet".

Settings (seat, field of view, look speed, leaning into corners):
  MelonLoader:  <game folder>\UserData\OthCockpit\jonkomonk.oth.cockpit.cfg
  BepInEx:      <game folder>\BepInEx\config\jonkomonk.oth.cockpit.cfg

When you press a wheel button, its name and action show briefly in
the top-left corner. Press F8 to turn that display off (and F8 again
to turn it back on). The choice is remembered.


---------------------------------------------------------------------
  FORCE FEEDBACK
---------------------------------------------------------------------

Four profiles, switch any time with the button you assigned to
"Force feedback profile" (Moza: RADIO):
  Soft ........ lighter
  Normal ...... between Soft and Realistic
  Realistic ... default
  Heavy ....... stronger

The overall strength also depends on your driver software. If it's
too strong or too weak, run the force feedback test (F3, option 3).


---------------------------------------------------------------------
  SETTINGS FILE  (optional)
---------------------------------------------------------------------

More can be tuned in this file (open it with Notepad):

  MelonLoader:  <game folder>\UserData\OthWheel\jonkomonk.oth.wheel.cfg
  BepInEx:      <game folder>\BepInEx\config\jonkomonk.oth.wheel.cfg

  (game folder: Steam > right-click over the hill > Manage >
   Browse local files)

Changes take effect within 2 seconds, even while the game runs.
Every setting has a short explanation above it. Examples:
  SetupKey ............ key for the setup wizard (default F3)
  LockDegrees ......... steering lock (default 540)
  ZoomSpeed ........... camera zoom speed
  CameraSpeed ......... camera look speed
  Profile / StrengthTrim  force feedback profile and overall strength
  [ButtonKeys] ........ which game key each action presses (only if
                        you changed the game's keyboard controls)

Your wheel setups (from the wizard) are in
  MelonLoader:  <game folder>\UserData\OthWheel\setups\
  BepInEx:      <game folder>\BepInEx\config\OthWheel\setups\
Delete the settings file to go back to the default settings.


---------------------------------------------------------------------
  PROBLEMS?
---------------------------------------------------------------------

"NO wheel found" message, or the wheel does nothing
  - Is the wheel plugged in and its driver software running? Then
    restart the game and keep its window in front while it starts.

"this wheel isn't set up yet"
  - Press F3 and go through the setup wizard.

No force feedback
  - "not tested yet": press F3 > 3 (force feedback test).
  - "NOT connected yet": click into the game window once.
  - Check that force feedback is on (not 0%) in the driver software.

Force feedback pushes the wheel the wrong way (away from center at
full lock)
  - Run the force feedback test again (F3 > 3), with your hands off
    the wheel when it asks.

The game doesn't start / crashes at start after installing
  - Run Uninstall.bat. The game is then back to normal.
  - Antivirus: some antivirus programs dislike "version.dll"
    (MelonLoader) or "winhttp.dll" (BepInEx) in the game folder.
    That's how the mod loader starts. Allow it.
  - "Both mod loaders are installed": keep only one (MelonLoader or
    BepInEx). Remove the other with the uninstaller of the mod that
    put it there, then run Install.bat again.

Camera mods (Rudimentary FPP, BonnetCam)
  - Their bonnet / first-person views open by zooming in past the
    closest view. The wheel's zoom buttons (Moza: S2 / HOME) work for
    that too.
  - Don't use them together with this pack's cockpit view: two
    first-person views fight over the camera.

After a game update the mod stops working
  - The game update may have changed things the mod relies on.
    Uninstall.bat makes the game work normally again; wait for a new
    version of the mod.

Something else
  - F3 > 6 saves a support report on your desktop (a zip with the
    logs, your setup and the device list). Send it to the mod's
    author together with a short description.


---------------------------------------------------------------------
  UNINSTALL
---------------------------------------------------------------------

Close the game, double-click Uninstall.bat. It removes the mod and
your wheel setups, and, if you want, the Pit House profile. The mod
loader:
  - MelonLoader: removed only if this installer put it there (it asks
    first if other mods use it). A MelonLoader you had before stays.
  - BepInEx: removed too (it asks first if other mods use it).
If the installer had moved TrailWheel aside, it offers to move it back.
