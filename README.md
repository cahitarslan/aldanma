**Türkçe** | [English](README.en.md)

# Aldanma

Canlı: **https://aldanma.cahitarslan.com**

**Tıklamadan önce bir bak.** Şüphelendiğin telefon numarasını, IBAN'ı, linki, ilanı ya da gelen SMS/WhatsApp mesajının tamamını yapıştır. Ne kadar riskli olduğunu ve daha önce kaç kişinin ihbar ettiğini gör.

Aldanma, Türkiye için yazılmış açık kaynaklı bir dolandırıcılık kontrol aracı. Bir sohbet robotu bir mesajın dolandırıcılığa *benzeyip benzemediğini* söyleyebilir. Ama geçen hafta üç kişinin tam bu IBAN'ı ihbar ettiğini, USOM'un bu adresi zararlı listesine aldığını ya da sitenin dört gün önce açıldığını bilemez. Bu proje tam olarak bu bilgileri topluyor.

- **Tek kutu.** Yapıştırdığın şey kendiliğinden tanınır: telefon numarası, IBAN, link ya da düz metin. Metnin içindeki her numara, IBAN ve link de ayrıca kontrol edilir.
- **Topluluk ihbarları.** Dolandırılan insanlar işin içindeki numarayı, IBAN'ı ve siteyi ihbar eder. Aynı ihbarda geçen kayıtlar birbirine bağlanır; böylece tek bir ihbar bütün bir dolandırıcılık düzeni hakkında uyarabilir.
- **Resmî engel listesi.** [USOM](https://www.usom.gov.tr)'un yayımladığı zararlı bağlantılar listesi günde iki kez içeri alınır. Böylece sorgular ilk günden işe yarar.
- **Alan adı yaşı.** Kayıt tarihine RDAP (WHOIS'in JSON tabanlı yeni hâli) üzerinden bakılır. Sahte sitelerin çoğu birkaç günlüktür.
- **Bilinen kalıplar.** Açık kural seti 21 Türk dolandırıcılık türünü tanır (sahte icra ve ceza bildirimi, kargo, e-Devlet, HGS/köprü otoyol, kapora, sahte iş ilanı, "anne telefonum bozuldu"...) ve resmî doğrulama yolunu gösterir.
- **Hiçbir şeye "güvenli" demez.** Üç sonuç var: yüksek risk, şüpheli, bilinen belirti yok. Araç bildiğini söyler, fazlasını değil.
- **Herkes okuyabilsin diye.** Büyük yazı ve yazı büyütme düğmesi, yüksek kontrast, büyük dokunma alanları, varsayılan açık tema ve isteğe bağlı koyu tema. Sonuçlar trafik levhası şekilleriyle gösterilir; anlamı renge bağlı kalmaz.

## Tasarımdan gelen gizlilik

| Veri | Ne oluyor |
|---|---|
| Yapıştırılan mesaj | **Tarayıcıda** incelenir. Sorgu için yalnızca içinde bulunan telefon numaraları, IBAN'lar ve linkler gönderilir. Metin cihazdan çıkmaz. |
| Sorgu | Bellekte işlenir; saklanmaz, günlüğe yazılmaz. |
| İhbar edilen numara ya da IBAN | Yalnızca HMAC-SHA256 gizli anahtarlı özeti ve maskeli hâli (`+90532 *** 45 67`) tutulur. Veritabanı sızsa bile numaralar ortaya çıkmaz. |
| İhbar eden | Ad, telefon ya da e-posta istenmez. IP adresi yalnızca gizli anahtarlı özet olarak tutulur; farklı ihbar edenleri saymak ve tekrarı engellemek için. |
| Tek tek ihbarlar | Hiç yayımlanmaz. Sorgularda yalnızca sayılar, tarihler ve en sık görülen dolandırıcılık türleri gösterilir. |
| "Bu sonuç doğru muydu?" cevapları | Yalnızca motorun verdiği seviye, puan ve kural kimlikleri. Metin yok, numara yok. |
| İncelemeye gönderilen mesaj | Yalnızca kullanıcı "Bu sonuç doğru muydu?" sorusunu cevapladıktan sonra onay kutusunu işaretlerse: yanlış bir sonucu bildirmek için ya da "evet" dedikten sonra isteğe bağlı olarak doğru değerlendirilmiş bir örneği paylaşmak için. İçindeki telefon numaraları ve IBAN'lar kaydedilmeden önce gizlenir. Yalnızca moderatörler okur. |

Resmî alan adları (örneğin `turkiye.gov.tr`, banka alan adları) ve kısa numaralar ihbar edilemez; kaydın sahibi itiraz edebilir. Kabul edilen itiraz, kaydı puanlamadan çıkarır.

## Nasıl gelişir? (kendi kendine öğrenmeden)

Hiçbir şey otomatik öğrenilmez: etiketsiz girdi araca yanlış da öğretir, dolandırıcılar da onu bilerek zehirleyebilir. Bunun yerine proje gözden geçirilen bir veri döngüsü işletir:

1. **Topluluk ihbarları ağırlık taşır ama tek başına karar vermez.** İhbar edenler ağa göre sayılır (IPv4 /24, IPv6 /64), her ağ günde en fazla 20 kayıt ihbar edebilir ve üçten az ağdan gelen ihbarlar hiçbir şeyi tek başına "yüksek risk" yapamaz (bunu bir test güvenceye alır).
2. **"Bu sonuç doğru muydu?"** sorusu her sonucun altında etiketli bir cevap saklar (seviye, puan, kural kimlikleri). Bu, kuralları gerçek kullanımda ölçer ve ileride bir modelin eğitim verisi olur.
3. **İnceleme kuyruğu.** Sonucun yanlış olduğunu söyleyen kullanıcı mesajı onayıyla ve gizlenmiş hâlde gönderebilir; doğru olduğunu söyleyen de aynı şekilde paylaşabilir (claim `confirmed`). Moderatör bunu kontrol edilmiş örnek olarak veri setine ekleyebilir. Moderatör etiketler; `tools/suggest-rules.mjs` etiketlenenlerden Claude'a aday kural değişiklikleri sorar, kararı bir kişi verir. Kabul edilen değişiklikler de diğer her kural değişikliği gibi veri setinden, ölçümlerden ve C#/JS eşlik testlerinden geçer.
4. **İleride bir model.** Birkaç bin etiketli örnek biriktiğinde kuralların yanına küçük ve açıklanabilir bir model eklenebilir.

## Puan nasıl hesaplanır?

```
input ──► InputParser ──► identifiers (phone / IBAN / domain / URL)  +  message text
                               │                                            │
              ┌────────────────┼──────────────────┐                         ▼
              ▼                ▼                  ▼                  MessageEngine
      community reports   USOM block list    RDAP domain age     (rules/rules.json)
              └────────────────┼──────────────────┘                         │
                               ▼                                            ▼
                          RiskScorer ──────────────► score 0-100 = max(message, each identifier)
                                                     55+ high · 25-54 suspicious · below: no known signs
```

| Sinyal | Ağırlık |
|---|---:|
| Farklı ihbar eden sayısı: 1 / 2 / 3 / 5 / 10+ | 25 / 40 / 55 / 70 / 85 |
| Son 30 günde ihbar edilmiş | +5 |
| USOM zararlı bağlantı listesinde | 80 |
| Alan adı 30 günden yeni / 180 günden yeni | 35 / 15 |
| Resmî bir alan adına benzetilmiş yazım (`yap1kredi.com.tr`) | 40 |
| Yabancı telefon numarası / ücretli 0900 hattı | 20 / 30 |
| Yabancı IBAN | 15 |
| Mesaj kalıpları (kategori, dil, niyet birleşimleri) | bkz. [rules/rules.json](rules/rules.json) |

Bütün ağırlıklar ve her puan cümlesi [`rules/rules.json`](rules/rules.json) dosyasında. C# ve JavaScript motorları aynı dosyayı okur ve 304 test girdisinin hepsinde alan alan aynı çıktıyı vermek zorundadır (CI'da kontrol edilir).

## Mimari

Kodu iki karar şekillendiriyor:

1. **Saf çekirdek.** `Aldanma.Core` alanın tamamını taşır: girdi ayrıştırma, mesaj motoru ve risk puanlayıcı. Hiçbir bağımlılığı yoktur, dışarıya çıkmaz (I/O yapmaz) ve her bilgiyi (ihbarlar, engel listesi eşleşmeleri, alan adı yaşı) girdi olarak alır. Bu, test etmeyi kolaylaştırır (testlerin çoğu veritabanı istemez) ve tarayıcının aynı kuralları JavaScript ikiziyle çalıştırmasını sağlar. Hexagonal/onion mimarinin işe yarayan kısmı, törenleri olmadan.
2. **API'de dikey dilimler.** `Aldanma.Api/Features/` her kullanım senaryosu için bir klasör içerir (`Check`, `Reports`, `Disputes`, `Feedback`, `Submissions`, `Moderation`, `Meta`, `Blocklist`); her klasörde o senaryonun uç noktası ve servisi durur. Ortak parçalar `Infrastructure/` altında (özetleme, RDAP, Turnstile, istemci IP'si, hız sınırları, yönetici erişimi, hata yönetimi, telemetri), EF Core modeli `Data/` altında.

Bilerek dışarıda bırakılanlar: EF Core üzerine repository arayüzleri (`DbContext` zaten öyle bir şey), MediatR tarzı yönlendirme ve ayrı bir uygulama katmanı. Altı küçük kullanım senaryosunda bunlar açıklık değil, dosya eklerdi. Yalnızca testlerin yerine koyduğu dış servislerin (`IDomainRegistrationLookup`, `IHumanCheck`) arayüzü var.

**İşletim.** Bozuk JSON dahil her hata aynı `{ "error", "traceId" }` şeklinde döner; beklenmeyen hatalar iz kimliğiyle günlüğe yazılır ve kullanıcıya genel bir mesaj döner. Günlükler yapılandırılmıştır (Development dışında JSON) ve sorgulanan ya da ihbar edilen hiçbir değeri içermez. OpenTelemetry izleri HTTP'yi, dış çağrıları ve PostgreSQL'i kapsar; metrikler türe ve seviyeye göre sorguları, ihbarları, itirazları, hız sınırı vuruşlarını ve engel listesi aktarımlarını içerir. `/health` canlılık kontrolüdür, `/health/ready` veritabanını da kontrol eder.

**Güvenlik.** Numaralar yerine gizli anahtarlı özetler; iki katmanlı taşma koruması (kenarda Cloudflare hacmi durdurur; uygulamada her uç noktada istemci başına token bucket, sunucu genelinde eşzamanlılık tavanı, istek süre sınırları, Kestrel bağlantı ve başlık sınırları ve dışarıya giden RDAP çağrılarına tavan veritabanını ve faturayı korur; bkz. [docs/DEPLOY.md](docs/DEPLOY.md#abuse-and-ddos-protection)); uç nokta başına hız sınırları (yönetici uç noktaları dahil); 64 KB istek sınırı; ihbar ve itirazlarda Turnstile; API'de kısıtlayıcı başlıklar ve sitede satır içi betik ya da stil içermeyen bir CSP; istemci IP'si yalnızca ayarlanmışsa vekil sunucu başlığından alınır, böylece sınırlardan kaçmak için taklit edilemez; düzgün bir özet anahtarı olmadan API çalışmaya başlamaz.

## Depo düzeni

| Yol | Ne var |
|---|---|
| `rules/rules.json` | Açık kural seti: kategoriler, kurumlar, resmî alan adları, niyet katmanı, kayıt sinyalleri, banka kodları |
| `src/Aldanma.Core` | Bağımlılıksız .NET kütüphanesi: `MessageEngine`, `InputParser`, `PhoneNumber`, `Iban`, `RiskScorer` |
| `src/Aldanma.Api` | ASP.NET Core minimal API: `Features/` (her kullanım senaryosu için bir klasör), `Infrastructure/`, `Data/` (EF Core + PostgreSQL) |
| `web/` | Statik site (çatısız): `engine.js` (mesaj motorunun JS ikizi), `lookup.js`, `app.js` (kontrol formu), `result.js`, `reporting.js`, `controls.js`, `dom.js` |
| `corpus/` | 279 etiketli sentetik mesaj + eşlik girdileri ([şema](corpus/SCHEMA.md)) |
| `tests/Aldanma.Tests` | xUnit: motor, eşlik, kayıt türleri, puanlama, API entegrasyon testleri |
| `test/` | JS motoru ve aday çıkarma için Node testleri |
| `tools/` | `evaluate.mjs` (herhangi bir veri setinde doğruluk), `parity.mjs`, `build-web.mjs`, `suggest-rules.mjs` (inceleme kuyruğundan Claude destekli kural önerileri) |

## Yerelde çalıştırma

Gerekenler: .NET 10 SDK, Node.js 22, PostgreSQL 16 (ya da Docker).

```bash
# 1. Database (Docker), or point appsettings.Development.json at your own PostgreSQL
docker run -d --name aldanma-db -e POSTGRES_USER=aldanma -e POSTGRES_PASSWORD=dev-password -p 5432:5432 postgres:16

# 2. API on http://localhost:5080 (applies migrations on start)
dotnet run --project src/Aldanma.Api

# 3. Static site on http://localhost:5173
ALDANMA_API_URL=http://localhost:5080 npm run build:web
npx serve dist -l 5173        # or: cd dist && python -m http.server 5173
```

Testler:

```bash
npm test                      # JS engine and candidate extraction
node tools/parity.mjs --check # expected JS output is current
dotnet test                   # .NET; API tests run when ALDANMA_TEST_DATABASE is set:
ALDANMA_TEST_DATABASE="Host=localhost;Username=aldanma;Password=dev-password" dotnet test
```

## API

| Yöntem | Yol | Gövde | Not |
|---|---|---|---|
| POST | `/api/lookup` | `{ "items": ["0532 123 45 67", "TR..", "site.xyz"] }` | En fazla 10 kayıt. Web sitesinin kullandığı uç nokta. |
| POST | `/api/check` | `{ "text": "...", "sender": "mobile" }` | Bir mesajın tamamının sunucu tarafında incelenmesi (API kullanıcıları için). Saklanmaz. |
| POST | `/api/reports` | `{ "inputs": [...], "category", "channel", "loss", "occurredOn": "2026-11", "humanToken" }` | İhbar başına en fazla 5 kayıt |
| POST | `/api/disputes` | `{ "input", "contact", "reason", "humanToken" }` | Kayıt sahibinin itirazı |
| POST | `/api/feedback` | `{ "correct", "level", "score", "rulesVersion", "kinds", "categories", "signals" }` | "Bu sonuç doğru muydu?" Yalnızca kural kimlikleri |
| POST | `/api/submissions` | `{ "text", "sender", "claim", "level", "rulesVersion", "consent": true, "humanToken" }` | Mesajı incelemeye gönderme (gizlenmiş). `claim`: `missed`, `false-alarm` ya da `confirmed` |
| GET | `/api/meta` | | Kural sürümü ve veritabanı sayıları |
| * | `/api/admin/*` | `X-Admin-Key` başlığı | İtirazlar, ihbar reddi, inceleme kuyruğu, geri bildirim dışa aktarımı |

İstemci başına hız sınırları (IPv4 adresi, IPv6 /64):

| Kapsam | Sınır |
|---|---|
| Bütün uç noktalar birlikte | Dakikada 120 istek, 30'luk ani yük |
| `/api/check`, `/api/lookup` | Dakikada 30 |
| `/api/reports` | Saatte 10 |
| `/api/disputes` | Saatte 3 |
| `/api/feedback` | Saatte 30 |
| `/api/submissions` | Saatte 5 |
| `/api/admin/*` | Dakikada 60 |

Reddedilen istek `Retry-After` başlığıyla 429 alır. Sunucu genelinde her örnekte aynı anda en fazla 200 istek çalışır.

## Yayına alma

Bkz. [docs/DEPLOY.md](docs/DEPLOY.md). Canlı sitede statik ön yüz Cloudflare Workers'ta, API ve PostgreSQL tek bir VPS'te (Docker, Coolify ile yönetiliyor) çalışıyor. Ücretsiz bir kurulum da (Cloudflare Workers + Neon PostgreSQL + Google Cloud Run) anlatılıyor.

## Doğruluk ve sınırlar

Mesaj motoru sentetik bir veri setinde ölçülüyor; rakamlar ve çekinceleri [docs/MEASUREMENT.md](docs/MEASUREMENT.md) dosyasında. Kısaca: kurallar hangi veriye bakılarak ayarlandıysa o veride kusursuz görünür; dürüst rakamlar yalnızca kör ölçümden gelir. Kullanıldıkça gelişen kısım topluluk veritabanıdır; başlangıçta USOM listesi dışında boştur.

Bilinen eksikler: `.tr` alan adları için herkese açık RDAP hizmeti yok, bu yüzden yaşları bilinmiyor; banka kodları ve resmî alan adları elle güncelleniyor; veri seti yalnızca Türkçe.

## Katkı

Kaçırılan bir dolandırıcılık ya da yanlışlıkla işaretlenen gerçek bir mesaj mı gördün? Bkz. [CONTRIBUTING.md](CONTRIBUTING.md). Kural değişiklikleri eğitim/ayrılmış set doğruluğunu ve C#/JS eşliğini korumalı.

## Lisans

MIT. Yazı tipleri: Braille Institute'un Atkinson Hyperlegible Next ve Mono yazı tipleri, SIL Open Font License 1.1 (`web/fonts/`). Görseller Google Gemini ile üretildi.

Bu bir bilgilendirme aracıdır; hukuki ya da mali danışmanlık değildir ve adı geçen hiçbir kurumla bağlantısı yoktur. Bkz. [LEGAL.tr.md](LEGAL.tr.md).
