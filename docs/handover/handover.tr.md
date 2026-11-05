# Kale: devir teslim

*Bu belgeyi Claude (Anthropic'in yapay zekâ kodlama asistanı) yazdı. Kale'nin kodunun çoğunu da 2026-10-04 ile 2026-11-04 arasında proje sahibinin yönlendirmesiyle o yazdı. Belgedeki bilgiler 2026-11-05 tarihli. English: [handover.en.md](handover.en.md).*

Bu belge Kale'yi devralacak geliştiriciler için. Başka dosyaların zaten iyi anlattığı şeyleri tekrarlamıyor. Size nereye bakacağınızı, nelerin eksik olduğunu ve işlerin neden böyle yapıldığını anlatıyor.

**Dil.** 0–5. ve 13. bölümler iki dilde de var. 6–12. bölümler teknik ve kodla birlikte değişiyor, bu yüzden yalnızca İngilizce. İki kopya kısa sürede birbirini tutmaz olurdu. Aşağıda her birinin Türkçe kısa özeti ve İngilizce metnin bağlantısı var.

## İçindekiler

- 0 · [Bu belge nasıl okunmalı](#0-bu-belge-nasıl-okunmalı)
- 1 · [Tek sayfada Kale](#1-tek-sayfada-kale)
- 2 · [Başlangıç: ilk gününüz](#2-başlangıç-ilk-gününüz)
- 3 · [Erişimler, sırlar, paket akışı, kişiler](#3-erişimler-sırlar-paket-akışı-kişiler)
- 4 · [Açık işler, riskler ve eksikler](#4-açık-işler-riskler-ve-eksikler)
- 5 · [Önerilen sonraki adımlar](#5-önerilen-sonraki-adımlar)
- 6 · [Teknik bölümlerin özeti (6–12, yalnızca İngilizce)](#6-teknik-bölümlerin-özeti)
- 13 · [Sözlük](#13-sözlük)

---

## 0. Bu belge nasıl okunmalı

**Okuma sırası.** İlk gün 1–4. bölümleri okuyun. Gerisini kodun o kısmında çalışmaya başladığınızda okuyun.

**Hangi bilgi nerede.** İki kaynak çelişirse şu sırayla güvenin: kod, sonra `CLAUDE.md`, sonra bu belge, sonra `DESIGN.md`.

| Dosya | Ne | Ne kadar güvenilir |
|---|---|---|
| [CLAUDE.md](../../CLAUDE.md) | Kurallar ve **onaylanmış bütün kararlar**. Her PR'da güncellenir. Claude Code bu dosyayı kendi talimatları olarak da okur (İngilizce metin, 11. bölüm). | Düz yazıyla en güncel anlatım. |
| [docs/DESIGN.md](../DESIGN.md) | Proje sahibinin ilk gün Claude'a verdiği **ilk görev tanımı**: gereksinimler, aşamalar, açık konular. | Tarihî bir belge. Bugünkü durumu değil, ne istendiğini anlatır. "Open items" bölümündeki konuların hepsi sonradan karara bağlandı (`CLAUDE.md`, "Decisions on the spec's open items"). Aşama listesi 6'da biter; yönetim arayüzü sonradan 7. aşama olarak eklendi. |
| [docs/design/phase-3-openiddict.md](../design/phase-3-openiddict.md) | Onaylanmış öneri: OpenIddict'in tabloları bizim `applications` tablomuzla nasıl ilişkilenir, anahtarlar nasıl yönetilir (A seçeneği). | Güncel. |
| [docs/design/locking.md](../design/locking.md) | Hangi kodun hangi satırları hangi sırayla kilitlediği ve nedeni (İngilizce). | Güncel; bu devir teslimle eklendi. |
| [docs/integration/](../integration/README.md) | **Kale'yi kullanan uygulamaların ekipleri** için hızlı başlangıç ve ayrıntılı kılavuz (TR ve EN). Örnekleri gerçekten çalıştırıldı. | Güncel. Uygulamaların fark edeceği her değişiklikte bunu da güncelleyin. |
| Koddaki yorumlar | İlk bakışta anlaşılmayan satırların *nedeni* (bu repodaki bir kural). | Güncel. Okuyun: gerekçelerin çoğu oradadır. |

## 1. Tek sayfada Kale

**Sorun.** Proje sahibinin birkaç C# API'si vardı ve her birinde kopyala-yapıştır bir kimlik doğrulama kodu bulunuyordu. Bir hata düzeltmesi her kopyaya ayrı ayrı yapılmak zorundaydı.

**Kale nedir.**
- **AuthService:** kullanıcıları, grupları, uygulamaları, rolleri, işlemleri ve oturumları yöneten merkezî servis. Kendi giriş sayfaları olan bir OpenID Connect sunucusu (OpenIddict).
- **Kale.Auth.Client:** ince bir NuGet paketi. Uygulama paketi ekler ve uç noktalarını `[Authorize(MyOperations.X)]` ile korur. Paket veri tutmaz: her istekte (önbellekle) gRPC üzerinden AuthService'e oturumun etkin olup olmadığını ve oturumun rolünün hangi işlemlere sahip olduğunu sorar.
- **Yönetim arayüzü:** yöneticilerin her şeyi yönettiği, Türkçe ve İngilizce bir Next.js uygulaması.

**Kimler kullanır.**

| Kim | Ne yapar |
|---|---|
| Uygulama ekipleri | Uygulamalarını kaydeder, işlemleri C# enum'u olarak tanımlar ve paketi ekler (`docs/integration`). |
| Yöneticiler | Yönetim arayüzünde kullanıcı, grup ve rol oluşturur, erişim verir. |
| Son kullanıcılar | Kale'nin sayfalarında giriş yapar; birden fazla rolü varsa birini seçer. |

**2026-11-05 itibarıyla durum.**

| Aşama (`DESIGN.md`) | Durum |
|---|---|
| 0 Ortam, `CLAUDE.md` | Tamam |
| 1 Veri modeli ve migration'lar | Tamam |
| 2 Uygulama kaydı ve işlem eşitleme | Tamam |
| 3 OpenIddict, giriş, rol seçimi, oturumlar, belirteçler | Tamam |
| 4 İstemci paketi | Tamam; sürüm **1.0.0** (`Directory.Build.props`) |
| 5 Geçersiz kılma olayları (RabbitMQ, outbox) | Tamam |
| 6 Uçtan uca SampleApp, her şey `podman compose` ile | Tamam; `scripts/smoke-test.sh` doğruluyor |
| 7 Yönetim arayüzü | Tamam; `scripts/browser-test.sh` gerçek tarayıcıda doğruluyor |

O tarihten sonra yapılanlar iyileştirme ve düzeltmelerdi: giriş sayfalarının yeni tasarımı, RabbitMQ'nun ilk açılışı ve Windows desteği (beklemede, 4. bölüm).

Bunların hepsi bir ayda yapıldı: 67 PR birleştirildi ve her biri ayrı bir özellik dalıydı. Her PR'ı `pr-reviewer` ajanı inceledi, proje sahibi onayladı.

**Henüz yapılmadı:** Kale **hiç üretime ya da üretime benzer bir ortama kurulmadı**. Her şey yerelde, macOS (Apple Silicon) ve Windows 10 üzerinde, Podman ile çalıştırıldı. İlk kurulumda karara bağlanması gerekenler İngilizce metnin 9. bölümünde.

## 2. Başlangıç: ilk gününüz

**Kurun:**
- `global.json` dosyasındaki .NET SDK sürümü
- Podman ve podman-compose 1.6
- Node 22
- git ve jq

Windows'ta Git Bash de gerekir, çünkü betikler bash ile yazıldı. Kale **macOS'ta (Apple Silicon)** geliştirildi ve bütün imajlar çok mimarili.

**Windows.** Çalışıyor ama bazı adımlar elle yapılıyor:
- Bash betiklerini Git Bash'ten çalıştırın.
- Şirketin TLS denetleyen vekil sunucusunun arkasında, Podman sanal makinesi ve imaj derlemeleri vekil sunucunun sertifikasına güvenmeli.

PR #68 (`scripts/dev-stack.ps1`) bunların hepsini otomatikleştiriyor ama beklemede (4. bölüm). Açıklamasında Windows'ta karşılaşılan her sorun ve çözümü yazıyor.

**Adımlar:**

1. Podman'ı başlatın: `podman machine start`. Her yeniden başlatmadan sonra bir kez gerekir.
2. Testcontainers'a Podman'ın yerini söyleyin: `~/.testcontainers.properties` dosyasını `ryuk.disabled=true` satırı ve bir `docker.host` satırıyla oluşturun.
   - **macOS:** `docker.host=unix://<soket>`. Soketi `podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}'` verir.
   - **Windows:** `docker.host=npipe://./pipe/docker_engine`. Podman bu boruyu makinesi çalışırken sağlar.
3. Yığını başlatın: `./scripts/dev-stack.sh`. `.env` dosyasını ve sırları oluşturur, imajları derler ve PostgreSQL, RabbitMQ, migration, AuthService, SampleApp ve yönetim arayüzünü başlatır. Sonunda adresleri yazar:
   - AuthService: `http://auth.kale.localhost:8080/`
   - SampleApp: `http://localhost:8082/notes`
   - Yönetim arayüzü: `http://localhost:8083/` (kullanıcı `admin`). Parolayı `podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password` verir.
4. Çalıştığını doğrulayın: `./scripts/smoke-test.sh` (gerçek bir giriş, role göre izin ve ret, RabbitMQ üzerinden gelen bir rol değişikliği). Ardından `dotnet build && dotnet test` ve `(cd admin-ui && npm ci && npm test && npm run typecheck && npm run build)`.
5. Elle deneyin:
   - Yönetim arayüzünde bir kullanıcı oluşturun, ona `sample` uygulamasında erişim ve bir rol verin, sonra SampleApp'e o kullanıcıyla giriş yapın.
   - Ardından rolü geri alın ve bir sonraki isteğin reddedildiğini görün.
   - Yönetim arayüzündeki **Test belirteci** sayfası, web ön yüzü olmadan belirteç almanızı sağlar.
6. [docs/integration/quickstart.tr.md](../integration/quickstart.tr.md) belgesini okuyun. Diğer ekipler Kale'yi bu belgeden tanıyor.

**Durdurmak için:** `podman compose down`. Sonuna `-v` eklerseniz veritabanı ve kuyruk verisi de silinir. Bir sonraki `dev-stack.sh` veritabanını, `sample` uygulamasını ve ilk yöneticiyi yeniden kurar; `sample` ile yönetim arayüzünün istemci sırları yenilenir. PostgreSQL, RabbitMQ ve yönetici parolaları birim (volume) değil Podman sırrı olduğu için olduğu gibi kalır.

## 3. Erişimler, sırlar, paket akışı, kişiler

*Bunlar yer tutucu: proje sahibi devretmeden önce dolduracak.*

| Ne | Nerede / kim |
|---|---|
| Kod deposu | `github.com/TasTugberk/Kale`. **Kişisel bir hesap.** Bir kuruluş hesabına taşıyın ya da yeni sorumluları yönetici yapın: `<doldurulacak>` |
| Proje sahibi (kararlar, onaylar) | `<ad, iletişim>` |
| Sistemi bilen diğer kişiler | `<adlar>` |
| `Kale.Auth.Client` ve `Kale.Auth.Contracts` için NuGet akışı | `<akış adresi>`. Henüz hiçbiri ayarlı değil: `scripts/publish-client.sh <akış>` verdiğiniz akışa gönderir, API anahtarı standart girdiden okunur. 1.0.0 sürümü derlendi ama `<yayımlandı mı? nereye?>` |
| Paketi kullanmaya başlamış uygulamalar | `<liste ya da "henüz yok">` |
| Üretim ve test ortamları | `<henüz yok>` |
| Üretim sırları: veritabanı parolası, RabbitMQ parolası, yönetim arayüzünün istemci sırrı, Data Protection sertifikası ve anahtarı | `<sır deposu, kimlerin erişimi var>`. Servislere dosya olarak ulaşmalılar (İngilizce metin, 9. bölüm) |
| Üretim veritabanı yedekleri | `<nerede, ne sıklıkla, geri yüklemeyi kim deniyor>`. Birlikte yedeklenmesi gerekenler için İngilizce metnin 9. bölümüne bakın |
| Geliştirme sırları | Her geliştiricinin makinesinde, `dev-stack.sh` tarafından oluşturulan Podman sırları: `kale_postgres_password`, `kale_rabbitmq_password`, `kale_sample_client_secret`, `kale_admin_password`, `kale_admin_ui_client_secret`. Devredilecek bir şey yok: her makine kendi sırlarını üretir |
| Depoda olmayan, makineye özel ayarlar | `~/.testcontainers.properties` (2. bölüm). AuthService'i `dotnet run` ile çalıştırmak için kullanıcı sırları (`scripts/dev-db.sh` yazar) ve uygulamalar için kullanıcı sırları (`scripts/register-app.sh` yazar) |
| Claude Code ayarları | Depoda: `CLAUDE.md` ve `.claude/agents/pr-reviewer.md`. Her kullanıcının Claude Code belleği (oturumlar arasında tuttuğu notlar) o kullanıcının makinesinde, depoda değil. İçindeki önemli bilgiler bu belgede |

## 4. Açık işler, riskler ve eksikler

Aşağıdaki her satır aynı başlıkla bir GitHub issue'su olmalı. Issue metinleri yazıldı ve oluşturulmayı bekliyor (2026-11-05'te depoda hiç issue yoktu). Oluşturulunca numaralarını buraya yazın ve iş listesini bundan sonra bu tabloda değil, issue'larda tutun.

| Konu | Neden önemli | Issue |
|---|---|---|
| **CI yok** | Derleme, testler ve imaj derlemeleri yalnızca geliştiricilerin makinelerinde ve `pr-reviewer` ajanı üzerinden çalışıyor. Testleri çalıştırılmamış bir PR'ın birleştirilmesini hiçbir şey engellemiyor. | `<#>` |
| **Hiç üretimde çalışmadı** | Kestrel üzerinde TLS, `amqps`, Data Protection sertifikası ve yedekler yazıldı ve birim testleri var, ama hiç birlikte kurulmadı (İngilizce metin, 9. bölüm). | `<#>` (bir sonrakiyle birlikte) |
| **TLS'i sonlandıran bir vekil sunucunun arkasında** | AuthService yönlendirme başlıklarını (forwarded headers) işlemiyor. Böyle bir vekil sunucunun arkasında OpenIddict istekleri düz `http` görür ve reddeder. Şimdilik ya TLS'i Kestrel'in kendisinde çalıştırın ya da önce bu eksiği giderin. | `<#>` |
| **Parola değiştirilemiyor ya da sıfırlanamıyor; MFA yok** | Parola yalnızca kullanıcı oluşturulurken belirleniyor. Parolasını unutan kişiye yeni bir hesap açmak gerekiyor. `DESIGN.md` Identity'yi kısmen MFA için seçmişti ama MFA yapılmadı. | `<#>` |
| **Kilitler: ortak bir danışma kilidi ve olası bir kilitlenme** | `KeyRing` ve `LastAdminCheck` aynı danışma kilidini, `(1002, 0)`, kullanıyor; bu yüzden anahtar yenileme ile son yönetici denetimi birbirini bekliyor (zararsız, iki satırlık düzeltme). Ayrıca iki yönetici `auth` uygulamasının aynı yönetici rolünü aynı anda değiştirirse kilitlenme (deadlock) olabilir; PostgreSQL bu durumda isteklerden birini başarısız sayar. Henüz yeniden üretilmedi ([locking.md](../design/locking.md)). | `<#>` |
| **İşlem kaydı istemci önbelleklerini geçersiz kılmıyor** | Bir uygulama daha az işlemle ya da değişmiş `[Implies]` ile yeniden kurulduğunda, çalışan örnekleri eski etkin işlemleri 5 dakikaya kadar kullanmaya devam ediyor. | `<#>` |
| **Yönetim arayüzünün oturumları bellekte** | Yalnızca tek örnek çalışabilir. Yeniden başlatma bütün yöneticilerin oturumunu kapatır. | `<#>` |
| **Gizli bilgi sağlayıcı soyutlaması yok** | `DESIGN.md` bir soyutlama istemişti. Şu an sırlar yalnızca dosyadan okunuyor. Dosyaların yeterli olup olmadığına karar verin. | `<#>` |
| **Gözlemlenebilirlik** | Yalnızca `GET /health` var ve veritabanını denetlemiyor. Metrik ya da iz (trace) yok. | `<#>` |
| **Denetim kaydı (audit)** | Kararlaştırıldığı gibi yalnızca alan olayları (kancalar) var. Kimin neyi değiştirdiğine dair saklanan bir kayıt yok. | `<#>` |
| **PR #68: Windows geliştirme yığını, beklemede** | `scripts/dev-stack.ps1`. Gönderilmiş sürüm uçtan uca çalışıyor. İncelemeden gelen düzeltmeler proje sahibinin makinesinde, başka bir çalışma ağacında **commit edilmemiş** halde duruyor ve şirketin antivirüsü yeniden yazılan betiği "zararlı içerik" diye engelliyor. Karar verin: (a) BT'den betiğin yanlış alarm olarak serbest bırakılmasını isteyin; (b) ilk yöneticinin parolasını kapsayıcının içinde, Podman sırrından okuyun, böylece hiçbir sır PowerShell'den geçmez; ya da (c) PR'ı kapatın ve Git Bash yolunu belgeleyin. | #68 |

**Bilerek konan sınırlar** (karara bağlandı, hata değil; ancak bilinçli olarak değiştirin):
- Her kullanıcının her uygulamada tek bir açık oturumu olur. Başka bir yerden yeniden giriş, önceki oturumu sonlandırır.
- Tek bir yönetici düzeyi var (`auth.Admin`). Uygulama sahipleri yalnızca kendi rollerini yönetemez.
- Gruplar düz (iç içe grup yok).
- Bir enum üyesinin adını değiştirmek yeni bir işlem oluşturur; eskisi eskimiş (obsolete) olur.
- İstemci uygulamanın her örneğinin kendi önbelleği var. Ortak önbellek gerekirse Redis değil, Valkey kullanın.

## 5. Önerilen sonraki adımlar

Bu sırayla:

1. **Sahiplik.** 3. bölümü doldurun, depoyu bir kuruluş hesabına taşıyın ve 4. bölümdeki issue'ları oluşturun.
2. **CI.** CI olmadan diğer her değişiklik risk taşır.
3. **Bir test ortamına ilk kurulum**, İngilizce metnin 9. bölümündeki listeye göre. Vekil sunucu sorusunu da orada karara bağlayın.
4. **Yöneticinin parola sıfırlaması**, ardından kullanıcının kendi parolasını değiştirmesi. MFA'yı ayrı bir tasarım olarak planlayın.
5. **Küçük düzeltmeler:** ortak danışma kilidi ve işlem kaydından sonra önbelleklerin geçersiz kılınması.
6. **Gözlemlenebilirlik:** OpenTelemetry ile metrik ve izler, `/health`'e veritabanı denetimi.
7. **`Kale.Auth.Client` 1.0.0'ı yayımlayın** (şirketin paket akışına) ve entegrasyon kılavuzunu izleyerek ilk gerçek uygulamayı bağlayın. Bu uygulamadan gelecek geri bildirim, sonraki herhangi bir özellikten daha değerli.
8. **PR #68 hakkında karar verin.**

---

## 6. Teknik bölümlerin özeti

| Bölüm | Ne anlatıyor |
|---|---|
| [6. Architecture and domain](handover.en.md#6-architecture-and-domain) | Bileşenler ve her birinin sorumluluğu. Yerel yığının çalışma şeması (portlar, `auth.kale.localhost` yayımlayıcısı). Güven sınırları. Veri modeli (ER şeması), veritabanının zorladığı kurallar, işlem kapsamaları (`[Implies]`) ve ETKİN oturumun tanımı. |
| [7. How data flows](handover.en.md#7-how-data-flows) | Veri akışları: uygulama ve işlem kaydı, giriş (sıra şeması), istemci uygulamada her isteğin denetimi (önbellekler; AuthService yanıt vermezse reddetme), belirteç yenileme, bir değişikliğin outbox → RabbitMQ → istemci önbelleği yolu (sıra şeması), yönetim arayüzü (BFF), arka plan işleri, yapılandırma ve sırlar. |
| [8. Decision log](handover.en.md#8-decision-log-the-alternatives-and-why) | Her kararın alternatifleri ve gerekçesi. Kayda geçmemiş, sonradan çıkarılmış gerekçeler † ile işaretli. |
| [9. Security and production checklist](handover.en.md#9-security-and-a-server-side-production-checklist) | Yalnızca geliştirme için olan anahtarlar ve onları üretimde neyin engellediği. AuthService ve yönetim arayüzü için üretim kontrol listesi (birlikte yedeklenmesi gerekenler dahil). Hiçbir yönetici giriş yapamadığında kurtarma adımları. |
| [10. Code tour](handover.en.md#10-code-tour-where-do-i-change-x) | "X'i nerede değiştiririm?" tablosu. |
| [11. Testing and how we work](handover.en.md#11-testing-and-how-we-work) | Testler, dallar ve PR'lar, inceleme döngüsü ve Claude Code'un bu projede nasıl kullanıldığı. `CLAUDE.md`'yi değiştirmek ajanların çalışma biçimini de değiştirir. |
| [12. Troubleshooting](handover.en.md#12-troubleshooting-server-and-dev-stack) | Sunucu ve geliştirme yığını sorunları ile çözümleri. Uygulama tarafındaki sorunlar için: [kılavuz §15](../integration/guide.tr.md). |
| [locking.md](../design/locking.md) | Kilit haritası: kim neyi hangi sırayla kilitliyor, sıra kuralları, bilinen sorunlar. |

## 13. Sözlük

Temel kavramlar (uygulama, işlem, rol, grup, erişim, oturum, erişim ve yenileme belirteçleri) [kılavuzun 2. bölümünde](../integration/guide.tr.md#2-karşılaşacağınız-kavramlar). Kale'nin kodunda geçen terimler:

| Türkçe | İngilizce (kod) | Anlamı |
|---|---|---|
| Erişim satırı | Access row (`UserApplication`) | Bir kullanıcının bir uygulamaya erişimi: etkin ya da değil, isteğe bağlı bir varsayılan rolle. |
| Etkin işlemler | Effective operations | Bir role verilen işlemler ile onların kapsadığı her şey, eskimiş işlemler hariç. |
| Kapsama | Implication (`[Implies]`) | Bir işleme sahip olmak başka bir işlemi de verir. |
| Eskimiş işlem | Obsolete operation | Uygulamanın artık tanımlamadığı işlem. Silinmez ama artık verilemez. |
| ETKİN oturum | ACTIVE session | Tam tanımı İngilizce metnin 6.4 bölümünde (`SessionCheck`). |
| Uygulama belirteci | App token | Bir uygulamanın AuthService'i çağırmak için aldığı istemci kimlik bilgisi (client credentials) belirteci (`aud` = `urn:kale:auth-api`, `sid` yok). |
| Giden kutusu (outbox) | Outbox | Değişiklikle aynı işlemde kaydedilen ve sonra RabbitMQ'ya gönderilen olayların tablosu. |
| Geçersiz kılma olayı | Invalidation event | `SessionStopped`, `RoleEdited`, `UserDisabled`: istemci uygulamalara önbellekteki bir yanıtı atmalarını söyler. |
| Yayımlayıcı (issuer) | Issuer | AuthService'in herkese açık temel adresi. Her belirteçte yer alır. |
| Anahtar halkası | Key ring | `server_keys` tablosundaki anahtarları şifreleyen Data Protection anahtarları. |
| Ön yüz için arka uç (BFF) | Backend for frontend | Belirteçleri tarayıcı yerine tutan yönetim arayüzü sunucusu. |
| Kapalı kalma | Fail closed | AuthService yanıt veremezse izin vermek yerine reddetmek. |
| Süper yönetici | Superadmin (`auth.Admin`) | Tek yönetici düzeyi. |
| h2c | h2c | TLS'siz HTTP/2. Yalnızca yerel geliştirmede gRPC için kullanılır. |
