# Builds the shareable installer zip: dist\OTH-Wheel-and-Cockpit-v<version>.zip (outside OneDrive).
# Works while the game is running (doesn't touch the installed plugins).
# Contains the wheel mod and the cockpit mod (..\..\OTH cockpit mod, built by its build.ps1; the car models
# come from its last model build), both builds of each (BepInEx + MelonLoader), both loaders, their
# licences and a "Manual install" folder.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$work = Join-Path $env:USERPROFILE 'dev\oth-wheelmod-build'
$downloads = Join-Path $work 'downloads'
$bepZip = Join-Path $downloads 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.789.zip'
$bepLicense = Join-Path $downloads 'BepInEx-LICENSE.txt'
$melonZip = Join-Path $downloads 'MelonLoader.x64-0.7.3.zip'
foreach ($f in $bepZip, $bepLicense, $melonZip) { if (-not (Test-Path $f)) { throw "Missing: $f" } }

& (Join-Path $root 'build.ps1') -Project OthWheel -NoInstall
& (Join-Path $root 'build.ps1') -Project OthWheel.Melon -NoInstall
$bepDll = Join-Path $work 'bin\OthWheel\Release\net6.0\OthWheel.dll'
$melonDll = Join-Path $work 'bin\OthWheel.Melon\Release\net6.0\OthWheel.Melon.dll'
$version = [regex]::Match((Get-Content (Join-Path $root 'src\OthWheel\Plugin.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Version not found in Plugin.cs' }

# The cockpit mod: DLLs (rebuilt now) and the car models from its last model build.
$cockpitRoot = Join-Path (Split-Path (Split-Path $root -Parent) -Parent) 'OTH cockpit mod'
& (Join-Path $cockpitRoot 'build.ps1') -NoModels -NoInstall
$cockpitBuild = Join-Path $env:USERPROFILE 'dev\oth-cockpit-build'
$cockpitBep = Join-Path $cockpitBuild 'bin\OthCockpit\Release\net6.0\OthCockpit.dll'
$cockpitMelon = Join-Path $cockpitBuild 'bin\OthCockpit.Melon\Release\net6.0\OthCockpit.Melon.dll'
$cockpitModels = @(Get-ChildItem -Path (Join-Path $cockpitBuild 'assets') -Filter 'Car_*.glb' -File)
if ($cockpitModels.Count -eq 0) { throw 'No cockpit models (Car_*.glb) in the cockpit build folder: run its build.ps1 first.' }

$name = 'OTH-Wheel-and-Cockpit'
$dist = Join-Path $work 'dist'
$stage = Join-Path $dist $name
# Empty the folder instead of deleting it: an Explorer window open on it would block the delete.
if (Test-Path $stage) { Get-ChildItem $stage -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'files'), (Join-Path $stage 'files\licenses'), (Join-Path $stage 'files\cockpit\models') | Out-Null

$src = Join-Path $root 'installer'
# .bat and .txt files need Windows line endings.
function Copy-Text([string]$from, [string]$to) {
    $text = (Get-Content $from -Raw) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($to, $text, [Text.Encoding]::ASCII)
}
foreach ($f in 'Install.bat', 'Uninstall.bat', 'README.txt') { Copy-Text (Join-Path $src $f) (Join-Path $stage $f) }
Copy-Item (Join-Path $src 'Moza Pit House profile') $stage -Recurse
Copy-Item (Join-Path $src 'files\installer.ps1'), (Join-Path $src 'files\BepInEx.cfg'), $bepDll, $melonDll, $bepZip, $melonZip (Join-Path $stage 'files')
Set-Content -Path (Join-Path $stage 'files\version.txt') -Value $version -Encoding ASCII
Copy-Item $cockpitBep, $cockpitMelon (Join-Path $stage 'files\cockpit')
$cockpitModels | Copy-Item -Destination (Join-Path $stage 'files\cockpit\models')
# MelonLoader's zip carries its own LICENSE/NOTICE (MelonLoader\Documentation); BepInEx's zip has none.
Copy-Item $bepLicense (Join-Path $stage 'files\licenses\BepInEx-LICENSE.txt')

# For people who install mods by hand (or with a mod manager).
$manual = Join-Path $stage 'Manual install'
$melonModels = Join-Path $manual 'MelonLoader\UserData\OthCockpit\models'
$bepModels = Join-Path $manual 'BepInEx\BepInEx\plugins\OthCockpit\models'
New-Item -ItemType Directory -Force -Path (Join-Path $manual 'MelonLoader\Mods'), $melonModels, $bepModels | Out-Null
Copy-Item $melonDll, $cockpitMelon (Join-Path $manual 'MelonLoader\Mods')
Copy-Item $bepDll, $cockpitBep (Join-Path $manual 'BepInEx\BepInEx\plugins')
$cockpitModels | Copy-Item -Destination $melonModels
$cockpitModels | Copy-Item -Destination $bepModels
Copy-Text (Join-Path $src 'Manual install.txt') (Join-Path $manual 'README.txt')

$zip = Join-Path $dist "$name-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
