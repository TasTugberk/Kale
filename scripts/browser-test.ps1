<#
.SYNOPSIS
  The admin UI in a real browser (Playwright, Chromium) against the running stack (scripts\dev-stack.ps1), on Windows,
  like scripts/browser-test.sh.

.DESCRIPTION
  Once per machine: cd admin-ui; npm ci; npx playwright install chromium
  Creates two applications and a user on every run; touches nothing else. Traces are off: they would record
  the admin's password (see admin-ui/playwright.config.ts). Arguments go to `npx playwright test` as they are:

    powershell -ExecutionPolicy Bypass -File scripts\browser-test.ps1
    powershell -ExecutionPolicy Bypass -File scripts\browser-test.ps1 --grep "role"
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# Set for this test run's own processes only: they are removed again at the end, so running the script in your own
# PowerShell session (.\scripts\browser-test.ps1) doesn't leave the admin's password in it.
$variables = 'KALE_E2E_UI_URL', 'KALE_E2E_SAMPLE_URL', 'KALE_E2E_ADMIN_USER', 'KALE_E2E_ADMIN_PASSWORD'
$exitCode = 1

Push-Location (Join-Path $PSScriptRoot '..')
try {
  $envSettings = Read-Env 6>$null

  $env:KALE_E2E_UI_URL = "http://localhost:$($envSettings.ADMIN_UI_PORT)"
  $env:KALE_E2E_SAMPLE_URL = "http://localhost:$($envSettings.SAMPLEAPP_PORT)"
  $env:KALE_E2E_ADMIN_USER = $envSettings.ADMIN_USER
  # The first admin's password, from its Podman secret.
  $env:KALE_E2E_ADMIN_PASSWORD = Read-PodmanSecret 'kale_admin_password'

  Set-Location admin-ui
  & npx playwright test @args
  $exitCode = $LASTEXITCODE
}
finally {
  foreach ($name in $variables) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
  }
  Pop-Location
}
exit $exitCode
