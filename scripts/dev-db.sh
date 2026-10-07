#!/usr/bin/env bash
# Local database in one command: starts PostgreSQL from compose.yaml, points AuthService at it,
# and applies the EF Core migrations. Safe to run again: every step skips what already exists.
set -euo pipefail

cd "$(dirname "$0")/.."

SECRET_NAME=kale_postgres_password
CONTAINER_NAME=kale-postgres

# 1. Settings (names, host, port) come from .env, created from .env.example on first run.
if [[ ! -f .env ]]; then
  cp .env.example .env
  echo "Created .env from .env.example"
fi
set -a
source .env
set +a

# 2. The database password is a random Podman secret, created once. Hex, so it never needs escaping.
#    PostgreSQL only reads it when the volume is first created. If you ever delete the secret, also delete
#    the volume (podman compose down -v), or the new password won't match the database.
if ! podman secret exists "$SECRET_NAME"; then
  openssl rand -hex 24 | tr -d '\n' | podman secret create "$SECRET_NAME" - >/dev/null
  echo "Created Podman secret $SECRET_NAME"
fi

# 3. Start PostgreSQL and wait for its healthcheck (up to 60 s).
podman compose up -d postgres
printf "Waiting for PostgreSQL"
for _ in $(seq 1 60); do
  if [[ "$(podman container inspect --format '{{.State.Health.Status}}' "$CONTAINER_NAME")" == "healthy" ]]; then
    break
  fi
  printf "."
  sleep 1
done
if [[ "$(podman container inspect --format '{{.State.Health.Status}}' "$CONTAINER_NAME")" != "healthy" ]]; then
  echo " PostgreSQL did not become healthy; see: podman logs $CONTAINER_NAME" >&2
  exit 1
fi
echo " ready"

# 4. Give AuthService the connection string through .NET user secrets: stored in your home folder,
#    never in the repo, and only loaded when running in Development.
PASSWORD="$(podman secret inspect --showsecret --format '{{.SecretData}}' "$SECRET_NAME")"
CONNECTION_STRING="Host=$POSTGRES_HOST;Port=$POSTGRES_PORT;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$PASSWORD"
dotnet user-secrets set "ConnectionStrings:AuthDb" "$CONNECTION_STRING" --project src/AuthService >/dev/null
echo "Set ConnectionStrings:AuthDb in AuthService user secrets"

# 5. Apply migrations. The design-time factory has no real database, so pass the connection explicitly.
dotnet ef database update --project src/AuthService --connection "$CONNECTION_STRING"
