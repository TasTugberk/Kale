# Kale'yi üretime kurmak

[English](guide.en.md) · **Türkçe**

Bu kılavuz, Kale'yi bir sunucuya ilk kez kuracak ve sonra işletecek kişi için. Boş bir Linux sunucudan başlayıp çalışan bir AuthService'e, yönetim arayüzüne ve kayıtlı ilk uygulamaya kadar gidiyor. Ardından günlük işleri anlatıyor: yedek, sürüm yükseltme, sağlık kontrolü, loglar.

Her şey **rootless Podman** ile, depodaki `Containerfile`'lardan yapılan konteynerlerde çalışıyor. Windows'ta (Podman machine) farklı olan yerlerde kısa bir **Windows** notu var.

> **Gerçekten denendi.** Aşağıdaki adımlar tek bir makinede, üretime benzer bir kurulumla çalıştırıldı: `ASPNETCORE_ENVIRONMENT=Production`, kendinden imzalı bir CA ve ondan sunucu sertifikaları, bir Data Protection sertifikası, Kestrel'de HTTP ve gRPC için TLS, önde bir reverse proxy, bütün sırlar Podman secret olarak, bir de `Kale.Auth.Client` kullanan ve AuthService'e TLS ile bağlanan bir uygulama (SampleApp). Bu kurulum [`deploy/`](../../deploy) klasöründe; tek komutla ayağa kalkıyor (2. bölüm). Denenmemiş birkaç şey **(denenmedi)** diye işaretli.

## İçindekiler

1. [Sonunda elinizde ne olacak](#1-sonunda-elinizde-ne-olacak)
2. [Önce kendi makinenizde deneyin](#2-önce-kendi-makinenizde-deneyin)
3. [Başlamadan önce](#3-başlamadan-önce)
4. [İmajlar](#4-imajlar)
5. [Ayar dosyası](#5-ayar-dosyası)
6. [Sertifikalar](#6-sertifikalar)
7. [Sırlar](#7-sırlar)
8. [PostgreSQL](#8-postgresql)
9. [AuthService ayarları](#9-authservice-ayarları)
10. [Reverse proxy](#10-reverse-proxy)
11. [İlk açılış: veritabanı, migration, AuthService](#11-ilk-açılış-veritabanı-migration-authservice)
12. [İlk yönetici ve yönetim arayüzü](#12-ilk-yönetici-ve-yönetim-arayüzü)
13. [Çalıştığını kontrol edin](#13-çalıştığını-kontrol-edin)
14. [Uygulamaları kaydetmek ve ekiplerine verilecekler](#14-uygulamaları-kaydetmek-ve-ekiplerine-verilecekler)
15. [Anahtarlar: yapacak bir şey yok](#15-anahtarlar-yapacak-bir-şey-yok)
16. [Yedek ve geri yükleme](#16-yedek-ve-geri-yükleme)
17. [Sürüm yükseltme ve sertifika yenileme](#17-sürüm-yükseltme-ve-sertifika-yenileme)
18. [Sunucu yeniden başlayınca](#18-sunucu-yeniden-başlayınca)
19. [Sağlık kontrolü ve loglar](#19-sağlık-kontrolü-ve-loglar)
20. [Sorun giderme](#20-sorun-giderme)
21. [Windows notları](#21-windows-notları)

## 1. Sonunda elinizde ne olacak

```
 tarayıcılar, uygulamalar
        │ https (443)
        ▼
 ┌────────────────┐  https, yeniden şifreli  ┌──────────────────────────────────────┐
 │ reverse proxy  │ ───────────────────────▶ │ AuthService  (8443'te Kestrel TLS:    │
 │ (burada Caddy) │                          │  giriş sayfaları, token'lar,          │
 │                │                          │  yönetim API'si, uygulamalara gRPC)   │
 │                │  http (iç ağ)            └───────────────┬──────────────────────┘
 │                │ ─────────▶ yönetim arayüzü (Next.js, :8083)│
 └────────────────┘                                          ▼
                                                        PostgreSQL
```

- **AuthService** (bir ya da daha çok kopya): giriş sayfaları, token'lar (OpenID Connect), yönetim API'si (`/admin/api`) ve uygulamaların çağırdığı gRPC API'si. **Yalnızca HTTPS** konuşur, tek bir porttan; hem HTTP/1.1 hem gRPC (HTTP/2) aynı porttan geçer.
- **Yönetim arayüzü** (tam olarak **bir** kopya: yöneticilerin oturumlarını bellekte tutar): uygulamalar, kullanıcılar, roller ve gruplar buradan yönetilir.
- **PostgreSQL**: her şey burada, token imzalama anahtarları da (şifreli olarak).
- Genel sertifikayı taşıyan bir **reverse proxy**. AuthService'e giden trafiği **yeniden TLS ile** gönderir: uygulamaların gRPC çağrıları HTTP/2 ister, AuthService de şifresiz HTTP/2'ye yalnızca geliştirme ortamında izin verir (10. bölüm).

Uygulamalar başka yerlerde çalışır. AuthService'e onun genel adresinden ulaşırlar; token'lar için de gRPC için de HTTPS ile.

## 2. Önce kendi makinenizde deneyin

[`deploy/`](../../deploy) klasörü, bu kılavuzun denendiği üretime benzer kurulumu içeriyor. Sunucuya kurmadan önce bir kez kendi makinenizde çalıştırın. Kendi adlarını, 9443 portunu ve kendi volume'larını kullandığı için geliştirme ortamına dokunmaz:

```bash
podman machine start                 # yalnızca macOS ve Windows'ta, her yeniden başlatmadan sonra bir kez
./deploy/production-example.sh       # örnek sertifikalar, sırlar, imajlar, sonra bütün yığın
./scripts/smoke-test.sh deploy/.env  # baştan sona, https üzerinden
```

Denemede smoke test'in çıktısı:

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

`deploy/certs/ca.crt` sertifikasına güvenmesini sağladıktan sonra tarayıcıda `https://admin.kale.localhost:9443/` adresini açın (Windows'ta nasıl yapılacağı 21. bölümde; macOS'ta: `security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db deploy/certs/ca.crt`). Yöneticinin parolası: `podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password`.

Durdurmak için: `(cd deploy && podman compose -f compose.production-example.yaml down)`. Veritabanı da silinsin isterseniz sonuna `-v` ekleyin.

Kılavuzun geri kalanı aynı kurulumu gerçek bir sunucu için adım adım anlatıyor. `deploy/production-example.sh` aynı komutları çalıştırıyor; yanında açık tutabilirsiniz.

## 3. Başlamadan önce

Gerekenler:

- Podman (denemede 6.1) ve podman-compose 1.6 kurulu **bir Linux sunucu** (`podman compose` onu çağırır). Konteynerleri çalıştıracak, root olmayan bir kullanıcı.
- **DNS adları**, örneğin AuthService için `auth.example.com`, yönetim arayüzü için `kale-admin.example.com`. AuthService'in adı **issuer** olur: her token onu taşır. Bir kez seçin, sonra değiştirmeyin.
- Bu adlar için **TLS sertifikaları**: kurumunuzun CA'sından ya da genel bir CA'dan (6. bölüm).
- **PostgreSQL**: kendi konteyneriniz (örnekteki gibi) ya da yönetilen bir veritabanı (8. bölüm).
- Komutları çalıştırdığınız makinede `openssl` ve `jq`.

`deploy/` klasörünü sunucuya kopyalayın (örneğin `~/kale/deploy`). Sunucuda o klasörde çalışacaksınız.

**Rootless Podman ve düşük portlar.** Root olmayan bir kullanıcı varsayılan olarak 1024'ün altındaki portları yayınlayamaz. 443'ü doğrudan yayınlamak için bir kez (root olarak) izin verin:

```bash
echo 'net.ipv4.ip_unprivileged_port_start=443' | sudo tee /etc/sysctl.d/90-kale-ports.conf
sudo sysctl --system
```

**(denenmedi)**: denemede 9443 portu kullanıldı.

## 4. İmajlar

Kale'nin üç imajı var: AuthService, yönetim arayüzü ve (yalnızca deneme için) SampleApp. Deponun bir sürüm etiketinden, deponun kökünde derleyin:

```bash
podman build -f src/AuthService/Containerfile -t localhost/kale-authservice:1.0.0 .
podman build -f admin-ui/Containerfile        -t localhost/kale-admin-ui:1.0.0 .
podman build -f samples/SampleApp/Containerfile -t localhost/kale-sampleapp:1.0.0 .   # yalnızca deneme için, 13. bölüm
```

Etiket olarak sürüm numarasını (`Directory.Build.props` içindeki `<VersionPrefix>`) kullanın; böylece yükseltme yeni bir etiket demek olur. Başka bir makinede derliyorsanız imajları ya kendi registry'nize gönderin ya da dosya olarak taşıyın (`podman save -o kale-authservice.tar localhost/kale-authservice:1.0.0`, sunucuda `podman load -i kale-authservice.tar`). Sunucunun işlemcisine göre derleyin: Apple Silicon bir Mac'te derlenen imaj `arm64` olur; `x86_64` bir sunucu için `--platform linux/amd64` ekleyin.

## 5. Ayar dosyası

Compose dosyasının kullandığı her değer `deploy/.env` dosyasından gelir (compose onu kendi klasöründen okur; git'e girmez). Örnekten kopyalayıp düzenleyin:

```bash
cd ~/kale/deploy
cp .env.production.example .env
```

| Anahtar | Örnek (deneme) | Sunucuda |
|---|---|---|
| `AUTHSERVICE_IMAGE`, `ADMIN_UI_IMAGE` | `localhost/kale-authservice:1.0.0` | Kendi imaj adlarınız ve sürüm |
| `POSTGRES_IMAGE`, `PROXY_IMAGE` | `postgres:18-alpine`, `caddy:2-alpine` | Aynı |
| `POSTGRES_DB`, `POSTGRES_USER` | `kale_auth`, `kale` | Aynı |
| `AUTH_HOST` | `auth.kale.localhost` | `auth.example.com` |
| `ADMIN_UI_HOST` | `admin.kale.localhost` | `kale-admin.example.com` |
| `SAMPLE_HOST` | `sample.kale.localhost` | Sunucuda SampleApp'i çıkarın (13. bölüm) |
| `PUBLIC_HTTPS_PORT` | `9443` | `443` |
| `PUBLIC_BIND_ADDRESS` | `127.0.0.1` | `0.0.0.0` |
| `ADMIN_USER` | `admin` | İlk yöneticinin kullanıcı adı |
| `ADMIN_UI_TIME_ZONE` | `Europe/Istanbul` | IANA saat dilimi adı; boşsa UTC |

Satırlar düz `ANAHTAR=DEĞER` olsun; tırnak ve boşluk kullanmayın. Bu dosyaya parola ya da sır yazılmaz: onlar Podman secret'tır (7. bölüm).

## 6. Sertifikalar

Kale üç tür sertifikaya ihtiyaç duyar. Hepsi PEM dosyası (`.crt` + `.key`; anahtar parolasız olur ve Podman secret olarak saklanır, 7. bölüm).

| Sertifika | Ne için | Nereden |
|---|---|---|
| **Proxy** | Genel adlar (`auth.example.com`, `kale-admin.example.com`) | Kurumunuzun CA'sı ya da genel bir CA |
| **AuthService** | Kestrel'in kendi TLS'i, AuthService'in genel adı (`auth.example.com`) için. Proxy ona bağlanırken bu adı kontrol eder | Kurumunuzun CA'sı (proxy'ninkiyle aynı sertifika da olabilir) |
| **Data Protection** | AuthService'in veritabanındaki anahtar halkasını şifreler | **Kendinden imzalı**, sizin ürettiğiniz: yalnızca şifreleme için kullanılır, kimse görmez |

Data Protection sertifikası (RSA; 10 yıllık, çünkü sonradan değiştirmek henüz desteklenmiyor, bkz. 16. bölüm):

```bash
mkdir -p certs
openssl req -x509 -newkey rsa:3072 -nodes -keyout certs/dataprotection.key \
  -subj "/CN=Kale Data Protection" -days 3650 -sha256 -out certs/dataprotection.crt
```

**Gerçek bir CA olmadan denemek için** `deploy/example-certificates.sh`, `.env` içindeki adlar için örnek bir CA ve iki sunucu sertifikası üretir (`openssl` komutları betiğin içinde). O zaman her istemcinin bu CA'ya güvenmesi gerekir (14. bölüm). Genel bir CA'dan alınmış sertifikalarda elle güvendirilecek bir şey yoktur.

Sunucu sertifikalarında DNS adları **Subject Alternative Name** içinde olmalı, `extendedKeyUsage = serverAuth` bulunmalı. `.crt` dosyasına zincirin tamamını koyun (önce sunucu sertifikası, sonra ara sertifikalar).

## 7. Sırlar

Parolalar, istemci sırları ve özel anahtarlar konteynerlere **yalnızca dosya olarak** ulaşır: `/run/secrets/<ad>` altına bağlanan Podman secret'lar. Asla `.env` dosyasında, compose dosyasında ya da bir ortam değişkeninde değil.

Podman secret'lar sırları imajların, compose dosyalarının, `podman inspect` çıktısının ve süreç ortamının dışında tutar ama **şifrelemez**: varsayılan `file` sürücüsüyle Podman onları kullanıcının depolama alanında (`~/.local/share/containers/storage/secrets`) düz dosya olarak saklar. Onları koruyan şey o hesaptır: Podman'ı çalıştıran sunucu hesabına yalnızca yöneticiler erişsin, ev klasörü de ortak yedeklere girmesin.

```bash
# Sertifikalar ve anahtarlar (CA sertifikası gizli değil ama aynı yolla bağlanıyor)
podman secret create kale_prod_proxy_tls_cert        certs/proxy.crt
podman secret create kale_prod_proxy_tls_key         certs/proxy.key
podman secret create kale_prod_authservice_tls_cert  certs/authservice.crt
podman secret create kale_prod_authservice_tls_key   certs/authservice.key
podman secret create kale_prod_dataprotection_cert   certs/dataprotection.crt
podman secret create kale_prod_dataprotection_key    certs/dataprotection.key
podman secret create kale_prod_ca_cert               certs/ca.crt   # yalnızca kendi CA'nızla; aşağıya bakın

# Parolalar: rastgele, doğrudan Podman'a (hiçbir dosyaya yazılmadan)
openssl rand -hex 24 | tr -d '\n' | podman secret create kale_prod_postgres_password -
printf 'Kale-%s' "$(openssl rand -hex 16)" | podman secret create kale_prod_admin_password -
```

Yönetici parolası AuthService'in kurallarına uymalı (en az 12 karakter; büyük harf, küçük harf, rakam ve sembol içermeli); yukarıdaki komut buna uyar. Sonradan okumak için: `podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password`. Akılda kalacak bir parola isterseniz ilk girişten sonra değiştirin.

Sırları oluşturduktan sonra **özel anahtarları ve Data Protection sertifikasını güvenli bir yerde saklayın** (bir parola yöneticisi ya da kasa) ve `certs/*.key` dosyalarını sunucudan silin: Podman'ın kendi kopyası var. Data Protection sertifikası ve anahtarı her yedeğin parçasıdır (16. bölüm).

Kendi CA'nızı kullanmıyorsanız `kale_prod_ca_cert`'ü compose dosyasından, `NODE_EXTRA_CA_CERTS` ve `SSL_CERT_FILE` ayarlarıyla birlikte çıkarın (9. bölüm); Caddyfile'daki `tls_trust_pool` satırını da silin (Caddy o zaman sistemin CA'larına güvenir).

## 8. PostgreSQL

**Kendi konteyneriniz** (`compose.production-example.yaml` içindeki gibi): PostgreSQL 18, ağa açılmamış, parolası `kale_prod_postgres_password`'den, verisi `postgres-data` volume'unda. Başka bir şey yapmanız gerekmiyor.

**Yönetilen ya da var olan bir PostgreSQL:** `postgres` servisini ve ona bağlı `depends_on` satırlarını çıkarın, `migrate` ile `authservice`'in bağlantı dizesini ayarlayın. Örnek:

```yaml
ConnectionStrings__AuthDb: Host=db.example.com;Port=5432;Database=kale_auth;Username=kale;SSL Mode=VerifyFull
AuthDb__PasswordFile: /run/secrets/kale_prod_postgres_password
```

- Parola bağlantı dizesine yazılmaz; `AuthDb:PasswordFile` onu ekler.
- Kullanıcı `kale_auth` veritabanının sahibi olmalı (migration'lar tablo oluşturup değiştirir). Onun için boş bir veritabanı açın.
- **Doğrudan bağlantı ya da yalnızca oturum düzeyinde (session) havuzlama.** Her AuthService kopyası olay bildirimleri için `LISTEN` ile açık duran bir bağlantı tutar. İşlem düzeyinde (transaction) havuzlama yapan bir proxy (transaction modunda PgBouncer, yönetilen veritabanlarının birçok "pooler" adresi) bunu bozar: uygulamalar durdurulan oturumları ve değişen rolleri zamanında öğrenemez. Veritabanının doğrudan adresini kullanın.
- **Oturum kontrollerinin yükü.** Her uygulama kopyasının kendi önbelleği vardır: oturum açmış bir kullanıcının oturumunu Kale'ye en fazla 2 dakikada bir sorar (olay akışı kesikken 20 saniyede bir) ve her kontrol üç küçük sorgudur. Yük yaklaşık olarak *kullanıcı sayısı × her kullanıcının ulaştığı uygulama kopyası sayısı × 3 sorgu ÷ 120 saniye* eder; bütün uygulamalar için ayrı ayrı hesaplanıp toplanır. Örneğin tek kopyayla çalışan bir uygulamada aynı anda etkin 10.000 kullanıcı saniyede yaklaşık 250 sorgu demektir (akış kesikken yaklaşık 1.500). Yük dengeleyici istekleri 3 kopyaya dağıtıyorsa bunun üç katına kadar çıkar, ikinci bir uygulama da ayrıca sayılır. Bu büyüklükte tek bir PostgreSQL için sorun değil; çok sayıda büyük uygulama bağlanmadan önce ölçün.
- Örnekteki `Gss Encryption Mode=Disable` yalnızca bir log satırını susturur (Alpine imajlarında Kerberos kütüphanesi yok); Kerberos kullanmıyorsanız kalsın.

Yönetilen bir veritabanıyla **(denenmedi)**: denemede kendi konteyneri kullanıldı.

## 9. AuthService ayarları

Development dışında AuthService, üretim ayarları olmadan açılmayı reddeder; geliştirmeye özel her anahtarı da reddeder. Hepsi aşağıda, `compose.production-example.yaml` içindeki gibi ortam değişkeni olarak (`__`, `:` yerine geçer):

| Ayar | Değer | Neden |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Bütün korumaları açar. `Development`/`Testing` dışındaki her değer açar |
| `Auth__Issuer` | `https://auth.example.com/` (sondaki `/` ile) | Her token'daki issuer. Zorunlu |
| `ConnectionStrings__AuthDb` + `AuthDb__PasswordFile` | 8. bölüm | |
| `DataProtection__CertificatePath` + `DataProtection__PrivateKeyPath` | `/run/secrets/kale_prod_dataprotection_cert` / `_key` | Zorunlu: yoksa AuthService açılmaz |
| `ASPNETCORE_HTTP_PORTS` | boş | Düz HTTP dinleyicisi hiç olmasın |
| `Kestrel__Endpoints__Https__Url` | `https://+:8443` | HTTP/1.1 ve gRPC için tek TLS ucu (HTTP/2'yi TLS kendisi anlaşır) |
| `Kestrel__Endpoints__Https__Certificate__Path` + `__KeyPath` | `/run/secrets/kale_prod_authservice_tls_cert` / `_key` | Kestrel'in sertifikası |

**Kesinlikle ayarlanmayacaklar** (Development/Testing dışında her biri açılışı durdurur): `OpenIddict__AllowInsecureHttpDevOnly`, `Grpc__AllowInsecureDevOnly` ve bir `http://` ucunda `Kestrel__Endpoints__*__Protocols=Http2` (ya da `Kestrel__EndpointDefaults__Protocols=Http2`). RabbitMQ'lu sürümden kalan ayarlar (`RabbitMq__*`) yok sayılır; silin.

**İsteğe bağlı:** `SignIn__RequireTwoFactor=true` iki adımlı girişi (doğrulama uygulaması) bütün kullanıcılar için zorunlu yapar. Henüz kurmamış biri, parolasını girdikten hemen sonra kurulum sayfasına gönderilir; kurana kadar hiçbir uygulamaya giriş yapamaz. Sessiz giriş yapan uygulamalar (`prompt=none`) bu sürede `interaction_required` alır. Varsayılan `false`: kullanıcılar `/account/two-factor` sayfasında kendileri açar. Telefonunu kaybeden birinin iki adımlı girişini yönetici, yönetim arayüzündeki kullanıcı sayfasından ya da `admin reset-two-factor <kullanıcı>` ile sıfırlar.

**İsteğe bağlı, varsayılan olarak kapalı:** `SignIn__AttemptsPerAddress` (`0`, kapalı) ve `SignIn__AttemptWindow` (varsayılan `00:01:00`): bir istemci adresinin, bütün hesaplar toplamında, her AuthService kopyasına bu süre içinde gönderebileceği parola ve iki adımlı giriş kodu sayısı. Sınır aşılınca giriş sayfası "çok fazla deneme" der (HTTP 429, `Retry-After` ile) ve hiçbir şeyi kontrol etmez. IPv6 adresleri /64 başına sayılır. **Bunu yalnızca AuthService istemcilerin gerçek adreslerini görüyorsa açın**: önünde reverse proxy yoksa, proxy bağlantıları olduğu gibi geçiriyorsa (TCP/SNI passthrough) ya da AuthService'in güvendiği bir proxy ise (aşağıdaki `ForwardedHeaders__*`); o zaman istemcinin adresi proxy'nin gönderdiği `X-Forwarded-For`'dan gelir. Güvenmediği bir proxy'nin arkasında bütün kullanıcılar proxy'nin adresinden gelir. Sınır o zaman herkesin denemelerini birlikte sayar: yoğun bir sabah girişler reddedilir, birkaç saniyede bir istek gönderen tek bir betik de herkesin girişini engelleyebilir. Saldırgan bütçeyi herkesle paylaştığı için hiçbir koruma da sağlamaz.

**İsteğe bağlı, varsayılan olarak hiçbiri:** `ForwardedHeaders__KnownProxies__0`, `__1`, ... (adresler) ve `ForwardedHeaders__KnownNetworks__0`, ... (ağlar, ör. `10.89.0.0/24`): AuthService'in `X-Forwarded-For` (istemcinin adresi) ve `X-Forwarded-Proto` (proxy'de `https`) başlıklarına inandığı reverse proxy'ler. Bu başlıkları herkes gönderebilir; yalnızca kendi proxy'lerinizi yazın. Adres ya da ağ olmayan bir değer AuthService'in açılmasını durdurur; `/8`'den (IPv4) ya da `/32`'den (IPv6) geniş bir ağ da. `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` de durdurur: ASP.NET Core'un bu kendi ayarı başlıklara her göndericiden inanırdı. Böyle bir proxy TLS'i kendisi sonlandırıp arkaya düz HTTP/1.1 de gönderebilir (giriş sayfaları, token'lar, yönetim API'si); gRPC ise yine TLS üzerinden HTTP/2 ister (10. bölüm).

**Compose ağına değil, proxy'nin kendi adresine güvenin.** Ağdaki her konteyner bu başlıkları gönderebilir, ağın geçidi (gateway) de: örnekte bu, yönetim arayüzü, SampleApp ve geçitten gelen bağlantılar (ör. sunucunun kendisinden) demek. Örnekte proxy'nin adresi sabit değil; ona bir adres verin: `compose.production-example.yaml` içinde ağa bir subnet, `proxy`'ye de bir adres (dosyanın sonunda `networks: default: ipam: config: - subnet: 10.89.10.0/24`, proxy'nin `networks: default:` altında `ipv4_address: 10.89.10.10`), ardından `authservice` için `ForwardedHeaders__KnownProxies__0: 10.89.10.10`. **(Örnek kurulumda denenmedi.)** Bundan ayrı olarak, 5 hatalı deneme tek bir hesabı 15 dakika kilitler; yönetici kilidi yönetim arayüzünde kullanıcının sayfasından (**Kilidi aç**) ya da `admin unlock <kullanıcı>` ile erkenden açar. Kilitlenen tek yönetici sizseniz komut yine işe yarar.

Yönetim arayüzünün ayarları:

| Ayar | Değer |
|---|---|
| `KALE_ENVIRONMENT` | `Production` |
| `KALE_AUTH_ISSUER` | `Auth__Issuer` ile aynı |
| `KALE_ADMIN_UI_URL` | `https://kale-admin.example.com/`: tarayıcıların ona ulaştığı adres (https olmalı) |
| `KALE_ADMIN_CLIENT_SECRET_FILE` | `/run/secrets/kale_prod_admin_ui_client_secret` (12. bölüm) |
| `KALE_TIME_ZONE` | `Europe/Istanbul` gibi bir IANA adı; boşsa UTC. Bilinmeyen bir ad açılışta durdurur |
| `NODE_EXTRA_CA_CERTS` | Yalnızca kendi CA'nızla: CA'nın sertifikası, Node.js AuthService'e güvensin diye |

`KALE_ALLOW_INSECURE_HTTP_DEV_ONLY` ayarlanmaz (Development/Testing dışında reddedilir). Test token sayfası ve grant'i Production'da yoktur.

## 10. Reverse proxy

Örnek Caddy kullanıyor ([`deploy/Caddyfile`](../../deploy/Caddyfile)); bu bölümde yazanları yapan her proxy olur.

**AuthService için** (`auth.example.com`):

- `https://authservice:8443` adresine **yeniden şifreleyerek** gönderin ve AuthService'in sertifikasını genel adına göre doğrulayın (Caddy'de `tls_server_name`). Örnek de böyle yapıyor ve önerilen kurulum bu olmaya devam ediyor: uygulamaların gRPC çağrıları ve olay akışları HTTP/2 ister, AuthService de şifresiz HTTP/2'ye (h2c) yalnızca geliştirme ortamında izin verir. Düz HTTP yalnızca AuthService'in güvendiği bir proxy'den (`ForwardedHeaders__*`, 9. bölüm) ve yalnızca HTTP/1.1 kısımları için işe yarar; proxy, istemcinin https kullandığını `X-Forwarded-Proto` ile bildirir. Başka bir göndericiden gelen isteği AuthService `http` olarak görür ve bütün OpenID Connect uçlarına `400 Bad Request` döner. TLS'e hiç dokunmadan ileten bir katman 4 proxy (SNI'ye göre yönlendirme) de olur **(denenmedi)**.
- **İstemcilerin gerçek adresleri:** proxy'yi `ForwardedHeaders__KnownProxies`/`KnownNetworks` ile tanıtın (9. bölüm); yoksa AuthService herkesi proxy'nin adresinden gelmiş görür (loglar, isteğe bağlı giriş sınırı). Caddy `X-Forwarded-For` ve `X-Forwarded-Proto` başlıklarını kendiliğinden gönderir.
- **Genel `Host` başlığını koruyun.** AuthService discovery belgesindeki adresleri bu başlıktan kurar. Denemede Caddy önce arka sunucunun adını gönderdi ve discovery `"token_endpoint": "https://authservice:8443/connect/token"` gösterdi; `header_up Host {hostport}` bunu düzeltti.
- **AuthService'e HTTP/2** (gRPC bunu ister; TLS üzerinde kendiliğinden anlaşılır) ve **uzun akışlar**: her uygulama, çalıştığı sürece olay bildirimleri için açık bir gRPC akışı tutar. Bu akışa okuma/yanıt zaman aşımı koymayın, tamponlamayın (Caddy'de `flush_interval -1`). Uygulamalar 30 saniyede bir HTTP/2 keep-alive ping gönderir ve bağlantı koparsa kendileri yeniden bağlanır; ama akışları sık sık kesen bir proxy, her seferinde uygulamaların önbelleklerini boşaltmasına yol açar.

**Yönetim arayüzü için** (`kale-admin.example.com`): TLS'i proxy'de sonlandırın, iç ağda `http://admin-ui:8083` adresine düz HTTP ile gönderin. Arayüz adreslerini `KALE_ADMIN_UI_URL`'den kurduğu için forwarded başlıklarına ihtiyacı yok.

Compose ağının içinde proxy genel adlara da cevap verir (ağ takma adları). Böylece yönetim arayüzü, sunucunun dışına çıkmadan AuthService'e genel adresinden ulaşır.

## 11. İlk açılış: veritabanı, migration, AuthService

`deploy/.env` ve sırlar hazırsa, `~/kale/deploy` klasöründe:

```bash
alias kc='podman compose -f compose.production-example.yaml'

kc up -d postgres                       # yönetilen veritabanında bunu atlayın
kc run --rm migrate                     # veritabanını günceller ve çıkar
kc up -d --no-deps authservice
podman inspect -f '{{.State.Health.Status}}' kale-prod-authservice   # "healthy" görünene kadar bekleyin
kc up -d --no-deps proxy
```

Migration, AuthService imajıyla ve yalnızca veritabanı ayarlarıyla çalışan tek seferlik bir konteynerdir (`AuthService admin migrate`). Her yeni sürüm açılmadan önce çalıştırın (17. bölüm); veritabanı zaten güncelse hiçbir şey yapmaz.

Sağlıklı bir açılışta AuthService'in logu:

```
info: AuthService.Invalidation.InvalidationNotificationListener[1211495886]
      Listening for invalidation events.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://[::]:8443
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Production
```

AuthService imzalama ve şifreleme anahtarlarını ilk açılışta kendisi üretir (15. bölüm).

## 12. İlk yönetici ve yönetim arayüzü

`AuthService admin bootstrap` tek seferde AuthService'in kendi uygulaması `auth`'u (yönetim arayüzünün istemcisi), onun `Admin` rolünü ve ilk yönetici kullanıcıyı kurar. Parolayı standart girdiden okur ve **yönetim arayüzünün istemci sırrını yalnızca bu bir kez yazar**: çıktıyı doğrudan bir Podman secret'a aktarın.

```bash
set -a; source .env; set +a
podman secret inspect --showsecret --format '{{.SecretData}}' kale_prod_admin_password \
  | podman exec -i kale-prod-authservice dotnet AuthService.dll admin bootstrap "$ADMIN_USER" "Kale Admin" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signin-oidc" \
      "https://${ADMIN_UI_HOST}:${PUBLIC_HTTPS_PORT}/signed-out" \
  | podman secret create kale_prod_admin_ui_client_secret -

kc up -d --no-deps admin-ui
```

(443 portunda adreslerdeki `:${PUBLIC_HTTPS_PORT}` kısmını çıkarabilir ya da bırakabilirsiniz; tarayıcı için ikisi aynı. Ama kayıtlı adres `KALE_ADMIN_UI_URL` ile birebir aynı olmalı.)

`podman exec … dotnet AuthService.dll` kullanın, asla `dotnet run` değil: `dotnet run` standart çıktıya kendi ilk satırını ekler ve o satır sırrın içine girer. `bootstrap` başarısız olursa nedeni ekranda görünür ve secret oluşmaz (Podman boş veriyi kabul etmez). İkinci kez çalışmaz; sır kaybolduysa 20. bölüme bakın.

Ardından `https://kale-admin.example.com/` adresinden ilk yönetici olarak giriş yapın.

**Yönetim arayüzü hakkında bilinmesi gerekenler:**

- **Yalnızca tek bir kopya çalıştırın.** Yöneticilerin girişlerini kendi belleğinde tutar; ikinci bir kopya onları tanımaz, yeniden başlatma (güncelleme, sunucunun yeniden açılması) da bütün yöneticilerin oturumunu kapatır. Yöneticiler yeniden giriş yapar; başka bir şey kaybolmaz.
- **Birden çok öğeyi değiştiren işlem tek tek gönderilir.** Örneğin bir kullanıcıya beş rol vermek beş ayrı istektir. Kale bunlardan birini reddederse (ör. son yönetici kuralı), ondan öncekiler uygulanmış olarak kalır, sonrakiler gönderilmez; form reddin nedenini gösterir. Ardından listeyi kontrol edin.

## 13. Çalıştığını kontrol edin

```bash
curl -s https://auth.example.com/.well-known/openid-configuration | jq '{issuer, token_endpoint, jwks_uri}'
```

Denemedeki sonuç (örnek CA yüzünden `--cacert certs/ca.crt` ile):

```json
{"issuer":"https://auth.kale.localhost:9443/","token_endpoint":"https://auth.kale.localhost:9443/connect/token","jwks_uri":"https://auth.kale.localhost:9443/.well-known/jwks"}
```

Üç adres de genel adres olmalı. Sonra bir tarayıcıdan yönetim arayüzüne giriş yapın.

Baştan sona bir kontrol için örnek, **SampleApp**'i de çalıştırıyor: sizin uygulamalarınızın yapacağı gibi `Kale.Auth.Client` kullanan küçük bir API (https, TLS üzerinden gRPC, geliştirme anahtarı yok). `./scripts/smoke-test.sh deploy/.env` bir kullanıcıyı giriş yaptırır, role göre izin/ret'i kontrol eder, rolü değiştirir ve değişikliğin proxy üzerinden olay akışıyla geldiğini doğrular. Gerçek sunucuda bu kontrolü bir kez yapıp sonra `sampleapp` servisini ve `sample` uygulamasını kaldırın (yönetim arayüzü → Uygulamalar → pasifleştir) ya da SampleApp'i hiç koymayıp gerçek bir uygulamayla kontrol edin.

## 14. Uygulamaları kaydetmek ve ekiplerine verilecekler

Kale kullanan her uygulama, her ortam için bir kez kaydedilir. Ya **yönetim arayüzünden** (Uygulamalar → Yeni uygulama; istemci sırrı bir kez gösterilir) ya da komut satırından:

```bash
podman exec kale-prod-authservice dotnet AuthService.dll admin create-application \
  orders "Orders" https://orders.example.com/signin-oidc https://orders.example.com/signout-callback-oidc
```

İstemci sırrını bir kez yazar. İki adres verilmezse uygulamanın yalnızca bir API'si olur (kullanıcıları ona doğrudan giriş yapamaz). Dönüş adresleri `https` olmalı (düz `http` yalnızca `localhost` için).

Uygulamanın ekibine verilecekler:

| Ne | Örnek |
|---|---|
| Kale'nin adresi (`Kale:Authority`) | `https://auth.example.com/` |
| Uygulamalarının anahtarı (`Kale:ApplicationKey`) | `orders` |
| İstemci sırrı, **güvenli bir kanaldan** (kasa, parola yöneticisi; asla e-posta ya da sohbet değil). Dosya olarak bağlarlar (`Kale:ClientSecretFile`) | — |
| Kendi CA'nız varsa onun sertifikası (`ca.crt`); sunucuları buna güvenmeli | Linux'ta .NET: sistemin güven deposu ya da örnekteki gibi `SSL_CERT_FILE`; Node.js: `NODE_EXTRA_CA_CERTS`; Windows: sertifika deposu |
| [Entegrasyon kılavuzu](../integration/guide.tr.md), özellikle 15. bölüm "Canlı ortama geçiş" | — |

Sunucuları `auth.example.com:443` adresine HTTP/2 ile ulaşabilmeli (gRPC de aynı adrese gider; `Kale:GrpcAddress` boş kalır). Sırrını kaybeden ekip yenisini alır: Uygulamalar → uygulama → Yeni sır, ya da `admin rotate-secret orders`.

**İstemci sırrını kesinti olmadan değiştirmek.** Yeni sır eskisini durdurmaz: eski sır `Applications:OldSecretValidFor` süresince (AuthService ayarı, varsayılan 7 gün) çalışmaya devam eder. Böylece uygulamanın çalışan kopyaları yeniden kurulana kadar token almaya devam eder. Kale yalnızca bir eski sır tutar: bu süre içinde ikinci kez yeni sır alınırsa daha eski olan hemen durur. Bir kopya eski sırla her kimlik doğruladığında AuthService uygulamanın adıyla bir uyarı yazar; böylece eski sırrı hâlâ kimin kullandığını görürsünüz.

1. Yeni sırrı oluşturun: Uygulamalar → uygulama → **Yeni sır**, ya da `podman exec kale-prod-authservice dotnet AuthService.dll admin rotate-secret orders` (standart çıktıya yalnızca sır yazılır, doğrudan `podman secret create --replace` komutuna verilebilir; eski sırrın bitiş zamanı standart hataya yazılır).
2. Sırrı ekibe güvenli bir kanaldan verin; ekip Podman secret'ını değiştirir ve kopyalarını tek tek yeniden oluşturur (`Kale.Auth.Client` sır dosyasını açılışta okur).
3. Eski sırla ilgili uyarı artık görünmüyorsa eski sırrı kaldırın: uygulamanın sayfasında **Eski sırrı kaldır**, ya da `admin retire-old-secret orders`. Kaldırmazsanız süresi dolunca kendiliğinden durur.

Sır sızdıysa eskisini hemen durdurun: **Yeni sır**'dan önce **Mevcut sırrı hemen durdur** kutusunu işaretleyin, ya da `admin rotate-secret orders --retire-old` kullanın. Çalışan kopyalar yeni sırrı alana kadar hata verir.

Sonra insanlara erişim verin: kullanıcılar, roller ve gruplar yönetim arayüzünde. Uygulama, açılırken kendi operasyonlarını kendisi kaydeder.

## 15. Anahtarlar: yapacak bir şey yok

AuthService token imzalama ve şifreleme anahtarlarını kendisi üretir, yayınlar, değiştirir ve siler; hepsi veritabanında (`server_keys`): 30 günde bir yeni anahtar, kullanılmaya başlamadan bir gün önce yayınlanır, yerine yenisi geçtikten sonra bir gün daha tutulur. Bütün kopyalar aynı anahtarları paylaşır ve her kopya onları saatte bir yeniden okur; yeniden başlatma gerekmez. Uygulamalar genel anahtarları `/.well-known/jwks` adresinden alır.

Bu anahtarlar ASP.NET Core Data Protection ile şifrelidir. Data Protection'ın kendi anahtar halkası da veritabanındadır (`data_protection_keys`) ve **Data Protection sertifikasıyla** şifrelenir. Data Protection kendi anahtarlarını da kendisi yeniler (90 günde bir).

Yani zincir şöyle: sertifika → `data_protection_keys` → `server_keys` → her token. Halkalardan biri kaybolursa **AuthService açılmaz** ("Unable to retrieve the decryption key", 20. bölüm); uygulamalar da önbellekteki cevapları bitince (5 dakika) istekleri reddetmeye başlar. Yedeklerde üçünün de bulunması bu yüzden. Halka geri gelmeyecek şekilde kaybolduysa yeni anahtarlarla nasıl baştan başlanacağı 16. bölümde.

## 16. Yedek ve geri yükleme

Yedeklenecekler:

1. **Veritabanının tamamı** (`data_protection_keys` ve `server_keys` içinde; ikisi aynı andan gelmeli).
2. **Data Protection sertifikası ve özel anahtarı** (6. bölümdeki dosyalar). Veritabanı yedeklerinden ayrı, en az onlar kadar güvenli bir yerde saklayın.
3. `.env` dosyanızla birlikte `deploy/` klasörü ve TLS sertifikaları (bunlar yeniden alınabilir).

`pg_dump` ile (custom formatta) veritabanı yedeği, ve denemek için başka bir veritabanına geri yükleme:

```bash
mkdir -p backups
backup="backups/kale_auth-$(date +%F).dump"
podman exec kale-prod-postgres pg_dump -U kale -d kale_auth -Fc > "$backup"

# Geri yüklemeyi deneyin: ayrı bir veritabanı, sonra aynı sertifikayla ona bağlanan bir AuthService.
podman exec kale-prod-postgres createdb -U kale kale_auth_restore_test
podman exec -i kale-prod-postgres pg_restore -U kale -d kale_auth_restore_test --no-owner < "$backup"
```

İkisi de denemede çalıştırıldı: yedekte bir `data_protection_keys` ve iki `server_keys` satırı vardı. Geri yüklenen veritabanına **aynı sertifikayla** bağlanan bir AuthService açıldı ve `Healthy` cevabı verdi. **Başka bir sertifikayla** ise hemen durdu:

```
fail: Microsoft.Extensions.Hosting.Internal.Host[11]
      Hosting failed to start
      System.Security.Cryptography.CryptographicException: Unable to retrieve the decryption key.
```

Geri yüklemeyi bu şekilde düzenli olarak deneyin, sonra deneme veritabanını silin (`dropdb -U kale kale_auth_restore_test`). Yönetilen bir veritabanında onun kendi yedeklerini kullanın (her şey içlerinde), sertifikayı ise siz saklayın.

**Data Protection sertifikası henüz değiştirilemiyor.** AuthService anahtar halkasını yalnızca ayarlı olan tek sertifikayla çözer; yeni bir sertifika mevcut anahtarları okunamaz hale getirir (yukarıdaki hata). Bu yüzden 10 yıllık üretiliyor. Bir gün değişmesi gerekirse, herkesin yeniden giriş yapacağını hesaba katın.

**Sertifika (ya da anahtar halkası) geri gelmeyecek şekilde kaybolduysa** yeni anahtarlarla baştan başlayın: AuthService'i durdurun, veritabanını yine de yedekleyin, iki anahtar tablosunu boşaltın ve yeni bir Data Protection sertifikasıyla başlatın (yeni secret'lar, 7. bölüm):

```bash
podman exec kale-prod-postgres psql -U kale -d kale_auth -c "delete from server_keys" -c "delete from data_protection_keys"
```

AuthService açılışta yeni anahtarlar üretir (geri yüklenmiş bir kopyada denendi: açıldı, `Healthy` dedi; iki yeni `server_keys` satırı ve bir `data_protection_keys` satırı oluştu). Geri kalan her şey (kullanıcılar, roller, uygulamalar ve sırları) yerinde kalır. Önceden verilmiş bütün token'lar ve refresh token'lar geçersiz olur, yani herkes yeniden giriş yapar; uygulamalar yeni genel anahtarları kendileri alır.

## 17. Sürüm yükseltme ve sertifika yenileme

**Yeni bir sürüm:**

```bash
# 1. Yeni imajlar (4. bölüm) ve deploy/.env içinde etiketleri:
#    AUTHSERVICE_IMAGE=localhost/kale-authservice:1.0.1, ADMIN_UI_IMAGE=...
# 2. Veritabanını yedekleyin (16. bölüm).
# 3. Migration, sonra yeni sürümü başlatın:
kc run --rm migrate
kc up -d --force-recreate --no-deps authservice
```

Önce sürüm notlarını okuyun: yeni bir sürüm yeni ayarlar isteyebilir. **Bazı sürümlerde `migrate`'ten önce AuthService'i durdurmanız gerekir**: migration, yanında çalışmaya devam eden eski kodun bozacağı bir kural ekler. Bu sürümlerde şunu çalıştırın:

```bash
kc stop authservice
kc run --rm migrate
kc up -d --force-recreate --no-deps authservice
```

Durdurmadan yeni sürüm sağlıklı açılana kadar (genellikle bir dakikadan kısa) kimse giriş yapamaz; uygulamalar bu sırada yukarıda anlatıldığı gibi önbellekteki cevaplarını kullanır. Bunu gerektiren sürümler:

| Şu migration'ı içeren sürümden itibaren | Neden |
|---|---|
| `OneOpenSessionPerUserAndApplication` (veritabanı, kullanıcı ve uygulama başına tek açık oturuma izin verir) | Eski giriş kodu, kullanıcının süresi dolmuş oturumunu önce kapatmaz; yeni index de bir sonraki girişini reddeder (kod alışverişinde sunucu hatası). Migration sürerken yapılan bir giriş de migration'ı başarısız kılabilir (yeniden çalıştırmak güvenlidir). Eski bir sürüme dönmek için de aynı durdurma, önce de bu migration'ın `Down`'ı gerekir |

Denemede (`1.0.0` etiketinden `1.0.1`'e yükseltme) migration çalıştı, AuthService yeni imajla sağlıklı açıldı, imzalama anahtarı da aynı kaldı.

`--force-recreate` gerekli, çünkü podman-compose yeni imajı kendiliğinden fark etmez. podman-compose 1.6, AuthService'e bağlı konteynerleri de (proxy, yönetim arayüzü, SampleApp) yeniden başlatır: **yöneticiler yönetim arayüzünden çıkış yapmış olur** (oturumları bellekte) ve birkaç saniyelik bir kesinti olur. Uygulamalar bunu atlatır: yeniden bağlanırlar ve önbellekteki cevaplarını 5 dakikaya kadar kullanırlar. Yükseltmeyi mesai dışında yapın.

**Bir TLS sertifikasını yenilemek** (proxy'nin ya da AuthService'in): secret'ı değiştirin, sonra konteyneri yeniden oluşturun (konteyner sırlarını oluşturulurken alır):

```bash
podman secret create --replace kale_prod_authservice_tls_cert certs/authservice.crt
podman secret create --replace kale_prod_authservice_tls_key  certs/authservice.key
kc up -d --force-recreate --no-deps authservice
```

Denemede yapıldı: konteyner ardından yeni sertifikayı (yeni seri numarasıyla) sundu. Proxy için `kale_prod_proxy_tls_*` secret'larını değiştirip `proxy`'yi yeniden oluşturun.

## 18. Sunucu yeniden başlayınca

Servislerde `restart: unless-stopped` var. Rootless Podman'da sunucu yeniden başladıktan sonra geri gelmeleri için iki şey daha gerekir **(denenmedi)**:

```bash
sudo loginctl enable-linger <kullanıcı>              # kullanıcının servisleri oturum açılmadan da çalışsın
systemctl --user enable --now podman-restart.service   # yeniden başlatma politikası olan konteynerleri açılışta başlatır
```

Daha büyük bir kurulumda, compose yerine Linux'a özgü seçenek Podman'ın Quadlet'idir (konteynerler için systemd birimleri).

## 19. Sağlık kontrolü ve loglar

**Sağlık:** her konteynerin bir healthcheck'i var (`podman ps` `healthy` gösterir). AuthService'in `GET /health` ucu yalnızca sürecin çalıştığını söyler: uygulama anahtarlarını veritabanından yüklemeden açılışı bitirmediği için `healthy`, veritabanının açılışta erişilebilir olduğunu gösterir, hâlâ erişilebilir olduğunu değil. Dışarıdan izlemek için discovery belgesini (13. bölüm) kontrol edin.

**Loglar:** `podman logs kale-prod-authservice` (ve `kale-prod-admin-ui`, `kale-prod-proxy`). Metrikler gelene kadar şunlara dikkat edin:

- **Error:** "Refreshing the server keys failed"; uygulamalarda "AuthService couldn't be reached to check the session; access refused (503)", "Couldn't ask AuthService about the session; access refused" ve "AuthService rejected this application's operations".
- **Warning:** "Listening for invalidation events failed; open streams ended", "Cleanup failed"; uygulamalarda "The invalidation stream ended; reconnecting" (arada bir normal, örneğin yükseltme sırasında; sık görülüyorsa proxy akışları kesiyordur) ve "Couldn't register operations".
- **Information:** reddedilen girişler ("Sign-in to {Application} refused for user {UserId}: {Reason}").

İyi işaretler: AuthService "Listening for invalidation events." yazar; her uygulama "Receiving invalidation events from AuthService." ve "Operations registered with AuthService" yazar.

Bir uygulamanın kendi logu, Data Protection anahtarlarının konteynerde ve şifresiz saklandığı uyarısını verebilir (denemede SampleApp verdi). Bu uygulamanın kendi Data Protection'ı ile ilgili, Kale'ninkiyle değil; bir API için zararsız, web arayüzü için entegrasyon kılavuzunun 14. bölümüne bakın.

## 20. Sorun giderme

| Belirti | Neden ve çözüm |
|---|---|
| AuthService açılışta duruyor: "Set Auth:Issuer…", "Set DataProtection:CertificatePath…", "… is only allowed in Development and Testing" | Eksik bir üretim ayarı ya da bir geliştirme anahtarı (9. bölüm). Mesaj hangisi olduğunu söyler. |
| "Kestrel is set to serve HTTP/2 without TLS" | Bir `http://` ucunda `Protocols=Http2` var. Yalnızca 9. bölümdeki https ucunu kullanın. |
| "Hosting failed to start … Unable to retrieve the decryption key" | Data Protection sertifikası veritabanıyla eşleşmiyor (başka bir sertifika, ya da anahtar halkası olmadan geri yüklenmiş bir veritabanı). Veritabanı hangi sertifikayla kurulduysa onu kullanın; kaybolduysa yeni anahtarlarla baştan başlamak için 16. bölüme bakın. |
| Konteyner `unhealthy` kalıyor, logu temiz; kontrol "certificate verify failed" diyor | Healthcheck sertifikayı `127.0.0.1`'e göre doğruluyor. AuthService'in healthcheck'inde `--no-check-certificate` kalsın (örnekteki gibi). |
| Proxy üzerinden bütün OpenID Connect adresleri `400 Bad Request` dönüyor | Proxy düz HTTP gönderiyor. `https://…:8443` adresine yeniden şifreleyerek gönderin (10. bölüm). |
| Discovery'de `https://authservice:8443/...` görünüyor | Proxy genel `Host` başlığını iletmiyor (10. bölüm). |
| Bir uygulama bağlanamıyor: "The SSL connection could not be established", "The remote certificate is invalid because of errors in the certificate chain: PartialChain" (`OperationRegistrationService` ve `InvalidationListener` uyarıları) | Uygulama sizin CA'nıza güvenmiyor (14. bölüm). Denemede SampleApp `SSL_CERT_FILE` olmadan başlatılınca görüldü. |
| Uygulamalar birkaç dakikada bir "The invalidation stream ended; reconnecting" yazıyor | Aradaki bir şey (proxy, yük dengeleyici, güvenlik duvarının boşta kalma süresi) uzun HTTP/2 akışlarını kesiyor. O yol için zaman aşımını kaldırın. |
| Olaylar uygulamalara hiç ulaşmıyor | AuthService "Listening for invalidation events." yazmalı; dinlemenin başarısız olduğunu yazıyorsa veritabanı bağlantısına bakın (transaction havuzlaması `LISTEN`'i bozar, 8. bölüm). |
| `bootstrap` `auth`'un zaten olduğunu söylüyor ama yönetim arayüzünün sırrı kayıp | `auth`'a yeni bir sır verin: `podman exec kale-prod-authservice dotnet AuthService.dll admin rotate-secret auth \| podman secret create --replace kale_prod_admin_ui_client_secret -`, sonra `kc up -d --force-recreate --no-deps admin-ui`. |
| Hiçbir yönetici giriş yapamıyor | Devir teslim belgesinin İngilizce metni, 9.3 (`admin create-user` ve `admin assign-role` ile yeni bir yönetici). Hesap kilidi 15 dakikada kendiliğinden açılır; `admin unlock <kullanıcı>` hemen açar (parola değişmez), `admin set-password` da açar. Telefon kaybolduysa (iki adımlı giriş): `admin reset-two-factor <kullanıcı>`. |
| `podman compose` 443 portunu yayınlayamıyor: "permission denied" | Rootless Podman ve düşük portlar (3. bölüm). |
| Yönetim arayüzü açılışta duruyor: "KALE_ADMIN_UI_URL must be an https address", "KALE_TIME_ZONE … is not a known time zone" | Ayarları (9. bölüm). |

Daha fazlası için [devir teslim belgesinin 12. bölümü (İngilizce)](../handover/handover.en.md#12-troubleshooting-server-and-dev-stack); uygulama tarafı için [entegrasyon kılavuzunun 16. bölümü](../integration/guide.tr.md#16-sorun-giderme).

## 21. Windows notları

Kale Windows'ta Podman machine ile (Podman'ın WSL 2 üzerindeki Linux sanal makinesi) aynı şekilde çalışır. Farklı olanlar **(bu kılavuz için Windows'ta denenmedi; geliştirme ortamından biliniyor)**:

- **Önce makineyi başlatın:** her Windows yeniden başlatmasından sonra `podman machine start`. Konteynerler onunla birlikte, ancak yeniden başlatma politikaları izin veriyorsa açılır. Kendiliğinden başlasın isterseniz `podman machine start`'ı Görev Zamanlayıcı'ya oturum açılışında çalışacak şekilde ekleyin.
- **Kabuk komutlarını Git Bash'te çalıştırın** (`openssl` onda var, betikler de bash). Sırları PowerShell'den değil Git Bash'ten oluşturun: PowerShell boru ile aktarılan bir sırrın içine BOM ya da CR LF ekleyebilir; parola o zaman çalışmaz.
- **Satır sonları:** depo metin dosyalarını LF ile tutar. Bir editör `deploy/.env` dosyasını CR LF ile kaydederse değerlerin sonuna görünmez bir `\r` eklenir ve adlar çözülmez. LF ile kaydedin.
- **CA'nıza güvenmek** (Edge ve Chrome Windows'un deposunu kullanır), PowerShell'de: `Import-Certificate -FilePath deploy\certs\ca.crt -CertStoreLocation Cert:\CurrentUser\Root`. Firefox'un kendi deposu var.
- `*.localhost` adları Edge'de, Chrome'da ve `curl.exe`'de bu makineye çözülür; 2. bölümdeki örnek olduğu gibi çalışır.
- **443 portu:** makinenin içinde yayınlanamıyorsa ya yüksek bir port kullanın (örnekteki 9443 gibi) ya da sanal makinede düşük portlara izin verin: `podman machine ssh "echo net.ipv4.ip_unprivileged_port_start=443 | sudo tee /etc/sysctl.d/90-kale-ports.conf && sudo sysctl --system"`.
- **TLS trafiğini inceleyen bir kurum proxy'si** imaj çekmeyi ve derlemeyi sertifika hatalarıyla bozar: Podman makinesinin o proxy'nin kök sertifikasına güvenmesi gerekir (bkz. PR #68).
