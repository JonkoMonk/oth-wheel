# Installer / uninstaller for the over the hill wheel mod and the cockpit mod (one pack).
# Started by Install.bat / Uninstall.bat (Windows PowerShell 5.1). All user communication is pop-ups.
# The player picks the parts: the wheel mod (steering wheels) and/or the cockpit view (everyone).
#
# Mod loader choice (only one loader per game folder works):
#   no loader yet        -> install MelonLoader (most over the hill mods use it) + our DLLs in Mods\
#   MelonLoader present  -> only add our DLLs to Mods\ (offer an update if it's older than 0.7)
#   BepInEx present      -> BepInEx builds in BepInEx\plugins\ (as before)
#   both present         -> stop: they don't work together
# Cockpit models: MelonLoader UserData\OthCockpit\models\, BepInEx BepInEx\plugins\OthCockpit\models\.
param(
    [switch]$Uninstall,
    [string]$GamePath = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

$Title = 'over the hill - wheel mod & cockpit'
$FilesDir = $PSScriptRoot
$PackageDir = Split-Path $FilesDir -Parent
$GameExe = 'over the hill.exe'
$BepDll = 'OthWheel.dll'
$MelonDll = 'OthWheel.Melon.dll'
$PluginCfg = 'jonkomonk.oth.wheel.cfg'
$CockpitFiles = Join-Path $FilesDir 'cockpit'
$CockpitBepDll = 'OthCockpit.dll'
$CockpitMelonDll = 'OthCockpit.Melon.dll'
$CockpitCfg = 'jonkomonk.oth.cockpit.cfg'
$PresetId = 'b7d3c2e8-4f1a-4c9e-9a51-0e7f3d2a6c41'   # fixed, so reinstalling replaces the same Pit House preset
# Files and folders BepInEx puts next to the game exe.
$BepItems = @('BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')
# Files and folders MelonLoader puts (or creates on first start) next to the game exe.
$MelonItems = @('MelonLoader', 'version.dll', 'Mods', 'Plugins', 'UserData', 'UserLibs')
$MelonTested = [version]'0.7.3'
$MelonMinimum = [version]'0.7.0'

# A hidden owner window keeps every pop-up in front of other windows.
$Owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false; Opacity = 0 }
$Owner.Show(); $Owner.Hide()

function Show-Message([string]$text, [string]$icon = 'Information', [string]$buttons = 'OK') {
    return [System.Windows.Forms.MessageBox]::Show($Owner, $text, $Title, $buttons, $icon).ToString()
}
function Fail([string]$text) {
    Show-Message "$text`n`nNothing more was changed." 'Error' | Out-Null
    exit 1
}

# --- finding the game ---------------------------------------------------------------------------

function Get-SteamLibraries {
    $roots = @()
    foreach ($k in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            if ($p.SteamPath) { $roots += $p.SteamPath }
            if ($p.InstallPath) { $roots += $p.InstallPath }
        } catch {}
    }
    $roots += "${env:ProgramFiles(x86)}\Steam"
    $libs = @()
    foreach ($r in $roots) {
        $r = $r -replace '/', '\'
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    return $libs | Select-Object -Unique
}

function Find-Game {
    if ($GamePath -and (Test-Path (Join-Path $GamePath $GameExe))) { return $GamePath }
    foreach ($lib in Get-SteamLibraries) {
        $p = Join-Path $lib 'steamapps\common\over the hill'
        if (Test-Path (Join-Path $p $GameExe)) { return $p }
    }
    Show-Message "The game folder could not be found automatically.`n`nIn the next window, select the folder that contains '$GameExe'.`n(Steam: right-click the game > Manage > Browse local files shows it.)" | Out-Null
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog -Property @{ Description = "Select the 'over the hill' game folder (the one with $GameExe)" }
    if ($dlg.ShowDialog($Owner) -ne 'OK') { Fail 'No game folder selected.' }
    if (-not (Test-Path (Join-Path $dlg.SelectedPath $GameExe))) { Fail "'$GameExe' is not in that folder." }
    return $dlg.SelectedPath
}

function Wait-GameClosed {
    while (Get-Process -Name 'over the hill' -ErrorAction SilentlyContinue) {
        $r = Show-Message "over the hill is still running.`n`nClose the game completely, then click Retry." 'Warning' 'RetryCancel'
        if ($r -ne 'Retry') { Fail 'Cancelled.' }
    }
}

# Steam game folders are usually writable; if not, start again with administrator rights.
function Ensure-WriteAccess([string]$game) {
    $probe = Join-Path $game 'oth-wheelmod-write-test.tmp'
    try { Set-Content -Path $probe -Value 'x' -ErrorAction Stop; Remove-Item $probe -Force; return } catch {}
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) { Fail "Can't write to the game folder:`n$game" }
    Show-Message "Windows needs administrator permission to change the game folder.`nClick Yes in the next window." | Out-Null
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-GamePath', "`"$game`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $argList } catch { Fail 'Administrator permission was not given.' }
    exit 0
}

# Downloaded files are marked "from the internet"; clear that so nothing gets blocked.
function Unblock-Items([string]$game, [string[]]$items) {
    foreach ($item in $items) {
        $p = Join-Path $game $item
        if (-not (Test-Path $p)) { continue }
        if ((Get-Item $p -Force) -is [IO.FileInfo]) { Unblock-File $p -ErrorAction SilentlyContinue }
        else { Get-ChildItem -Path $p -Recurse -Force -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue }
    }
}

function Get-PitHousePresetDir {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    $dir = Join-Path $docs 'MOZA Pit House\Presets\Motor'
    if (Test-Path $dir) { return $dir }
    return $null
}

function Wait-PitHouseClosed([string]$why) {
    while (Get-Process -Name 'MOZA Pit House' -ErrorAction SilentlyContinue) {
        $r = Show-Message "MOZA Pit House is running.`n`n$why`n`nClose Pit House completely (also from the system tray next to the clock), then click Retry.`nClick Cancel to skip this step." 'Warning' 'RetryCancel'
        if ($r -ne 'Retry') { return $false }
    }
    return $true
}

# --- mod loaders ----------------------------------------------------------------------------------

function Test-BepInEx([string]$game) { return Test-Path (Join-Path $game 'BepInEx\core') }
function Test-MelonLoader([string]$game) { return (Test-Path (Join-Path $game 'MelonLoader')) -and (Test-Path (Join-Path $game 'version.dll')) }

function Get-MelonVersion([string]$game) {
    foreach ($rel in 'MelonLoader\net6\MelonLoader.dll', 'MelonLoader\MelonLoader.dll') {
        $dll = Join-Path $game $rel
        if (-not (Test-Path $dll)) { continue }
        $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
        $m = [regex]::Match([string]$v, '\d+\.\d+\.\d+')
        if ($m.Success) { return [version]$m.Value }
    }
    return $null
}

function Test-DotNet6 {
    return [bool](Get-ChildItem -Path (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App') -Directory -Filter '6.*' -ErrorAction SilentlyContinue)
}

# Simple key: value lines, e.g. "MelonLoader installed by this installer: True".
function Read-InstallLog([string]$path) {
    $values = @{}
    if (Test-Path $path) {
        foreach ($line in Get-Content $path) {
            $i = $line.IndexOf(': ')
            if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 2) }
        }
    }
    return $values
}

# --- the game's hidden DirectInput setting -------------------------------------------------------
# The game only lets its DirectInput (wheel) code run when DIRECT_INPUT_DEVICES_SUPPORT_ENABLED is
# true in its settings file; normally only the developers' console switches it on. The mod was
# tested with it on. Changing it from inside the game crashed it, so it is written here, with the
# game closed.
# File format: int32 count, then per entry: string key (7-bit length prefix), int32 length,
# .NET BinaryFormatter blob of the value. A boxed Boolean blob ends with <value byte> 0x0B.
$DirectInputKey = 'DIRECT_INPUT_DEVICES_SUPPORT_ENABLED'

function Get-GameSettingsFile {
    return Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\over the hill\Settings\settings'
}

function Test-BoolBlob([byte[]]$data, [int]$start, [int]$len) {
    if ($len -lt 20 -or $data[$start + $len - 1] -ne 0x0B -or $data[$start + $len - 2] -gt 1) { return $false }
    $text = [Text.Encoding]::ASCII.GetString($data, $start, $len)
    return $text.Contains('System.Boolean')
}

# Returns 'enabled', 'already', 'missing' (no settings file yet) or 'unknown' (format not recognised; nothing written).
function Enable-GameDirectInput {
    $file = Get-GameSettingsFile
    if (-not (Test-Path $file)) { return 'missing' }
    $data = [IO.File]::ReadAllBytes($file)
    $ms = New-Object IO.MemoryStream(, $data)
    $reader = New-Object IO.BinaryReader($ms)
    $template = $null
    $valueAt = -1
    try {
        $count = $reader.ReadInt32()
        if ($count -lt 1 -or $count -gt 10000) { return 'unknown' }
        for ($i = 0; $i -lt $count; $i++) {
            $key = $reader.ReadString()
            $len = $reader.ReadInt32()
            $start = [int]$ms.Position
            if ($len -lt 0 -or $start + $len -gt $data.Length) { return 'unknown' }
            $ms.Position += $len
            $isBool = Test-BoolBlob $data $start $len
            if ($key -eq $DirectInputKey) {
                if (-not $isBool) { return 'unknown' }
                $valueAt = $start + $len - 2
            } elseif ($isBool -and -not $template) {
                $template = [byte[]]$data[$start..($start + $len - 1)]
            }
        }
        if ($ms.Position -ne $data.Length) { return 'unknown' }
    } catch { return 'unknown' } finally { $reader.Dispose() }

    if ($valueAt -ge 0 -and $data[$valueAt] -eq 1) { return 'already' }
    if ($valueAt -lt 0 -and -not $template) { return 'unknown' }

    $backup = "$file.before-wheelmod"
    if (-not (Test-Path $backup)) { Copy-Item $file $backup }
    if ($valueAt -ge 0) {
        $data[$valueAt] = 1
        [IO.File]::WriteAllBytes($file, $data)
    } else {
        # Add the entry, using another Boolean setting's blob as the template.
        $template[$template.Length - 2] = 1
        $out = New-Object IO.MemoryStream
        $writer = New-Object IO.BinaryWriter($out)
        $writer.Write([int]($count + 1))
        $writer.Write($data, 4, $data.Length - 4)
        $writer.Write([string]$DirectInputKey)
        $writer.Write([int]$template.Length)
        $writer.Write($template)
        $writer.Flush()
        [IO.File]::WriteAllBytes($file, $out.ToArray())
        $writer.Dispose()
    }
    return 'enabled'
}

# --- install --------------------------------------------------------------------------------------

function Install-ForBepInEx([string]$game, [bool]$wheel) {
    $loaderZip = Get-ChildItem -Path $FilesDir -Filter 'BepInEx-*.zip' | Select-Object -First 1
    if (-not $loaderZip) { Fail 'The BepInEx zip is missing from the installer folder.' }
    # Update BepInEx to the tested build. Existing plugins and settings of other mods are kept.
    try { Expand-Archive -Path $loaderZip.FullName -DestinationPath $game -Force } catch { Fail "Copying the mod loader failed:`n$($_.Exception.Message)" }

    $config = Join-Path $game 'BepInEx\config'
    $plugins = Join-Path $game 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $config, $plugins | Out-Null
    $bepCfg = Join-Path $config 'BepInEx.cfg'
    if ((Test-Path $bepCfg) -and -not (Test-Path "$bepCfg.before-wheelmod")) { Copy-Item $bepCfg "$bepCfg.before-wheelmod" }
    # Console window off: it breaks wheel detection. Unity log listening off: crashes on Unity 6.
    Copy-Item (Join-Path $FilesDir 'BepInEx.cfg') $bepCfg -Force
    if ($wheel) {
        Copy-Item (Join-Path $FilesDir $BepDll) (Join-Path $plugins $BepDll) -Force
        # Older versions used a different plugin id: keep the player's settings under the current name.
        $cfgPath = Join-Path $config $PluginCfg
        foreach ($old in @(Get-ChildItem -Path $config -File | Where-Object { $_.Name -like '*.oth.wheel.cfg' -and $_.Name -ne $PluginCfg })) {
            if (Test-Path $cfgPath) { Remove-Item $old.FullName -Force } else { Move-Item $old.FullName $cfgPath }
        }
    }
    Unblock-Items $game $BepItems
    return @{
        Log = Join-Path $game 'BepInEx\OthWheel-install.txt'
        Lines = @("Mod loader: BepInEx")
        FirstStart = "The FIRST start takes 1-2 minutes before anything happens (the mod loader prepares itself; it needs internet once). It is NOT stuck, don't close it."
    }
}

function Install-ForMelonLoader([string]$game, [bool]$hadLoader, [bool]$wheel) {
    $loaderZip = Get-ChildItem -Path $FilesDir -Filter 'MelonLoader*.zip' | Select-Object -First 1
    if (-not $loaderZip) { Fail 'The MelonLoader zip is missing from the installer folder.' }
    $log = Join-Path $game 'UserData\OthWheel-install.txt'
    $before = Read-InstallLog $log
    $installedByUs = ($before['MelonLoader installed by this installer'] -eq 'True') -or -not $hadLoader

    if ($hadLoader) {
        $v = Get-MelonVersion $game
        if ($v -and $v -lt $MelonMinimum) {
            $r = Show-Message ("MelonLoader $v is installed, but the wheel mod needs version $MelonMinimum or newer (tested with $MelonTested).`n`n" +
                "Update MelonLoader to $MelonTested now? Your other mods and their settings are kept.") 'Question' 'YesNo'
            if ($r -ne 'Yes') { Fail 'MelonLoader is too old for the wheel mod.' }
            try { Expand-Archive -Path $loaderZip.FullName -DestinationPath $game -Force } catch { Fail "Updating MelonLoader failed:`n$($_.Exception.Message)" }
        }
    } else {
        try { Expand-Archive -Path $loaderZip.FullName -DestinationPath $game -Force } catch { Fail "Copying the mod loader failed:`n$($_.Exception.Message)" }
    }

    $mods = Join-Path $game 'Mods'
    New-Item -ItemType Directory -Force -Path $mods, (Join-Path $game 'UserData') | Out-Null

    # Another wheel mod fights over the same wheel: offer to park it (not delete it).
    $moved = $before['TrailWheel moved to']
    $trail = Join-Path $mods 'TrailWheel.dll'
    if ($wheel -and (Test-Path $trail)) {
        $r = Show-Message ("Another wheel mod is installed: TrailWheel.`n`nTwo wheel mods at the same time fight over the wheel (steering and force feedback).`n`n" +
            "Move TrailWheel to the folder Mods\disabled\ ? It isn't deleted: move it back any time to use it again.") 'Question' 'YesNo'
        if ($r -eq 'Yes') {
            $disabled = Join-Path $mods 'disabled'
            New-Item -ItemType Directory -Force -Path $disabled | Out-Null
            Move-Item $trail (Join-Path $disabled 'TrailWheel.dll') -Force
            $moved = 'Mods\disabled\TrailWheel.dll'
        }
    }

    if ($wheel) { Copy-Item (Join-Path $FilesDir $MelonDll) (Join-Path $mods $MelonDll) -Force }
    Unblock-Items $game $MelonItems

    $firstStart = "The FIRST start takes 1-2 minutes before anything happens (MelonLoader prepares the game; it needs internet once). A black text window opens next to the game: that's MelonLoader's log, it's normal. The game is NOT stuck, don't close it."
    if (-not (Test-DotNet6)) {
        $firstStart += "`n     MelonLoader needs Microsoft .NET 6, which isn't on this PC yet: on the first start it downloads and installs it by itself. If Windows asks for permission, click Yes."
    }
    $lines = @("Mod loader: MelonLoader", "MelonLoader installed by this installer: $installedByUs", "MelonLoader version: $(Get-MelonVersion $game)")
    if ($moved) { $lines += "TrailWheel moved to: $moved" }
    return @{ Log = $log; Lines = $lines; FirstStart = $firstStart }
}

# The cockpit mod's DLL and one model per car that has a cockpit. Returns the number of cars.
function Install-Cockpit([string]$game, [bool]$bep) {
    if ($bep) {
        $plugins = Join-Path $game 'BepInEx\plugins'
        Copy-Item (Join-Path $CockpitFiles $CockpitBepDll) (Join-Path $plugins $CockpitBepDll) -Force
        $models = Join-Path $plugins 'OthCockpit\models'
    } else {
        Copy-Item (Join-Path $CockpitFiles $CockpitMelonDll) (Join-Path $game "Mods\$CockpitMelonDll") -Force
        $models = Join-Path $game 'UserData\OthCockpit\models'
    }
    New-Item -ItemType Directory -Force -Path $models | Out-Null
    # Replace the models completely, so an update never leaves an old car's cockpit behind.
    Get-ChildItem -Path $models -Filter 'Car_*.glb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $cars = @(Get-ChildItem -Path (Join-Path $CockpitFiles 'models') -Filter 'Car_*.glb' -File)
    $cars | Copy-Item -Destination $models -Force
    Unblock-Items (Split-Path $models -Parent) @('models')
    return $cars.Count
}

function Install-Mod {
    $version = (Get-Content (Join-Path $FilesDir 'version.txt') -Raw).Trim()
    $r = Show-Message ("This installs mods for over the hill (pack $version). You choose the parts in the next two questions:`n`n" +
        "WHEEL MOD (for steering wheels)`n" +
        "  - steering wheel, pedals (clutch too) and wheel buttons`n" +
        "  - force feedback calculated from the car's tyres`n" +
        "  - Moza R3 works right away; other wheels are set up with an in-game wizard`n`n" +
        "COCKPIT VIEW (for everyone: keyboard, controller or wheel)`n" +
        "  - first-person view from the driver's seat with a real interior`n" +
        "  - working dials, warning lights, steering wheel, pedals and levers`n" +
        "  - cars with a cockpit so far: $(Get-CockpitCars)`n`n" +
        "It uses a mod loader: MelonLoader (installed if the game has no mod loader yet), or BepInEx if you already use that. " +
        "No original game files are changed, and Uninstall.bat removes everything again.`n`n" +
        "Continue?") 'Question' 'YesNo'
    if ($r -ne 'Yes') { exit 0 }
    $wheel = (Show-Message "Install the WHEEL MOD?`n`n(Yes if you play with a steering wheel. No for keyboard / controller only.)" 'Question' 'YesNo') -eq 'Yes'
    $cockpit = (Show-Message "Install the COCKPIT VIEW?`n`n(First-person view from the driver's seat.)" 'Question' 'YesNo') -eq 'Yes'
    if (-not $wheel -and -not $cockpit) { Show-Message 'Nothing selected, so nothing was installed.' | Out-Null; exit 0 }

    $game = Find-Game
    Wait-GameClosed

    $hasBep = Test-BepInEx $game
    $hasMelon = Test-MelonLoader $game
    if ($hasBep -and $hasMelon) {
        Fail ("Both mod loaders are installed in the game folder: MelonLoader and BepInEx. They don't work together, so the game can't run mods like this.`n`n" +
            "Remove one of them (with the uninstaller of the mod that installed it, or by hand), then run Install.bat again.`n`nGame folder:`n$game")
    }
    Ensure-WriteAccess $game

    if ($hasBep) { $result = Install-ForBepInEx $game $wheel }
    else { $result = Install-ForMelonLoader $game $hasMelon $wheel }
    $cars = if ($cockpit) { Install-Cockpit $game $hasBep } else { 0 }

    $diResult = if ($wheel) { Enable-GameDirectInput } else { 'not needed (no wheel mod)' }
    # Keep what an earlier install put there (e.g. the wheel mod when only the cockpit is added now).
    $before = Read-InstallLog $result.Log
    $wheelInstalled = $wheel -or ($before['Wheel mod installed'] -eq 'True') -or ($before.Count -gt 0 -and -not $before.ContainsKey('Wheel mod installed'))
    $cockpitInstalled = $cockpit -or ($before['Cockpit installed'] -eq 'True')
    Set-Content -Path $result.Log -Value (@(
        "OTH mods pack $version installed $(Get-Date -Format 'yyyy-MM-dd HH:mm')") + $result.Lines + @(
        "Wheel mod installed: $wheelInstalled",
        "Cockpit installed: $cockpitInstalled",
        "Mod loader was already installed before: $($hasBep -or $hasMelon)",
        "Game DirectInput setting: $diResult"))
    if ($diResult -eq 'missing') {
        Show-Message ("The game's settings file doesn't exist yet (the game was never started on this PC).`n`n" +
            "Start the game once until the main menu, close it, and run Install.bat again. That's needed only once.") 'Warning' | Out-Null
    } elseif ($diResult -eq 'unknown') {
        Show-Message ("The game's settings file has a format the installer doesn't know (game update?), so one setting couldn't be switched on.`n`n" +
            "The mod is installed, but the wheel may not work fully. Check the README (Problems).") 'Warning' | Out-Null
    }

    # Moza Pit House profile, only for Moza owners (Pit House installed).
    $presetSrc = Get-ChildItem -Path (Join-Path $PackageDir 'Moza Pit House profile') -Filter '*.mzpreset' -ErrorAction SilentlyContinue | Select-Object -First 1
    $presetDir = Get-PitHousePresetDir
    $presetNote = ''
    if ($wheel -and $presetSrc -and $presetDir) {
        if (Wait-PitHouseClosed 'It has to be closed so it can pick up the new wheel profile.') {
            Copy-Item $presetSrc.FullName (Join-Path $presetDir "$PresetId.mzpreset") -Force
            $presetNote = "Moza: Pit House profile installed: 'R3 - over the hill (wheel mod)'. Select it in Pit House (wheelbase presets) before playing.`n`n"
        } else {
            $presetNote = "Moza: Pit House profile skipped. Run Install.bat again later with Pit House closed, or import it by hand from the 'Moza Pit House profile' folder.`n`n"
        }
    }

    $loaderName = if ($hasBep) { 'BepInEx' } else { 'MelonLoader' }
    $parts = @()
    if ($wheel) { $parts += 'wheel mod' }
    if ($cockpit) { $parts += "cockpit view ($cars car$(if ($cars -ne 1) { 's' }))" }
    $list = @()
    if ($wheel) { $list += "Plug in the wheel and start its driver software (Moza Pit House, Logitech G HUB, ...)." }
    $list += "Start the game from Steam. $($result.FirstStart)"
    if ($wheel) {
        $list += "Keep the game window in front while it starts (don't alt-tab)."
        $list += "Get into the car.`n     - Moza R3: works right away.`n" +
            "     - Other wheels: the setup wizard opens by itself (about 2 minutes: turn the wheel, press the pedals, press the buttons you want).`n" +
            "     Press F3 any time for the wizard / wheel menu."
    }
    if ($cockpit) {
        $list += "Cockpit view: zoom the camera in as far as it goes, then zoom in once more (or press F6$(if ($wheel) { '; Moza: the CAM button' })). Zoom out to leave."
    }
    $steps = "FIRST START - please read:`n"
    for ($i = 0; $i -lt $list.Count; $i++) { $steps += "  $($i + 1). $($list[$i])`n" }
    Show-Message ("Installed: $($parts -join ' + ') (mod loader: $loaderName)!`n`n$presetNote" + $steps +
        "`nLater starts are fast again. The README has the details.`n`nOpen the README now?") 'Information' 'YesNo' |
        ForEach-Object { if ($_ -eq 'Yes') { Start-Process notepad.exe (Join-Path $PackageDir 'README.txt') } }
}

# "DingDing, Protector" from the models in the pack.
function Get-CockpitCars {
    $names = @(Get-ChildItem -Path (Join-Path $CockpitFiles 'models') -Filter 'Car_*.glb' -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.BaseName -replace '^Car_', '' })
    if ($names.Count -eq 0) { return 'none yet' }
    return $names -join ', '
}

# --- uninstall ------------------------------------------------------------------------------------

function Uninstall-BepInEx([string]$game) {
    $bep = Join-Path $game 'BepInEx'
    foreach ($f in @("plugins\$BepDll", "config\$PluginCfg", 'OthWheel-install.txt', 'config\OthWheel-reconnect-crashed',
                     "plugins\$CockpitBepDll", "config\$CockpitCfg")) {
        $p = Join-Path $bep $f
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    Get-ChildItem -Path (Join-Path $bep 'config') -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.oth.wheel.cfg' } | Remove-Item -Force
    foreach ($dir in 'config\OthWheel', 'plugins\OthCockpit') {
        $p = Join-Path $bep $dir
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }

    $otherPlugins = @(Get-ChildItem -Path (Join-Path $bep 'plugins') -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    $removeLoader = $true
    if ($otherPlugins.Count -gt 0) {
        $r = Show-Message ("The wheel mod and cockpit view are removed.`n`nThe BepInEx mod loader also has other mods installed:`n  " + (($otherPlugins | Select-Object -First 5 | ForEach-Object Name) -join "`n  ") +
            "`n`nRemove BepInEx (and those mods) too?") 'Question' 'YesNo'
        $removeLoader = ($r -eq 'Yes')
    }
    if ($removeLoader) {
        foreach ($item in $BepItems) {
            $p = Join-Path $game $item
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
    }
}

function Uninstall-MelonLoader([string]$game) {
    $log = Read-InstallLog (Join-Path $game 'UserData\OthWheel-install.txt')
    foreach ($rel in "Mods\$MelonDll", 'UserData\OthWheel-install.txt', "Mods\$CockpitMelonDll") {
        $p = Join-Path $game $rel
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    foreach ($dir in 'UserData\OthWheel', 'UserData\OthCockpit') {
        $p = Join-Path $game $dir
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }

    # A wheel mod we parked during install: offer to bring it back.
    $parked = Join-Path $game 'Mods\disabled\TrailWheel.dll'
    if ($log['TrailWheel moved to'] -and (Test-Path $parked)) {
        $r = Show-Message "The installer moved TrailWheel (another wheel mod) to Mods\disabled\.`n`nMove it back so it works again?" 'Question' 'YesNo'
        if ($r -eq 'Yes') {
            Move-Item $parked (Join-Path $game 'Mods\TrailWheel.dll') -Force
            $dir = Join-Path $game 'Mods\disabled'
            if (-not (Get-ChildItem $dir -Force -ErrorAction SilentlyContinue)) { Remove-Item $dir -Force }
        }
    }

    # MelonLoader itself: only if this installer put it there.
    if ($log['MelonLoader installed by this installer'] -ne 'True') { return }
    $otherMods = @(Get-ChildItem -Path (Join-Path $game 'Mods'), (Join-Path $game 'Plugins') -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    if ($otherMods.Count -gt 0) {
        $r = Show-Message ("The wheel mod and cockpit view are removed.`n`nMelonLoader (installed together with them) is also used by other mods:`n  " + (($otherMods | Select-Object -First 5 | ForEach-Object Name) -join "`n  ") +
            "`n`nRemove MelonLoader (and those mods) too?") 'Question' 'YesNo'
        if ($r -ne 'Yes') { return }
    }
    foreach ($item in $MelonItems) {
        $p = Join-Path $game $item
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

function Uninstall-Mod {
    $r = Show-Message "This removes the wheel mod and the cockpit view (and your wheel setups and cockpit settings) from over the hill.`n`nContinue?" 'Question' 'YesNo'
    if ($r -ne 'Yes') { exit 0 }

    $game = Find-Game
    Wait-GameClosed
    Ensure-WriteAccess $game

    $found = $false
    if ((Test-Path (Join-Path $game "BepInEx\plugins\$BepDll")) -or (Test-Path (Join-Path $game "BepInEx\plugins\$CockpitBepDll")) -or
        (Test-Path (Join-Path $game 'BepInEx\OthWheel-install.txt'))) { Uninstall-BepInEx $game; $found = $true }
    if ((Test-Path (Join-Path $game "Mods\$MelonDll")) -or (Test-Path (Join-Path $game "Mods\$CockpitMelonDll")) -or
        (Test-Path (Join-Path $game 'UserData\OthWheel-install.txt'))) { Uninstall-MelonLoader $game; $found = $true }

    $presetDir = Get-PitHousePresetDir
    $preset = if ($presetDir) { Join-Path $presetDir "$PresetId.mzpreset" } else { $null }
    $presetNote = ''
    if ($preset -and (Test-Path $preset)) {
        $r = Show-Message "Also remove the Pit House profile 'R3 - over the hill (wheel mod)'?" 'Question' 'YesNo'
        if ($r -eq 'Yes' -and (Wait-PitHouseClosed 'It has to be closed to remove the profile.')) { Remove-Item $preset -Force; $presetNote = "`nThe Pit House profile was removed." }
    }

    $head = if ($found) { 'Uninstalled. over the hill is back to normal.' } else { "The mods weren't found in the game folder, so nothing was removed there." }
    Show-Message ($head + $presetNote +
        "`n`n(If Steam ever reports problems with the game, use Steam > game > Properties > Installed Files > Verify integrity.)") | Out-Null
}

try {
    if ($Uninstall) { Uninstall-Mod } else { Install-Mod }
} catch {
    Fail "Something went wrong:`n$($_.Exception.Message)"
}
