<#
.SYNOPSIS
    Builds and packages the JellyShelf plugin into a Jellyfin plugin repository.

.DESCRIPTION
    Produces, under .\dist :
      - jellyfin-plugin-jellyshelf_<version>.zip  (the DLL + meta.json)
      - manifest.json                          (the repository file to host)

    Host BOTH files at a URL reachable by your Jellyfin server, then add the
    URL of manifest.json in Jellyfin: Dashboard -> Plugins -> Repositories.

.PARAMETER BaseUrl
    Public base URL of the folder where the zip and manifest.json are hosted,
    e.g. https://github.com/<user>/<repo>/releases/download/v1.0.0.0
    or    http://<nas-ip>:8080/jellyfin-plugins
    The zip's sourceUrl becomes "<BaseUrl>/<zip name>".

.PARAMETER Version
    Plugin version (must match AssemblyVersion in the .csproj). Default 1.0.0.0.

.EXAMPLE
    pwsh ./package.ps1 -BaseUrl "http://192.168.1.10:8080/jellyfin-plugins"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$Version = "1.0.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# --- Plugin identity (keep in sync with Plugin.cs / build.yaml) ---
$guid        = "e7f3b8d2-4c6a-4f1e-9b3a-2d5c7e9a1f40"
$name        = "JellyShelf"
$owner       = "nicolas"
$category    = "Metadata"
$targetAbi   = "12.3.0.0"
$overview    = "Read Calibre metadata.db read-only to enrich the Jellyfin book library."
$description = "Reads a Calibre metadata.db (read-only) and populates genres, tags, authors, series, titles and covers for the book library. Files stay in place; original Calibre paths are preserved."
$changelog   = "Initial release."

$dll = Join-Path $root "bin/$Configuration/net10.0/Jellyfin.Plugin.JellyShelf.dll"
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "==> Building ($Configuration) version $Version..."
dotnet build -c $Configuration `
    "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" `
    (Join-Path $root "Jellyfin.Plugin.JellyShelf.csproj") | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

Write-Host "==> Staging..."
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $dll -Destination $stage

# meta.json lives inside the zip; Jellyfin reads it once extracted.
$meta = [ordered]@{
    category    = $category
    changelog   = $changelog
    description = $description
    overview    = $overview
    owner       = $owner
    targetAbi   = $targetAbi
    timestamp   = $timestamp
    version     = $Version
    status      = "Active"
    autoUpdate  = $true
    name        = $name
    guid        = $guid
}
$meta | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $stage "meta.json") -Encoding UTF8

Write-Host "==> Zipping..."
$zipName = "jellyfin-plugin-jellyshelf_$Version.zip"
$zipPath = Join-Path $dist $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath

$md5 = (Get-FileHash -Algorithm MD5 -Path $zipPath).Hash.ToLower()
$sourceUrl = "$($BaseUrl.TrimEnd('/'))/$zipName"

Write-Host "==> Writing manifest.json..."
$manifest = [ordered]@{
    guid        = $guid
    name        = $name
    description = $description
    overview    = $overview
    owner       = $owner
    category    = $category
    versions    = @(
        [ordered]@{
            version   = $Version
            changelog = $changelog
            targetAbi = $targetAbi
            sourceUrl = $sourceUrl
            checksum  = $md5
            timestamp = $timestamp
        }
    )
}
ConvertTo-Json -InputObject @($manifest) -Depth 6 | Set-Content -Path (Join-Path $dist "manifest.json") -Encoding UTF8

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Zip        : $zipPath"
Write-Host "  Checksum   : $md5"
Write-Host "  Manifest   : $(Join-Path $dist 'manifest.json')"
Write-Host "  sourceUrl  : $sourceUrl"
Write-Host ""
Write-Host "  Repository URL to paste in Jellyfin -> Plugins -> Repositories:" -ForegroundColor Cyan
Write-Host "    $($BaseUrl.TrimEnd('/'))/manifest.json"
