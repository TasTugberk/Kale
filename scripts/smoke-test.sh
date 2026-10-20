#!/usr/bin/env bash
# End-to-end check of the running stack (scripts/dev-stack.sh): a user signs in through AuthService's real
# pages (authorization code + PKCE), and SampleApp allows or refuses their requests by role. Then an admin
# edits the role, and SampleApp follows within seconds (invalidation event through RabbitMQ).
# Creates a new user and role on every run; touches nothing else.
set -euo pipefail

cd "$(dirname "$0")/.."
set -a
source .env
set +a

AUTH="http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}"
SAMPLE="http://localhost:${SAMPLEAPP_PORT}"
REDIRECT_URI="http://localhost:${SAMPLEAPP_PORT}/signin-oidc"
RUN_ID="$(openssl rand -hex 4)"
USER_NAME="smoke-${RUN_ID}"
PASSWORD="Smoke-test-$(openssl rand -hex 8)"
ROLE="Reader-${RUN_ID}"
COOKIES="$(mktemp)"
trap 'rm -f "$COOKIES"' EXIT

admin() { podman exec -i kale-authservice dotnet AuthService.dll admin "$@" 2>/dev/null; }
pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1" >&2; exit 1; }

# 1. A user with a role holding only sample.NoteRead. SampleApp registers its operations at startup, so wait
#    until AuthService knows them.
echo "$PASSWORD" | admin create-user "$USER_NAME" "Smoke Test" || fail "create user"
admin create-role sample "$ROLE" || fail "create role"
for _ in $(seq 1 30); do
  admin set-role-operations sample "$ROLE" sample.NoteRead && break
  sleep 1
done || true
admin set-role-operations sample "$ROLE" sample.NoteRead || fail "give the role sample.NoteRead"
admin assign-role "$USER_NAME" sample "$ROLE" || fail "assign the role"
pass "user $USER_NAME with role $ROLE (sample.NoteRead)"

# 2. Sign in like a browser: authorize -> login page -> post the form -> redirect back with a code.
verifier="$(openssl rand -hex 32)"
challenge="$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')"
authorize="${AUTH}/connect/authorize?client_id=sample&response_type=code&scope=openid&code_challenge=${challenge}&code_challenge_method=S256&state=smoke&redirect_uri=$(printf '%s' "$REDIRECT_URI" | jq -sRr @uri)"

location() { curl -s -o /dev/null -w '%{redirect_url}' -b "$COOKIES" -c "$COOKIES" "$@"; }
login_url="$(location "$authorize")"
[[ "$login_url" == *"/account/login"* ]] || fail "authorize should send an unknown browser to the login page (got: $login_url)"
login_page="$(curl -s -b "$COOKIES" -c "$COOKIES" "$login_url")"
field() { printf '%s' "$login_page" | grep -o "name=\"$1\"[^>]*value=\"[^\"]*\"" | head -1 | sed 's/.*value="\([^"]*\)"/\1/' | sed 's/&amp;/\&/g'; }
after_login="$(location "$login_url" \
  --data-urlencode "__RequestVerificationToken=$(field __RequestVerificationToken)" \
  --data-urlencode "ReturnUrl=$(field ReturnUrl)" \
  --data-urlencode "UserName=$USER_NAME" \
  --data-urlencode "Password=$PASSWORD")"
[[ -n "$after_login" ]] || fail "the login form was not accepted"
[[ "$after_login" == http* ]] || after_login="${AUTH}${after_login}"
back_to_app="$(location "$after_login")"
code="$(printf '%s' "$back_to_app" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')"
[[ -n "$code" ]] || fail "no authorization code (got: $back_to_app)"
pass "signed in through the login page"

# 3. The app exchanges the code (with its client secret and the PKCE verifier) for tokens.
client_secret="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_sample_client_secret)"
token="$(curl -s "${AUTH}/connect/token" \
  --data-urlencode grant_type=authorization_code --data-urlencode client_id=sample \
  --data-urlencode "client_secret=${client_secret}" --data-urlencode "code=${code}" \
  --data-urlencode "redirect_uri=${REDIRECT_URI}" --data-urlencode "code_verifier=${verifier}" | jq -r .access_token)"
[[ -n "$token" && "$token" != null ]] || fail "no access token"
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
for _ in $(seq 1 20); do
  [[ "$(write)" == 201 ]] && { pass "POST /notes right after the role edit: 201 (event delivered)"; exit 0; }
  sleep 0.5
done
fail "the role edit didn't reach SampleApp within 10 s"
