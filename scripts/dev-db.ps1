<#
.SYNOPSIS
  Local database in one command on Windows, like scripts/dev-db.sh: starts PostgreSQL from compose.yaml, points
  AuthService at it, and applies the EF Core migrations. Safe to run again: every step skips what already exists.

.DESCRIPTION
  For running AuthService with `dotnet run` instead of the whole stack (scripts\dev-stack.ps1). Needs a running
  `podman machine`, podman-compose on PATH, and the dotnet-ef tool. Written for Windows PowerShell 5.1:

    powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$secretName = 'kale_postgres_password'
$containerName = 'kale-postgres'

function Get-HealthStatus {
  return (Invoke-Podman @('container', 'inspect', '--format', '{{.State.Health.Status}}', $containerName)).Output.Trim()
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
  # 1. Settings (names, host, port) come from .env, created from .env.example on first run and completed with
  #    keys added to it since.
  $envSettings = Read-Env

  # 2. The database password is a random Podman secret, created once. Hex, so it never needs escaping.
  #    PostgreSQL only reads it when the volume is first created. If you ever delete the secret, also delete
  #    the volume (podman compose down -v), or the new password won't match the database.
  if (-not (Test-PodmanSecret $secretName)) {
    New-PodmanSecret $secretName (New-RandomHex 24)
    Write-Host "Created Podman secret $secretName"
  }

  # 3. Start PostgreSQL and wait for its healthcheck (up to 60 s).
  Invoke-PodmanStep compose up -d postgres
  Write-Host -NoNewline 'Waiting for PostgreSQL'
  for ($i = 0; $i -lt 60; $i++) {
    if ((Get-HealthStatus) -eq 'healthy') {
      break
    }
    Write-Host -NoNewline '.'
    Start-Sleep -Seconds 1
  }
  if ((Get-HealthStatus) -ne 'healthy') {
    Exit-WithError " PostgreSQL did not become healthy; see: podman logs $containerName"
  }
  Write-Host ' ready'

  # 4. Give AuthService the connection string through .NET user secrets: stored in your home folder,
  #    never in the repo, and only loaded when running in Development. It goes to dotnet user-secrets as JSON on
  #    standard input, so the password isn't on a command line.
  $password = Read-PodmanSecret $secretName
  $connectionString = "Host=$($envSettings.POSTGRES_HOST);Port=$($envSettings.POSTGRES_PORT);" +
    "Database=$($envSettings.POSTGRES_DB);Username=$($envSettings.POSTGRES_USER);Password=$password"
  $settings = @{ 'ConnectionStrings:AuthDb' = $connectionString } | ConvertTo-Json
  $result = Invoke-Native 'dotnet' @('user-secrets', 'set', '--project', 'src/AuthService') $settings
  if ($result.ExitCode -ne 0) {
    Exit-WithError "$($result.Output)$($result.Errors)"
  }
  Write-Host 'Set ConnectionStrings:AuthDb in AuthService user secrets'

  # 5. Apply migrations. The design-time factory has no real database, so pass the connection explicitly. (Like
  #    dev-db.sh, this puts the local database's password on dotnet-ef's command line: it has no other way in.)
  & dotnet ef database update --project src/AuthService --connection $connectionString
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}
finally {
  Pop-Location
}
