<#
.SYNOPSIS
    Builds BenchBook's release downloads: a zip for x64 and one for Arm64, each a
    folder with BenchBook.exe, plus SHA256SUMS.txt.

.DESCRIPTION
    The same script runs on GitHub (.github\workflows\release.yml) and on your PC.
    The zips are unpackaged and unsigned, so they need no certificate. Windows
    warns the first time BenchBook.exe runs, and there's no share target and no
    automatic updates (CLAUDE.md, "Releasing").

    The version is stamped on BenchBook.exe as e.g. 0.2.0.123, where 123 is the
    number of commits, as the Mac counts builds. Settings > About shows "Version 0.2.0 (123)".

.EXAMPLE
    .\build\Release.ps1 -Version 0.2.0
#>
param(
    # e.g. 0.2.0, or 0.2.0-beta.1. A leading "v" from a tag is removed.
    [Parameter(Mandatory)] [string] $Version,
    # Where the zips and checksums go. Emptied first.
    [string] $Output = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$Version = $Version.TrimStart("v")
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') {
    throw "The version must look like 0.2.0 or 0.2.0-beta.1, not '$Version'."
}
$prefix = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$build = [int](git rev-list --count HEAD)
if ($LASTEXITCODE -ne 0) { throw "git couldn't count the commits." }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ("BenchBook Release " + [guid]::NewGuid().ToString("N"))

try {
    foreach ($target in @(@{ Platform = "x64"; Runtime = "win-x64" }, @{ Platform = "ARM64"; Runtime = "win-arm64" })) {
        $folder = Join-Path $staging "$($target.Runtime)\BenchBook"
        Write-Host "Building BenchBook $Version ($build) for $($target.Runtime)"
        dotnet publish src\BenchBook.App\BenchBook.App.csproj `
            -c Release `
            -r $target.Runtime `
            -p:Platform=$($target.Platform) `
            -p:BenchBookUnpackaged=true `
            -p:Version=$Version `
            -p:AssemblyVersion="$prefix.0" `
            -p:FileVersion="$prefix.$build" `
            -p:IncludeSourceRevisionInInformationalVersion=false `
            -o $folder
        if ($LASTEXITCODE -ne 0) { throw "The $($target.Runtime) build failed." }

        # Debug symbols aren't needed to run BenchBook.
        Get-ChildItem $folder -Recurse -Filter *.pdb | Remove-Item
        Copy-Item (Join-Path $PSScriptRoot "Read Me.txt") $folder

        $zip = Join-Path $Output "BenchBook-$Version-$($target.Runtime).zip"
        Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal
    }

    # So anyone can check a download is the one built here.
    Get-ChildItem $Output -Filter *.zip | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    } | Set-Content (Join-Path $Output "SHA256SUMS.txt") -Encoding ascii
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

Get-ChildItem $Output | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
