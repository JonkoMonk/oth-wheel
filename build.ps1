# Builds a plugin (output goes outside OneDrive) and copies the DLL into every game folder that runs its loader:
#   OthWheel / OthWheelProbe (BepInEx) -> BepInEx\plugins of the real game and/or %USERPROFILE%\dev\oth-bep-test\game
#   OthWheel.Melon (MelonLoader)       -> Mods\ of the real game and/or %USERPROFILE%\dev\oth-ml-test\game
# Usage: powershell -File build.ps1 [-Project OthWheelProbe] [-NoInstall]
param(
    [string]$Project = "OthWheelProbe",
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")

$proj = Join-Path $PSScriptRoot "src\$Project\$Project.csproj"
dotnet build $proj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dll = "$env:USERPROFILE\dev\oth-wheelmod-build\bin\$Project\Release\net6.0\$Project.dll"
if (-not $NoInstall) {
    if (Get-Process "over the hill" -ErrorAction SilentlyContinue) { throw "Close the game first, the DLL is locked while it runs." }
    $real = "C:\Program Files (x86)\Steam\steamapps\common\over the hill"
    if ($Project -like '*.Melon') {
        $targets = @($real, "$env:USERPROFILE\dev\oth-ml-test\game") | Where-Object { Test-Path (Join-Path $_ 'MelonLoader') } | ForEach-Object { Join-Path $_ 'Mods' }
    } else {
        $targets = @($real, "$env:USERPROFILE\dev\oth-bep-test\game") | Where-Object { Test-Path (Join-Path $_ 'BepInEx\core') } | ForEach-Object { Join-Path $_ 'BepInEx\plugins' }
    }
    if (-not $targets) { Write-Host "No game folder with this mod loader found; built only." }
    foreach ($target in $targets) {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item $dll $target -Force
        Write-Host "Installed $dll -> $target"
    }
}
