#!/usr/bin/env bash
# The whole local stack in one command: PostgreSQL, RabbitMQ, AuthService (with its database migrated) and
# SampleApp, built from this checkout. Safe to run again: every step skips what already exists.
set -euo pipefail

cd "$(dirname "$0")/.."

# Waits (up to 2 minutes) for a container's healthcheck to pass.
wait_healthy() {
  local container="$1"
  printf "Waiting for %s" "$container"
  for _ in $(seq 1 120); do
    if [[ "$(podman container inspect --format '{{.State.Health.Status}}' "$container" 2>/dev/null)" == "healthy" ]]; then
      echo " ready"
      return
    fi
    printf "."
    sleep 1
  done
  echo " not healthy; see: podman logs $container" >&2
  exit 1
}

# 1. Settings come from .env, created from .env.example on first run and completed with keys added to it since.
source scripts/env.sh
load_env

# 2. Random passwords as Podman secrets, created once. Hex, so they never need escaping. PostgreSQL reads its
#    password only when the volume is first created: if you delete that secret, delete the volume too
#    (podman compose down -v).
for secret in kale_postgres_password kale_rabbitmq_password; do
  if ! podman secret exists "$secret"; then
    openssl rand -hex 24 | tr -d '\n' | podman secret create "$secret" - >/dev/null
    echo "Created Podman secret $secret"
  fi
done

# 3. Images from this checkout.
podman compose build

# 4. AuthService, with what it depends on: PostgreSQL, RabbitMQ, and the one-shot migration.
podman compose up -d authservice
wait_healthy kale-authservice

# 5. The sample's application in AuthService, and its client secret as a Podman secret. The secret is printed
#    only when the application is created, so creating is tried every time: it succeeds after the database was
#    reset (podman compose down -v), and then the stale secret is replaced.
errors="$(mktemp)"
trap 'rm -f "$errors"' EXIT
if client_secret="$(podman exec kale-authservice dotnet AuthService.dll admin create-application \
    sample "Sample App" "http://localhost:${SAMPLEAPP_PORT}/signin-oidc" 2>"$errors")"; then
  # A container still using the old secret has to go before the secret can be replaced.
  podman rm -f kale-sampleapp >/dev/null 2>&1 || true
  podman secret rm kale_sample_client_secret >/dev/null 2>&1 || true
  printf '%s' "$client_secret" | podman secret create kale_sample_client_secret - >/dev/null
  echo "Created application 'sample' and Podman secret kale_sample_client_secret"
elif ! grep -q "already exists" "$errors"; then
  cat "$errors" >&2
  exit 1
elif ! podman secret exists kale_sample_client_secret; then
  echo "The application 'sample' exists but its secret is gone. Start over with:" >&2
  echo "  podman compose down -v && ./scripts/dev-stack.sh" >&2
  exit 1
fi

# 6. The sample app, which registers its operations with AuthService on startup.
podman compose up -d sampleapp
wait_healthy kale-sampleapp

echo
echo "AuthService: http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}/  (sign-in pages, tokens)"
echo "SampleApp:   http://localhost:${SAMPLEAPP_PORT}/notes"
echo "Check it end to end: ./scripts/smoke-test.sh"
