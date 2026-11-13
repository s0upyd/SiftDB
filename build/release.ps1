<#
.SYNOPSIS
  Builds a SiftDB release: a self-contained publish of the host, packed by Velopack into an installer and update
  packages (design section 9).

.DESCRIPTION
  Output goes to artifacts/: publish/ holds the app files, releases/ what goes on GitHub Releases
  (SiftDB-win-Setup.exe, the .nupkg full and delta packages, releases.<channel>.json, a portable zip).

  For delta updates, the previous release must be in artifacts/releases first; -DownloadPrevious fetches it from
  GitHub Releases (the release workflow does this).

.EXAMPLE
  ./build/release.ps1                      # version from the csproj, stable channel
  ./build/release.ps1 -Version 0.2.0-beta.1 -Channel beta
#>
param(
    [string] $Version,
    [string] $Channel = 'win',
    [switch] $DownloadPrevious,
    [string] $Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$host_ = Join-Path $root 'src/SiftDB.Host/SiftDB.Host.csproj'
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'
$repoUrl = 'https://github.com/s0upyd/SiftDB'

if (-not $Version) { $Version = ([xml](Get-Content $host_)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
Write-Host "SiftDB $Version, channel $Channel"

function Invoke-Step([string] $what, [scriptblock] $step) {
    Write-Host "==> $what"
    & $step
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

Push-Location $root
try {
    Invoke-Step 'Restore tools' { dotnet tool restore }

    # Start clean: Velopack refuses to pack a version the output folder already holds. -DownloadPrevious refills it.
    foreach ($dir in $publish, $releases) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    # Self-contained, so no .NET install is needed. Not single-file: Velopack's deltas work per file.
    Invoke-Step 'Publish' {
        dotnet publish $host_ -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
    }

    if ($DownloadPrevious) {
        Invoke-Step 'Download the previous release (for deltas)' {
            $args_ = @('vpk', 'download', 'github', '--repoUrl', $repoUrl, '--channel', $Channel, '-o', $releases)
            if ($Token) { $args_ += @('--token', $Token) }
            if ($Channel -ne 'win') { $args_ += '--pre' }
            dotnet @args_
        }
    }

    # --framework webview2: Setup.exe installs the Edge WebView2 Runtime first when the machine lacks it.
    Invoke-Step 'Pack' {
        dotnet vpk pack --packId SiftDB --packVersion $Version --packDir $publish --mainExe SiftDB.exe `
            --icon (Join-Path $root "src/SiftDB.Host/Assets/SiftDB.ico") --packTitle SiftDB --packAuthors 'SiftDB' --runtime win-x64 --channel $Channel --framework webview2 --outputDir $releases
    }
    Write-Host "Done: $releases"
}
finally {
    Pop-Location
}
