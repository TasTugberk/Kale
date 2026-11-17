# Installing Kale in production

**English** · [Türkçe](guide.tr.md)

This guide is for the person who installs and runs Kale on a server for the first time. It takes you from an empty Linux server to a working AuthService, admin UI and first registered application, and then covers the day-to-day work: backups, upgrades, health checks and logs.

Everything here runs in containers with **rootless Podman**, from the repository's own `Containerfile`s. Where Windows (Podman machine) differs, a short **Windows** note says so.

> **Tried for real.** The steps below were run on one machine with a production-like setup: `ASPNETCORE_ENVIRONMENT=Production`, a self-signed CA with server certificates, a Data Protection certificate, TLS on Kestrel for HTTP and gRPC, a reverse proxy in front, every secret a Podman secret, and an application (SampleApp) using `Kale.Auth.Client` over TLS. That setup lives in [`deploy/`](../../deploy) and can be started with one command (section 2). The few things that weren't tried are marked **(not tried)**.

## Contents

1. [What you'll end up with](#1-what-youll-end-up-with)
2. [Try it on one machine first](#2-try-it-on-one-machine-first)
3. [Before you start](#3-before-you-start)
4. [Images](#4-images)
5. [Settings file](#5-settings-file)
6. [Certificates](#6-certificates)
7. [Secrets](#7-secrets)
8. [PostgreSQL](#8-postgresql)
9. [AuthService's settings](#9-authservices-settings)
10. [The reverse proxy](#10-the-reverse-proxy)
11. [First start: database, migration, AuthService](#11-first-start-database-migration-authservice)
12. [The first admin and the admin UI](#12-the-first-admin-and-the-admin-ui)
13. [Check that it works](#13-check-that-it-works)
14. [Registering applications, and what to give their teams](#14-registering-applications-and-what-to-give-their-teams)
15. [Keys: nothing to do](#15-keys-nothing-to-do)
16. [Backups and restore](#16-backups-and-restore)
17. [Upgrades and certificate renewal](#17-upgrades-and-certificate-renewal)
18. [Starting after a reboot](#18-starting-after-a-reboot)
19. [Health checks and logs](#19-health-checks-and-logs)
20. [Troubleshooting](#20-troubleshooting)
21. [Windows notes](#21-windows-notes)

## 1. What you'll end up with

```
 browsers, applications
        │ https (443)
        ▼
 ┌────────────────┐  https, re-encrypted   ┌──────────────────────────────────────┐
 │ reverse proxy  │ ─────────────────────▶ │ AuthService  (Kestrel TLS on 8443:    │
 │ (Caddy here)   │                        │  sign-in pages, tokens, admin API,    │
 │                │                        │  gRPC for applications)               │
 │                │  http (internal)       └───────────────┬──────────────────────┘
 │                │ ─────────▶ admin UI (Next.js, :8083)    │
 └────────────────┘                                        ▼
                                                     PostgreSQL
```

- **AuthService** (one or more instances): sign-in pages, tokens (OpenID Connect), the admin API (`/admin/api`) and the gRPC API that applications call. It serves **only HTTPS**, on one port, for both HTTP/1.1 and gRPC (HTTP/2).
- **Admin UI** (exactly **one** instance: it keeps admins' sessions in memory): where admins manage applications, users, roles and groups.
- **PostgreSQL**: everything, including the token signing keys (encrypted).
- **A reverse proxy** with the public certificate. It sends AuthService's traffic on **over TLS again** (re-encrypted), because AuthService doesn't read `X-Forwarded-*` headers yet ([issue #72](https://github.com/TasTugberk/Kale/issues/72)): a proxy that forwards plain HTTP to it doesn't work (section 10).

Applications run elsewhere. They reach AuthService at its public address, over HTTPS for tokens and gRPC.

## 2. Try it on one machine first

[`deploy/`](../../deploy) holds the production-like setup this guide was tested with. Before installing a server, run it once on your own machine (it uses its own names, port 9443 and volumes, so it doesn't disturb the dev stack):

```bash
podman machine start                 # macOS and Windows only, once after each reboot
./deploy/production-example.sh       # example certificates, secrets, images, then the whole stack
./scripts/smoke-test.sh deploy/.env  # end to end over https
```

What the smoke test printed on the test run:

```
PASS  user smoke-e98a9b19 with role Reader-e98a9b19 (sample.NoteRead)
PASS  signed in through the login page
PASS  access token for audience sample
PASS  GET /notes without a token: 401
PASS  GET /notes with sample.NoteRead: 200
PASS  POST /notes without sample.NoteWrite: 403
PASS  POST /notes right after the role edit: 201 (event delivered over the stream)
PASS  signing in again ends the earlier session: its token is refused (401), the new one works
PASS  signed in to the admin UI as admin
...
PASS  a user with access to auth but without auth.Admin is signed out again and told why
```

Open `https://admin.kale.localhost:9443/` in a browser after trusting `deploy/certs/ca.crt` (section 21 shows how on Windows; on macOS: `security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db deploy/certs/ca.crt`). The admin's password: `podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password`.

Stop it with `(cd deploy && podman compose -f compose.production-example.yaml down)`. Add `-v` to delete its database too.

The rest of this guide is the same setup, step by step, for a real server. `deploy/production-example.sh` runs the same commands; read it alongside.

## 3. Before you start

You need:

- **A Linux server** with Podman (the test used Podman 6.1) and podman-compose 1.6 (`podman compose` calls it). A user account to run the containers as (not root).
- **DNS names**, e.g. `auth.example.com` for AuthService and `kale-admin.example.com` for the admin UI. The AuthService name becomes the **issuer**: every token carries it, so choose it once and keep it.
- **TLS certificates** for those names, from your organization's CA or a public one (section 6).
- **PostgreSQL**: your own container (as in the example) or a managed one (section 8).
- `openssl` and `jq` on the machine you run the commands from.

Copy the `deploy/` folder to the server (for example to `~/kale/deploy`). On the server you'll work in that folder.

**Low ports with rootless Podman.** A rootless user can't publish ports below 1024 by default. To publish 443 directly, allow it once (as root):

```bash
echo 'net.ipv4.ip_unprivileged_port_start=443' | sudo tee /etc/sysctl.d/90-kale-ports.conf
sudo sysctl --system
```

**(not tried)**: the test machine used port 9443.

## 4. Images

Kale has three images: AuthService, the admin UI, and (for testing only) SampleApp. Build them from a release tag of the repository, from its root:

```bash
podman build -f src/AuthService/Containerfile -t localhost/kale-authservice:1.0.0 .
podman build -f admin-ui/Containerfile        -t localhost/kale-admin-ui:1.0.0 .
podman build -f samples/SampleApp/Containerfile -t localhost/kale-sampleapp:1.0.0 .   # only to test, section 13
```

Use the version (`<VersionPrefix>` in `Directory.Build.props`) as the tag, so an upgrade is a new tag. If you build on another machine, either push the images to your registry, or move them as files (`podman save -o kale-authservice.tar localhost/kale-authservice:1.0.0`, then `podman load -i kale-authservice.tar` on the server). Build for the server's processor: an image built on an Apple Silicon Mac is `arm64`; for an `x86_64` server add `--platform linux/amd64`.

## 5. Settings file

Every value the compose file uses comes from `deploy/.env` (compose reads it from the compose file's folder; it's git-ignored). Create it from the example and edit it:

```bash
cd ~/kale/deploy
cp .env.production.example .env
```

| Key | Example (test) | On a server |
|---|---|---|
| `AUTHSERVICE_IMAGE`, `ADMIN_UI_IMAGE` | `localhost/kale-authservice:1.0.0` | Your image names and the version |
| `POSTGRES_IMAGE`, `PROXY_IMAGE` | `postgres:18-alpine`, `caddy:2-alpine` | Same |
| `POSTGRES_DB`, `POSTGRES_USER` | `kale_auth`, `kale` | Same |
| `AUTH_HOST` | `auth.kale.localhost` | `auth.example.com` |
| `ADMIN_UI_HOST` | `admin.kale.localhost` | `kale-admin.example.com` |
| `SAMPLE_HOST` | `sample.kale.localhost` | Remove SampleApp on a server (section 13) |
| `PUBLIC_HTTPS_PORT` | `9443` | `443` |
| `PUBLIC_BIND_ADDRESS` | `127.0.0.1` | `0.0.0.0` |
| `ADMIN_USER` | `admin` | The first admin's user name |
| `ADMIN_UI_TIME_ZONE` | `Europe/Istanbul` | An IANA time zone name; empty means UTC |

Keep plain `KEY=VALUE` lines, without quotes or spaces. No passwords or secrets in this file: they are Podman secrets (section 7).

## 6. Certificates

Kale needs three kinds of certificates. All are PEM files (`.crt` + `.key`, the key unencrypted: it's protected as a Podman secret).

| Certificate | For | Where it comes from |
|---|---|---|
| **Proxy** | The public names (`auth.example.com`, `kale-admin.example.com`) | Your CA or a public CA |
| **AuthService** | Kestrel's own TLS, for AuthService's public name (`auth.example.com`). The proxy connects to it and checks this name | Your CA (can be the same certificate as the proxy's) |
| **Data Protection** | Encrypts AuthService's key ring in the database | **Self-signed**, made by you: it's only used for encryption, nobody else sees it |

The Data Protection certificate (RSA; 10 years, since replacing it isn't supported yet: see section 16):

```bash
mkdir -p certs
openssl req -x509 -newkey rsa:3072 -nodes -keyout certs/dataprotection.key \
  -subj "/CN=Kale Data Protection" -days 3650 -sha256 -out certs/dataprotection.crt
```

**For a test without a real CA**, `deploy/example-certificates.sh` makes an example CA and the two server certificates for the names in `.env` (the `openssl` commands are in the script). Then every client has to trust that CA (section 14). With certificates from a public CA, nothing has to be trusted by hand.

Requirements for the server certificates: the DNS names in **Subject Alternative Name**, `extendedKeyUsage = serverAuth`. Put the full chain (server certificate, then intermediates) in the `.crt` file.

## 7. Secrets

Passwords, client secrets and private keys reach the containers **only as files**: Podman secrets, mounted under `/run/secrets/<name>`. Never in `.env`, in the compose file, or in an environment variable.

```bash
# Certificates and keys (the CA certificate isn't secret, but is mounted the same way)
podman secret create kale_prod_proxy_tls_cert        certs/proxy.crt
podman secret create kale_prod_proxy_tls_key         certs/proxy.key
podman secret create kale_prod_authservice_tls_cert  certs/authservice.crt
podman secret create kale_prod_authservice_tls_key   certs/authservice.key
podman secret create kale_prod_dataprotection_cert   certs/dataprotection.crt
podman secret create kale_prod_dataprotection_key    certs/dataprotection.key
podman secret create kale_prod_ca_cert               certs/ca.crt   # only with your own CA; see below

# Passwords: random, straight into Podman (never written to a file)
openssl rand -hex 24 | tr -d '\n' | podman secret create kale_prod_postgres_password -
printf 'Kale-%s' "$(openssl rand -hex 16)" | podman secret create kale_prod_admin_password -
```

The admin's password must meet AuthService's rules (at least 12 characters, an uppercase letter, a symbol); the command above does. Read it later with `podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password`, and change it after the first sign-in if you want a password a person can remember.

After creating the secrets, **store the private keys and the Data Protection certificate somewhere safe** (a password manager or a vault) and delete `certs/*.key` from the server: Podman keeps its own copy. The Data Protection certificate and key are part of every backup (section 16).

If you don't use your own CA, remove `kale_prod_ca_cert` from the compose file, with `NODE_EXTRA_CA_CERTS` and `SSL_CERT_FILE` (section 9), and delete the `tls_trust_pool` line from the Caddyfile (Caddy then trusts the system's CAs).

## 8. PostgreSQL

**Your own container** (as in `compose.production-example.yaml`): PostgreSQL 18, not published to the network, its password from `kale_prod_postgres_password`, data in the `postgres-data` volume. Nothing more to do.

**A managed or existing PostgreSQL:** remove the `postgres` service and its `depends_on` entries, and set the connection string of `migrate` and `authservice`, for example:

```yaml
ConnectionStrings__AuthDb: Host=db.example.com;Port=5432;Database=kale_auth;Username=kale;SSL Mode=VerifyFull
AuthDb__PasswordFile: /run/secrets/kale_prod_postgres_password
```

- The password never goes into the connection string; `AuthDb:PasswordFile` adds it.
- The user owns the `kale_auth` database (migrations create and change tables). Create an empty database for it.
- **Direct connection, or a session-pooling proxy only.** Each AuthService instance keeps one connection open with `LISTEN` for invalidation events. A transaction-pooling proxy (PgBouncer in transaction mode, many managed "pooler" endpoints) breaks it: applications then don't learn about stopped sessions or edited roles in time. Use the database's direct address.
- `Gss Encryption Mode=Disable` in the example only silences a log line (the Alpine images have no Kerberos library); keep it unless you use Kerberos.

**(not tried)** with a managed database: the test used its own container.

## 9. AuthService's settings

Outside Development, AuthService refuses to start without its production settings, and refuses every development-only switch. These are all of them, as environment variables (`__` stands for `:`) in `compose.production-example.yaml`:

| Setting | Value | Why |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Turns every guard on. Anything but `Development`/`Testing` does |
| `Auth__Issuer` | `https://auth.example.com/` (with the final `/`) | The issuer in every token. Required |
| `ConnectionStrings__AuthDb` + `AuthDb__PasswordFile` | Section 8 | |
| `DataProtection__CertificatePath` + `DataProtection__PrivateKeyPath` | `/run/secrets/kale_prod_dataprotection_cert` / `_key` | Required: without them startup fails |
| `ASPNETCORE_HTTP_PORTS` | empty | No plain-HTTP listener at all |
| `Kestrel__Endpoints__Https__Url` | `https://+:8443` | One TLS endpoint for HTTP/1.1 and gRPC (HTTP/2 is negotiated by TLS) |
| `Kestrel__Endpoints__Https__Certificate__Path` + `__KeyPath` | `/run/secrets/kale_prod_authservice_tls_cert` / `_key` | Kestrel's certificate |

**Must not be set** (each one stops startup outside Development/Testing): `OpenIddict__AllowInsecureHttpDevOnly`, `Grpc__AllowInsecureDevOnly`, and any `Kestrel__Endpoints__*__Protocols=Http2` on an `http://` endpoint (or `Kestrel__EndpointDefaults__Protocols=Http2`). Settings left from the RabbitMQ version (`RabbitMq__*`) are ignored; remove them.

The admin UI's settings:

| Setting | Value |
|---|---|
| `KALE_ENVIRONMENT` | `Production` |
| `KALE_AUTH_ISSUER` | The same as `Auth__Issuer` |
| `KALE_ADMIN_UI_URL` | `https://kale-admin.example.com/`: where browsers reach it (must be https) |
| `KALE_ADMIN_CLIENT_SECRET_FILE` | `/run/secrets/kale_prod_admin_ui_client_secret` (section 12) |
| `KALE_TIME_ZONE` | An IANA name such as `Europe/Istanbul`; empty means UTC. An unknown one stops it at startup |
| `NODE_EXTRA_CA_CERTS` | Only with your own CA: its certificate, so Node.js trusts AuthService |

Not set: `KALE_ALLOW_INSECURE_HTTP_DEV_ONLY` (refused outside Development/Testing). The test-token page and grant don't exist in Production.

## 10. The reverse proxy

The example uses Caddy ([`deploy/Caddyfile`](../../deploy/Caddyfile)); any proxy works if it does what this section says.

**For AuthService** (`auth.example.com`):

- **Re-encrypt** to `https://authservice:8443`, checking AuthService's certificate for its public name (Caddy: `tls_server_name`). **Don't** forward plain HTTP: AuthService would see `http` requests and answer every OpenID Connect endpoint with `400 Bad Request` (it requires HTTPS, and it ignores `X-Forwarded-Proto`; [issue #72](https://github.com/TasTugberk/Kale/issues/72)). A layer-4 proxy that passes TLS through untouched (SNI routing) works too **(not tried)**.
- **Keep the public `Host` header.** AuthService builds the addresses in its discovery document from it. On the test, Caddy first sent the upstream's name, and discovery advertised `"token_endpoint": "https://authservice:8443/connect/token"`; `header_up Host {hostport}` fixed it.
- **HTTP/2 to AuthService** (gRPC needs it; over TLS it's negotiated), and **long streams**: every application keeps one gRPC stream open for invalidation events, for as long as it runs. No read/response timeout on it, no buffering (Caddy: `flush_interval -1`). Applications send HTTP/2 keep-alive pings every 30 seconds and reconnect by themselves, but a proxy that cuts streams often makes them clear their caches each time.

**For the admin UI** (`kale-admin.example.com`): terminate TLS and forward plain HTTP to `http://admin-ui:8083` on the internal network. The admin UI builds its addresses from `KALE_ADMIN_UI_URL`, so it needs no forwarded headers.

Inside the compose network, the proxy also answers to the public names (network aliases), so the admin UI reaches AuthService at its public address without leaving the server.

## 11. First start: database, migration, AuthService

From `~/kale/deploy`, with `deploy/.env` and the secrets in place:

```bash
alias kc='podman compose -f compose.production-example.yaml'

kc up -d postgres                       # skip with a managed database
kc run --rm migrate                     # brings the database up to date, then exits
kc up -d --no-deps authservice
podman inspect -f '{{.State.Health.Status}}' kale-prod-authservice   # wait for: healthy
kc up -d --no-deps proxy
```

The migration is a one-shot container with the AuthService image and only the database settings (`AuthService admin migrate`). Run it before every new version starts (section 17); it does nothing when the database is current.

AuthService's log at a good start:

```
info: AuthService.Invalidation.InvalidationNotificationListener[1211495886]
      Listening for invalidation events.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://[::]:8443
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Production
```

AuthService creates its signing and encryption keys at the first start (section 15).

## 12. The first admin and the admin UI

`AuthService admin bootstrap` sets up AuthService's own application `auth` (the admin UI's client), its role `Admin` and the first admin user, all at once. It reads the password from standard input and **prints the admin UI's client secret, only this once**: pipe it straight into a Podman secret.

```bash
set -a; source .env; set +a
podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password \
  | podman exec -i kale-prod-authservice dotnet AuthService.dll admin bootstrap "$ADMIN_USER" "Kale Admin" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signin-oidc" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signed-out" \
  | podman secret create kale_prod_admin_ui_client_secret -

kc up -d --no-deps admin-ui
```

(With port 443, leave `:${PUBLIC_HTTPS_PORT}` out of the addresses, or keep it: both mean the same to browsers, but the registered address must match `KALE_ADMIN_UI_URL` exactly.)

Use `podman exec … dotnet AuthService.dll`, never `dotnet run`: `dotnet run` adds its own first line to standard output, which would end up in the secret. `bootstrap` refuses to run twice; if the secret got lost, see section 20.

Then sign in at `https://kale-admin.example.com/` as the first admin.

## 13. Check that it works

```bash
curl -s https://auth.example.com/.well-known/openid-configuration | jq '{issuer, token_endpoint, jwks_uri}'
```

On the test (`--cacert certs/ca.crt` because of the example CA):

```json
{"issuer":"https://auth.kale.localhost:9443/","token_endpoint":"https://auth.kale.localhost:9443/connect/token","jwks_uri":"https://auth.kale.localhost:9443/.well-known/jwks"}
```

All three addresses must be the public one. Then sign in to the admin UI in a browser.

For a full end-to-end check, the example also runs **SampleApp**, a small API that uses `Kale.Auth.Client` like your applications will (https, gRPC over TLS, no development switches), and `./scripts/smoke-test.sh deploy/.env` signs a user in, checks allow/deny by role, edits the role and checks that the change arrives over the invalidation stream through the proxy. On a real server, either run that check once and then remove the `sampleapp` service and the `sample` application (admin UI → Applications → deactivate), or leave SampleApp out from the start and check with a real application.

## 14. Registering applications, and what to give their teams

Each application that uses Kale is registered once, per environment. Either in the **admin UI** (Applications → New application; the client secret is shown once), or on the command line:

```bash
podman exec kale-prod-authservice dotnet AuthService.dll admin create-application \
  orders "Orders" https://orders.example.com/signin-oidc https://orders.example.com/signout-callback-oidc
```

It prints the client secret once. Without the two addresses, the application has only an API (its users can't sign in to it directly). Return addresses must be `https` (plain `http` only for `localhost`).

Give the application's team:

| What | Example |
|---|---|
| Kale's address (`Kale:Authority`) | `https://auth.example.com/` |
| Their application key (`Kale:ApplicationKey`) | `orders` |
| The client secret, **through a secure channel** (a vault, a password manager; never e-mail or chat). They mount it as a file (`Kale:ClientSecretFile`) | — |
| With your own CA: its certificate (`ca.crt`), which their servers must trust | .NET on Linux: system trust store, or `SSL_CERT_FILE` as in the example; Node.js: `NODE_EXTRA_CA_CERTS`; Windows: the certificate store |
| The [integration guide](../integration/guide.en.md), especially §14 "Going to production" | — |

Their servers must reach `auth.example.com:443` over HTTP/2 (gRPC goes to the same address; `Kale:GrpcAddress` stays empty). A team that loses its secret gets a new one: Applications → the application → new secret, or `admin rotate-secret orders` (the old one stops at once).

Then give people access: users, roles and groups in the admin UI. The application registers its own operations when it starts.

## 15. Keys: nothing to do

AuthService creates, publishes, rotates and deletes its token signing and encryption keys by itself, in the database (`server_keys`): a new key every 30 days, published a day before it's used, kept a day after its successor took over. All instances share them, and each instance reloads them every hour, so no restart is needed. Applications fetch the public keys from `/.well-known/jwks`.

Those keys are encrypted with ASP.NET Core Data Protection, whose own key ring is in the database too (`data_protection_keys`) and is encrypted with the **Data Protection certificate**. Data Protection rotates its keys by itself (every 90 days).

So the chain is: certificate → `data_protection_keys` → `server_keys` → every token. Lose any link and every token, refresh token and session is invalid (users sign in again; applications keep working). That's why backups need all three.

## 16. Backups and restore

Back up:

1. **The whole database** (`data_protection_keys` and `server_keys` are in it, and must come from the same moment).
2. **The Data Protection certificate and its private key** (the files from section 6). Store them apart from the database backups, as safely as the database.
3. The `deploy/` folder with your `.env`, and the TLS certificates (these can be re-issued).

A database backup with `pg_dump` (custom format), and a restore into another database to test it:

```bash
podman exec kale-prod-postgres pg_dump -U kale -d kale_auth -Fc > backups/kale_auth-$(date +%F).dump

# Test the restore: a separate database, then AuthService against it with the same certificate.
podman exec kale-prod-postgres createdb -U kale kale_auth_restore_test
podman exec -i kale-prod-postgres pg_restore -U kale -d kale_auth_restore_test --no-owner < backups/kale_auth-2026-11-17.dump
```

Both were run on the test: the dump held one `data_protection_keys` row and two `server_keys` rows. An AuthService started against the restored database **with the same certificate** started and answered `Healthy`. Started **with another certificate**, it stopped at once:

```
fail: Microsoft.Extensions.Hosting.Internal.Host[11]
      Hosting failed to start
      System.Security.Cryptography.CryptographicException: Unable to retrieve the decryption key.
```

Test a restore like this regularly, then drop the test database (`dropdb -U kale kale_auth_restore_test`). With a managed database, use its own backups (they contain everything), and keep the certificate yourself.

**The Data Protection certificate can't be replaced yet.** AuthService decrypts the key ring only with the one configured certificate, so a new certificate makes the existing keys unreadable (the error above). That's why it's made for 10 years. If it ever has to change, plan for every user signing in again.

## 17. Upgrades and certificate renewal

**A new version:**

```bash
# 1. The new images (section 4), and their tags in deploy/.env:
#    AUTHSERVICE_IMAGE=localhost/kale-authservice:1.0.1, ADMIN_UI_IMAGE=...
# 2. Back up the database (section 16).
# 3. Migrate, then start the new version:
kc run --rm migrate
kc up -d --force-recreate --no-deps authservice
```

Read the release notes first: a version may need new settings. On the test (an upgrade from tag `1.0.0` to `1.0.1`), the migration ran, AuthService came up healthy with the new image, and the signing key stayed the same.

`--force-recreate` is needed because podman-compose doesn't notice a new image by itself. podman-compose 1.6 also restarts the containers that depend on AuthService (proxy, admin UI, SampleApp): **admins are signed out of the admin UI** (it keeps sessions in memory), and there's a few seconds' gap. Applications ride it out: they reconnect and keep their cached answers for up to 5 minutes. Upgrade outside working hours.

**Renewing a TLS certificate** (the proxy's or AuthService's): replace the secret, then recreate the container (a container gets its secrets when it's created):

```bash
podman secret create --replace kale_prod_authservice_tls_cert certs/authservice.crt
podman secret create --replace kale_prod_authservice_tls_key  certs/authservice.key
kc up -d --force-recreate --no-deps authservice
```

Tried on the test: the container served the new certificate (new serial number) afterwards. For the proxy, replace `kale_prod_proxy_tls_*` and recreate `proxy`.

## 18. Starting after a reboot

The services have `restart: unless-stopped`. With rootless Podman, two more things make them come back after a server reboot **(not tried)**:

```bash
sudo loginctl enable-linger <user>              # the user's services run without a login session
systemctl --user enable --now podman-restart.service   # starts containers with a restart policy at boot
```

For a larger setup, Podman's Quadlet (systemd units for containers) is the Linux-native alternative to compose.

## 19. Health checks and logs

**Health:** each container has a healthcheck (`podman ps` shows `healthy`). AuthService's `GET /health` only says the process runs: since the app finishes starting only after its keys load from the database, healthy means the database was reachable at startup, not that it still is. From outside, watch the discovery document (section 13) instead.

**Logs:** `podman logs kale-prod-authservice` (and `kale-prod-admin-ui`, `kale-prod-proxy`). Watch for these until there are metrics:

- **Error:** "Refreshing the server keys failed"; in applications, "AuthService couldn't be reached to check the session; access refused (503)", "Couldn't ask AuthService about the session; access refused" and "AuthService rejected this application's operations".
- **Warning:** "Listening for invalidation events failed; open streams ended", "Cleanup failed"; in applications, "The invalidation stream ended; reconnecting" (now and then is fine, e.g. during an upgrade; often means the proxy cuts streams) and "Couldn't register operations".
- **Information:** refused sign-ins ("Sign-in to {Application} refused for user {UserId}: {Reason}").

Good signs: AuthService logs "Listening for invalidation events."; each application logs "Receiving invalidation events from AuthService." and "Operations registered with AuthService".

An application's own log may warn that Data Protection keys are stored in the container and not encrypted (SampleApp did on the test). That's about the application's own Data Protection, not Kale's; for an API it's harmless, for a web front end see the integration guide §14.

## 20. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| AuthService stops at startup: "Set Auth:Issuer…", "Set DataProtection:CertificatePath…", "… is only allowed in Development and Testing" | A missing production setting or a development switch (section 9). The message names it. |
| "Kestrel is set to serve HTTP/2 without TLS" | A `Protocols=Http2` on an `http://` endpoint. Use the https endpoint of section 9 only. |
| "Hosting failed to start … Unable to retrieve the decryption key" | The Data Protection certificate doesn't match the database (another certificate, or a database restored without its key ring). Use the certificate the database was set up with (section 16). |
| The container stays `unhealthy`, its log is fine; the check says "certificate verify failed" | The healthcheck checks the certificate against `127.0.0.1`. Keep `--no-check-certificate` in AuthService's healthcheck (as in the example). |
| Every OpenID Connect address answers `400 Bad Request` through the proxy | The proxy forwards plain HTTP. Re-encrypt to `https://…:8443` (section 10). |
| Discovery lists `https://authservice:8443/...` | The proxy doesn't pass the public `Host` header (section 10). |
| An application can't connect: SSL/certificate errors (`UntrustedRoot`, "unable to get local issuer certificate") | It doesn't trust your CA (section 14). |
| Applications log "The invalidation stream ended; reconnecting" every few minutes | Something between them and AuthService cuts long HTTP/2 streams (proxy, load balancer, firewall idle timeout). Remove the timeout for that route. |
| Events don't reach applications at all | AuthService should log "Listening for invalidation events."; if it warns that listening failed, check its database connection (a transaction-pooling proxy breaks `LISTEN`, section 8). |
| `bootstrap` says `auth` already exists, but the admin UI secret is lost | Give `auth` a new secret: `podman exec kale-prod-authservice dotnet AuthService.dll admin rotate-secret auth \| podman secret create --replace kale_prod_admin_ui_client_secret -`, then `kc up -d --force-recreate --no-deps admin-ui`. |
| No admin can sign in | Handover guide §9.3 (create a new admin with `admin create-user` and `admin assign-role`). A lockout ends by itself after 15 minutes; `admin set-password` ends it at once. |
| `podman compose` can't publish port 443: "permission denied" | Rootless Podman and low ports (section 3). |
| The admin UI stops at startup: "KALE_ADMIN_UI_URL must be an https address", "KALE_TIME_ZONE … is not a known time zone" | Its settings (section 9). |

More in the [handover guide §12](../handover/handover.en.md#12-troubleshooting-server-and-dev-stack) and, for applications, the [integration guide §15](../integration/guide.en.md#15-troubleshooting).

## 21. Windows notes

Kale runs the same way on Windows with Podman machine (Podman's Linux VM, on WSL 2). What's different **(not tried on Windows for this guide; known from the dev stack)**:

- **Start the machine first:** `podman machine start` after every Windows restart; the containers start with it only if their restart policy allows. To start it automatically, add `podman machine start` to Task Scheduler at logon.
- **Run the shell commands in Git Bash** (it has `openssl`, and the scripts are bash). Create secrets from Git Bash, not PowerShell: PowerShell can put a byte-order mark or CR LF into a piped secret, and the password then doesn't work.
- **Line endings:** the repository stores text files with LF. If an editor saves `deploy/.env` with CR LF, the values get an invisible `\r` and names stop resolving. Save with LF.
- **Trusting your CA** (Edge and Chrome use Windows' store), in PowerShell: `Import-Certificate -FilePath deploy\certs\ca.crt -CertStoreLocation Cert:\CurrentUser\Root`. Firefox has its own store.
- `*.localhost` names resolve to this machine in Edge, Chrome and `curl.exe`, so the example of section 2 works as it is.
- **Port 443:** if publishing it fails inside the machine, either keep a high port (as the example does with 9443) or allow low ports in the VM: `podman machine ssh "echo net.ipv4.ip_unprivileged_port_start=443 | sudo tee /etc/sysctl.d/90-kale-ports.conf && sudo sysctl --system"`.
- **A TLS-inspecting company proxy** breaks image pulls and builds with certificate errors: the Podman machine has to trust the proxy's root certificate (see PR #68).
