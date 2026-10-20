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

# 1. Settings come from .env, created from .env.example on first run. Keys added to .env.example since then
#    are appended with their defaults, so an older .env keeps working.
if [[ ! -f .env ]]; then
  cp .env.example .env
  echo "Created .env from .env.example"
fi
while IFS= read -r line; do
  key="${line%%=*}"
  if [[ "$line" =~ ^[A-Z_]+= ]] && ! grep -q "^${key}=" .env; then
    echo "$line" >> .env
    echo "Added $key to .env"
  fi
done < .env.example
set -a
source .env
set +a

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

# 5. The sample's application in AuthService and its client secret, once. The secret is printed only at
#    creation, so it goes straight into a Podman secret.
if ! podman secret exists kale_sample_client_secret; then
  if ! client_secret="$(podman exec kale-authservice dotnet AuthService.dll admin create-application \
      sample "Sample App" "http://localhost:${SAMPLEAPP_PORT}/signin-oidc")"; then
    echo "Couldn't create the application 'sample'. If it exists but its secret is gone, start over with:" >&2
    echo "  podman compose down -v && ./scripts/dev-stack.sh" >&2
    exit 1
  fi
  printf '%s' "$client_secret" | podman secret create kale_sample_client_secret - >/dev/null
  echo "Created application 'sample' and Podman secret kale_sample_client_secret"
fi

# 6. The sample app, which registers its operations with AuthService on startup.
podman compose up -d sampleapp
wait_healthy kale-sampleapp

echo
echo "AuthService: http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}/  (sign-in pages, tokens)"
echo "SampleApp:   http://localhost:${SAMPLEAPP_PORT}/notes"
echo "Check it end to end: ./scripts/smoke-test.sh"
