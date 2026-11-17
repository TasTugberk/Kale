#!/usr/bin/env bash
# Sets up and starts the production-like example (compose.production-example.yaml) on this machine: the steps of
# docs/production/guide.en.md in one go, with example certificates. Safe to run again: every step skips what
# already exists. Check it end to end with: ./scripts/smoke-test.sh deploy/.env
# Stop it with: (cd deploy && podman compose -f compose.production-example.yaml down)
set -euo pipefail

cd "$(dirname "$0")"
source ../scripts/env.sh
load_env_file .env .env.production.example

compose() { podman compose -f compose.production-example.yaml "$@"; }

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

# A Podman secret from a file, once.
secret_from_file() {
  if ! podman secret exists "$1"; then
    podman secret create "$1" "$2" >/dev/null
    echo "Created Podman secret $1"
  fi
}

# 1. Certificates (example CA, TLS for the proxy and AuthService, Data Protection), then every file as a secret.
./example-certificates.sh
secret_from_file kale_prod_ca_cert certs/ca.crt
secret_from_file kale_prod_proxy_tls_cert certs/proxy.crt
secret_from_file kale_prod_proxy_tls_key certs/proxy.key
secret_from_file kale_prod_authservice_tls_cert certs/authservice.crt
secret_from_file kale_prod_authservice_tls_key certs/authservice.key
secret_from_file kale_prod_dataprotection_cert certs/dataprotection.crt
secret_from_file kale_prod_dataprotection_key certs/dataprotection.key

# 2. Passwords: random, straight into Podman secrets (never in a file or a variable).
if ! podman secret exists kale_prod_postgres_password; then
  openssl rand -hex 24 | tr -d '\n' | podman secret create kale_prod_postgres_password - >/dev/null
  echo "Created Podman secret kale_prod_postgres_password"
fi
if ! podman secret exists kale_prod_admin_password; then
  printf 'Kale-%s' "$(openssl rand -hex 16)" | podman secret create kale_prod_admin_password - >/dev/null
  echo "Created Podman secret kale_prod_admin_password"
fi

# 3. Images from this checkout, tagged as in deploy/.env. (A real server pulls released images instead.)
(
  cd ..
  podman build -q -f src/AuthService/Containerfile -t "$AUTHSERVICE_IMAGE" . >/dev/null
  podman build -q -f admin-ui/Containerfile -t "$ADMIN_UI_IMAGE" . >/dev/null
  podman build -q -f samples/SampleApp/Containerfile -t "$SAMPLEAPP_IMAGE" . >/dev/null
)
echo "Built $AUTHSERVICE_IMAGE, $ADMIN_UI_IMAGE, $SAMPLEAPP_IMAGE"

# 4. PostgreSQL, the migration (in the foreground: a failure stops here), AuthService and the proxy.
compose up -d postgres
wait_healthy kale-prod-postgres
compose run --rm migrate
compose up -d --force-recreate --no-deps authservice
wait_healthy kale-prod-authservice
compose up -d --force-recreate --no-deps proxy

# 5. The first admin and the admin UI's client ("auth"), once: the client secret is printed only then.
errors="$(mktemp)"
trap 'rm -f "$errors"' EXIT
if admin_ui_secret="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password \
    | podman exec -i kale-prod-authservice dotnet AuthService.dll admin bootstrap "$ADMIN_USER" "Kale Admin" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signin-oidc" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signed-out" 2>"$errors")"; then
  podman rm -f kale-prod-admin-ui >/dev/null 2>&1 || true
  podman secret rm kale_prod_admin_ui_client_secret >/dev/null 2>&1 || true
  printf '%s' "$admin_ui_secret" | podman secret create kale_prod_admin_ui_client_secret - >/dev/null
  echo "Created the first admin '$ADMIN_USER' and Podman secret kale_prod_admin_ui_client_secret"
elif ! grep -q "already exists" "$errors"; then
  cat "$errors" >&2
  exit 1
fi

# 6. The sample application (stands for one of yours), once.
if sample_secret="$(podman exec kale-prod-authservice dotnet AuthService.dll admin create-application \
    sample "Sample App" "https://${SAMPLE_HOST}:${PUBLIC_HTTPS_PORT}/signin-oidc" 2>"$errors")"; then
  podman rm -f kale-prod-sampleapp >/dev/null 2>&1 || true
  podman secret rm kale_prod_sample_client_secret >/dev/null 2>&1 || true
  printf '%s' "$sample_secret" | podman secret create kale_prod_sample_client_secret - >/dev/null
  echo "Created application 'sample' and Podman secret kale_prod_sample_client_secret"
elif ! grep -q "already exists" "$errors"; then
  cat "$errors" >&2
  exit 1
fi

# 7. The admin UI and the sample application.
compose up -d --force-recreate --no-deps admin-ui sampleapp
wait_healthy kale-prod-admin-ui
wait_healthy kale-prod-sampleapp

echo
echo "AuthService: https://${AUTH_HOST}:${PUBLIC_HTTPS_PORT}/.well-known/openid-configuration"
echo "Admin UI:    https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/  (user ${ADMIN_USER}; password: podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password)"
echo "Trust deploy/certs/ca.crt in your browser (or OS) first, or it will warn about the certificate."
echo "Check it end to end: ./scripts/smoke-test.sh deploy/.env"
