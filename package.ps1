# Builds the shareable installer zips in %USERPROFILE%\dev\oth-wheelmod-build\dist (outside OneDrive):
#   OTH-Wheel-v<wheel version>.zip                        wheel mod only
#   OTH-Cockpit-v<cockpit version>-alpha.zip              cockpit mod only
#   OTH-Wheel-and-Cockpit-v<wheel>+<cockpit>-alpha.zip    both (the installer asks which parts)
# Every zip has the same installer: it offers the parts that are in its zip and keeps what an earlier
# install put there. Works while the game is running (doesn't touch the installed plugins).
# The cockpit mod is ..\..\OTH cockpit mod (DLLs rebuilt by its build.ps1; car models from its last
# model build, assets_v2 if it exists). Each zip has our LICENSE.txt and a "Manual install" folder. No mod
# loader inside (Nexus quarantines zips with archives inside): installer.ps1 downloads the official one.
# Usage: powershell -File package.ps1 [-Parts Wheel,Cockpit,Pack]   (default: all three)
param(
    [ValidateSet('Wheel', 'Cockpit', 'Pack')][string[]]$Parts = @('Wheel', 'Cockpit', 'Pack')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$work = Join-Path $env:USERPROFILE 'dev\oth-wheelmod-build'
$CockpitStage = 'alpha'
$needWheel = ($Parts -contains 'Wheel') -or ($Parts -contains 'Pack')
$needCockpit = ($Parts -contains 'Cockpit') -or ($Parts -contains 'Pack')

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
#   @wheel  @cockpit  @pack (both)  @single (only one)  @cockpitonly  ...  @end
# Numbered steps at the start of a line are renumbered per section (removed steps leave no gaps).
function Write-Text([string]$from, [string]$to, [bool]$wheel, [bool]$cockpit, [string]$title) {
    $keep = @{ wheel = $wheel; cockpit = $cockpit; pack = ($wheel -and $cockpit); single = ($wheel -xor $cockpit); cockpitonly = ($cockpit -and -not $wheel) }
    $out = New-Object System.Collections.Generic.List[string]
    $show = $true
    $n = 0
    foreach ($line in (Get-Content $from)) {
        if ($line -match '^@(\w+)$') {
            if ($Matches[1] -eq 'end') { $show = $true }
            elseif ($keep.ContainsKey($Matches[1])) { $show = $keep[$Matches[1]] }
            else { throw "Unknown marker $line in $from" }
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

    # Never ship the Windows account name (build paths, notes).
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $b = [IO.File]::ReadAllBytes($file.FullName)
        # As 8-bit text and as UTF-16 at both byte alignments.
        foreach ($t in [Text.Encoding]::ASCII.GetString($b), [Text.Encoding]::Unicode.GetString($b), [Text.Encoding]::Unicode.GetString($b, 1, [Math]::Max(0, $b.Length - 1))) {
            if ($t.IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Account name found in $($file.FullName)" }
        }
    }

    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

if ($Parts -contains 'Wheel') { New-Package "OTH-Wheel-v$wheelVersion" $true $false }
if ($Parts -contains 'Cockpit') { New-Package "OTH-Cockpit-v$cockpitVersion" $false $true }
if ($Parts -contains 'Pack') { New-Package "OTH-Wheel-and-Cockpit-v$wheelVersion+$cockpitVersion" $true $true }
