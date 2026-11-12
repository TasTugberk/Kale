<#
.SYNOPSIS
  Connects an application to the local Kale (scripts\dev-stack.ps1) in one step on Windows, like
  scripts/register-app.sh: registers it in Kale and writes every setting builder.AddKaleAuth<T>() needs into the .NET
  user secrets of each given project. Local development only: the settings use plain http and Kale's dev-only gRPC
  port.

.DESCRIPTION
  Written for Windows PowerShell 5.1. A relative project folder is relative to the folder you run the script from.

    powershell -ExecutionPolicy Bypass -File scripts\register-app.ps1 orders -Web http://localhost:5199 C:\src\Orders.Api C:\src\Orders.Web
    powershell -ExecutionPolicy Bypass -File scripts\register-app.ps1 orders -NewSecret C:\src\Orders.Api C:\src\Orders.Web
#>
param(
  [Parameter(Position = 0)]
  [string]$Key,
  [string]$Web,
  [switch]$NewSecret,
  [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
  [string[]]$Project
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Show-Usage {
  [Console]::Error.WriteLine(@'
Usage: scripts\register-app.ps1 <key> [-Web <address>] [-NewSecret] <project folder>...

  <key>             the application's key in Kale, e.g. orders (lowercase letters, digits, -; permanent)
  -Web <address>    the address of its web front end, e.g. http://localhost:5199: Kale may then send users back
                    to <address>/signin-oidc and <address>/signout-callback-oidc. Leave it out for an API only.
  -NewSecret        the key is already registered: replace its client secret (the old one stops working) and
                    write the new one into the given projects
  <project folder>  one or more .NET projects that share this key (e.g. its API and its web front end)
'@)
  exit 2
}

# Kale's own messages are kept apart; a failure shows them.
function Exit-Refused([string]$Message, $Result) {
  [Console]::Error.WriteLine($Message)
  [Console]::Error.WriteLine((Select-LastLines $Result.Errors 3))
  if ($Result.Errors -match 'already exists') {
    [Console]::Error.WriteLine('To replace its secret and write it into these projects, add -NewSecret.')
  }
  exit 1
}

function Invoke-KaleAdmin([string[]]$Arguments) {
  return Invoke-Podman (@('exec', '-i', 'kale-authservice', 'dotnet', 'AuthService.dll', 'admin') + $Arguments)
}

if (-not $Key -or -not $Project) {
  Show-Usage
}
foreach ($folder in $Project) {
  if ($folder.StartsWith('-', [System.StringComparison]::Ordinal)) {
    Show-Usage
  }
}
# Like ${2%/} in register-app.sh: one trailing slash goes.
$Web = $Web -replace '/$', ''

# Everything that can be refused is checked before Kale is asked: once Kale has made a secret, it's shown only
# this once (and with -NewSecret the old one has already stopped working).
if ($Key -ceq 'auth' -or $Key -ceq 'sample') {
  Exit-WithError "'$Key' belongs to the local stack itself (the admin UI, SampleApp); choose another key."
}
if ($NewSecret -and $Web) {
  Exit-WithError '-Web works only when registering. To change an application''s return addresses, use the admin UI: Applications, then its key.'
}
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
  Exit-WithError 'This script needs the .NET SDK (dotnet).'
}

# Full paths now, before the script moves to the repository: dotnet then runs from there.
$projectPaths = @()
foreach ($folder in $Project) {
  $projectPaths += [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $folder))
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
  $envSettings = Read-Env 6>$null
  $running = (Invoke-Podman @('container', 'inspect', '--format', '{{.State.Running}}', 'kale-authservice')).Output.Trim()
  if ($running -ne 'true') {
    Exit-WithError "Kale isn't running: start it with scripts\dev-stack.ps1"
  }

  for ($i = 0; $i -lt $Project.Count; $i++) {
    $csproj = Get-ChildItem -LiteralPath $projectPaths[$i] -Filter '*.csproj' -File -ErrorAction SilentlyContinue
    if (-not $csproj) {
      Exit-WithError "No .NET project (*.csproj) in '$($Project[$i])'."
    }
    # Adds a UserSecretsId to the project once (it says so when there already is one). Done now, so a project that
    # can't take user secrets is found before the secret exists.
    $init = Invoke-Native 'dotnet' @('user-secrets', 'init', '--project', $projectPaths[$i])
    if ($init.ExitCode -ne 0) {
      [Console]::Error.WriteLine("$($init.Output)$($init.Errors)")
      Exit-WithError "Couldn't set up user secrets in '$($Project[$i])'."
    }
  }

  # Only the secret comes back on standard output; it stays in this process and goes to the projects on stdin.
  if ($NewSecret) {
    $result = Invoke-KaleAdmin @('rotate-secret', $Key)
    $refusal = "Couldn't replace the secret of '$Key':"
  }
  elseif ($Web) {
    $result = Invoke-KaleAdmin @('create-application', $Key, $Key, "$Web/signin-oidc", "$Web/signout-callback-oidc")
    $refusal = "Couldn't register '$Key':"
  }
  else {
    $result = Invoke-KaleAdmin @('create-application', $Key, $Key)
    $refusal = "Couldn't register '$Key':"
  }
  if ($result.ExitCode -ne 0) {
    Exit-Refused $refusal $result
  }

  # The addresses the application uses to reach the local Kale: the issuer (the same for browsers and apps), and
  # the dev-only gRPC port published on APP_BIND_ADDRESS.
  $settings = [ordered]@{
    'Kale:Authority' = "http://$($envSettings.AUTHSERVICE_HOST):$($envSettings.AUTHSERVICE_PORT)/"
    'Kale:GrpcAddress' = "http://$($envSettings.APP_BIND_ADDRESS):$($envSettings.AUTHSERVICE_GRPC_PORT)/"
    'Kale:AllowInsecureHttpDevOnly' = 'true'
    'Kale:ApplicationKey' = $Key
    'Kale:ClientSecret' = $result.Output.TrimEnd("`r", "`n")
  } | ConvertTo-Json
  # From here on only $settings holds the secret (like `unset secret` in register-app.sh).
  Remove-Variable result

  for ($i = 0; $i -lt $Project.Count; $i++) {
    $written = Invoke-Native 'dotnet' @('user-secrets', 'set', '--project', $projectPaths[$i]) $settings
    if ($written.ExitCode -ne 0) {
      Exit-WithError "$($written.Output)$($written.Errors)"
    }
    Write-Host "Wrote Kale's settings into the user secrets of $($Project[$i])"
  }

  Write-Host ''
  Write-Host "'$Key' is registered in Kale. In each project:"
  Write-Host '    builder.AddKaleAuth<YourOperations>();     // Program.cs, before builder.Build()'
  Write-Host '    [Authorize(YourOperations.Something)]      // on each endpoint to protect'
  Write-Host 'Start it with `dotnet run` (Development). Then give people access in the admin UI'
  Write-Host "(http://localhost:$($envSettings.ADMIN_UI_PORT)/): a role with your operations, and access to '$Key'."
}
finally {
  Pop-Location
}
