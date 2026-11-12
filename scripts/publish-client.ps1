<#
.SYNOPSIS
  Publishes Kale's packages for applications (Kale.Auth.Contracts, then Kale.Auth.Client) to a NuGet feed on
  Windows, like scripts/publish-client.sh, at the version in Directory.Build.props (<VersionPrefix>). Applications
  then only run `dotnet add package Kale.Auth.Client`.

.DESCRIPTION
  Written for Windows PowerShell 5.1.

    powershell -ExecutionPolicy Bypass -File scripts\publish-client.ps1 <feed>

    <feed>  the feed's https push address (e.g. your company's Nexus or BaGet), or a folder for a local feed

  A feed address needs its API key: type it when asked, or give it on standard input
  (Get-Content key.txt | powershell -ExecutionPolicy Bypass -File scripts\publish-client.ps1 <feed>). It never goes
  on a command line, where other processes could see it.
#>
param(
  [Parameter(Position = 0)]
  [string]$Feed
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Show-Usage {
  [Console]::Error.WriteLine(@'
scripts\publish-client.ps1 <feed>

  <feed>  the feed's https push address (e.g. your company's Nexus or BaGet), or a folder for a local feed
'@)
  exit 2
}

function Exit-AlreadyPublished([string]$Package, [string]$Version) {
  Exit-WithError "$Package $Version is already published: raise <VersionPrefix> in Directory.Build.props, then publish again."
}

# Like `read -r` in publish-client.sh: the first line of standard input, or typed without being shown.
function Read-ApiKey([string]$Feed, $PipedLines) {
  if ($PipedLines) {
    # Piped in from this PowerShell session (Get-Content key.txt | ...).
    $key = [string]@($PipedLines)[0]
  }
  elseif ([Console]::IsInputRedirected) {
    # Piped or redirected into powershell.exe (... | powershell -File ..., or cmd's < key.txt).
    $key = [Console]::In.ReadLine()
  }
  else {
    $typed = Read-Host -AsSecureString "API key for $Feed"
    $key = (New-Object System.Net.NetworkCredential('', $typed)).Password
  }
  if ($key) {
    return $key.Trim()
  }
  return ''
}

# The script's pipeline input, if it has any. ($input is read only then, so it can't wait for the console.)
$pipedLines = @()
if ($MyInvocation.ExpectingInput) {
  $pipedLines = @($input)
}

if (-not $Feed) {
  Show-Usage
}
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
  Exit-WithError 'This script needs the .NET SDK (dotnet).'
}

# The API key travels with the push: only over https.
if ($Feed -clike 'http://*') {
  Exit-WithError 'Use the feed''s https address: over http the API key would travel unencrypted.'
}
$isServer = $Feed -clike 'https://*'

# A folder is taken where the command was run, before moving to the repository.
if (-not $isServer) {
  New-Item -ItemType Directory -Force -Path $Feed | Out-Null
  $Feed = (Resolve-Path -LiteralPath $Feed).ProviderPath
}

Push-Location (Join-Path $PSScriptRoot '..')
$packages = Join-Path ([System.IO.Path]::GetTempPath()) ('kale-packages-' + [Guid]::NewGuid())
$previousApiKey = $env:NUGET_API_KEY
try {
  $props = [System.IO.File]::ReadAllText((Join-Path (Get-Location).ProviderPath 'Directory.Build.props'))
  $match = [regex]::Match($props, '<VersionPrefix>(.*)</VersionPrefix>')
  $version = $match.Groups[1].Value
  if (-not $version) {
    Exit-WithError 'No <VersionPrefix> in Directory.Build.props.'
  }

  $apiKey = ''
  if ($isServer) {
    $apiKey = Read-ApiKey $Feed $pipedLines
    if (-not $apiKey) {
      Exit-WithError 'Give the feed''s API key on standard input.'
    }
  }
  else {
    # A folder feed would silently overwrite a version, and applications that already have it cached would keep
    # the old copy: refuse here, as a server feed does (409).
    foreach ($package in 'Kale.Auth.Contracts', 'Kale.Auth.Client') {
      if (Test-Path -LiteralPath (Join-Path $Feed "$package.$version.nupkg")) {
        Exit-AlreadyPublished $package $version
      }
    }
  }

  New-Item -ItemType Directory -Path $packages | Out-Null
  foreach ($project in 'Auth.Contracts', 'Auth.Client') {
    $packed = Invoke-Native 'dotnet' @('pack', "src/$project", '-c', 'Release', '-o', $packages)
    if ($packed.ExitCode -ne 0) {
      [Console]::Error.WriteLine((Select-LastLines "$($packed.Output)$($packed.Errors)" 20))
      Exit-WithError "Couldn't pack src/$project."
    }
  }

  # Contracts first: the client depends on it. NUGET_API_KEY is in this process's environment only while dotnet
  # pushes (dotnet inherits it), and is put back as it was right after.
  foreach ($package in 'Kale.Auth.Contracts', 'Kale.Auth.Client') {
    $env:NUGET_API_KEY = $apiKey
    try {
      $pushed = Invoke-Native 'dotnet' @('nuget', 'push', (Join-Path $packages "$package.$version.nupkg"), '--source', $Feed)
    }
    finally {
      $env:NUGET_API_KEY = $previousApiKey
    }
    if ($pushed.ExitCode -ne 0) {
      $text = "$($pushed.Output)$($pushed.Errors)"
      if ($text -match 'already exists|409|conflict') {
        Exit-AlreadyPublished $package $version
      }
      [Console]::Error.WriteLine($text)
      Exit-WithError "Couldn't publish $package $version."
    }
    Write-Host "Published $package $version"
  }

  Write-Host ''
  Write-Host 'Applications can now add it, in their project''s folder (once):'
  Write-Host '    dotnet new nugetconfig      (unless there is a nuget.config already)'
  Write-Host "    dotnet nuget add source $Feed --name kale --configfile nuget.config"
  Write-Host "    dotnet add package Kale.Auth.Client --version $version"
  Write-Host 'If the feed has a separate address for reading packages (some servers do), use that one in add source.'
}
finally {
  $env:NUGET_API_KEY = $previousApiKey
  if (Test-Path -LiteralPath $packages) {
    Remove-Item -LiteralPath $packages -Recurse -Force
  }
  Pop-Location
}
