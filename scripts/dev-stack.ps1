<#
.SYNOPSIS
  The whole local stack on Windows, like scripts/dev-stack.sh: PostgreSQL, RabbitMQ, AuthService (with its database
  migrated), SampleApp and the admin UI, built from this checkout. Safe to run again: every step skips what exists.

.DESCRIPTION
  Needs Podman for Windows (a running `podman machine`) and podman-compose on PATH (see the message if it's missing).
  Written for Windows PowerShell 5.1, which every Windows has. Run it from the repository root:

    powershell -ExecutionPolicy Bypass -File scripts\dev-stack.ps1
    powershell -ExecutionPolicy Bypass -File scripts\dev-stack.ps1 -CaCertificate C:\certs\company-root.pem

  smoke-test.sh and browser-test.sh stay bash scripts: run them from Git Bash.

.PARAMETER CaCertificate
  A network whose proxy inspects TLS re-signs every HTTPS connection with its own root certificate (PEM). With it,
  the Podman VM trusts that certificate (image pulls), and the image builds get it too: the VM's CA bundle replaces
  each build step's system bundle (dotnet restore, apk), and an npmrc points npm at it. Nothing in the repository
  changes, and the images are the same as without it. A relative path is relative to the folder you run the script
  from.
#>
param(
  [string]$CaCertificate
)

$ErrorActionPreference = 'Stop'

# Runs podman with the given arguments; stops the script if it fails. For commands whose output only goes to the
# console. (Native commands don't throw in PowerShell: their exit code has to be checked.)
function Invoke-PodmanStep {
  & podman @args
  if ($LASTEXITCODE -ne 0) {
    throw "podman $($args -join ' ') failed (exit code $LASTEXITCODE)"
  }
}

# Runs podman and returns its exit code, standard output and standard error, writing $InputText to its standard input
# exactly: UTF-8 without a byte order mark, and no newline added. Windows PowerShell can't redirect a native command's
# standard error while $ErrorActionPreference is Stop, and adds CR LF to text piped into one, so this goes through
# Process directly.
function Invoke-Podman([string[]]$Arguments, [string]$InputText) {
  $start = New-Object System.Diagnostics.ProcessStartInfo
  $start.FileName = (Get-Command podman -CommandType Application).Source
  $start.Arguments = ($Arguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' '
  # Process starts in .NET's current folder, which Push-Location doesn't change; & uses PowerShell's. Same for both.
  $start.WorkingDirectory = (Get-Location).ProviderPath
  $start.UseShellExecute = $false
  $start.RedirectStandardInput = $true
  $start.RedirectStandardOutput = $true
  $start.RedirectStandardError = $true

  # On Windows PowerShell (.NET Framework), Process.Start wraps standard input in a writer with the console's input
  # encoding and turns AutoFlush on, and that flush already writes the encoding's byte order mark into the pipe, before
  # anything else is written. With a UTF-8 console (code page 65001) the mark would become the first character of a
  # secret or of the first admin's password. So when the console's input encoding has a mark, it is UTF-8 without one
  # while the process starts (the writer takes it then), and is put back right after.
  $consoleEncoding = [Console]::InputEncoding
  $encodingHasMark = $consoleEncoding.GetPreamble().Length -gt 0
  if ($encodingHasMark) {
    [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
  }
  try {
    $process = [System.Diagnostics.Process]::Start($start)
  }
  finally {
    if ($encodingHasMark) {
      [Console]::InputEncoding = $consoleEncoding
    }
  }

  # Read standard error in the background, so neither pipe can fill up and block the other.
  $errors = $process.StandardError.ReadToEndAsync()
  # The text goes to the stream itself as UTF-8 bytes (no mark), not through the writer, whatever its encoding.
  $stdin = $process.StandardInput.BaseStream
  if ($InputText) {
    $bytes = (New-Object System.Text.UTF8Encoding $false).GetBytes($InputText)
    $stdin.Write($bytes, 0, $bytes.Length)
  }
  $stdin.Close()
  $output = $process.StandardOutput.ReadToEnd()
  $process.WaitForExit()
  return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Errors = $errors.Result }
}

# One command-line argument, quoted for Windows' parser when it has spaces or quotes.
function ConvertTo-Argument([string]$Value) {
  # An empty argument must still be one (""), or every argument after it moves up a place.
  if ($Value -eq '') {
    return '""'
  }
  if ($Value -notmatch '[\s"]') {
    return $Value
  }

  return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

# Creates a Podman secret holding exactly $Value, from standard input like dev-stack.sh, so it's never in a file.
function New-PodmanSecret([string]$Name, [string]$Value) {
  $result = Invoke-Podman @('secret', 'create', $Name, '-') $Value
  if ($result.ExitCode -ne 0) {
    throw "Creating the Podman secret $Name failed: $($result.Errors)"
  }
}

function Test-PodmanSecret([string]$Name) {
  & podman secret exists $Name
  return $LASTEXITCODE -eq 0
}

function New-RandomHex([int]$Bytes) {
  $buffer = New-Object byte[] $Bytes
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
  return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

# Waits (up to 2 minutes) for a container's healthcheck to pass.
function Wait-Healthy([string]$Container) {
  Write-Host -NoNewline "Waiting for $Container"
  for ($i = 0; $i -lt 120; $i++) {
    $status = (Invoke-Podman @('container', 'inspect', '--format', '{{.State.Health.Status}}', $Container)).Output.Trim()
    if ($status -eq 'healthy') {
      Write-Host ' ready'
      return
    }
    Write-Host -NoNewline '.'
    Start-Sleep -Seconds 1
  }
  throw "$Container is not healthy; see: podman logs $Container"
}

# Settings from .env, created from .env.example on first run and completed with keys added to it since (like
# scripts/env.sh). podman-compose reads .env itself; the values are read here for the script's own steps.
function Read-Env {
  if (-not (Test-Path .env)) {
    Copy-Item .env.example .env
    Write-Host 'Created .env from .env.example'
  }

  # -cmatch, not -match: -match ignores case by the computer's culture, and in Turkish "I" isn't the upper case of
  # "i", so [A-Z] would miss every key with an I in it (ADMIN_USER, POSTGRES_IMAGE, ...).
  $present = @{}
  foreach ($line in Get-Content .env) {
    if ($line -cmatch '^([A-Z_]+)=') { $present[$Matches[1]] = $true }
  }
  foreach ($line in Get-Content .env.example) {
    if ($line -cmatch '^([A-Z_]+)=' -and -not $present.ContainsKey($Matches[1])) {
      # A hand-edited .env may not end with a newline; appending would then join two keys on one line.
      $text = [System.IO.File]::ReadAllText((Resolve-Path .env))
      if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { Add-Content .env '' }
      Add-Content .env $line
      Write-Host "Added $($Matches[1]) to .env"
    }
  }

  $settings = @{}
  foreach ($line in Get-Content .env) {
    if ($line -cmatch '^([A-Z_]+)=(.*)$') { $settings[$Matches[1]] = $Matches[2] }
  }
  return $settings
}

# Makes the Podman VM trust the proxy's root certificate (for image pulls), and puts the npmrc for the image builds
# next to the VM's CA bundle. This runs on every run with -CaCertificate: tee overwrites the same files, so a repeat
# changes nothing, and a different certificate replaces the old one.
function Initialize-CaCertificate([string]$Path) {
  $pem = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
  if ($pem -notmatch '-----BEGIN CERTIFICATE-----') {
    throw "$Path is not a PEM certificate"
  }

  $anchor = '/etc/pki/ca-trust/source/anchors/kale-proxy-root.crt'
  $install = "sudo tee $anchor >/dev/null && sudo update-ca-trust && sudo mkdir -p /etc/kale-build && " +
    "echo 'cafile=/etc/ssl/certs/ca-certificates.crt' | sudo tee /etc/kale-build/npmrc >/dev/null"
  $result = Invoke-Podman @('machine', 'ssh', $install) $pem
  if ($result.ExitCode -ne 0) {
    throw "Could not install the certificate in the Podman VM: $($result.Errors)"
  }
  Write-Host 'The Podman VM trusts the given CA certificate'
}

# Resolved before the script moves to the repository root, so a relative path means the folder it was typed in.
if ($CaCertificate) {
  $CaCertificate = (Resolve-Path $CaCertificate).ProviderPath
}

# The folder and PATH are put back in finally, so running the script as .\scripts\dev-stack.ps1 (in the caller's own
# session, not with -File) leaves the caller's shell as it was, also after a failure.
$originalPath = $env:Path
Push-Location (Join-Path $PSScriptRoot '..')
try {
  # `podman machine ssh` (used for -CaCertificate) runs the first ssh on PATH and gives it NUL, Windows' null device,
  # as a file. Git's ssh (first on PATH in Git Bash) takes that for a file name and creates a file "NUL" in the
  # current folder, the build context, which the image builds then can't read. Windows' own OpenSSH understands it.
  $windowsOpenSsh = Join-Path $env:SystemRoot 'System32\OpenSSH'
  if (Test-Path (Join-Path $windowsOpenSsh 'ssh.exe')) {
    $env:Path = "$windowsOpenSsh;$env:Path"
  }

  # 0. podman-compose: `podman compose` hands over to it.
  if ((Invoke-Podman @('compose', 'version')).ExitCode -ne 0) {
    throw "podman compose needs podman-compose: python -m pip install --user podman-compose, then add pip's user " +
      "Scripts folder to PATH (python -c `"import sysconfig; print(sysconfig.get_path('scripts', 'nt_user'))`"). " +
      "Behind a TLS-inspecting proxy, add --cert <its root certificate> to the pip command."
  }

  # 1. Settings. The ones this script uses itself must be there (compose checks its own).
  $envSettings = Read-Env
  foreach ($key in 'AUTHSERVICE_HOST', 'AUTHSERVICE_PORT', 'SAMPLEAPP_PORT', 'ADMIN_UI_PORT', 'ADMIN_USER') {
    if (-not $envSettings[$key]) {
      throw "Set $key in .env"
    }
  }

  # 2. Random passwords as Podman secrets, created once. Hex, so they never need escaping. PostgreSQL reads its
  #    password only when the volume is first created: if you delete that secret, delete the volume too
  #    (podman compose down -v).
  foreach ($secret in 'kale_postgres_password', 'kale_rabbitmq_password') {
    if (-not (Test-PodmanSecret $secret)) {
      New-PodmanSecret $secret (New-RandomHex 24)
      Write-Host "Created Podman secret $secret"
    }
  }

  # The first admin's password must also meet AuthService's password rules (an uppercase letter, a symbol).
  if (-not (Test-PodmanSecret 'kale_admin_password')) {
    New-PodmanSecret 'kale_admin_password' "Kale-$(New-RandomHex 16)"
    Write-Host 'Created Podman secret kale_admin_password'
  }

  # 3. Images from this checkout. Behind a TLS-inspecting proxy, compose adds two read-only mounts from the Podman VM
  #    to every build: its CA bundle (which now trusts the proxy's root) as the build step's system bundle, for
  #    dotnet restore and apk, and an npmrc that points npm at it. The value must follow "=" in the same argument, or
  #    podman-compose takes -v for an option of its own.
  if ($CaCertificate) {
    Initialize-CaCertificate $CaCertificate
    $buildMounts = '-v /etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem:/etc/ssl/certs/ca-certificates.crt:ro ' +
      '-v /etc/kale-build/npmrc:/usr/local/etc/npmrc:ro'
    Invoke-PodmanStep compose "--podman-build-args=$buildMounts" build
  }
  else {
    Invoke-PodmanStep compose build
  }

  # 4. PostgreSQL and RabbitMQ, then the migration and AuthService from the images just built. podman-compose doesn't
  #    notice a rebuilt image (the container's settings didn't change), so the app containers are recreated on every
  #    run; the migration runs in the foreground, and a failure stops here.
  Invoke-PodmanStep compose up -d postgres rabbitmq
  Wait-Healthy 'kale-postgres'
  Wait-Healthy 'kale-rabbitmq'
  Invoke-PodmanStep compose run --rm migrate
  Invoke-PodmanStep compose up -d --force-recreate --no-deps authservice
  Wait-Healthy 'kale-authservice'

  # 5. The sample's application in AuthService, and its client secret as a Podman secret. The secret is printed only
  #    when the application is created, so creating is tried every time: it succeeds after the database was reset
  #    (podman compose down -v), and then the stale secret is replaced.
  $sampleRedirect = "http://localhost:$($envSettings.SAMPLEAPP_PORT)/signin-oidc"
  $created = Invoke-Podman @('exec', 'kale-authservice', 'dotnet', 'AuthService.dll', 'admin', 'create-application',
    'sample', 'Sample App', $sampleRedirect)
  if ($created.ExitCode -eq 0) {
    # A container still using the old secret has to go before the secret can be replaced.
    Invoke-Podman @('rm', '-f', 'kale-sampleapp') | Out-Null
    Invoke-Podman @('secret', 'rm', 'kale_sample_client_secret') | Out-Null
    New-PodmanSecret 'kale_sample_client_secret' $created.Output.TrimEnd("`r", "`n")
    Write-Host "Created application 'sample' and Podman secret kale_sample_client_secret"
  }
  elseif ($created.Errors -notmatch 'already exists') {
    throw "Creating the application 'sample' failed: $($created.Errors)"
  }
  elseif (-not (Test-PodmanSecret 'kale_sample_client_secret')) {
    throw "The application 'sample' exists but its secret is gone. Start over with: podman compose down -v, then this script."
  }

  # 6. The first admin and the admin UI's application ("auth"), once: `admin bootstrap` prints the UI's client secret
  #    only then. After a database reset it succeeds again and the stale secret is replaced.
  $adminPassword = Invoke-Podman @('secret', 'inspect', '--showsecret', '--format', '{{.SecretData}}', 'kale_admin_password')
  if ($adminPassword.ExitCode -ne 0) {
    throw "Reading the Podman secret kale_admin_password failed: $($adminPassword.Errors)"
  }
  $uiPort = $envSettings.ADMIN_UI_PORT
  $bootstrap = Invoke-Podman @('exec', '-i', 'kale-authservice', 'dotnet', 'AuthService.dll', 'admin', 'bootstrap',
    $envSettings.ADMIN_USER, 'Kale Admin', "http://localhost:$uiPort/signin-oidc", "http://localhost:$uiPort/signed-out") `
    $adminPassword.Output
  if ($bootstrap.ExitCode -eq 0) {
    Invoke-Podman @('rm', '-f', 'kale-admin-ui') | Out-Null
    Invoke-Podman @('secret', 'rm', 'kale_admin_ui_client_secret') | Out-Null
    New-PodmanSecret 'kale_admin_ui_client_secret' $bootstrap.Output.TrimEnd("`r", "`n")
    Write-Host "Created the first admin '$($envSettings.ADMIN_USER)' and Podman secret kale_admin_ui_client_secret"
  }
  elseif ($bootstrap.Errors -notmatch 'already exists') {
    throw "admin bootstrap failed: $($bootstrap.Errors)"
  }
  elseif (-not (Test-PodmanSecret 'kale_admin_ui_client_secret')) {
    throw "The application 'auth' exists but the admin UI's secret is gone. Start over with: podman compose down -v, then this script."
  }

  # 7. The sample app, which registers its operations with AuthService on startup.
  Invoke-PodmanStep compose up -d --force-recreate --no-deps sampleapp
  Wait-Healthy 'kale-sampleapp'

  # 8. The admin UI.
  Invoke-PodmanStep compose up -d --force-recreate --no-deps admin-ui
  Wait-Healthy 'kale-admin-ui'

  Write-Host ''
  Write-Host "AuthService: http://$($envSettings.AUTHSERVICE_HOST):$($envSettings.AUTHSERVICE_PORT)/  (sign-in pages, tokens)"
  Write-Host "SampleApp:   http://localhost:$($envSettings.SAMPLEAPP_PORT)/notes"
  Write-Host "Admin UI:    http://localhost:$uiPort/  (user $($envSettings.ADMIN_USER); password: podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password)"
  Write-Host 'Check it end to end from Git Bash: ./scripts/smoke-test.sh'
}
finally {
  Pop-Location
  $env:Path = $originalPath
}
