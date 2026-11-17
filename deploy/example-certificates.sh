#!/usr/bin/env bash
# Certificates for the production-like example (deploy/compose.production-example.yaml), written to deploy/certs/
# (git-ignored). Skips every file that already exists. See docs/production/guide.en.md, section 4.
#
#   ca.crt / ca.key                     An example certificate authority. A real server gets its TLS certificates
#                                       from your organization's CA (or a public one) instead.
#   proxy.crt / proxy.key               TLS for the reverse proxy: the public names of AuthService, the admin UI
#                                       and the sample application.
#   authservice.crt / authservice.key   TLS for AuthService's own Kestrel (HTTP and gRPC), for its public name.
#   dataprotection.crt / .key           Encrypts AuthService's Data Protection key ring. Self-signed on purpose,
#                                       in production too: it's only used for encryption, never shown to anyone.
#
# Plain openssl commands that work with OpenSSL 3 (Linux, Git Bash on Windows) and LibreSSL (macOS).
set -euo pipefail

cd "$(dirname "$0")"
source ../scripts/env.sh
# The host names come from deploy/.env (created from .env.production.example if missing).
load_env_file .env .env.production.example

mkdir -p certs
cd certs

# The example CA (5 years). Its key signs the two server certificates below and is needed for nothing else.
if [[ ! -f ca.crt ]]; then
  printf '%s\n' \
    "basicConstraints = critical, CA:TRUE" \
    "keyUsage = critical, keyCertSign, cRLSign" \
    "subjectKeyIdentifier = hash" > ca.ext
  openssl req -new -newkey rsa:3072 -nodes -keyout ca.key -subj "/CN=Kale example CA" -out ca.csr 2>/dev/null
  openssl x509 -req -in ca.csr -signkey ca.key -days 1825 -sha256 -extfile ca.ext -out ca.crt 2>/dev/null
  rm ca.csr ca.ext
  echo "Created certs/ca.crt"
fi

# A server certificate signed by the example CA (397 days, the most browsers accept), for the given DNS names.
server_certificate() {
  local name="$1"
  shift
  [[ -f "$name.crt" ]] && return
  local alt_names
  alt_names="$(printf 'DNS:%s,' "$@")"
  printf '%s\n' \
    "basicConstraints = critical, CA:FALSE" \
    "keyUsage = critical, digitalSignature, keyEncipherment" \
    "extendedKeyUsage = serverAuth" \
    "subjectKeyIdentifier = hash" \
    "authorityKeyIdentifier = keyid" \
    "subjectAltName = ${alt_names%,}" > "$name.ext"
  openssl req -new -newkey rsa:3072 -nodes -keyout "$name.key" -subj "/CN=$1" -out "$name.csr" 2>/dev/null
  openssl x509 -req -in "$name.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -days 397 -sha256 \
    -extfile "$name.ext" -out "$name.crt" 2>/dev/null
  rm "$name.csr" "$name.ext"
  echo "Created certs/$name.crt"
}

server_certificate proxy "$AUTH_HOST" "$ADMIN_UI_HOST" "$SAMPLE_HOST"
server_certificate authservice "$AUTH_HOST"

# The Data Protection certificate: self-signed, RSA, 10 years. AuthService accepts only this one certificate for
# decrypting its key ring, so replacing it later isn't supported yet: give it a long life and back it up.
if [[ ! -f dataprotection.crt ]]; then
  openssl req -x509 -newkey rsa:3072 -nodes -keyout dataprotection.key -subj "/CN=Kale Data Protection" \
    -days 3650 -sha256 -out dataprotection.crt 2>/dev/null
  echo "Created certs/dataprotection.crt"
fi
