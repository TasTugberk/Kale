# Uygulamanızı Kale'ye bağlamak

[English](guide.en.md) · **Türkçe**

> **Yeni misiniz?** [Hızlı başlangıç](quickstart.tr.md) bir API'yi beş adımda korur. Bu kılavuz her şeyi ayrıntılı anlatır.

Bu kılavuz, uygulamalarında kullanıcı girişini ve "kim neyi yapabilir" kararını **Kale**'ye bırakmak isteyen geliştiriciler içindir. Sıfırdan başlar: Kale, OpenID Connect veya OAuth bilmeniz gerekmez. İçindeki her kod örneği gerçek bir Kale'ye karşı çalıştırılarak denenmiştir.

Örnekler **Orders** (Siparişler) adlı küçük bir uygulama kurar. İki parçası vardır:

- **Orders.Api**: uç noktaları (endpoint) Kale ile korunan bir ASP.NET Core API'si.
- **Orders.Web**: bir web arayüzünün (ör. React) sunucusu: kullanıcıları `Kale.Auth.Web` paketiyle Kale üzerinden giriş yaptırır ve API'yi çağırır.

Sizin uygulamanızda yalnızca bir API ya da farklı bir arayüz olabilir; ihtiyacınız olan kısımları alın.

## İçindekiler

1. [Kale uygulamanız için ne yapar](#1-kale-uygulamanız-için-ne-yapar)
2. [Karşılaşacağınız kavramlar](#2-karşılaşacağınız-kavramlar)
3. [Parçalar birbiriyle nasıl konuşur](#3-parçalar-birbiriyle-nasıl-konuşur)
4. [Başlamadan önce](#4-başlamadan-önce)
5. [Adım 1: Uygulamanızı Kale'ye kaydedin](#5-adım-1-uygulamanızı-kaleye-kaydedin)
6. [Adım 2: Kale.Auth.Client paketini ekleyin](#6-adım-2-kaleauthclient-paketini-ekleyin)
7. [Adım 3: Kullanıcıların yapabileceklerini listeleyin (işlemler)](#7-adım-3-kullanıcıların-yapabileceklerini-listeleyin-işlemler)
8. [Adım 4: API'nizi koruyun](#8-adım-4-apinizi-koruyun)
9. [Adım 5: Çalıştırın](#9-adım-5-çalıştırın)
10. [Adım 6: Kişilere erişim verin](#10-adım-6-kişilere-erişim-verin)
11. [Adım 7: Web arayüzünüzden kullanıcıları giriş yaptırın](#11-adım-7-web-arayüzünüzden-kullanıcıları-giriş-yaptırın)
12. [Diğer arayüzler ve diller (düz HTTP)](#12-diğer-arayüzler-ve-diller-düz-http)
13. [Kale nasıl davranır (ne beklemeli)](#13-kale-nasıl-davranır-ne-beklemeli)
14. [Canlı ortama geçiş](#14-canlı-ortama-geçiş)
15. [Sorun giderme](#15-sorun-giderme)
16. [Başvuru](#16-başvuru)

---

## 1. Kale uygulamanız için ne yapar

Kale merkezi bir giriş ve yetki servisidir. Her uygulama kendi kullanıcılarını, parolalarını ve yetki kurallarını tutmak yerine hepsi Kale'ye sorar.

- **Kale'nin işi:** kullanıcı hesapları ve parolalar, giriş sayfası (Türkçe ve İngilizce), roller, gruplar, kimin hangi uygulamayı kullanabileceği ve oturumlar (sonlandırmak dahil).
- **Uygulamanızın işi:** içinde *neler* yapılabileceğini söylemek (**işlemler**, ör. "siparişleri oku", "siparişi onayla") ve her uç noktayı gereken işlemle işaretlemek.

Hangi kişinin hangi işlemleri alacağına, bir yönetici Kale'nin yönetim arayüzünde roller aracılığıyla karar verir. Yetkiler değiştiğinde kodunuz değişmez; değişiklik o sırada giriş yapmış kullanıcılar için bile hemen geçerli olur.

## 2. Karşılaşacağınız kavramlar

| Kavram | Anlamı | Orders örneğinde |
|---|---|---|
| **Uygulama** (application) | Uygulamanızın Kale'deki kaydı. Bir **anahtarı** (kısa, kalıcı adı), bir **istemci sırrı** (client secret; Kale'ye karşı parolası) ve Kale'nin kullanıcıları geri gönderebileceği adresleri vardır. | anahtar `orders` |
| **İşlem** (operation) | Kullanıcının uygulamanızda yapabileceği tek bir şey. Kodda siz tanımlarsınız; Kale `{anahtar}.{Ad}` olarak saklar. | `orders.OrderRead`, `orders.OrderCreate` |
| **Rol** | Bir uygulamadaki işlemlerin adlandırılmış kümesi; yönetici oluşturur. | `Clerk` = `orders.OrderCreate` |
| **Grup** | Aynı rolleri alan kullanıcılar kümesi (ekipler için pratik). | `Depo ekibi` |
| **Erişim** | Kullanıcının uygulamanızı kullanıp kullanamayacağı. Giriş için etkin erişim **ve** bir rol gerekir. | |
| **Oturum** (session) | Bir kullanıcının bir uygulamaya, seçtiği tek bir rolle yaptığı bir giriş. En fazla 8 saat sürer. | |
| **Erişim belirteci** (access token) | Arayüzünüzün her istekte API'nize gönderdiği değer (`Authorization: Bearer …`). 10 dakika geçerlidir; yalnızca kullanıcıyı ve oturumu belirtir. | |
| **Yenileme belirteci** (refresh token) | Arayüzünüzün kullanıcıya yeniden sormadan yeni bir erişim belirteci almasını sağlar. Her yenileme yanıtında yenisi gelir: her zaman en yenisini saklayın. | |

## 3. Parçalar birbiriyle nasıl konuşur

```
 Tarayıcı                Orders.Web                   Kale (AuthService)              Orders.Api
    |   siteyi aç            |                                |                             |
    |----------------------->|  giriş yok: Kale'ye git        |                             |
    |<-----------------------|------------------------------->|                             |
    |   Kale giriş sayfası (kullanıcı adı, parola; birden çok rol varsa rol)                |
    |<------------------------------------------------------->|                             |
    |   tek kullanımlık bir kodla Orders.Web'e dönüş          |                             |
    |----------------------->|  kod + sır -> belirteçler      |                             |
    |                        |------------------------------->|                             |
    |                        |  GET /orders  (Bearer erişim belirteci)                      |
    |                        |------------------------------------------------------------->|
    |                        |                                |  oturum etkin mi?           |
    |                        |                                |  rolünün işlemleri neler?   |
    |                        |                                |  (gRPC, önbellekli)         |
    |                        |                                |<----------------------------|
    |                        |  siparişlerle 200 ya da 403                                  |
    |                        |<-------------------------------------------------------------|
```

- Orders.Api her belirtecin imzasını kendisi doğrular. Oturumun hâlâ etkin olup olmadığını ve rolünün hangi işlemlere sahip olduğunu Kale'ye (gRPC ile) sorar. Yanıtlar kısa bir süre önbellekte tutulur.
- Bir yönetici bir oturumu sonlandırdığında ya da bir rolü değiştirdiğinde Kale bunu (RabbitMQ ile) duyurur ve API'niz önbellekteki yanıtı yaklaşık bir saniye içinde unutur. RabbitMQ yoksa (yerel ortama karşı `dotnet run` ile çalıştırdığınız bir uygulamada olduğu gibi; yerel ortam RabbitMQ'yu makinenize açmaz) önbellekteki yanıtların süresi dolar: bir oturum için en fazla 20 saniye, bir rolün işlemleri için 5 dakika.

## 4. Başlamadan önce

Gerekenler:

- **.NET 10 SDK** (`dotnet --version` `10.x` göstermeli).
- **Çalışan bir Kale** ve adresi:
  - **Yerel geliştirme için** Kale deposundaki ortamı çalıştırın: `podman machine start`, ardından `./scripts/dev-stack.sh`. Kale `http://auth.kale.localhost:8080/` (giriş ve belirteçler) ve `http://localhost:8081/` (gRPC, yalnızca yerel geliştirme) adreslerindedir. Yönetim arayüzü `http://localhost:8083/` adresindedir.
  - **Ortak bir ortamda** Kale ekibinden `https://` adresini isteyin.
- **Bir Kale yönetici hesabı** (uygulamanızı kaydetmek ve kişilere rol vermek için). Yerelde bu `admin` kullanıcısıdır; parolasını `podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password` gösterir.

## 5. Adım 1: Uygulamanızı Kale'ye kaydedin

Uygulamanız için bir **anahtar**, bir **ad** ve Kale'nin kullanıcıları geri gönderebileceği adresler gerekir.

- **Anahtar:** 2 ile 63 karakter arası: küçük harf, rakam ve `-`; harfle başlar (ör. `orders`). Kalıcıdır: hem istemci kimliği (client id) hem belirtecin hedef kitlesi (audience) hem de işlemlerinizin önekidir.
- **Giriş dönüş adresi:** web arayüzünüzün girişten sonra kullanıcıyı karşıladığı adres. ASP.NET Core'un OpenID Connect işleyicisiyle (adım 7) bu `https://<siteniz>/signin-oidc` olur.
- **Çıkış dönüş adresi:** `https://<siteniz>/signout-callback-oidc`.
- Adresler `https://` olmalıdır. Düz `http://` yalnızca `localhost` için ve yalnızca yerel geliştirme için çalışan bir Kale'de kabul edilir; ortak bir Kale yalnızca `https://` kabul eder.

**Yönetim arayüzünde:** **Uygulamalar** sayfasını açın, **Yeni uygulama**'ya basın, formu doldurup **Oluştur**'a basın. Pencere **istemci sırrını bir kez** gösterir: hemen kopyalayıp güvenle saklayın. Kale sırrın yalnızca özetini (hash) tutar, yeniden gösteremez. Kaybolursa uygulamayı açıp **Yeni sır** düğmesini kullanın (eski sır hemen geçersiz olur).

**Yerelde tek adımda, projeleriniz oluştuktan sonra:** `./scripts/register-app.sh orders --web http://localhost:5199 <Orders.Api klasörü> <Orders.Web klasörü>` uygulamayı kaydeder ve her ayarı (sır dahil) listelenen bütün projelerin user secrets'ına yazar. Bu adımın kalanını ve 4. ile 7. adımlardaki `dotnet user-secrets` komutlarını atlayabilirsiniz. Uygulamanın bütün projelerini bu tek komutta listeleyin: komutu sonra yeniden çalıştırmak `--new-secret` gerektirir ve bu, listelediğiniz her yerde sırrı değiştirir. Bkz. [hızlı başlangıç](quickstart.tr.md).

**Ya da yerelde bir komutla:**

```bash
podman exec -i kale-authservice dotnet AuthService.dll admin create-application \
  orders "Orders" http://localhost:5199/signin-oidc http://localhost:5199/signout-callback-oidc
```

Komut yalnızca istemci sırrını yazdırır; doğrudan bir sır deposuna aktarabilirsiniz (adım 4).

> İstemci sırrı bir paroladır. Asla depoya göndermeyin (commit). `appsettings.json` dosyasına, compose dosyasındaki bir ortam değişkenine ya da bir loga koymayın. Yerelde .NET user secrets kullanın, konteynerlerde dosya olarak bağlanan bir Podman secret kullanın (adım 4).

## 6. Adım 2: Kale.Auth.Client paketini ekleyin

`Kale.Auth.Client`, API'nizin kullandığı pakettir. nuget.org'da yoktur: Kale ekibi onu `scripts/publish-client.sh <kaynak>` ile (kaynağın API anahtarı standart girdiden) şirketinizin NuGet kaynağına yayınlar. Bir kaynaktan almak için projenizin klasöründe kaynağı (`dotnet new nugetconfig`, sonra `dotnet nuget add source <kaynak> --name kale --configfile nuget.config`) ve paketi ekleyin, aşağıdaki derlemeyi atlayın. Kaynak yoksa Kale deposundan oluşturun:

**Paketleri oluşturun** (Kale deposunda):

```bash
dotnet pack src/Auth.Contracts -c Release -o ./packages
dotnet pack src/Auth.Client -c Release -o ./packages
```

Bu, `Kale.Auth.Client.<sürüm>.nupkg` ve `Kale.Auth.Contracts.<sürüm>.nupkg` dosyalarını üretir (istemcinin ikisine de ihtiyacı var).

**Çözümünüzü bu paketlere yönlendirin:** çözümünüzün yanına bir `nuget.config` koyun:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="kale" value="/path/to/kale/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

**API projesini oluşturup paketi ekleyin:**

```bash
dotnet new web -n Orders.Api
cd Orders.Api
dotnet add package Kale.Auth.Client
```

> NuGet bir paket sürümünü makinenizde önbelleğe alır. Kale ekibi bir değişikliği yayınlarken sürümü artırır. Paketleri bir değişiklikten sonra kendiniz oluşturuyorsanız önce Kale'nin `Directory.Build.props` dosyasındaki `VersionPrefix` değerini artırın; yoksa NuGet elindeki eski kopyayı kullanmaya devam eder.

## 7. Adım 3: Kullanıcıların yapabileceklerini listeleyin (işlemler)

Uygulamanızda kullanıcının yapabileceği her şeyi tek bir `enum` içinde yazın. Her üye Kale'de bir işlem olur (`orders.OrderRead`, ...).

```csharp
// OrderOperations.cs
using Auth.Client;

namespace Orders.Api;

/// <summary>Bu uygulamada bir kullanıcıya izin verilebilecek her şey.</summary>
public enum OrderOperations
{
    OrderRead,

    // Sipariş oluşturabilen, siparişleri okuyabilir de.
    [Implies(OrderRead)] OrderCreate,

    OrderApprove,
}
```

- **`[Implies(...)]`:** bu işleme sahip olan, listelenenlere de sahip olur. Böylece uç noktalar yalnızca gereken en alt işlemi belirtir. Kapsamalar zincir oluşturabilir ama döngü oluşturamaz.
- **Adlar:** İngilizce harfler (A–Z, a–z), rakam ve `_`; harfle ya da `_` ile başlar; en fazla 100 karakter. `orders.` önekini kendiniz eklemeyin. Türkçe harfler (ç, ğ, ı, ö, ş, ü) derlenir ama Kale bütün listeyi reddeder: uygulamanız hiç işlem kaydedilmeden çalışır ve logunda `AuthService rejected this application's operations: …` yazar.
- **Listeyi değiştirmek:** uygulamanız açılırken listeyi Kale'ye gönderir. Yeni üyeler eklenir. Kaldırdığınız üyeler **geçersiz** (obsolete) olur: artık rollere verilemez ve bu işlemler için yapılan kontroller başarısız olur.
- **Üyeleri yeniden adlandırmayın.** Ad değişikliği yeni bir işlemdir; eskisi geçersiz olur ve bütün roller onu kaybeder. Yeni adı ekleyin, yöneticiden rollere vermesini isteyin, sonra eskisini kaldırın.

## 8. Adım 4: API'nizi koruyun

### Program.cs

```csharp
using Auth.Client;
using Orders.Api;

var builder = WebApplication.CreateBuilder(args);

// Her ayar "Kale" yapılandırma bölümünden gelir (appsettings, user secrets, ortam). Konteynerde bunun yerine
// Kale:ClientSecretFile ve Kale:RabbitMqPasswordFile bağlanmış sır dosyalarını gösterir.
builder.AddKaleAuth<OrderOperations>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var orders = new List<Order> { new(1, "Coffee beans", Approved: false) };

// Herkes ([Authorize] yok).
app.MapGet("/health", () => "OK");

// orders.OrderRead'e (ya da onu kapsayan orders.OrderCreate'e) sahip kullanıcılar.
app.MapGet("/orders", [Authorize(OrderOperations.OrderRead)] () => orders);

app.MapPost("/orders", [Authorize(OrderOperations.OrderCreate)] (NewOrder input) =>
{
    var order = new Order(orders.Count + 1, input.Item, Approved: false);
    orders.Add(order);
    return Results.Created($"/orders/{order.Id}", order);
});

// İki öznitelik de sağlanmalı (VE): OrderApprove ve OrderRead (doğrudan ya da OrderCreate üzerinden).
app.MapPost("/orders/{id:int}/approve",
    [Authorize(OrderOperations.OrderApprove)] [Authorize(OrderOperations.OrderRead)] (int id) =>
    {
        var index = orders.FindIndex(o => o.Id == id);
        if (index < 0)
        {
            return Results.NotFound();
        }

        orders[index] = orders[index] with { Approved = true };
        return Results.Ok(orders[index]);
    });

// Çağıran kim: kullanıcının kimliği ("sub") ve Kale oturumu ("sid").
app.MapGet("/me", [Authorize(OrderOperations.OrderRead)] (HttpContext context) => new
{
    UserId = context.User.FindFirst("sub")?.Value,
    SessionId = context.User.FindFirst("sid")?.Value,
});

app.Run();

record Order(int Id, string Item, bool Approved);
record NewOrder(string Item);
```

**`[Authorize]` kuralları:**

- `Auth.Client`'ın `[Authorize(...)]` özniteliğini **kendi** işlem enum'unuzun üyeleriyle kullanın. (Bir uç nokta başka bir enum kullanırsa uygulama açılmaz.)
- **Birden çok işlem içeren tek öznitelik VEYA demektir:** `[Authorize(OrderOperations.OrderCreate, OrderOperations.OrderApprove)]` ikisinden birine sahip kullanıcıları içeri alır.
- **Birden çok öznitelik VE demektir:** hepsi sağlanmalıdır.
- **Öznitelik yoksa uç nokta herkese açıktır:** belirteç olmadan bile herkes çağırabilir.
- Controller ve action'larda da aynı şekilde çalışır:

```csharp
[ApiController]
[Route("orders")]
[Authorize(OrderOperations.OrderRead)]           // her action OrderRead ister...
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok();

    [HttpPost("{id:int}/approve")]
    [Authorize(OrderOperations.OrderApprove)]    // ...bu ayrıca OrderApprove da ister (VE)
    public IActionResult Approve(int id) => Ok();
}
```

- **Ad çakışması:** ASP.NET Core'un `Microsoft.AspNetCore.Authorization` içinde kendi `[Authorize]` özniteliği vardır. Bir dosya o namespace'i de içeri alırsa derleyici ikisini ayırt edemez (CS0104 hatası); o `using`'i eklemeyin ya da `[Auth.Client.Authorize(...)]` yazın.
- **`[AllowAnonymous]` uç noktayı herkese açar:** Kale'nin kontrolünü de atlar. Yalnızca gerçekten giriş gerektirmeyen uç noktalarda kullanın.

**Çağıran kim:** erişim belirtecinde kullanıcıyla ilgili yalnızca iki claim vardır: `sub` (kullanıcının Kale'deki kimliği) ve `sid` (oturum kimliği). Ad, e-posta, rol ya da işlem içermez. Kullanıcılarla ilgili kendi verilerinizi `sub` ile eşleyerek saklayın. İşlemlere belirteç değil Kale karar verir; bu yüzden bir rol değişikliği hemen geçerli olur. Kullanıcının adı ise girişle gelir: arayüzünüzün aldığı **kimlik belirteci** (ID token) `name` (görünen ad) ve `preferred_username` (kullanıcı adı) taşır; değerler girişteki ya da son yenilemedeki hâlleridir (11. bölümdeki arayüz örneğine bakın). API'nizin bunlara ihtiyacı varsa onlara kendiniz iletin.

### Yerel geliştirme ayarları (appsettings.Development.json)

```json
{
  "Kale": {
    "Authority": "http://auth.kale.localhost:8080/",
    "ApplicationKey": "orders",
    "GrpcAddress": "http://localhost:8081/",
    "AllowInsecureHttpDevOnly": true
  }
}
```

`scripts/register-app.sh` (adım 1) bunların hepsini, sır dahil, projenin user secrets'ına yazar. Elle ayarlamak için istemci sırrı .NET user secrets'a gider (makinenizde, proje klasörünün dışında):

```bash
dotnet user-secrets init
dotnet user-secrets set "Kale:ClientSecret" "<adım 1'deki sır>"   # register-app.sh yazdıysa gerekmez
```

> `AllowInsecureHttpDevOnly` düz `http://` adreslere izin verir. Uygulamanız bu ayarla `Development` ve `Testing` dışındaki ortamlarda açılmayı reddeder.

## 9. Adım 5: Çalıştırın

```bash
dotnet run --urls http://localhost:5198
```

Logda işlemlerinizin Kale'ye ulaştığını görmelisiniz:

```
info: Auth.Client.OperationRegistrationService[...]
      Operations registered with AuthService: 3 added, 0 reactivated, 0 obsoleted, 0 unchanged.
```

Kale henüz erişilebilir değilse uygulama arka planda denemeye devam eder (bekleme süresi giderek uzar, en fazla 30 saniye) ve bu arada istekleri karşılar. Kale yanıt verene kadar kontroller güvenli tarafta kalarak reddedilir (403). API'yi şimdi çağırırsanız belirteç olmadan **401** alırsınız. Kullanıcılar, bir yönetici onlara erişim verdikten sonra içeri girebilir (sonraki adım).

## 10. Adım 6: Kişilere erişim verin

İşlemleriniz kaydedildikten sonra bunu bir yönetici Kale'nin yönetim arayüzünde yapar:

1. **Roller:** **Uygulamalar** sayfasını açın, uygulamanızın satırında **Roller**'e tıklayın. **Yeni rol** ile bir rol oluşturun (ör. `Clerk`), sonra listeden açın: **İşlem ekle** ile sahip olması gereken işlemleri seçin (ör. `orders.OrderCreate`); **Kaldır** bir işlemi geri alır.
2. **Kullanıcılar:** **Kullanıcılar** sayfasını, ardından kullanıcıyı açın. **Uygulama erişimleri** bölümünde uygulamanızı seçip **Erişim ver**'e basın. **Roller (doğrudan)** bölümünde **Rol ata** ile rolü seçin.
3. **İsterseniz gruplar:** rolleri tek tek atamak yerine **Gruplar** sayfasında rolü ve kullanıcıları bir gruba ekleyin (**Rol ekle**, **Üye ekle**).

Bir kullanıcı uygulamanıza yalnızca **etkin erişimi** ve en az **bir rolü** (doğrudan ya da grup üzerinden) varsa giriş yapabilir. Birden çok rolü varsa, erişiminde bir **varsayılan rol** yoksa girişte birini seçer.

## 11. Adım 7: Web arayüzünüzden kullanıcıları giriş yaptırın

Arayüzünüz kullanıcıları **OpenID Connect** ile (yetkilendirme kodu + PKCE) giriş yaptırır. Kale'deki uygulamalar *gizli istemcidir* (confidential): kodu belirteçlerle değiştirmek istemci sırrını gerektirir. Bu yüzden bu iş bir **sunucuda** yapılmalıdır: sunucu tarafında çalışan bir web uygulamasında ya da bir backend-for-frontend'de. Yalnızca tarayıcıda çalışan bir uygulama (SPA, ör. React) sırrı saklayamaz: onu sunan ya da önünde duran ASP.NET Core projesi kullanıcıyı giriş yaptırır ve belirteçleri tutar; tarayıcıda yalnızca bir çerez bulunur.

**`Kale.Auth.Web`** paketi bunların hepsini iki satırla yapar. Aşağıda bir React arayüzünün sunucusu olan **Orders.Web** var. Orders.Api ile aynı Kale uygulamasını (anahtar `orders`) kullanır, çünkü Kale'nin belirteçleri tek bir hedef kitle için üretilir: uygulama anahtarı.

```bash
dotnet new web -n Orders.Web
cd Orders.Web
dotnet add package Kale.Auth.Web
dotnet user-secrets init
dotnet user-secrets set "Kale:ClientSecret" "<adım 1'deki sır>"   # register-app.sh yazdıysa gerekmez
```

(NuGet beslemeniz yoksa Kale deposunda diğerlerinin yanına derleyin: `dotnet pack src/Auth.Web -c Release -o ./packages`. `Kale.Auth.Client`'a ihtiyaç duymaz.)

`appsettings.Development.json` (bir API'ninkiyle aynı `Kale` bölümü):

```json
{
  "Kale": {
    "Authority": "http://auth.kale.localhost:8080/",
    "ApplicationKey": "orders",
    "AllowInsecureHttpDevOnly": true
  },
  "OrdersApi": "http://localhost:5198/"
}
```

`Program.cs`:

```csharp
using Auth.Web;

var builder = WebApplication.CreateBuilder(args);

// Kale üzerinden giriş: tüm ayarlar "Kale" bölümünden gelir (konteynerde Kale:ClientSecretFile).
builder.AddKaleSignIn();

// Orders.Api, giriş yapmış kullanıcı adına çağrılır: AddKaleAccessToken kullanıcının erişim belirtecini ekler.
builder.Services.AddHttpClient("orders", http => http.BaseAddress = builder.Configuration.GetValue<Uri>("OrdersApi"))
    .AddKaleAccessToken();

var app = builder.Build();

app.UseDefaultFiles();   // wwwroot'taki React derlemesi
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// GET /auth/login, POST /auth/logout, GET /auth/me
app.MapKaleSignIn();

// Arayüzün API'si: Orders.Api'nin yanıtını aynen iletir.
app.MapGet("/api/orders", async (IHttpClientFactory httpClients) =>
{
    using var response = await httpClients.CreateClient("orders").GetAsync("orders");
    return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

app.Run();
```

İkisini de çalıştırın (Orders.Api'de `dotnet run --urls http://localhost:5198`, Orders.Web'de `dotnet run --urls http://localhost:5199`); `http://localhost:5199` web adresi olarak kayıtlı olmalıdır (`register-app.sh ... --web http://localhost:5199`, adım 1). Kale deposundaki `samples/SampleWeb` aynı şeydir; React'in yerinde küçük bir sayfa vardır.

**Arayüzün kullandıkları:**

| İstek | Ne yapar |
|---|---|
| `GET /auth/login?returnUrl=/orders` | Bu adresi **açın** (bir bağlantı, `location.href`): Kale'de giriş, ardından `returnUrl`'e dönüş. `returnUrl` bu sitedeki bir yol olmalıdır (başka bir şey 400 alır). |
| `POST /auth/logout?returnUrl=/` | Sayfalarınızdaki **bir form** (`fetch` değil: tarayıcının Kale'ye gitmesi gerekir): hem burada hem Kale'de çıkış yapar. Başka bir siteden gelen gönderim 403 alır. |
| `GET /auth/me` | Giriş yapmış kullanıcı için `{"sub": …, "sid": …, "name": …, "preferred_username": …}`, yoksa **401**. Kale bunları göndermediği sürece `name` ve `preferred_username` `null` olur. |

```ts
// Örneğin React'te: kim giriş yapmış, 401 gelirse ne yapılır
const me = await fetch("/auth/me");
if (me.status === 401) {
  location.href = "/auth/login?returnUrl=" + encodeURIComponent(location.pathname);
}
```

```html
<form method="post" action="/auth/logout"><button>Çıkış yap</button></form>
```

**Paketin sizin yerinize yaptıkları:**

- **Giriş:** `openid offline_access` kapsamlarıyla yetkilendirme kodu + PKCE; claim adları Kale'nin yazdığı gibi kalır (`sub`, `sid`). Giriş, bir Kale oturumu gibi **girişten itibaren en fazla 8 saat** sürer; belirteçler ne kadar yenilenirse yenilensin uzamaz. Çerez httpOnly ve SameSite=Lax'tır; Development/Testing dışında Secure'dur.
- **Taze belirteçler:** erişim belirtecinin bitmesine bir dakika kala bir sonraki istek onu yenileme belirteciyle yeniler ve yeni yenileme belirtecini saklar. Aynı kullanıcının eşzamanlı istekleri tek bir yenilemeyi paylaşır.
- **Oturum sona erdiğinde** (bir yönetici sonlandırdı, kullanıcı başka bir cihazdan giriş yaptı, erişimi ya da rolü kaldırıldı): Kale yenilemeyi reddeder ve kullanıcının **hem burada hem Kale'de** çıkışı yapılır. Bir `fetch` **401** alır; tarayıcının açtığı bir sonraki sayfa (ör. o 401'den sonra `/auth/login`) önce Kale'nin çıkışından geçer. Böylece Kale, kullanıcıyı hemen yeniden içeri almak yerine parolasını sorar; aksi hâlde bu yeni giriş, kullanıcının diğer cihazındaki oturumunu sonlandırırdı.
- **Kale'ye ulaşılamadığında:** kullanıcı giriş yapmış olarak kalır; bir sonraki istek yeniden dener. Yalnızca Kale'nin reddi birinin çıkışını yaptırır.
- **Giriş yapılmamışsa:** korunan bir uç nokta `fetch`'e **401** ile yanıt verir, açılmakta olan bir sayfayı ise Kale'nin girişine gönderir. Giriş yapılmış ama izin yoksa: **403**.
- **Kale girişi reddettiğinde** (ör. kullanıcının uygulamanıza erişimi yok): tarayıcı `?kaleError=access_denied` ile `returnUrl`'e döner; arayüzünüz kendi mesajını gösterir.
- **Siteler arası isteklere karşı:** bir şeyi değiştiren istek (POST, PUT, PATCH, DELETE) yalnızca kendi sitenizden geliyorsa (`Origin` başlığına göre) giriş yapılmış sayılır. Çıkış da aynı denetimi yapar.

**Kendi kodunuzda kullanıcının belirteci:** `await HttpContext.GetKaleAccessTokenAsync()` güncel erişim belirtecini verir (giriş yapan yoksa null). `.AddKaleAccessToken()` eklenmiş bir `HttpClient` onu kendiliğinden gönderir; bu istemciyi kullanıcının isteğini işlerken kullanın.

**Belirteçler nerede tutulur:** sunucuda, ASP.NET Core Data Protection ile şifrelenmiş olarak; çerezde yalnızca bir anahtar bulunur (belirteçler çerezin içinde olsaydı her istekte yaklaşık 7 KB giderdi). Varsayılan olarak bellekte tutulurlar: uygulamayı yeniden başlatmak kullanıcılarının bu uygulamadaki girişini sonlandırır (bir sayfa açmak, Kale oturumları sürdükçe parola sormadan Kale üzerinden yeniden giriş yaptırır). **Birden çok örnek (instance) çalıştırıyorsanız** ortak bir `IDistributedCache` (ör. Valkey) kaydedin ve Data Protection anahtarlarını örnekler arasında paylaştırın (bölüm 14).

**API ve arayüz tek projede:** hem `builder.AddKaleAuth<OrderOperations>()` hem de `builder.AddKaleSignIn()` çağırın; sıra fark etmez. Her istek taşıdığı şeyle denetlenir: `Authorization: Bearer` belirteci varsa eskisi gibi o, yoksa tarayıcının çerezi. `[Authorize(OrderOperations.OrderRead)]` ikisinde de aynı oturum ve işlem denetimleriyle çalışır; böylece React kodunuz API'nizi düz bir `fetch("/orders")` ile çağırır, tarayıcıda belirteç bulunmaz.

**Akılda tutulacaklar:**

- **Dönüş adresleri**, Kale'ye kaydedilenlerle (adım 1) port dahil **birebir aynı olmalıdır**. Ters vekil sunucunun (reverse proxy) arkasındaysanız asıl şemayı ve sunucu adını iletin (`app.UseForwardedHeaders()`): dönüş adresi ve aynı köken denetimleri bunlardan oluşturulur.
- **API'nizden 403** "izin yok" demektir: kullanıcının rolünde işlem yoktur *ya da* oturumu az önce sona ermiştir (bir sonraki yenileme bunu fark edene kadar). "İzniniz yok" mesajı gösterin; döngü içinde yeniden denemeyin.

## 12. Diğer arayüzler ve diller (düz HTTP)

Kale standart OpenID Connect konuşur; bu yüzden her dil kullanıcıları giriş yaptırabilir. Keşif belgesi (discovery) `<Kale adresi>/.well-known/openid-configuration` adresindedir. OpenID Connect kitaplıklarının çoğu yalnızca Kale adresine, anahtarınıza ve sırrınıza ihtiyaç duyar. Kitaplığı "authorization code + PKCE" olarak, `openid offline_access` kapsamlarıyla (scope) ayarlayın.

Aynı akış elle, yerel Kale için. `orders` ve `http://localhost:5199` yerine kendi anahtarınızı ve dönüş adresinizi kullanın; dönüş adresi kayıtlı olmalıdır (`register-app.sh ... --web <adres>`).

**1. Bir PKCE çifti** (rastgele bir sır ve onun SHA-256 özeti) ve rastgele bir `state` üretin:

```bash
verifier="$(openssl rand -base64 48 | tr -d '=+/\n' | cut -c1-64)"
challenge="$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')"
state="$(openssl rand -hex 8)"
```

**2. Tarayıcıyı Kale'nin girişine gönderin:**

```
http://auth.kale.localhost:8080/connect/authorize
  ?client_id=orders
  &response_type=code
  &scope=openid%20offline_access
  &redirect_uri=http%3A%2F%2Flocalhost%3A5199%2Fsignin-oidc
  &code_challenge=<challenge>
  &code_challenge_method=S256
  &state=<state>
```

Girişten sonra Kale tarayıcıyı `http://localhost:5199/signin-oidc?code=...&state=...` adresine gönderir. `state` değerinin sizinki olduğunu kontrol edin.

**3. Kodu belirteçlerle değiştirin** (sunucunuzda; sır Basic kimlik doğrulamasıyla gider):

```bash
curl -s http://auth.kale.localhost:8080/connect/token -u "orders:<istemci sırrı>" \
  -d grant_type=authorization_code -d "code=<kod>" \
  --data-urlencode "redirect_uri=http://localhost:5199/signin-oidc" -d "code_verifier=$verifier"
```

Yanıtta `access_token` (10 dakika), `refresh_token`, `id_token`, `expires_in` (600) ve `token_type` vardır.

Oturum 2. adımda değil burada başlar: Kale kullanıcının erişimini ve rolünü yeniden denetler, uygulamanızdaki önceki oturumunu sonlandırır ve yenisini oluşturur. Kullanıcı arada erişimini (ya da rolünü) kaybettiyse yanıt `invalid_grant` olur: girişi yeniden başlatın. Belirteçlerle hiç değiştirmediğiniz bir kodun hiçbir etkisi olmaz.

**4. API'nizi** `Authorization: Bearer <access_token>` ile çağırın.

**5. Erişim belirtecinin süresi dolmadan yenileyin** (yanıttaki yeni yenileme belirtecini saklayın; eskisi 30 saniye sonra artık çalışmaz):

```bash
curl -s http://auth.kale.localhost:8080/connect/token -u "orders:<istemci sırrı>" \
  -d grant_type=refresh_token -d "refresh_token=<refresh_token>"
```

**6. Çıkış:** tarayıcıyı `id_token_hint=<id_token>` ve `post_logout_redirect_uri=<kayıtlı bir çıkış dönüş adresi>` ile `connect/endsession` adresine gönderin. Kale oturumu sonlandırır ve kendi giriş çerezini siler.

**Girişteki hatalar** dönüş adresinize `?error=...` olarak gelir:

| `error` | Anlamı |
|---|---|
| `access_denied` | Kullanıcının uygulamanıza etkin erişimi ya da rolü yok, veya uygulama devre dışı. |
| `login_required` | `prompt=none` gönderdiniz ve kullanıcı Kale'de giriş yapmamış. |
| `account_selection_required` | `prompt=none` gönderdiniz ve kullanıcının bir rol seçmesi gerekiyor. |

**İsteğe bağlı giriş parametreleri:**

- **`prompt=login`:** parolayı her zaman sor.
- **`prompt=none`:** hiçbir sayfa gösterme; bunun yerine yukarıdaki hatalardan birini döndür.
- **`max_age=<saniye>`:** son giriş bundan eskiyse parolayı yeniden sor.

**Başka bir dilde yazılmış API:** `Kale.Auth.Client` .NET içindir. Başka bir dilde yazılmış bir API için:

- JWT'yi kendiniz doğrulayın: imzayı Kale'nin JWKS'sine (keşif belgesinden) karşı, `iss` = Kale'nin adresi (keşif belgesindeki `issuer`), `aud` = anahtarınız, `typ` = `at+jwt` ve geçerlilik süresi (`exp`, `nbf`).
- Kale'nin gRPC servislerini (`src/Auth.Contracts/Protos`) bir client-credentials belirteciyle çağırın: oturumun etkin olup olmadığı için `SessionService.GetSession`, rolünün işlemleri için `RoleService.GetEffectiveOperations`.
- Bunu yapmadan önce Kale ekibiyle konuşun.

## 13. Kale nasıl davranır (ne beklemeli)

| Durum | Ne olur |
|---|---|
| Erişim belirtecinin ömrü | 10 dakika. Bir dakika kala yenileyin. |
| Oturumun ömrü | Ne kadar yenilerseniz yenileyin, girişten itibaren en fazla 8 saat (Kale'de ayarlanabilir). Sonra kullanıcı yeniden giriş yapar. |
| Oturum ne zaman başlar | Uygulamanız kodu belirteçlerle değiştirdiğinde; Kale giriş sayfasını gösterdiğinde ya da kodu gönderdiğinde değil. |
| Kullanıcı yeniden giriş yapar (başka cihaz, başka tarayıcı) | **Uygulamanızdaki** önceki oturumu, yeni girişin kodu belirteçlerle değiştirildiğinde sona erer; belirteçleri reddedilir. Diğer uygulamalardaki oturumları kalır. |
| Bir yönetici oturumu sonlandırır, kullanıcıyı devre dışı bırakır, erişimini ya da rolünü kaldırır | Oturum sona erer. API'niz belirtecini RabbitMQ ile yaklaşık bir saniye içinde, onsuz (ör. yerel ortama karşı `dotnet run`) 20 saniye içinde reddeder. Yenileme hemen reddedilir. |
| Bir yönetici bir rolün işlemlerini değiştirir | Giriş yapmış kullanıcılar için RabbitMQ ile yaklaşık bir saniye içinde, onsuz 5 dakika içinde geçerli olur. |
| Kale yanıt vermez | Önbellekteki yanıtlar süreleri dolana kadar kullanılır (bir oturum için 20 saniye, bir rolün işlemleri için 5 dakika). Sonrasında Kale 5 saniye içinde yanıt vermezse API'niz reddeder (403): Kale'nin yanıtı olmadan kimseyi içeri almaz. |
| Kullanıcının birden çok rolü var | Erişiminde varsayılan rol yoksa Kale girişte hangisini kullanacağını sorar. Bir oturumun tek rolü olur. Başka bir rolü kullanmak için kullanıcı yeniden giriş yapıp onu seçer. |

## 14. Canlı ortama geçiş

- **Kale adresi** `https://` olmalı (`Kale:Authority`). `AllowInsecureHttpDevOnly` ayarını kaldırın: uygulamanız bu ayarla Development/Testing dışında açılmaz.
- **gRPC** varsayılan olarak aynı `https://` adrese gider. Kale ekibi başka bir adres vermedikçe `Kale:GrpcAddress` ayarını boş bırakın.
- **RabbitMQ** Development/Testing dışında zorunludur: `Kale:RabbitMqUri` `amqps://<kullanıcı>@<sunucu>/` olur, parola bir sırdır (`Kale:RabbitMqPasswordFile`). RabbitMQ olmadan sonlandırılan oturumlar önbellek süresi dolana kadar çalışmaya devam ederdi; bu yüzden uygulama açılmayı reddeder.
- **Sırlar dosya olarak:** istemci sırrı ve RabbitMQ parolası bağlanmış dosyalardır (Podman secret). `Kale:ClientSecretFile` ve `Kale:RabbitMqPasswordFile` ile okunurlar, asla ortam değişkeni olarak verilmezler.
- **Canlı ortam adreslerinizi** Kale'ye kaydedin (`https://…/signin-oidc`, `https://…/signout-callback-oidc`). Her ortamın kendi Kale uygulaması ve sırrı olabilir.
- **Bir web arayüzü (`Kale.Auth.Web`)** kullanıcılarının girişlerini ASP.NET Core Data Protection ile şifreleyerek tutar. Bu anahtarları kalıcı bir yerde saklayın (ör. bağlanmış bir birimde `builder.Services.AddDataProtection().PersistKeysToFileSystem(...)`): yoksa konteynerde her yeniden başlatmada kaybolurlar ve tüm kullanıcıların çıkışı yapılır. Birden çok örnek çalıştırıyorsanız bu anahtarları paylaştırın ve ortak bir `IDistributedCache` (ör. Valkey) kaydedin; böylece her örnek her girişi tanır. Ters vekil sunucunun arkasında `app.UseForwardedHeaders()` kullanın; dönüş adresleri dışarıdan görünen `https://` adresle oluşsun.

Örnek `appsettings.Production.json` (içinde sır yok):

```json
{
  "Kale": {
    "Authority": "https://auth.example.com/",
    "ApplicationKey": "orders",
    "RabbitMqUri": "amqps://orders@rabbitmq.example.com/",
    "ClientSecretFile": "/run/secrets/orders_client_secret",
    "RabbitMqPasswordFile": "/run/secrets/orders_rabbitmq_password"
  }
}
```

## 15. Sorun giderme

| Belirti | Olası neden |
|---|---|
| Açılışta hata: "Set KaleAuthOptions.ApplicationKey and ClientSecret." | Sır ayarlanmamış. Yerelde: projenin klasöründe `dotnet user-secrets set "Kale:ClientSecret" ...`. (`dotnet run --no-build` ile, son derlemeden sonra kurulan user secrets görülmez; normal `dotnet run` kullanın.) |
| Açılışta hata: "...must use https (AllowInsecureHttpDevOnly is for local development only)." | `AllowInsecureHttpDevOnly` olmadan bir `http://` adres. |
| Açılışta bir uç noktanın `[Authorize]`'ı hakkında hata | `AddKaleAuth<T>`'ye verilenden başka bir enum'un üyelerini kullanıyor. |
| Açılışta hata: "KaleAuthOptions.AllowInsecureHttpDevOnly is only allowed in Development and Testing." | `AllowInsecureHttpDevOnly` Development/Testing dışında ayarlanmış. Kaldırın ve `https://` adresler kullanın. |
| Logda: `AuthService rejected this application's operations: …` | Bir işlem adı geçersiz (ör. Türkçe bir harf ya da 100 karakterden uzun) veya `[Implies]` döngü oluşturuyor. Log hangisi olduğunu söyler. Düzeltene kadar hiçbir işlem kaydedilmez. |
| Logda, tekrar tekrar: `Couldn't register operations with AuthService; retrying in …` | Kale'ye ulaşılamıyor (`Kale:GrpcAddress` ayarını, yerelde `http://localhost:8081/`, ve ortamın çalıştığını kontrol edin) ya da istemci sırrı yanlış (Kale uygulamanın kendi girişini reddeder). |
| Her çağrıda 401 | Belirteç yok, süresi dolmuş, ya da başka bir uygulama için üretilmiş (`aud` anahtarınız değil). |
| İzni olması gereken bir kullanıcıya 403 | Rolünde işlem yok (yönetim arayüzünde **Roller**'e bakın), oturumu sona ermiş ya da Kale'ye ulaşılamıyor. |
| Giriş sitenizde `error=access_denied` ile bitiyor (`Kale.Auth.Web` ile: `?kaleError=access_denied`) | Kullanıcının uygulamanıza etkin erişimi ya da rolü yok (adım 6), veya uygulama devre dışı. |
| Bir web arayüzünün kullanıcılarının her yeniden başlatmada ya da dağıtımda çıkışı yapılıyor | Data Protection anahtarları ya da girişler bellekte veya konteynerin içinde tutuluyor (bölüm 14). |
| Kale giriş sayfası yerine hata gösteriyor | `redirect_uri` kayıtlı bir giriş dönüş adresiyle birebir aynı değil. |
| Yenilerken `invalid_grant` | Eski bir yenileme belirteci (30 saniyeden önce yenisiyle değiştirilmiş) ya da oturum sona ermiş. Yeniden giriş yapın. |

## 16. Başvuru

**`KaleAuthOptions`** (yapılandırma bölümü `Kale`):

| Ayar | Zorunlu | Anlamı |
|---|---|---|
| `Authority` | evet | Kale'nin adresi, ör. `https://auth.example.com/`. |
| `ApplicationKey` | evet | Uygulamanızın Kale'deki anahtarı. |
| `ClientSecret` | evet | İstemci sırrınız (user secrets'tan ya da `ClientSecretFile`'dan okunur). |
| `GrpcAddress` | hayır | `Authority` değilse Kale'nin gRPC adresi (yerelde `http://localhost:8081/`). |
| `RabbitMqUri` | Development dışında | `amqps://kullanıcı@sunucu/` (yerelde `amqp://`). |
| `RabbitMqPassword` | `RabbitMqUri` ile | Konteynerlerde `RabbitMqPasswordFile`'dan okunur. |
| `AllowInsecureHttpDevOnly` | hayır | `http://` adreslere izin verir; yalnızca Development/Testing. |

`Kale.Auth.Web` (`AddKaleSignIn`) aynı bölümden `Authority`, `ApplicationKey`, `ClientSecret` (ya da `ClientSecretFile`) ve `AllowInsecureHttpDevOnly` ayarlarını okur.

**Kale'nin uç noktaları** (adresine göre):

| Yol | Kullanımı |
|---|---|
| `.well-known/openid-configuration` | Keşif (uç noktalar, anahtarlar). |
| `connect/authorize` | Tarayıcıda girişi başlatmak. |
| `connect/token` | Kod → belirteçler, yenileme. |
| `connect/endsession` | Çıkış. |

**Belirteç içerikleri** (kullanacaklarınız; belirteçler `iss`, `iat` ve `jti` gibi standart claim'ler de taşır): erişim belirteci: `sub` (kullanıcı kimliği), `sid` (oturum kimliği), `aud` (anahtarınız), `exp`, `client_id`, `scope`; tür `at+jwt`. Kimlik belirteci (ID token): `sub`, `sid`, `name` (görünen ad), `preferred_username` (kullanıcı adı).
