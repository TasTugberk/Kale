# Shared by the PowerShell dev scripts, like env.sh for the bash ones. Dot-source it at the top of a script:
#
#   . (Join-Path $PSScriptRoot 'common.ps1')
#
# Written for Windows PowerShell 5.1, which every Windows has: no ??, no ternary, no && or ||.

# Runs podman with the given arguments; stops the script if it fails. For commands whose output only goes to the
# console. (Native commands don't throw in PowerShell: their exit code has to be checked.)
function Invoke-PodmanStep {
  & podman @args
  if ($LASTEXITCODE -ne 0) {
    throw "podman $($args -join ' ') failed (exit code $LASTEXITCODE)"
  }
}

# Runs a program and returns its exit code, standard output and standard error, writing $InputText to its standard
# input exactly: UTF-8 without a byte order mark, and no newline added. Windows PowerShell can't redirect a native
# command's standard error while $ErrorActionPreference is Stop, and adds CR LF to text piped into one, so this goes
# through Process directly. This is also how secrets reach a program: through its standard input, never its command
# line (which other processes can read) or a file.
function Invoke-Native([string]$Program, [string[]]$Arguments, [string]$InputText) {
  $start = New-Object System.Diagnostics.ProcessStartInfo
  $start.FileName = (Get-Command $Program -CommandType Application | Select-Object -First 1).Source
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

function Invoke-Podman([string[]]$Arguments, [string]$InputText) {
  return Invoke-Native 'podman' $Arguments $InputText
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

# Creates a Podman secret holding exactly $Value, from standard input like the bash scripts, so it's never in a file.
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

# A Podman secret's value, without the newline podman prints after it (like $(...) in bash).
function Read-PodmanSecret([string]$Name) {
  $result = Invoke-Podman @('secret', 'inspect', '--showsecret', '--format', '{{.SecretData}}', $Name)
  if ($result.ExitCode -ne 0) {
    throw "Reading the Podman secret $Name failed: $($result.Errors)"
  }
  return $result.Output.TrimEnd("`r", "`n")
}

function New-RandomHex([int]$Bytes) {
  $buffer = New-Object byte[] $Bytes
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
  return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

# Settings from .env, created from .env.example on first run and completed with keys added to it since (like
# scripts/env.sh). podman-compose reads .env itself; the values are read here for the script's own steps. Run it from
# the repository root. Its messages are Write-Host, so `Read-Env 6>$null` silences them (like load_env >/dev/null).
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
  $path = (Resolve-Path .env).ProviderPath
  foreach ($line in Get-Content .env.example) {
    if ($line -cmatch '^([A-Z_]+)=' -and -not $present.ContainsKey($Matches[1])) {
      # A hand-edited .env may not end with a newline; appending would then join two keys on one line. LF, not the
      # CR LF of Add-Content: the bash scripts (Git Bash) source this file too, and would keep the CR in the value.
      $text = [System.IO.File]::ReadAllText($path)
      if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { [System.IO.File]::AppendAllText($path, "`n") }
      [System.IO.File]::AppendAllText($path, "$line`n")
      Write-Host "Added $($Matches[1]) to .env"
    }
  }

  $settings = @{}
  foreach ($line in Get-Content .env) {
    if ($line -cmatch '^([A-Z_]+)=(.*)$') { $settings[$Matches[1]] = $Matches[2] }
  }
  return $settings
}

# The last $Count lines of a program's output (like tail -n).
function Select-LastLines([string]$Text, [int]$Count) {
  $lines = $Text.TrimEnd("`r", "`n") -split "`r?`n"
  return ($lines | Select-Object -Last $Count) -join [Environment]::NewLine
}

# Prints the message on standard error and ends the script with exit code 1, like `fail` in the bash scripts.
# (A throw would add PowerShell's own position lines to the message.)
function Exit-WithError([string]$Message) {
  [Console]::Error.WriteLine($Message)
  exit 1
}
