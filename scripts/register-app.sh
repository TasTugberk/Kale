#!/usr/bin/env bash
# Connects an application to the local Kale (scripts/dev-stack.sh) in one step: registers it in Kale and writes
# every setting builder.AddKaleAuth<T>() needs into the .NET user secrets of each given project.
# Local development only: the settings use plain http and Kale's dev-only gRPC port.
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: scripts/register-app.sh <key> [--web <address>] [--desktop] [--new-secret] <project folder>...

  <key>             the application's key in Kale, e.g. orders (lowercase letters, digits, -; permanent)
  --web <address>   the address of its web front end, e.g. http://localhost:5199: Kale may then send users back
                    to <address>/signin-oidc and <address>/signout-callback-oidc. Leave it out for an API only.
  --desktop         also let desktop apps (WPF) sign users in to it: client id <key>.desktop, no secret
                    (docs/integration, "Desktop (WPF) applications")
  --new-secret      the key is already registered: give it a new client secret and write it into the given
                    projects. The old one keeps working for a while (Kale's Applications:OldSecretValidFor,
                    7 days by default), so other copies still set up with it don't fail; retire it sooner in
                    the admin UI (Applications, then its key) or with: AuthService admin retire-old-secret <key>
  <project folder>  one or more .NET projects that share this key (e.g. its API and its web front end)
EOF
  exit 2
}

fail() {
  echo "$1" >&2
  exit 1
}

[[ $# -ge 2 ]] || usage
KEY="$1"
shift
WEB=""
NEW_SECRET=false
DESKTOP=false
PROJECTS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --web) [[ $# -ge 2 ]] || usage; WEB="${2%/}"; shift 2 ;;
    --desktop) DESKTOP=true; shift ;;
    --new-secret) NEW_SECRET=true; shift ;;
    -*) usage ;;
    *) PROJECTS+=("$1"); shift ;;
  esac
done
[[ ${#PROJECTS[@]} -gt 0 ]] || usage

# Everything that can be refused is checked before Kale is asked: once Kale has made a secret, it's shown only
# this once.
case "$KEY" in
  auth | sample) fail "'$KEY' belongs to the local stack itself (the admin UI, SampleApp); choose another key." ;;
esac
[[ "$NEW_SECRET" == false || -z "$WEB" ]] \
  || fail "--web works only when registering. To change an application's return addresses, use the admin UI: Applications, then its key."
command -v jq >/dev/null || fail "This script needs jq (e.g. brew install jq)."
command -v dotnet >/dev/null || fail "This script needs the .NET SDK (dotnet)."

KALE="$(cd "$(dirname "$0")/.." && pwd)"
cd "$KALE"
source scripts/env.sh
load_env >/dev/null
[[ "$(podman container inspect --format '{{.State.Running}}' kale-authservice 2>/dev/null)" == true ]] \
  || fail "Kale isn't running: start it with ./scripts/dev-stack.sh"

for project in "${PROJECTS[@]}"; do
  [[ -n "$(find "$project" -maxdepth 1 -name '*.csproj' 2>/dev/null)" ]] || fail "No .NET project (*.csproj) in '$project'."
  # Adds a UserSecretsId to the project once (it says so when there already is one). Done now, so a project that
  # can't take user secrets is found before the secret exists.
  dotnet user-secrets init --project "$project" >/dev/null || fail "Couldn't set up user secrets in '$project'."
done

# Kale's own messages go to a file; a failure shows them.
ERRORS="$(mktemp)"
trap 'rm -f "$ERRORS"' EXIT
admin() { podman exec -i kale-authservice dotnet AuthService.dll admin "$@" 2>"$ERRORS"; }
refused() {
  echo "$1" >&2
  tail -3 "$ERRORS" >&2
  if grep -q "already exists" "$ERRORS"; then
    echo "To replace its secret and write it into these projects, add --new-secret." >&2
  fi
  exit 1
}

# Only the secret comes back on standard output; it stays in this variable and goes to the projects on stdin.
if [[ "$NEW_SECRET" == true ]]; then
  secret="$(admin rotate-secret "$KEY")" || refused "Couldn't replace the secret of '$KEY':"
  # Kale's note on standard error: until when the old secret still works.
  cat "$ERRORS" >&2
elif [[ -n "$WEB" ]]; then
  secret="$(admin create-application "$KEY" "$KEY" "$WEB/signin-oidc" "$WEB/signout-callback-oidc")" || refused "Couldn't register '$KEY':"
else
  secret="$(admin create-application "$KEY" "$KEY")" || refused "Couldn't register '$KEY':"
fi

# The addresses the application uses to reach the local Kale: the issuer (the same for browsers and apps), and
# the dev-only gRPC port published on APP_BIND_ADDRESS.
settings="$(jq -n \
  --arg authority "http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}/" \
  --arg grpc "http://${APP_BIND_ADDRESS}:${AUTHSERVICE_GRPC_PORT}/" \
  --arg key "$KEY" \
  --arg secret "$secret" \
  '{"Kale:Authority": $authority, "Kale:GrpcAddress": $grpc, "Kale:AllowInsecureHttpDevOnly": "true",
    "Kale:ApplicationKey": $key, "Kale:ClientSecret": $secret}')"
unset secret

for project in "${PROJECTS[@]}"; do
  printf '%s' "$settings" | dotnet user-secrets set --project "$project" >/dev/null
  echo "Wrote Kale's settings into the user secrets of $project"
done

# Only after the secret is safely in the projects (Kale shows it once). A failure here doesn't stop the script:
# the registration worked, and desktop sign-in can be turned on later (admin UI: Applications, then its key).
if [[ "$DESKTOP" == true ]]; then
  if admin desktop-sign-in "$KEY" on; then
    echo "Desktop apps sign in to '$KEY' as the client '$KEY.desktop' (no secret)."
  else
    echo "Couldn't turn on desktop sign-in for '$KEY'; turn it on in the admin UI (Applications, then '$KEY'):" >&2
    tail -3 "$ERRORS" >&2
  fi
fi

cat <<EOF

'$KEY' is registered in Kale. In each project:
    builder.AddKaleAuth<YourOperations>();     // Program.cs, before builder.Build()
    [Authorize(YourOperations.Something)]      // on each endpoint to protect
Start it with \`dotnet run\` (Development). Then give people access in the admin UI
(http://localhost:${ADMIN_UI_PORT}/): a role with your operations, and access to '$KEY'.
EOF
