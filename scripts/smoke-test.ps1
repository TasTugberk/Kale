<#
.SYNOPSIS
  End-to-end check of the running stack (scripts\dev-stack.ps1) on Windows, like scripts/smoke-test.sh: a user signs in
  through AuthService's real pages (authorization code + PKCE), and SampleApp allows or refuses their requests by
  role. Then an admin edits the role, and SampleApp follows within seconds (invalidation event through RabbitMQ).
  Finally the first admin signs in to the admin UI. Creates a new user and role on every run; touches nothing else.

.DESCRIPTION
  Written for Windows PowerShell 5.1; needs neither curl nor jq (HTTP through .NET's HttpClient, JSON through
  ConvertFrom-Json):

    powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
Add-Type -AssemblyName System.Net.Http

function Write-Pass([string]$Text) {
  Write-Host "PASS  $Text"
}

function Exit-Fail([string]$Text, [string]$Details) {
  [Console]::Error.WriteLine("FAIL  $Text")
  if ($Details) {
    [Console]::Error.WriteLine($Details)
  }
  exit 1
}

# The start of a page, for a failure message (like head -c 300).
function Get-Start([string]$Text) {
  return $Text.Substring(0, [Math]::Min(300, $Text.Length))
}

# Ordinal, not the culture's comparison (which can differ with a Turkish culture).
function Test-StartsWith([string]$Text, [string]$Prefix) {
  return $Text.StartsWith($Prefix, [System.StringComparison]::Ordinal)
}

# The admin command's own messages (and logs) are kept; a failure shows them.
$script:adminErrors = ''
function Invoke-KaleAdmin([string[]]$Arguments, [string]$InputText) {
  $result = Invoke-Podman (@('exec', '-i', 'kale-authservice', 'dotnet', 'AuthService.dll', 'admin') + $Arguments) $InputText
  $script:adminErrors = $result.Errors
  return $result.ExitCode -eq 0
}

function Exit-AdminFailed([string]$Text) {
  Exit-Fail $Text (Select-LastLines $script:adminErrors 5)
}

# A browser of its own: its own cookies, like one of smoke-test.sh's curl cookie jars (kept in memory here, not in
# temporary files). -WithoutCookies is curl without a jar.
function New-Browser([switch]$WithoutCookies) {
  $handler = New-Object System.Net.Http.HttpClientHandler
  # Every redirect is looked at, like curl without -L.
  $handler.AllowAutoRedirect = $false
  # The stack is on this machine: a proxy from the Windows settings must not get these requests.
  $handler.UseProxy = $false
  if ($WithoutCookies) {
    $handler.UseCookies = $false
  }
  $browser = New-Object System.Net.Http.HttpClient $handler
  # .NET Framework would otherwise ask for "100 Continue" before sending a form; curl doesn't.
  $browser.DefaultRequestHeaders.ExpectContinue = $false
  return $browser
}

# Sends a request and returns its status, the absolute address it redirects to (empty if none, like curl's
# %{redirect_url}), and its body. -Form is a form post (like --data-urlencode), -Json a JSON post.
function Invoke-Http {
  param($Browser, [string]$Url, [string]$Method = 'GET', $Form, [string]$Json, [hashtable]$Headers = @{})

  $address = [Uri]$Url
  $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $address)
  # Windows doesn't resolve names under .localhost (browsers and curl do that themselves), and "localhost" may be ::1
  # first while the stack listens on 127.0.0.1 only (APP_BIND_ADDRESS). So such a request goes to 127.0.0.1, with
  # the name in the Host header, as a browser would send it.
  if ($address.Host -eq 'localhost' -or $address.Host.EndsWith('.localhost', [System.StringComparison]::Ordinal)) {
    $builder = New-Object System.UriBuilder $address
    $builder.Host = '127.0.0.1'
    $request.RequestUri = $builder.Uri
    $request.Headers.Host = $address.Authority
  }
  foreach ($name in $Headers.Keys) {
    [void]$request.Headers.TryAddWithoutValidation($name, $Headers[$name])
  }
  if ($Form) {
    $fields = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    foreach ($name in $Form.Keys) {
      $fields[$name] = [string]$Form[$name]
    }
    $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($fields)
  }
  elseif ($Json) {
    $request.Content = [System.Net.Http.StringContent]::new($Json, [System.Text.Encoding]::UTF8, 'application/json')
  }

  $response = $Browser.SendAsync($request).GetAwaiter().GetResult()
  try {
    $location = ''
    if ($response.Headers.Location) {
      # Relative to the address as asked for (with its name), like curl.
      $location = [Uri]::new($address, $response.Headers.Location).AbsoluteUri
    }
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Location = $location; Body = $body }
  }
  finally {
    $response.Dispose()
    $request.Dispose()
  }
}

# "<status> <redirect address>", like curl -w '%{http_code} %{redirect_url}'.
function Get-StatusAndLocation($Browser, [string]$Url) {
  $response = Invoke-Http $Browser $Url
  return "$($response.Status) $($response.Location)"
}

# A hidden form field's value from a page (the login form's anti-forgery token and return address).
function Get-FormField([string]$Page, [string]$Name) {
  $match = [regex]::Match($Page, 'name="' + [regex]::Escape($Name) + '"[^>]*value="([^"]*)"')
  return $match.Groups[1].Value.Replace('&amp;', '&')
}

# Signs in through AuthService's real pages like a browser, with its own cookies: authorize -> login page -> post
# the form. Returns where AuthService sends the browser back (the client's address, with the code).
function Invoke-SignIn($Browser, [string]$Authorize, [string]$User, [string]$Password) {
  $loginUrl = (Invoke-Http $Browser $Authorize).Location
  if (-not $loginUrl.Contains('/account/login')) {
    Exit-Fail "authorize should send an unknown browser to the login page (got: $loginUrl)"
  }
  $loginPage = (Invoke-Http $Browser $loginUrl).Body
  $form = [ordered]@{
    '__RequestVerificationToken' = Get-FormField $loginPage '__RequestVerificationToken'
    'ReturnUrl' = Get-FormField $loginPage 'ReturnUrl'
    'UserName' = $User
    'Password' = $Password
  }
  $afterLogin = (Invoke-Http $Browser $loginUrl -Method 'POST' -Form $form).Location
  if (-not $afterLogin) {
    Exit-Fail "the login form was not accepted for $User"
  }
  return (Invoke-Http $Browser $afterLogin).Location
}

# PKCE: the challenge is the verifier's SHA-256, base64url without padding.
function Get-CodeChallenge([string]$Verifier) {
  $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::ASCII.GetBytes($Verifier))
  return [Convert]::ToBase64String($hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-AuthorizeUrl([string]$Challenge) {
  return "$auth/connect/authorize?client_id=sample&response_type=code&scope=openid&code_challenge=$Challenge" +
    "&code_challenge_method=S256&state=smoke&redirect_uri=$([Uri]::EscapeDataString($redirectUri))"
}

function Get-Code([string]$BackToApp) {
  return [regex]::Match($BackToApp, '[?&]code=([^&]*)').Groups[1].Value
}

# The app exchanges the code (with its client secret and the PKCE verifier) for tokens. The client secret stays in
# this process: from podman's standard output into the request body, never on a command line.
function Invoke-CodeExchange([string]$Code, [string]$Verifier) {
  $form = [ordered]@{
    'grant_type' = 'authorization_code'
    'client_id' = 'sample'
    'client_secret' = Read-PodmanSecret 'kale_sample_client_secret'
    'code' = $Code
    'redirect_uri' = $redirectUri
    'code_verifier' = $Verifier
  }
  return (Invoke-Http $anonymous "$auth/connect/token" -Method 'POST' -Form $form).Body
}

# The access token from a token response, or empty (like jq -r '.access_token // empty').
function Get-AccessToken([string]$Response) {
  try {
    return [string](ConvertFrom-Json $Response).access_token
  }
  catch {
    return ''
  }
}

function Get-NotesStatus([string]$Token) {
  return (Invoke-Http $anonymous "$sample/notes" -Headers @{ Authorization = "Bearer $Token" }).Status
}

function Get-WriteStatus([string]$Token) {
  return (Invoke-Http $anonymous "$sample/notes" -Method 'POST' -Json '{"text":"smoke"}' -Headers @{ Authorization = "Bearer $Token" }).Status
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
  $envSettings = Read-Env 6>$null
  $adminUser = $envSettings.ADMIN_USER

  $auth = "http://$($envSettings.AUTHSERVICE_HOST):$($envSettings.AUTHSERVICE_PORT)"
  $sample = "http://localhost:$($envSettings.SAMPLEAPP_PORT)"
  $redirectUri = "http://localhost:$($envSettings.SAMPLEAPP_PORT)/signin-oidc"
  $runId = New-RandomHex 4
  $userName = "smoke-$runId"
  $password = "Smoke-test-$(New-RandomHex 8)"
  $role = "Reader-$runId"
  $anonymous = New-Browser -WithoutCookies

  # 1. A user with a role holding only sample.NoteRead. SampleApp registers its operations at startup, so wait
  #    until AuthService knows them.
  if (-not (Invoke-KaleAdmin @('create-user', $userName, 'Smoke Test') "$password`n")) { Exit-AdminFailed 'create user' }
  if (-not (Invoke-KaleAdmin @('create-role', 'sample', $role))) { Exit-AdminFailed 'create role' }
  $granted = $false
  for ($i = 0; $i -lt 30; $i++) {
    if (Invoke-KaleAdmin @('set-role-operations', 'sample', $role, 'sample.NoteRead')) {
      $granted = $true
      break
    }
    Start-Sleep -Seconds 1
  }
  if (-not $granted) { Exit-AdminFailed 'give the role sample.NoteRead (has SampleApp registered its operations?)' }
  if (-not (Invoke-KaleAdmin @('assign-role', $userName, 'sample', $role))) { Exit-AdminFailed 'assign the role' }
  Write-Pass "user $userName with role $role (sample.NoteRead)"

  # 2. Sign in to the sample like a browser, with authorization code + PKCE.
  $verifier = New-RandomHex 32
  $backToApp = Invoke-SignIn (New-Browser) (Get-AuthorizeUrl (Get-CodeChallenge $verifier)) $userName $password
  $code = Get-Code $backToApp
  if (-not $code) { Exit-Fail "no authorization code (got: $backToApp)" }
  Write-Pass 'signed in through the login page'

  # 3. The app exchanges the code for tokens.
  $response = Invoke-CodeExchange $code $verifier
  $token = Get-AccessToken $response
  if (-not $token) { Exit-Fail 'no access token' $response }
  Write-Pass 'access token for audience sample'

  # 4. SampleApp: reading is allowed, writing isn't; no token is unauthenticated.
  if ((Invoke-Http $anonymous "$sample/notes").Status -ne 401) { Exit-Fail 'GET /notes without a token should be 401' }
  Write-Pass 'GET /notes without a token: 401'
  if ((Get-NotesStatus $token) -ne 200) { Exit-Fail 'GET /notes should be 200' }
  Write-Pass 'GET /notes with sample.NoteRead: 200'
  if ((Get-WriteStatus $token) -ne 403) { Exit-Fail 'POST /notes should be 403 without sample.NoteWrite' }
  Write-Pass 'POST /notes without sample.NoteWrite: 403'

  # 5. An admin adds sample.NoteWrite to the role. SampleApp caches role operations for 5 minutes, so a 201
  #    within seconds means the "role edited" event arrived through RabbitMQ.
  if (-not (Invoke-KaleAdmin @('set-role-operations', 'sample', $role, 'sample.NoteRead', 'sample.NoteWrite'))) { Exit-Fail 'edit the role' }
  $edited = $false
  for ($i = 0; $i -lt 20; $i++) {
    if ((Get-WriteStatus $token) -eq 201) {
      $edited = $true
      break
    }
    Start-Sleep -Milliseconds 500
  }
  if (-not $edited) { Exit-Fail "the role edit didn't reach SampleApp within 10 s" }
  Write-Pass 'POST /notes right after the role edit: 201 (event delivered)'

  # 5b. The same user signs in again from another browser (fresh cookies): one session per user and application,
  #     so the first session ends and SampleApp refuses its still-valid token within seconds.
  $secondVerifier = New-RandomHex 32
  $secondBack = Invoke-SignIn (New-Browser) (Get-AuthorizeUrl (Get-CodeChallenge $secondVerifier)) $userName $password
  $secondCode = Get-Code $secondBack
  if (-not $secondCode) { Exit-Fail "no authorization code for the second sign-in (got: $secondBack)" }
  $secondResponse = Invoke-CodeExchange $secondCode $secondVerifier
  $secondToken = Get-AccessToken $secondResponse
  if (-not $secondToken) { Exit-Fail 'no access token for the second sign-in' $secondResponse }
  $refused = $false
  for ($i = 0; $i -lt 20; $i++) {
    if ((Get-NotesStatus $token) -eq 403) {
      $refused = $true
      break
    }
    Start-Sleep -Milliseconds 500
  }
  if (-not $refused) { Exit-Fail "the first session's token should be refused within 10 s of signing in again" }
  if ((Get-NotesStatus $secondToken) -ne 200) { Exit-Fail 'the new session''s token should work' }
  Write-Pass 'signing in again ends the earlier session: its token is refused (403), the new one works'

  # 6. The admin UI: the first admin signs in through it (it keeps the tokens on its server) and sees their name.
  #    Fresh browsers: this one isn't signed in to AuthService yet.
  $ui = "http://localhost:$($envSettings.ADMIN_UI_PORT)"
  $uiBrowser = New-Browser
  $adminAuthBrowser = New-Browser
  try {
    $toAuthService = (Invoke-Http $uiBrowser "$ui/login").Location
  }
  catch {
    Exit-Fail "the admin UI doesn't answer at $ui"
  }
  $adminPassword = Read-PodmanSecret 'kale_admin_password'
  $backToUi = Invoke-SignIn $adminAuthBrowser $toAuthService $adminUser $adminPassword
  if (-not (Test-StartsWith $backToUi "$ui/signin-oidc?")) { Exit-Fail "AuthService should send the admin back to the admin UI (got: $backToUi)" }
  Invoke-Http $uiBrowser $backToUi | Out-Null
  $homePage = (Invoke-Http $uiBrowser "$ui/").Body
  if (-not $homePage.Contains("($adminUser)")) { Exit-Fail 'the admin UI home page should show the signed-in admin' (Get-Start $homePage) }
  Write-Pass "signed in to the admin UI as $adminUser"
  # The lists are grids of 10 rows a page, and every run (and every browser-test run) adds applications, roles and
  # users, so this run's ones needn't be on a list's first page. Their ids come from the database (read only), and
  # their own pages are checked. The pages are in Turkish here (no Accept-Language is sent): the checks look at
  # markup, except where a text is asked for in English.
  function Invoke-Sql([string]$Query) {
    $result = Invoke-Podman @('exec', 'kale-postgres', 'psql', '-U', $envSettings.POSTGRES_USER, '-d', $envSettings.POSTGRES_DB, '-tAc', $Query)
    return $result.Output.Trim()
  }
  $sampleId = Invoke-Sql "select id from applications where key = 'sample'"
  $roleId = Invoke-Sql "select r.id from roles r join applications a on a.id = r.application_id where a.key = 'sample' and r.name = '$role'"
  $userId = Invoke-Sql "select id from users where user_name = '$userName'"
  if (-not $sampleId -or -not $roleId -or -not $userId) { Exit-Fail "the database should know sample, $role and $userName" }
  $applications = (Invoke-Http $uiBrowser "$ui/applications").Body
  # Sorted by key, auth is always on the first page.
  if (-not $applications.Contains('>auth</code>')) {
    Exit-Fail 'the admin UI''s applications page should list auth' (Get-Start $applications)
  }
  Write-Pass 'the admin UI lists the applications'
  $rolesPage = (Invoke-Http $uiBrowser "$ui/applications/$sampleId/roles").Body
  if (-not $rolesPage.Contains('>sample.NoteWrite</code>')) {
    Exit-Fail 'the admin UI should show sample''s roles and operations' (Get-Start $rolesPage)
  }
  $rolePage = (Invoke-Http $uiBrowser "$ui/applications/$sampleId/roles/$roleId").Body
  if (-not ($rolePage.Contains(">$role</h1>") -and $rolePage.Contains('>sample.NoteWrite</code>'))) {
    Exit-Fail "the admin UI should show $role with the operation step 4 gave it (sample.NoteWrite)" (Get-Start $rolePage)
  }
  Write-Pass 'the admin UI shows sample''s roles and a role''s operations'
  $usersPage = (Invoke-Http $uiBrowser "$ui/users").Body
  if (-not $usersPage.Contains(">$adminUser</a>")) {
    Exit-Fail "the admin UI's users page should list $adminUser" (Get-Start $usersPage)
  }
  $userPage = (Invoke-Http $uiBrowser "$ui/users/$userId").Body
  # The access row, and the direct role after its application's key (not just an <option> of the default-role select;
  # React puts an empty comment between the two texts).
  $directRole = '>sample</code> (<!-- -->)?' + [regex]::Escape($role) + '<'
  if (-not ($userPage.Contains('<li data-application="sample"') -and $userPage -cmatch $directRole)) {
    Exit-Fail "the admin UI should show $userName's access to sample and role $role" (Get-Start $userPage)
  }
  Write-Pass 'the admin UI shows a user''s access and roles'
  $groupsPage = (Invoke-Http $uiBrowser "$ui/groups" -Headers @{ 'Accept-Language' = 'en' }).Body
  if (-not $groupsPage.Contains('New group</button>')) {
    Exit-Fail 'the admin UI''s groups page should load with its new-group button' (Get-Start $groupsPage)
  }
  Write-Pass 'the admin UI shows the groups'
  # The user from step 1 is still signed in to sample (step 2).
  $sessionsPage = (Invoke-Http $uiBrowser "$ui/sessions?userId=$userId").Body
  $sessionRow = '>sample</code></td><td[^>]*>' + [regex]::Escape($role) + '</td>'
  if (-not ($sessionsPage -cmatch $sessionRow)) {
    Exit-Fail "the admin UI should list $userName's open session in sample" (Get-Start $sessionsPage)
  }
  Write-Pass 'the admin UI lists a user''s open sessions'
  if ((Get-StatusAndLocation $anonymous "$ui/") -ne "307 $ui/login") {
    Exit-Fail 'the admin UI must send a browser without a session to /login'
  }
  Write-Pass 'the admin UI sends a browser without a session to sign in'

  # Another site can't sign the admin out (its form post carries its own Origin).
  $crossSite = (Invoke-Http $uiBrowser "$ui/logout" -Method 'POST' -Headers @{ Origin = 'https://evil.example' }).Status
  if ($crossSite -ne 403) { Exit-Fail "a sign-out posted by another site should be refused (got: $crossSite)" }
  if ((Invoke-Http $uiBrowser "$ui/").Status -ne 200) { Exit-Fail 'the refused sign-out must leave the admin signed in' }
  Write-Pass 'another site can''t sign the admin out'

  # Signing out ends the session here and at AuthService.
  $toEndSession = (Invoke-Http $uiBrowser "$ui/logout" -Method 'POST' -Headers @{ Origin = $ui }).Location
  if (-not (Test-StartsWith $toEndSession "$auth/connect/endsession?")) {
    Exit-Fail "signing out should go on to AuthService's end-session (got: $toEndSession)"
  }
  $signedOut = (Invoke-Http $adminAuthBrowser $toEndSession).Location
  if (-not (Test-StartsWith $signedOut "$ui/signed-out")) {
    Exit-Fail "AuthService should return to the admin UI's signed-out page (got: $signedOut)"
  }
  if ((Get-StatusAndLocation $uiBrowser "$ui/") -ne "307 $ui/login") {
    Exit-Fail 'after signing out the admin UI must ask to sign in again'
  }
  Write-Pass 'signing out ends the admin UI session and AuthService''s'

  # 7. Someone without auth.Admin (the user from step 1) can't get into the admin UI.
  $otherUiBrowser = New-Browser
  $toAuthService = (Invoke-Http $otherUiBrowser "$ui/login").Location
  $backToUi = Invoke-SignIn (New-Browser) $toAuthService $userName $password
  $landing = (Invoke-Http $otherUiBrowser $backToUi).Location
  if ($landing -ne "$ui/not-admin") { Exit-Fail "a user without auth.Admin should land on the not-allowed page (got: $landing)" }
  if ((Get-StatusAndLocation $otherUiBrowser "$ui/") -ne "307 $ui/login") {
    Exit-Fail 'a user without auth.Admin must not get a session'
  }
  Write-Pass 'a user without auth.Admin is refused by the admin UI'

  # 8. With access to "auth" but still without auth.Admin: the sign-in completes at AuthService, so the admin UI
  #    ends that new AuthService session at once and explains on its signed-out page.
  if (-not (Invoke-KaleAdmin @('create-role', 'auth', "Viewer-$runId"))) { Exit-AdminFailed 'create a role of auth without auth.Admin' }
  if (-not (Invoke-KaleAdmin @('assign-role', $userName, 'auth', "Viewer-$runId"))) { Exit-AdminFailed 'give the user that role' }
  $viewerUiBrowser = New-Browser
  $viewerAuthBrowser = New-Browser
  $toAuthService = (Invoke-Http $viewerUiBrowser "$ui/login").Location
  $backToUi = Invoke-SignIn $viewerAuthBrowser $toAuthService $userName $password
  $toEndSession = (Invoke-Http $viewerUiBrowser $backToUi).Location
  if (-not (Test-StartsWith $toEndSession "$auth/connect/endsession?")) {
    Exit-Fail "a non-admin's AuthService session should be ended (got: $toEndSession)"
  }
  $landing = (Invoke-Http $viewerAuthBrowser $toEndSession).Location
  if (-not ((Test-StartsWith $landing "$ui/signed-out") -and $landing.Contains('state=not-admin'))) {
    Exit-Fail "the non-admin should land on the signed-out page with the reason (got: $landing)"
  }
  if ((Get-StatusAndLocation $viewerUiBrowser "$ui/") -ne "307 $ui/login") {
    Exit-Fail 'a non-admin must not get a session'
  }
  Write-Pass 'a user with access to auth but without auth.Admin is signed out again and told why'
}
finally {
  Pop-Location
}
