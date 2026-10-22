# Hızlı başlangıç: API'nizi Kale ile koruyun

[English](quickstart.en.md) · **Türkçe**

Makinenizde beş adım. Gerekenler: **.NET 10 SDK**, **Podman**, **jq** ve Kale deposunun bir kopyası. Geri kalan her şey (web üzerinden giriş, canlı ortam, diğer diller) [ayrıntılı kılavuzda](guide.tr.md).

## 1. Kale'yi başlatın (bir kez)

Kale deposunda:

```bash
podman machine start          # her yeniden başlatmadan sonra bir kez
./scripts/dev-stack.sh        # PostgreSQL, RabbitMQ, Kale ve yönetim arayüzü, konteynerlerde
```

Yönetim arayüzü <http://localhost:8083/> adresindedir. `admin` olarak giriş yapın; parolayı şu komut gösterir:

```bash
podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password
```

## 2. Paketi projenize ekleyin

Paketi bir kez, Kale deposunda oluşturun:

```bash
dotnet pack src/Auth.Contracts -c Release -o ./packages
dotnet pack src/Auth.Client -c Release -o ./packages
```

Sonra projenizin klasöründe (`/path/to/kale`, Kale deposunun sizdeki kopyasıdır):

```bash
dotnet new nugetconfig
dotnet nuget add source /path/to/kale/packages --name kale --configfile nuget.config
dotnet add package Kale.Auth.Client
```

## 3. Projenizi Kale'ye bağlayın

Kale deposunda, uygulamanızın anahtarı (burada `inventory`) ve projenizin klasörüyle:

```bash
./scripts/register-app.sh inventory /path/to/Inventory.Api
```

Bu komut uygulamayı Kale'ye kaydeder ve projenizin ihtiyaç duyduğu her şeyi (Kale'nin adresi, anahtar, istemci sırrı) projenin .NET user secrets'ına yazar. `appsettings` dosyalarınıza dokunulmaz ve sır deponuza hiç girmez. Uygulamanın bir web arayüzü varsa, ör. `http://localhost:5000` adresinde, `--web http://localhost:5000` ekleyin.

## 4. Kodu yazın

Kullanıcıların yapabileceklerini bir enum olarak listeleyin:

```csharp
using Auth.Client;

namespace Inventory.Api;

// Bu uygulamada bir kullanıcıya izin verilebilecek her şey.
public enum InventoryOperations
{
    StockRead,
    [Implies(StockRead)] StockUpdate,   // stoğu güncelleyebilen stoğu okuyabilir de
}
```

Sonra `Program.cs` dosyasında bağlanmak için bir satır, korunacak her uç noktaya da bir öznitelik:

```csharp
using Auth.Client;
using Inventory.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddKaleAuth<InventoryOperations>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/stock", [Authorize(InventoryOperations.StockRead)] () => new[] { "apples: 12" });
app.MapPost("/stock", [Authorize(InventoryOperations.StockUpdate)] () => Results.NoContent());

app.Run();
```

## 5. Çalıştırın ve kişilere erişim verin

```bash
dotnet run
```

Logda işlemlerinizin Kale'ye ulaştığını görürsünüz:

```
Operations registered with AuthService: 2 added, 0 reactivated, 0 obsoleted, 0 unchanged.
```

Uç noktalarınız artık korunuyor: belirteç olmadan **401** döner.

Kimin neyi yapabileceğine yönetim arayüzünde karar verin:

1. **Uygulamalar** → `inventory` satırında **Roller**. **Yeni rol** bölümünde bir rol oluşturun (ör. `Clerk`), sonra işlemlerini işaretleyin (ör. `inventory.StockUpdate`) ve **İşlemleri kaydet**'e basın.
2. **Kullanıcılar** → bir kullanıcı. **Uygulamalar** bölümünde `inventory`'yi seçip **Erişim ver**'e basın. **Roller (doğrudan)** bölümünde `Clerk`'i atayın.

O kullanıcının belirteciyle `/stock` artık **200** döner; işlemi olmayan biri için **403**. Bir kullanıcının nasıl belirteç alacağı bir sonraki bölümde.

## API'nizi bir kullanıcı olarak çağırmak

Bir istek, giriş yapmış kullanıcının erişim belirtecini (`Authorization: Bearer …`) ister. Bu belirteci, kullanıcı Kale üzerinden giriş yaptığında uygulamanızın web arayüzü alır:

- **ASP.NET Core bir web arayüzü:** [kılavuzun 7. adımı](guide.tr.md#11-adım-7-web-arayüzünüzden-kullanıcıları-giriş-yaptırın).
- **Denemek için elle:** kaydederken `--web` kullanın (3. adım), sonra [düz HTTP akışını](guide.tr.md#12-diğer-arayüzler-ve-diller-düz-http) izleyin.

## Sonra

- İşlemlerin ve `[Authorize]`'ın kuralları (VEYA, VE, controller'lar): [kılavuz, 3. ve 4. adımlar](guide.tr.md#7-adım-3-kullanıcıların-yapabileceklerini-listeleyin-işlemler)
- Ne beklemeli (belirteç ömürleri, sonlandırılan oturumlar, uygulama başına tek oturum): [kılavuz, 13. bölüm](guide.tr.md#13-kale-nasıl-davranır-ne-beklemeli)
- Canlı ortama geçiş: [kılavuz, 14. bölüm](guide.tr.md#14-canlı-ortama-geçiş)
- Bir şey çalışmıyor: [sorun giderme](guide.tr.md#15-sorun-giderme)
