#!/usr/bin/env bash
# Connects an application to the local Kale (scripts/dev-stack.sh) in one step: registers it in Kale and writes
# every setting builder.AddKaleAuth<T>() needs into the .NET user secrets of each given project.
#
#   scripts/register-app.sh <key> [--web <address>] [--new-secret] <project folder>...
#
#   <key>             the application's key in Kale, e.g. orders (lowercase letters, digits, -; permanent)
#   --web <address>   the address of its web front end, e.g. http://localhost:5199: Kale may then send users back
#                     to <address>/signin-oidc and <address>/signout-callback-oidc. Leave it out for an API only.
#   --new-secret      the key is already registered: replace its client secret (the old one stops working) and
#                     write the new one into the given projects
#   <project folder>  one or more .NET projects that share this key (e.g. its API and its web front end)
#
# Local development only: the settings use plain http and Kale's dev-only gRPC port.
set -euo pipefail

usage() {
  sed -n '5,13p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

[[ $# -ge 2 ]] || usage
KEY="$1"
shift
WEB=""
NEW_SECRET=false
PROJECTS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --web) [[ $# -ge 2 ]] || usage; WEB="${2%/}"; shift 2 ;;
    --new-secret) NEW_SECRET=true; shift ;;
    -*) usage ;;
    *) PROJECTS+=("$1"); shift ;;
  esac
done
[[ ${#PROJECTS[@]} -gt 0 ]] || usage
for project in "${PROJECTS[@]}"; do
  [[ -n "$(find "$project" -maxdepth 1 -name '*.csproj' 2>/dev/null)" ]] || { echo "No .NET project (*.csproj) in '$project'." >&2; exit 1; }
done

KALE="$(cd "$(dirname "$0")/.." && pwd)"
cd "$KALE"
source scripts/env.sh
load_env >/dev/null
podman container exists kale-authservice || { echo "Kale isn't running: start it with ./scripts/dev-stack.sh" >&2; exit 1; }

# Kale's own messages go to a file; a failure shows them.
ERRORS="$(mktemp)"
trap 'rm -f "$ERRORS"' EXIT
admin() { podman exec -i kale-authservice dotnet AuthService.dll admin "$@" 2>"$ERRORS"; }

# Only the secret comes back on standard output; it stays in this variable and goes to the projects on stdin.
if [[ "$NEW_SECRET" == true ]]; then
  secret="$(admin rotate-secret "$KEY")" || { echo "Couldn't replace the secret of '$KEY':" >&2; tail -3 "$ERRORS" >&2; exit 1; }
elif [[ -n "$WEB" ]]; then
  secret="$(admin create-application "$KEY" "$KEY" "$WEB/signin-oidc" "$WEB/signout-callback-oidc")" \
    || { echo "Couldn't register '$KEY':" >&2; tail -3 "$ERRORS" >&2; echo "If it's already registered, add --new-secret." >&2; exit 1; }
else
  secret="$(admin create-application "$KEY" "$KEY")" \
    || { echo "Couldn't register '$KEY':" >&2; tail -3 "$ERRORS" >&2; echo "If it's already registered, add --new-secret." >&2; exit 1; }
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
  # init adds a UserSecretsId to the project once (it says so when there already is one).
  dotnet user-secrets init --project "$project" >/dev/null
  printf '%s' "$settings" | dotnet user-secrets set --project "$project" >/dev/null
  echo "Wrote Kale's settings into the user secrets of $project"
done

cat <<EOF

'$KEY' is registered in Kale. In each project:
    builder.AddKaleAuth<YourOperations>();     // Program.cs, before builder.Build()
    [Authorize(YourOperations.Something)]      // on each endpoint to protect
Start it with \`dotnet run\` (Development). Then give people access in the admin UI
(http://localhost:${ADMIN_UI_PORT}/): a role with your operations, and access to '$KEY'.
EOF
