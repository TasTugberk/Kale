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

# The first admin's password must also meet AuthService's password rules (an uppercase letter, a symbol).
if ! podman secret exists kale_admin_password; then
  printf 'Kale-%s' "$(openssl rand -hex 16)" | podman secret create kale_admin_password - >/dev/null
  echo "Created Podman secret kale_admin_password"
fi

# 3. Images from this checkout.
podman compose build

# 4. PostgreSQL and RabbitMQ, then the migration and AuthService from the images just built. podman-compose
#    doesn't notice a rebuilt image (the container's settings didn't change), so the app containers are
#    recreated on every run; the migration runs in the foreground, and a failure stops here.
podman compose up -d postgres rabbitmq
wait_healthy kale-postgres
wait_healthy kale-rabbitmq
podman compose run --rm migrate
podman compose up -d --force-recreate --no-deps authservice
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

# 6. The first admin and the admin UI's application ("auth"), once: `admin bootstrap` prints the UI's client
#    secret only then. After a database reset it succeeds again and the stale secret is replaced.
if admin_ui_secret="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password \
    | podman exec -i kale-authservice dotnet AuthService.dll admin bootstrap "${ADMIN_USER}" "Kale Admin" \
      "http://localhost:${ADMIN_UI_PORT}/signin-oidc" "http://localhost:${ADMIN_UI_PORT}/signed-out" 2>"$errors")"; then
  podman rm -f kale-admin-ui >/dev/null 2>&1 || true
  podman secret rm kale_admin_ui_client_secret >/dev/null 2>&1 || true
  printf '%s' "$admin_ui_secret" | podman secret create kale_admin_ui_client_secret - >/dev/null
  echo "Created the first admin '${ADMIN_USER}' and Podman secret kale_admin_ui_client_secret"
elif ! grep -q "already exists" "$errors"; then
  cat "$errors" >&2
  exit 1
fi

# 6. The sample app, which registers its operations with AuthService on startup.
podman compose up -d --force-recreate --no-deps sampleapp
wait_healthy kale-sampleapp

# 7. The admin UI.
podman compose up -d --force-recreate --no-deps admin-ui
wait_healthy kale-admin-ui

echo
echo "AuthService: http://${AUTHSERVICE_HOST}:${AUTHSERVICE_PORT}/  (sign-in pages, tokens)"
echo "SampleApp:   http://localhost:${SAMPLEAPP_PORT}/notes"
echo "Admin UI:    http://localhost:${ADMIN_UI_PORT}/  (user ${ADMIN_USER}; password: podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password)"
echo "Check it end to end: ./scripts/smoke-test.sh"
