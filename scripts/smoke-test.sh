#!/usr/bin/env bash
# End-to-end check of the running stack (scripts/dev-stack.sh): a user signs in through AuthService's real
# pages (authorization code + PKCE), and SampleApp allows or refuses their requests by role. Then an admin
# edits the role, and SampleApp follows within seconds (invalidation event through RabbitMQ). Finally the first
# admin signs in to the admin UI.
# Creates a new user and role on every run; touches nothing else.
set -euo pipefail

cd "$(dirname "$0")/.."
source scripts/env.sh
load_env >/dev/null

AUTH="http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}"
SAMPLE="http://localhost:${SAMPLEAPP_PORT}"
REDIRECT_URI="http://localhost:${SAMPLEAPP_PORT}/signin-oidc"
RUN_ID="$(openssl rand -hex 4)"
USER_NAME="smoke-${RUN_ID}"
PASSWORD="Smoke-test-$(openssl rand -hex 8)"
ROLE="Reader-${RUN_ID}"
COOKIES="$(mktemp)"
ADMIN_ERRORS="$(mktemp)"
trap 'rm -f "$COOKIES" "$ADMIN_ERRORS"' EXIT

# The admin command's own messages (and logs) go to a file; a failure shows them.
admin() { podman exec -i kale-authservice dotnet AuthService.dll admin "$@" 2>"$ADMIN_ERRORS"; }
pass() { echo "PASS  $1"; }
fail() {
  echo "FAIL  $1" >&2
  if [[ -n "${2:-}" ]]; then echo "$2" >&2; fi
  exit 1
}
admin_failed() { fail "$1" "$(tail -5 "$ADMIN_ERRORS")"; }

# 1. A user with a role holding only sample.NoteRead. SampleApp registers its operations at startup, so wait
#    until AuthService knows them.
echo "$PASSWORD" | admin create-user "$USER_NAME" "Smoke Test" || admin_failed "create user"
admin create-role sample "$ROLE" || admin_failed "create role"
granted=false
for _ in $(seq 1 30); do
  if admin set-role-operations sample "$ROLE" sample.NoteRead; then
    granted=true
    break
  fi
  sleep 1
done
[[ "$granted" == true ]] || admin_failed "give the role sample.NoteRead (has SampleApp registered its operations?)"
admin assign-role "$USER_NAME" sample "$ROLE" || admin_failed "assign the role"
pass "user $USER_NAME with role $ROLE (sample.NoteRead)"

# Signs in through AuthService's real pages like a browser, with its own cookie jar: authorize -> login page
# -> post the form. Prints where AuthService sends the browser back (the client's address, with the code).
sign_in() {
  local authorize="$1" user="$2" password="$3" jar="$4"
  location() { curl -s -o /dev/null -w '%{redirect_url}' -b "$jar" -c "$jar" "$@"; }
  local login_url login_page after_login
  login_url="$(location "$authorize")"
  [[ "$login_url" == *"/account/login"* ]] || fail "authorize should send an unknown browser to the login page (got: $login_url)"
  login_page="$(curl -s -b "$jar" -c "$jar" "$login_url")"
  field() { printf '%s' "$login_page" | grep -o "name=\"$1\"[^>]*value=\"[^\"]*\"" | head -1 | sed 's/.*value="\([^"]*\)"/\1/' | sed 's/&amp;/\&/g'; }
  after_login="$(location "$login_url" \
    --data-urlencode "__RequestVerificationToken=$(field __RequestVerificationToken)" \
    --data-urlencode "ReturnUrl=$(field ReturnUrl)" \
    --data-urlencode "UserName=$user" \
    --data-urlencode "Password=$password")"
  [[ -n "$after_login" ]] || fail "the login form was not accepted for $user"
  [[ "$after_login" == http* ]] || after_login="${AUTH}${after_login}"
  location "$after_login"
}

# 2. Sign in to the sample like a browser, with authorization code + PKCE.
verifier="$(openssl rand -hex 32)"
challenge="$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')"
authorize="${AUTH}/connect/authorize?client_id=sample&response_type=code&scope=openid&code_challenge=${challenge}&code_challenge_method=S256&state=smoke&redirect_uri=$(printf '%s' "$REDIRECT_URI" | jq -sRr @uri)"
back_to_app="$(sign_in "$authorize" "$USER_NAME" "$PASSWORD" "$COOKIES")"
code="$(printf '%s' "$back_to_app" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')"
[[ -n "$code" ]] || fail "no authorization code (got: $back_to_app)"
pass "signed in through the login page"

# 3. The app exchanges the code (with its client secret and the PKCE verifier) for tokens.
# The client secret goes to curl on stdin (@-), not on its command line, where other processes could see it.
# (Without the newline podman prints after it, which curl would send as part of the secret.)
response="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_sample_client_secret | tr -d '\n' | curl -s "${AUTH}/connect/token" \
  --data-urlencode grant_type=authorization_code --data-urlencode client_id=sample \
  --data-urlencode "client_secret@-" --data-urlencode "code=${code}" \
  --data-urlencode "redirect_uri=${REDIRECT_URI}" --data-urlencode "code_verifier=${verifier}")"
token="$(printf '%s' "$response" | jq -r '.access_token // empty' 2>/dev/null || true)"
[[ -n "$token" ]] || fail "no access token" "$response"
pass "access token for audience sample"

# 4. SampleApp: reading is allowed, writing isn't; no token is unauthenticated.
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
[[ "$(status "${SAMPLE}/notes")" == 401 ]] || fail "GET /notes without a token should be 401"
pass "GET /notes without a token: 401"
[[ "$(status -H "Authorization: Bearer $token" "${SAMPLE}/notes")" == 200 ]] || fail "GET /notes should be 200"
pass "GET /notes with sample.NoteRead: 200"
write() { status -H "Authorization: Bearer $token" -H 'Content-Type: application/json' -d '{"text":"smoke"}' "${SAMPLE}/notes"; }
[[ "$(write)" == 403 ]] || fail "POST /notes should be 403 without sample.NoteWrite"
pass "POST /notes without sample.NoteWrite: 403"

# 5. An admin adds sample.NoteWrite to the role. SampleApp caches role operations for 5 minutes, so a 201
#    within seconds means the "role edited" event arrived through RabbitMQ.
admin set-role-operations sample "$ROLE" sample.NoteRead sample.NoteWrite || fail "edit the role"
edited=false
for _ in $(seq 1 20); do
  if [[ "$(write)" == 201 ]]; then
    edited=true
    break
  fi
  sleep 0.5
done
[[ "$edited" == true ]] || fail "the role edit didn't reach SampleApp within 10 s"
pass "POST /notes right after the role edit: 201 (event delivered)"

# 6. The admin UI: the first admin signs in through it (it keeps the tokens on its server) and sees their name.
#    Fresh cookie jars: this browser isn't signed in to AuthService yet.
UI="http://localhost:${ADMIN_UI_PORT}"
UI_COOKIES="$(mktemp)"
ADMIN_AUTH_COOKIES="$(mktemp)"
trap 'rm -f "$COOKIES" "$ADMIN_ERRORS" "$UI_COOKIES" "$ADMIN_AUTH_COOKIES"' EXIT
to_authservice="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$UI_COOKIES" -c "$UI_COOKIES" "${UI}/login")" \
  || fail "the admin UI doesn't answer at ${UI}"
admin_password="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password)"
back_to_ui="$(sign_in "$to_authservice" "$ADMIN_USER" "$admin_password" "$ADMIN_AUTH_COOKIES")"
[[ "$back_to_ui" == "${UI}/signin-oidc?"* ]] || fail "AuthService should send the admin back to the admin UI (got: $back_to_ui)"
curl -s -o /dev/null -b "$UI_COOKIES" -c "$UI_COOKIES" "$back_to_ui"
home="$(curl -s -b "$UI_COOKIES" "${UI}/")"
[[ "$home" == *"(${ADMIN_USER})"* ]] || fail "the admin UI home page should show the signed-in admin" "$(printf '%s' "$home" | head -c 300)"
pass "signed in to the admin UI as ${ADMIN_USER}"
applications="$(curl -s -b "$UI_COOKIES" "${UI}/applications")"
[[ "$applications" == *"<code>sample</code>"* && "$applications" == *"<code>auth</code>"* ]] \
  || fail "the admin UI's applications page should list sample and auth" "$(printf '%s' "$applications" | head -c 300)"
pass "the admin UI lists the applications"
# (|| true: without a link, fail below says so; set -e would stop here silently.)
sample_roles="$(printf '%s' "$applications" | sed 's/<tr/\n<tr/g' | grep '<code>sample</code>' | grep -o '/applications/[^"]*/roles' | head -1 || true)"
[[ -n "$sample_roles" ]] || fail "the applications page should link to sample's roles"
roles_page="$(curl -s -b "$UI_COOKIES" "${UI}${sample_roles}")"
[[ "$roles_page" == *"<h3>${ROLE}</h3>"* && "$roles_page" == *"sample.NoteWrite"* ]] \
  || fail "the admin UI should show sample's roles, with ${ROLE} (from ${UI}${sample_roles})" "$(printf '%s' "$roles_page" | head -c 300)"
pass "the admin UI shows sample's roles"
users_page="$(curl -s -b "$UI_COOKIES" "${UI}/users")"
user_link="$(printf '%s' "$users_page" | grep -o "href=\"/users/[^\"]*\">${USER_NAME}<" | sed 's/href="\([^"]*\)".*/\1/' || true)"
[[ -n "$user_link" ]] || fail "the admin UI's users page should list ${USER_NAME}" "$(printf '%s' "$users_page" | head -c 300)"
user_page="$(curl -s -b "$UI_COOKIES" "${UI}${user_link}")"
# The access row holds its form; the direct-roles row names the role (not just an <option> of a select).
[[ "$user_page" == *"<td><code>sample</code></td><td><form"* && "$user_page" == *"<td><code>sample</code></td><td>${ROLE}</td>"* ]] \
  || fail "the admin UI should show ${USER_NAME}'s access to sample and role ${ROLE}" "$(printf '%s' "$user_page" | grep -o '<table.*</table>' | head -c 1500)"
pass "the admin UI shows a user's access and roles"
[[ "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "${UI}/")" == "307 ${UI}/login" ]] \
  || fail "the admin UI must send a browser without a session to /login"
pass "the admin UI sends a browser without a session to sign in"

# Another site can't sign the admin out (its form post carries its own Origin).
cross_site="$(curl -s -o /dev/null -w '%{http_code}' -b "$UI_COOKIES" -X POST -H 'Origin: https://evil.example' "${UI}/logout")"
[[ "$cross_site" == 403 ]] || fail "a sign-out posted by another site should be refused (got: $cross_site)"
[[ "$(curl -s -o /dev/null -w '%{http_code}' -b "$UI_COOKIES" "${UI}/")" == 200 ]] || fail "the refused sign-out must leave the admin signed in"
pass "another site can't sign the admin out"

# Signing out ends the session here and at AuthService.
to_end_session="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$UI_COOKIES" -c "$UI_COOKIES" -X POST -H "Origin: ${UI}" "${UI}/logout")"
[[ "$to_end_session" == "${AUTH}/connect/endsession?"* ]] || fail "signing out should go on to AuthService's end-session (got: $to_end_session)"
signed_out="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$ADMIN_AUTH_COOKIES" -c "$ADMIN_AUTH_COOKIES" "$to_end_session")"
[[ "$signed_out" == "${UI}/signed-out"* ]] || fail "AuthService should return to the admin UI's signed-out page (got: $signed_out)"
[[ "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -b "$UI_COOKIES" "${UI}/")" == "307 ${UI}/login" ]] \
  || fail "after signing out the admin UI must ask to sign in again"
pass "signing out ends the admin UI session and AuthService's"

# 7. Someone without auth.Admin (the user from step 1) can't get into the admin UI.
OTHER_UI_COOKIES="$(mktemp)"
OTHER_AUTH_COOKIES="$(mktemp)"
trap 'rm -f "$COOKIES" "$ADMIN_ERRORS" "$UI_COOKIES" "$ADMIN_AUTH_COOKIES" "$OTHER_UI_COOKIES" "$OTHER_AUTH_COOKIES"' EXIT
to_authservice="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$OTHER_UI_COOKIES" -c "$OTHER_UI_COOKIES" "${UI}/login")"
back_to_ui="$(sign_in "$to_authservice" "$USER_NAME" "$PASSWORD" "$OTHER_AUTH_COOKIES")"
landing="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$OTHER_UI_COOKIES" -c "$OTHER_UI_COOKIES" "$back_to_ui")"
[[ "$landing" == "${UI}/not-admin" ]] || fail "a user without auth.Admin should land on the not-allowed page (got: $landing)"
[[ "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -b "$OTHER_UI_COOKIES" "${UI}/")" == "307 ${UI}/login" ]] \
  || fail "a user without auth.Admin must not get a session"
pass "a user without auth.Admin is refused by the admin UI"

# 8. With access to "auth" but still without auth.Admin: the sign-in completes at AuthService, so the admin UI
#    ends that new AuthService session at once and explains on its signed-out page.
admin create-role auth "Viewer-${RUN_ID}" || admin_failed "create a role of auth without auth.Admin"
admin assign-role "$USER_NAME" auth "Viewer-${RUN_ID}" || admin_failed "give the user that role"
VIEWER_UI_COOKIES="$(mktemp)"
VIEWER_AUTH_COOKIES="$(mktemp)"
trap 'rm -f "$COOKIES" "$ADMIN_ERRORS" "$UI_COOKIES" "$ADMIN_AUTH_COOKIES" "$OTHER_UI_COOKIES" "$OTHER_AUTH_COOKIES" "$VIEWER_UI_COOKIES" "$VIEWER_AUTH_COOKIES"' EXIT
to_authservice="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$VIEWER_UI_COOKIES" -c "$VIEWER_UI_COOKIES" "${UI}/login")"
back_to_ui="$(sign_in "$to_authservice" "$USER_NAME" "$PASSWORD" "$VIEWER_AUTH_COOKIES")"
to_end_session="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$VIEWER_UI_COOKIES" -c "$VIEWER_UI_COOKIES" "$back_to_ui")"
[[ "$to_end_session" == "${AUTH}/connect/endsession?"* ]] || fail "a non-admin's AuthService session should be ended (got: $to_end_session)"
landing="$(curl -s -o /dev/null -w '%{redirect_url}' -b "$VIEWER_AUTH_COOKIES" -c "$VIEWER_AUTH_COOKIES" "$to_end_session")"
[[ "$landing" == "${UI}/signed-out"*"state=not-admin"* ]] || fail "the non-admin should land on the signed-out page with the reason (got: $landing)"
[[ "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -b "$VIEWER_UI_COOKIES" "${UI}/")" == "307 ${UI}/login" ]] \
  || fail "a non-admin must not get a session"
pass "a user with access to auth but without auth.Admin is signed out again and told why"
