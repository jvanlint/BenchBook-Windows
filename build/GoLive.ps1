<#
.SYNOPSIS
    Publishes a draft release, then puts its update feeds on the website, so
    copies of BenchBook are offered the update.

.DESCRIPTION
    The release workflow makes a draft release with the zips and the signed
    update feeds attached (CLAUDE.md, "Releasing"). This script:

    1. checks the draft has both feeds, for this version;
    2. publishes the draft;
    3. checks both zips download;
    4. copies the feeds into the website repository and pushes them.

    Copies are offered the update only after step 4. Needs gh, signed in, and
    git with permission to push to the website repository.

.EXAMPLE
    .\build\GoLive.ps1 -Version 1.4.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    # The website's repository; GitHub Pages serves its main branch.
    [string] $Site = "jvanlint/BenchBook-Site"
)

$ErrorActionPreference = "Stop"
$Version = $Version.TrimStart("v")
$tag = "v$Version"
$runtimes = @("x64", "arm64")
$feeds = $runtimes | ForEach-Object { "appcast-windows-$_.xml"; "appcast-windows-$_.xml.signature" }

$release = gh release view $tag --json isDraft,assets,url | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "There's no release for $tag." }
$names = $release.assets | ForEach-Object { $_.name }
$missing = $feeds | Where-Object { $names -notcontains $_ }
if ($missing) { throw "The $tag release has no update feeds ($($missing -join ', ')). Was the BENCHBOOK_UPDATE_PRIVATE_KEY secret set when it was built?" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("BenchBook Go Live " + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $downloaded = Join-Path $work "feeds"
    gh release download $tag --dir $downloaded @($feeds | ForEach-Object { "--pattern"; $_ })
    if ($LASTEXITCODE -ne 0) { throw "The feeds couldn't be downloaded from the release." }
    foreach ($runtime in $runtimes) {
        $text = Get-Content (Join-Path $downloaded "appcast-windows-$runtime.xml") -Raw
        if ($text -notmatch "<sparkle:version>$([regex]::Escape($Version))</sparkle:version>") {
            throw "appcast-windows-$runtime.xml isn't for $Version."
        }
    }

    if ($release.isDraft) {
        Write-Host "Publishing $tag"
        gh release edit $tag --draft=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The release couldn't be published." }
    }

    $repository = gh repo view --json nameWithOwner --jq .nameWithOwner
    foreach ($runtime in $runtimes) {
        $url = "https://github.com/$repository/releases/download/$tag/BenchBook-$Version-win-$runtime.zip"
        $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
        if ($response.StatusCode -ne 200) { throw "$url doesn't download ($($response.StatusCode))." }
        Write-Host "Downloads: $url"
    }

    $checkout = Join-Path $work "site"
    gh repo clone $Site $checkout -- --depth 1
    if ($LASTEXITCODE -ne 0) { throw "The website repository couldn't be cloned." }
    Copy-Item (Join-Path $downloaded "*") $checkout
    git -C $checkout add -- $feeds
    git -C $checkout diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "The website already has these feeds."
    }
    else {
        git -C $checkout commit -m "BenchBook for Windows $Version" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The feeds couldn't be committed." }
        git -C $checkout push
        if ($LASTEXITCODE -ne 0) { throw "The feeds couldn't be pushed to $Site." }
        Write-Host "The feeds are on $Site. Copies of BenchBook are offered $Version once GitHub Pages updates, usually within a few minutes."
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
