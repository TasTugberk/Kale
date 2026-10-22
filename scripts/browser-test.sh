#!/usr/bin/env bash
# The admin UI in a real browser (Playwright, Chromium) against the running stack (scripts/dev-stack.sh).
# Once per machine: (cd admin-ui && npm ci && npx playwright install chromium).
# Creates two applications and a user on every run; touches nothing else. Traces are off: they would record
# the admin's password (see admin-ui/playwright.config.ts).
set -euo pipefail

cd "$(dirname "$0")/.."
source scripts/env.sh
load_env >/dev/null

export KALE_E2E_UI_URL="http://localhost:${ADMIN_UI_PORT}"
export KALE_E2E_ADMIN_USER="$ADMIN_USER"
# The first admin's password, from its Podman secret (only for this test run's own processes).
KALE_E2E_ADMIN_PASSWORD="$(podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password | tr -d '\n')"
export KALE_E2E_ADMIN_PASSWORD

cd admin-ui
npx playwright test "$@"
