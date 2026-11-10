# Builds DogShare and uploads it to the Steam Workshop with SteamCMD.
#
#   .\publish-workshop.ps1 -SteamUser <login>                     first upload: creates a Private item
#   .\publish-workshop.ps1 -SteamUser <login> -Visibility Public  make it public
#   .\publish-workshop.ps1 -SteamUser <login> -ChangeNote "Fixed X"
#
# SteamCMD asks for your password / Steam Guard code itself. This script never sees or saves them.
# The first upload writes the new item id to workshop\publishedfileid.txt; commit that file so
# later runs update the same item instead of creating a new one.
param(
    [Parameter(Mandatory = $true)][string]$SteamUser,
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\ZED ZONE",
    [ValidateSet("Public", "FriendsOnly", "Private", "Unlisted")][string]$Visibility = "Private",
    [string]$ChangeNote = "",
    [switch]$UpdateDescription,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$AppId   = "1211600"
$Title   = "DogShare - co-op dogs for guests"
$Content = Join-Path $PSScriptRoot "workshop\content"
$ModDir  = Join-Path $Content "DogShare"
$Preview = Join-Path $PSScriptRoot "workshop\preview.png"
$IdFile  = Join-Path $PSScriptRoot "workshop\publishedfileid.txt"
$Dll     = Join-Path $PSScriptRoot "src\bin\Release\net6.0\DogShare.dll"

# ---- 1. build and stage the item folder
if (-not $SkipBuild) {
    dotnet build src\DogShare.csproj -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $Dll)) { throw "Missing $Dll - build first" }
$Version = [System.Reflection.AssemblyName]::GetAssemblyName($Dll).Version.ToString(3)
Copy-Item $Dll (Join-Path $ModDir "DogShare.dll") -Force
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $ModDir "README.md") -Force
$modJson = Join-Path $ModDir "mod.json"
$json = Get-Content $modJson -Raw
$json = $json -replace '"version"\s*:\s*"[^"]*"', ('"version": "' + $Version + '"')
[System.IO.File]::WriteAllText($modJson, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "==> Staged DogShare $Version in workshop\content\DogShare" -ForegroundColor Cyan

# ---- 2. SteamCMD (downloaded once into tools\steamcmd)
$Tools = Join-Path $PSScriptRoot "tools\steamcmd"
$SteamCmd = Join-Path $Tools "steamcmd.exe"
if (-not (Test-Path $SteamCmd)) {
    Write-Host "==> Downloading SteamCMD" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $Tools | Out-Null
    $zip = Join-Path $Tools "steamcmd.zip"
    Invoke-WebRequest "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $Tools -Force
    Remove-Item $zip
}

# ---- 3. item description (VDF)
$Id = "0"
if (Test-Path $IdFile) { $Id = (Get-Content $IdFile -Raw).Trim() }
$isNew = ($Id -eq "0" -or $Id -eq "")
if (-not $ChangeNote) { $ChangeNote = "DogShare $Version" }

function Clean([string]$s) { return ($s -replace '"', "'" -replace '\\', '/') }
function P([string]$p) { return ($p -replace '\\', '/') }

$lines = @(
    '"workshopitem"', '{',
    ('  "appid" "' + $AppId + '"'),
    ('  "publishedfileid" "' + $Id + '"'),
    ('  "contentfolder" "' + (P $Content) + '"'),
    ('  "previewfile" "' + (P $Preview) + '"'),
    ('  "changenote" "' + (Clean $ChangeNote) + '"')
)
if ($isNew -or $PSBoundParameters.ContainsKey("Visibility")) {
    $vis = @{ Public = 0; FriendsOnly = 1; Private = 2; Unlisted = 3 }[$Visibility]
    $lines += ('  "visibility" "' + $vis + '"')
}
if ($isNew -or $UpdateDescription) {
    $desc = Get-Content (Join-Path $PSScriptRoot "workshop\description.txt") -Raw
    $lines += ('  "title" "' + (Clean $Title) + '"')
    $lines += ('  "description" "' + (Clean $desc) + '"')
}
$lines += '}'

$Dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $Dist | Out-Null
$Vdf = Join-Path $Dist "workshop_item.vdf"
[System.IO.File]::WriteAllText($Vdf, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

# ---- 4. upload
if ($isNew) { Write-Host "==> Creating a new Workshop item ($Visibility)" -ForegroundColor Cyan }
else        { Write-Host "==> Updating Workshop item $Id" -ForegroundColor Cyan }
& $SteamCmd +login $SteamUser +workshop_build_item $Vdf +quit

# SteamCMD writes the item id back into the VDF after a successful upload.
$after = Get-Content $Vdf -Raw
if ($after -match '"publishedfileid"\s+"(\d+)"' -and $Matches[1] -ne "0") {
    $newId = $Matches[1]
    if ($isNew) {
        Set-Content -Path $IdFile -Value $newId -Encoding ASCII
        Write-Host "    saved item id to workshop\publishedfileid.txt - commit it" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host " Done: https://steamcommunity.com/sharedfiles/filedetails/?id=$newId" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host " Upload did not finish - read the SteamCMD output above." -ForegroundColor Red
    Write-Host " (First time: accept the Workshop agreement on the Steam website if it asks.)" -ForegroundColor Red
    exit 1
}
