# Builds the shareable installer zips in %USERPROFILE%\dev\oth-wheelmod-build\dist (outside OneDrive):
#   OTH-Wheel-v<wheel version>.zip                        wheel mod only
#   OTH-Cockpit-v<cockpit version>-alpha.zip              cockpit mod only
#   OTH-Wheel-and-Cockpit-v<wheel>+<cockpit>-alpha.zip    both (the installer asks which parts)
#   OTH-Wheel-v<wheel>-manual.zip, OTH-Cockpit-v<cockpit>-alpha-manual.zip
#                                                         no installer, no scripts: Mods\ + UserData\ to copy into
#                                                         the game folder by hand (MelonLoader only). For Nexus,
#                                                         which quarantines zips with .bat/.ps1; the installer
#                                                         zips go on GitHub.
# Every installer zip has the same installer: it offers the parts that are in its zip and keeps what an earlier
# install put there. Works while the game is running (doesn't touch the installed plugins).
# The cockpit mod is ..\..\OTH cockpit mod (DLLs rebuilt by its build.ps1; car models from its last
# model build, assets_v2 if it exists). Each zip has our LICENSE.txt and a "Manual install" folder. No mod
# loader inside (Nexus quarantines zips with archives inside): installer.ps1 downloads the official one.
# Usage: powershell -File package.ps1 [-Parts Wheel,Cockpit,Pack,WheelManual,CockpitManual]   (default: all five)
param(
    [ValidateSet('Wheel', 'Cockpit', 'Pack', 'WheelManual', 'CockpitManual')][string[]]$Parts = @('Wheel', 'Cockpit', 'Pack', 'WheelManual', 'CockpitManual')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$work = Join-Path $env:USERPROFILE 'dev\oth-wheelmod-build'
$CockpitStage = 'alpha'
$needWheel = ($Parts -contains 'Wheel') -or ($Parts -contains 'Pack') -or ($Parts -contains 'WheelManual')
$needCockpit = ($Parts -contains 'Cockpit') -or ($Parts -contains 'Pack') -or ($Parts -contains 'CockpitManual')

function Get-Version([string]$pluginCs) {
    $v = [regex]::Match((Get-Content $pluginCs -Raw), 'Version = "([^"]+)"').Groups[1].Value
    if (-not $v) { throw "Version not found in $pluginCs" }
    return $v
}

if ($needWheel) {
    & (Join-Path $root 'build.ps1') -Project OthWheel -NoInstall
    & (Join-Path $root 'build.ps1') -Project OthWheel.Melon -NoInstall
    $wheelVersion = Get-Version (Join-Path $root 'src\OthWheel\Plugin.cs')
    $bepDll = Join-Path $work 'bin\OthWheel\Release\net6.0\OthWheel.dll'
    $melonDll = Join-Path $work 'bin\OthWheel.Melon\Release\net6.0\OthWheel.Melon.dll'
}
if ($needCockpit) {
    $cockpitRoot = Join-Path (Split-Path (Split-Path $root -Parent) -Parent) 'OTH cockpit mod'
    & (Join-Path $cockpitRoot 'build.ps1') -NoModels -NoInstall
    $cockpitVersion = (Get-Version (Join-Path $cockpitRoot 'src\OthCockpit\Plugin.cs')) + "-$CockpitStage"
    $cockpitBuild = Join-Path $env:USERPROFILE 'dev\oth-cockpit-build'
    $cockpitBep = Join-Path $cockpitBuild 'bin\OthCockpit\Release\net6.0\OthCockpit.dll'
    $cockpitMelon = Join-Path $cockpitBuild 'bin\OthCockpit.Melon\Release\net6.0\OthCockpit.Melon.dll'
    $modelDir = Join-Path $cockpitBuild 'assets_v2'
    if (-not (Test-Path $modelDir)) { $modelDir = Join-Path $cockpitBuild 'assets' }
    $cockpitModels = @(Get-ChildItem -Path $modelDir -Filter 'Car_*.glb' -File)
    if ($cockpitModels.Count -eq 0) { throw "No cockpit models (Car_*.glb) in $modelDir`: run the cockpit's build.ps1 first." }
}

$src = Join-Path $root 'installer'
$dist = Join-Path $work 'dist'

# README / Manual install: keep the blocks for the parts in this zip. Block markers on their own line:
#   @wheel  @cockpit  @pack (both)  @single (only one)  @cockpitonly  @installer  @manual (zip without
#   installer)  ...  @end.  Join with + for "all of them": @wheel+manual.
# Numbered steps at the start of a line are renumbered per section (removed steps leave no gaps).
function Write-Text([string]$from, [string]$to, [bool]$wheel, [bool]$cockpit, [string]$title, [bool]$manual = $false) {
    $keep = @{ wheel = $wheel; cockpit = $cockpit; pack = ($wheel -and $cockpit); single = ($wheel -xor $cockpit); cockpitonly = ($cockpit -and -not $wheel)
               installer = (-not $manual); manual = $manual }
    $out = New-Object System.Collections.Generic.List[string]
    $show = $true
    $n = 0
    foreach ($line in (Get-Content $from)) {
        if ($line -match '^@([\w+]+)$') {
            if ($Matches[1] -eq 'end') { $show = $true; continue }
            $show = $true
            foreach ($m in $Matches[1].Split('+')) {
                if (-not $keep.ContainsKey($m)) { throw "Unknown marker $line in $from" }
                $show = $show -and $keep[$m]
            }
            continue
        }
        if (-not $show) { continue }
        if ($line -match '^-{20,}$') { $n = 0 }
        if ($line -match '^\d+\. ') { $n++; $line = $line -replace '^\d+\.', "$n." }
        $out.Add($line.Replace('{TITLE}', $title))
    }
    # Collapse the runs of blank lines that removed blocks leave behind (max 2).
    $text = ($out -join "`r`n") -replace "(`r`n){4,}", "`r`n`r`n`r`n"
    [IO.File]::WriteAllText($to, $text.TrimEnd() + "`r`n", [Text.Encoding]::ASCII)
}
function Copy-Text([string]$from, [string]$to) {
    $text = (Get-Content $from -Raw) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($to, $text, [Text.Encoding]::ASCII)
}

# Never ship the Windows account name (build paths, notes).
function Test-NoAccountName([string]$dir) {
    foreach ($file in Get-ChildItem $dir -Recurse -File) {
        $b = [IO.File]::ReadAllBytes($file.FullName)
        # As 8-bit text and as UTF-16 at both byte alignments.
        foreach ($t in [Text.Encoding]::ASCII.GetString($b), [Text.Encoding]::Unicode.GetString($b), [Text.Encoding]::Unicode.GetString($b, 1, [Math]::Max(0, $b.Length - 1))) {
            if ($t.IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Account name found in $($file.FullName)" }
        }
    }
}

function New-Package([string]$name, [bool]$wheel, [bool]$cockpit) {
    $titles = @()
    if ($wheel) { $titles += "OTH Wheel $wheelVersion" }
    if ($cockpit) { $titles += "OTH Cockpit $cockpitVersion" }
    $title = "over the hill  -  $($titles -join '  +  ')"

    $stage = Join-Path $dist $name
    # Empty the folder instead of deleting it: an Explorer window open on it would block the delete.
    if (Test-Path $stage) { Get-ChildItem $stage -Force | Remove-Item -Recurse -Force }
    $files = Join-Path $stage 'files'
    New-Item -ItemType Directory -Force -Path $files | Out-Null

    foreach ($f in 'Install.bat', 'Uninstall.bat') { Copy-Text (Join-Path $src $f) (Join-Path $stage $f) }
    Copy-Text (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $stage 'LICENSE.txt')
    Write-Text (Join-Path $src 'README.txt') (Join-Path $stage 'README.txt') $wheel $cockpit $title
    Copy-Item (Join-Path $src 'files\installer.ps1'), (Join-Path $src 'files\BepInEx.cfg') $files
    Set-Content -Path (Join-Path $files 'package.txt') -Value ($titles -join ' + ') -Encoding ASCII

    $manual = Join-Path $stage 'Manual install'
    New-Item -ItemType Directory -Force -Path (Join-Path $manual 'MelonLoader\Mods'), (Join-Path $manual 'BepInEx\BepInEx\plugins') | Out-Null
    if ($wheel) {
        Copy-Item (Join-Path $src 'Moza Pit House profile') $stage -Recurse
        Copy-Item $bepDll, $melonDll $files
        Set-Content -Path (Join-Path $files 'version.txt') -Value $wheelVersion -Encoding ASCII
        Copy-Item $melonDll (Join-Path $manual 'MelonLoader\Mods')
        Copy-Item $bepDll (Join-Path $manual 'BepInEx\BepInEx\plugins')
    }
    if ($cockpit) {
        $c = Join-Path $files 'cockpit'
        New-Item -ItemType Directory -Force -Path (Join-Path $c 'models') | Out-Null
        Copy-Item $cockpitBep, $cockpitMelon $c
        Set-Content -Path (Join-Path $c 'version.txt') -Value $cockpitVersion -Encoding ASCII
        $cockpitModels | Copy-Item -Destination (Join-Path $c 'models')
        Copy-Item $cockpitMelon (Join-Path $manual 'MelonLoader\Mods')
        Copy-Item $cockpitBep (Join-Path $manual 'BepInEx\BepInEx\plugins')
        foreach ($m in 'MelonLoader\UserData\OthCockpit\models', 'BepInEx\BepInEx\plugins\OthCockpit\models') {
            New-Item -ItemType Directory -Force -Path (Join-Path $manual $m) | Out-Null
            $cockpitModels | Copy-Item -Destination (Join-Path $manual $m)
        }
    }
    Write-Text (Join-Path $src 'Manual install.txt') (Join-Path $manual 'README.txt') $wheel $cockpit $title

    Test-NoAccountName $stage

    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Zip $stage $zip "$name/"
    Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

# Compress-Archive on Windows PowerShell writes backslashes into the entry names, which isn't valid zip and
# shows up wrong outside Windows, so we write the entries ourselves.
function New-Zip([string]$from, [string]$zip, [string]$prefix = '') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = (Get-Item -LiteralPath $from).FullName.TrimEnd('\')
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File) {
            $entry = $prefix + $f.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
        }
    } finally { $archive.Dispose() }
}

# Manual zip (one mod): the zip's root is laid out like the game folder (Mods\, UserData\), the way
# MelonLoader mods are usually shared, so it can be copied in as is. No .bat/.ps1 inside.
function New-ManualPackage([string]$name, [bool]$wheel) {
    $mod = if ($wheel) { 'OTH Wheel' } else { 'OTH Cockpit' }
    $title = if ($wheel) { "over the hill  -  OTH Wheel $wheelVersion  (manual install)" } else { "over the hill  -  OTH Cockpit $cockpitVersion  (manual install)" }
    $stage = Join-Path $dist $name
    if (Test-Path $stage) { Get-ChildItem $stage -Force | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Mods') | Out-Null
    if ($wheel) {
        Copy-Item $melonDll (Join-Path $stage 'Mods')
    } else {
        Copy-Item $cockpitMelon (Join-Path $stage 'Mods')
        $m = Join-Path $stage 'UserData\OthCockpit\models'
        New-Item -ItemType Directory -Force -Path $m | Out-Null
        $cockpitModels | Copy-Item -Destination $m
    }
    Write-Text (Join-Path $src 'README.txt') (Join-Path $stage "$mod README.txt") $wheel (-not $wheel) $title $true
    Copy-Text (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $stage "$mod LICENSE.txt")
    Test-NoAccountName $stage

    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Zip $stage $zip
    Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

if ($Parts -contains 'Wheel') { New-Package "OTH-Wheel-v$wheelVersion" $true $false }
if ($Parts -contains 'Cockpit') { New-Package "OTH-Cockpit-v$cockpitVersion" $false $true }
if ($Parts -contains 'Pack') { New-Package "OTH-Wheel-and-Cockpit-v$wheelVersion+$cockpitVersion" $true $true }
if ($Parts -contains 'WheelManual') { New-ManualPackage "OTH-Wheel-v$wheelVersion-manual" $true }
if ($Parts -contains 'CockpitManual') { New-ManualPackage "OTH-Cockpit-v$cockpitVersion-manual" $false }
