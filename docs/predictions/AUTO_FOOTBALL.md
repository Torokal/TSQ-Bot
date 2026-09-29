# TSQ Öngörü · Otomatik futbol öngörüleri

Galatasaray, Fenerbahçe ve Beşiktaş'ın (erkek A takımları) desteklenen organizasyonlardaki maçları için, maç günü
otomatik olarak **sabit oranlı** bir TSQ Öngörü açan, mevcut Öngörü modülünün bir uzantısı. Yeni ekonomi, yeni cüzdan
veya ayrı bot yoktur. Açılan kart normal bir öngörüdür: aynı katılım/değiştirme/geri çekme, aynı sonuçlandırma ve
iptal/iade, aynı turnuva. **Sonuç otomatik girilmez**; ödemeyi her zaman yönetici karttan yapar.

Varsayılan: **Disabled**. Sağlayıcı kapsamı ve gerçek veri doğrulaması: [PROVIDER_VERIFICATION.md](PROVIDER_VERIFICATION.md).

## Kapsam

| Otomatik | Elle kalan |
|---|---|
| Desteklenen organizasyonlarda maç keşfi (üç kulübün iç saha + deplasman maçları) | Sonuç girme (✅ Sonuçlandır) |
| Maçın Türkiye takvim gününde, 09:00'da (erken maçta başlangıçtan 2 saat önce) kart açma | İptal / iade (↩️) |
| Gerçek sağlayıcı verisinden normal süre 1-X-2 oranı, yayında sabitlenir | Saat değişince/maç kaybolunca kilitlenen kartın incelenmesi |
| Başlangıçtan 2 dakika önce otomatik kilit (mevcut kilit worker'ı ve işlem içi saat kontrolü) | Belirsiz teslimi incelemek (`DELIVERY_UNKNOWN`) |

Kapsam dışı: otomatik sonuçlandırma/ödeme, canlı/dinamik oran, handikap/alt-üst/golcü/kupon, AI ile oran veya sonuç,
gerçek para, bahis sitesine yönlendirme, ücretli API, ikinci sağlayıcı, yeni admin paneli veya slash komutu.

**Organizasyonlar (açık allow-list):** `soccer_turkey_super_league`, `soccer_uefa_champs_league`,
`soccer_uefa_champs_league_qualification`, `soccer_uefa_europa_league`, `soccer_uefa_europa_conference_league`.
Sağlayıcının kataloğu (`/v4/sports`) hangisinin şu an sezonda olduğunu söyler; yalnızca sezondakilerin maç listesi okunur.
**Türkiye Kupası ve Türkiye Süper Kupası sağlayıcının belgelediği anahtarlar arasında yok** ve başka bir anahtara
eşlenmez: bu maçlar otomatik açılmaz (elle açılabilir). Bir takımın hangi Avrupa organizasyonunda oynadığı tahmin
edilmez; desteklenen organizasyonlarda gerçekten dönen maçlar takım filtresinden geçer.

**Takımlar:** sağlayıcı adlarının kontrollü bir listesiyle **birebir** eşleşme (boşluk/harf büyüklüğü/Türkçe harf
katlanır: "Beşiktaş JK" = "besiktas jk"). "Fenerbahçe U19", "Galatasaray W", "Beşiktaş Women" veya "Fener" gibi adlar asla
eşleşmez. Yeni bir yazım gerçek veride görülünce listeye eklenir (`predictions football-check` benzer ama eşleşmeyen adları
`REVIEW:` satırıyla gösterir). İki takip edilen kulübün derbisi **tek maç, tek öngörü**dür (takım kodları "GS,FB").

## Çalışma modları

| Mod | Ne yapar |
|---|---|
| `Disabled` (varsayılan) | Hiç HTTP çağrısı, planlama ve kayıt yok. Açık otomatik kartlar, kilitleri, kullanıcı verileri ve yönetici işlemleri aynen devam eder. |
| `Observe` | Sağlayıcıyı okur ve "hangi maçı, hangi oranla, ne zaman açardım" kararını **yalnızca kendi satırlarına** (`prediction_auto_event`, `Mode = Observe`) ve loglara yazar. Prediction, cüzdan, başlangıç coini, turnuva veya Discord mesajı **yok**. Observe satırları Live'ın tekillik anahtarından ayrıdır: Live'a geçince bir maç "zaten yayımlandı" sayılmaz. |
| `Live` | Bütün kapılar sağlanınca gerçek otomatik öngörüyü açar. |

Canlı yayın için hepsi gerekir; biri eksikse otomasyon kendini **Disabled** gibi çalıştırır, botu ve manuel Öngörü'yü
durdurmaz, `/bot status` ve `doctor` nedenini gösterir:
- ayar bölümünde sorun yok (yanlış tip, bilinmeyen organizasyon, exchange bookmaker, resmi olmayan host, …),
- API anahtarı tanımlı,
- tam olarak **tek** izinli sunucu (`Discord:AllowedGuildIds`),
- (yalnızca Live, kart açarken) Predictions modülü o sunucuda açık ve bot öngörü kanalında gerekli izinlere sahip.

Modül kapalıyken Live hiçbir **ücretli** oran çağrısı yapmaz; maç bekler, açılırsa ve süre yeterse kart açılır.

## Ayarlar

`Predictions:Automation` (appsettings, user-secrets veya `TOROSQUAD_Predictions__Automation__…`):

| Anahtar | Varsayılan | Not |
|---|---|---|
| `Mode` | `Disabled` | `Disabled`, `Observe`, `Live` |
| `Provider` | `TheOddsApi` | tek desteklenen |
| `BaseUrl` | `https://api.the-odds-api.com/` | yalnızca resmi host (ipv6-api da kabul) |
| `PublishLocalTime` | `09:00` | `HH:mm`, `TimeZone` saatiyle |
| `TimeZone` | `Europe/Istanbul` | |
| `LockBeforeKickoffMinutes` | `2` | |
| `MinLeadTimeToPublishMinutes` | `15` | yeni kart için başlangıca en az kalan süre |
| `EarlyPublishLeadMinutes` | `120` | erken maç için başlangıçtan önce |
| `MaxOddsAgeMinutes` | `30` | bizim ürün eşiğimiz (sağlayıcı garantisi değil) |
| `DiscoveryIntervalMinutes` | `15` | ücretsiz maç listesi |
| `CatalogIntervalHours` | `12` | ücretsiz katalog |
| `DiscoveryHorizonHours` | `48` | |
| `MaxOddsAttemptsPerEvent` | `4` | |
| `CreditReserve` | `50` | kalan kredi bu değere inince ücretli çağrılar durur |
| `Region` | `eu` | tek bölge (maliyet 1); başka bölge doğrulanmadı |
| `TimeoutSeconds` | `15` | |
| `CompetitionKeys` | (boş = yukarıdaki beş) | allow-list dışı anahtar ayar sorunudur |
| `BookmakerPriority` | (boş = `pinnacle, onexbet, marathonbet, williamhill, betsson, nordicbet, sport888, unibet_nl, unibet_fr, betclic_fr`) | exchange (`betfair_ex_*`, `matchbook`, …) reddedilir |

**Secret:** `Predictions:Automation:TheOddsApi:ApiKey` — yerelde `dotnet user-secrets set
"Predictions:Automation:TheOddsApi:ApiKey" "<anahtar>" --project src/ToroSquad.Bot`, Railway'de
`TOROSQUAD_Predictions__Automation__TheOddsApi__ApiKey`. Değer hiçbir yerde gösterilmez; `SecretConfigurationKeys` ile
redaction'a kayıtlıdır ve ayrıca `apiKey=` sorgu desenini her log/doctor çıktısında maskeleyen bir kural vardır.

## Akış

1. **Döngü:** ayrı `AutoFootballWorker`, yaklaşık dakikada bir (10 sn'lik prediction döngüsünde dış API çağrılmaz). Süreç
   içinde aynı anda tek geçiş; aynı sorgu iki kez eşzamanlı çalışmaz.
2. **Keşif (ücretsiz):** katalog ~12 saatte bir; sezondaki organizasyonların maç listesi ~15 dakikada bir
   (`şimdi−3 saat … şimdi+48 saat`). Takip edilen kulüplerin maçları satır olur. Boş liste, API hatası ve kapsam dışı
   organizasyon ayrı durumlardır; boş liste "maç yok" demektir, hata değil. Liste, sezonun eksiksiz fikstür arşivi değildir.
3. **Yayın zamanı:** maçın Türkiye takvim günü 09:00 — başlangıç 09:00'dan önce ya da hemen sonraysa başlangıçtan 2 saat
   önce, ama o günün yerel gece yarısından önce asla. Yeni kart yalnızca başlangıca en az 15 dakika varken; bot 09:00'da
   kapalıysa açıldığında bugünün kaçan kartını açar, dünün maçını veya başlamış maçı asla. Tek bir test edilebilir hesap:
   `AutoSchedule`.
4. **Oran (ücretli):** yayın zamanı gelen maçlar için organizasyon başına **tek** `/odds` çağrısı (`regions=eu`,
   `markets=h2h`, `oddsFormat=decimal`, `eventIds=` yalnızca o maçlar). Yayımlanmış maçın oranı bir daha indirilmez.
5. **Seçim:** öncelik listesindeki **ilk** bookmaker'ın bu maça ait **tek** `h2h` pazarından tam, geçerli ve taze 1-X-2
   seti. Karıştırma, en iyi fiyatı seçme, ortalama, marj ekleme, normalizasyon, rastgelelik yok. Sonuçlar dizideki sıraya
   göre değil, adla eşleşir (ev sahibi adı, deplasman adı ve `Draw`). `h2h_lay`, `h2h_3_way`, `draw_no_bet` vb. okunmaz.
6. **Doğrulama:** maç kimliği, organizasyon, ev sahibi/deplasman ve başlangıç zamanı keşifteki kayıtla aynı; tam üç
   benzersiz sonuç; fiyatlar decimal olarak ayrıştırılır ve **tek bir yerde** (`OddsSelector.ToX100`) iki ondalığa
   **aşağı** yuvarlanır (kaynağın söylediğinden fazla ödeme yok), 1.01–1000.00 dışı reddedilir (kırpılmaz). Pazarın
   `last_update` alanı (belgede: sistemin o pazarı bookmaker'da son gördüğü an; bookmaker düzeyindeki alan kullanımdan
   kalkmış) zorunludur; 30 dakikadan eski veya 2 dakikadan fazla gelecekteki zaman reddedilir. Ham fiyatlar
   (`RawPrices`) ve karttaki ×100 değerler ayrı saklanır.
7. **Oran yoksa:** 2.00 varsayılmaz, AI'ya sorulmaz, eski oran kullanılmaz, eksik set yayımlanmaz. Durum `WaitingForOdds`;
   en fazla 4 deneme, son güvenli ana kadar yayılmış (en az 3, en fazla 45 dakika arayla; aynı saniyede yığılmaz). Deneme
   **çağrıdan önce** veritabanına yazılır: restart sayacı sıfırlamaz, ikinci bir süreç aynı denemeyi yapmaz. Süre/deneme
   bitince kart açılmaz, satır nedeniyle `Skipped` olur.
8. **Yayın (Live):** mevcut Publishing → kart → Open hattı (`PredictionService.PublishAutomaticAsync`, yalnızca bu
   servis çağırır; hiçbir buton/komut ulaşamaz). Tek yazma işleminde: satır hâlâ Live, yayımlanmamış ve bağsız mı;
   öngörü **o an aktif** turnuvaya bağlanır (eski plan kapanmış turnuvaya yazamaz; turnuva kapanışı aynı yazma kilidiyle
   sıralanır); satır öngörüye bağlanır (benzersiz). Gönderimden hemen önce saat, otomasyon modu ve modül yeniden
   denetlenir; geç kalan kart hiç gönderilmeden bırakılır (`Abandoned`, satır `Skipped`).

## Kart

```
🟢 Katılım Açık
## Galatasaray - Fenerbahçe maç sonucu ne olur?
1️⃣ Galatasaray kazanır  Oran: 1.85
2️⃣ Beraberlik           Oran: 3.40
3️⃣ Fenerbahçe kazanır   Oran: 4.20
👥 Katılım · ⏳ Kilitlenme (başlangıçtan 2 dk önce) · ⚽ Planlanan başlama · 📈 Oran kaynağı (bookmaker · zamanı)
📜 Kural: normal süre (90 dk + uzatma dakikaları; uzatma devreleri ve penaltılar hariç)
[🎯 Tahmin Yap]  [🔒 Kilitle] [✅ Sonuçlandır] [↩️ İptal / İade]
TSQ Öngörü #42 · Otomatik · Sabit oran
```

Gerçek ev/deplasman sırası kullanılır; takip edilen kulüpler kendi Türkçe adıyla, rakip sağlayıcının yazdığı gibi
(etkisizleştirilmiş, ping'siz) yazılır. Kanal: `Predictions:ChannelId` (ikinci bir kanal ayarı yok). Kartta TSQ turnuvası
görünmez. Logo, promosyon, affiliate veya "bahis yap" bağlantısı yoktur. Kilit bir "maç başladı" bildirimi değildir.

## Yetki, ekonomi, tekillik

- Otomatik öngörünün insan yaratıcısı yoktur: `Origin = AutoFootball`, `CreatorUserId = 0`. Bota ya da ayarlayan admine
  cüzdan, başlangıç coini veya liderlik uygunluğu verilmez (uygunluk kuralındaki "öngörü yayımlamış" yolu yalnızca manuel
  öngörüler içindir). Gerçek oyuncular tahmin yaparak mevcut kurala göre uygun olur.
- Yönetim: yalnızca **Administrator veya sunucu sahibi**. Yaratıcı rolü tek başına otomatik kartları yönetemez; manuel
  kartların kuralları değişmedi.
- Tekillik: `GuildId + Provider + ExternalEventId + MarketKind (+ Mode)` veritabanında benzersiz; turnuva anahtarın
  parçası değildir — yeni turnuva, restart, deploy, iki süreç veya derbi aynı maçı ikinci kez açmaz. Yönetici kartı iptal
  ederse maç yeniden açılmaz. Keşfedilmiş ama açılmamış maç turnuva bitirmeyi engellemez; açılan (Publishing/Open/Locked)
  öngörü mevcut koruma kapsamındadır.
- Kullanıcı sonucu değiştirirse o öngörünün kayıtlı sabit oranı kullanılır; yeni API çağrısı yapılmaz. Sonradan gelen
  yeni oranlar yayımlanmış kartı veya tahminleri değiştirmez. Manuel kartın varsayılan oran davranışı değişmedi.

## Teslimat

- Discord'a gönderim veritabanı işlemi dışında yapılır; belirsiz gönderim (zaman aşımı) mevcut uzlaştırmayla son
  mesajlarda aranır, bulunursa kaydedilir; bulunamazsa grace süresi sonunda öngörü `Abandoned`, satır
  `ReviewRequired / DELIVERY_UNKNOWN` olur ve **asla ikinci kart gönderilmez**.
- Veritabanı tarafındaki garanti: bir maça en fazla bir otomatik öngörü ve ekonomik kayıt. Discord tarafında "tam olarak
  bir kez" teslim **kanıtlanamaz**; çözülemeyen belirsizlikte çoğaltmak yerine inceleme için durulur.
- Discord gönderimi kesin reddederse öngörü satırı silinir, maç 5 dakika sonra aynı sabit oran setiyle (hâlâ tazeyse)
  yeniden denenir; 3 retten sonra `CHANNEL_UNAVAILABLE`.
- Silinen kart mevcut kart onarımıyla (kilitli yedek kart) yönetilebilir kalır; yeni maç/öngörü açılmaz, yatırımlar korunur.

## Saat değişikliği, erteleme, kaybolan maç

- **Yayından önce:** yeni başlangıç zamanıyla gün/yayın/kilit yeniden hesaplanır; eski oran okuması atılır.
- **Yayından sonra:** otomatik yeniden açma veya süre uzatma yok. Açık kart kilitlenir (`NeedsReview`), satır
  `ReviewRequired / SCHEDULE_CHANGED`; ilk ve son planlanan zaman (`KickoffAt`, `LatestKickoffAt`) saklanır; oranlar ve
  yatırımlar değişmez. Aynı UTC anının farklı gösterimi değişiklik sayılmaz.
- The Odds API'nin maç listesinde erteleme/iptal durumu alanı **yoktur** (yalnızca planlanan `commence_time`); böyle bir
  alan uydurulmaz. Listeden kaybolan maç "iptal" sayılmaz; gelecekteki bir maç art arda iki keşifte görünmezse açık kart
  güvenlik için kilitlenir (`EVENT_MISSING`), otomatik ödeme/iade yapılmaz.
- Yetkilinin manuel kilidi geri açılmaz; sonuçlanmış/iptal kart yeniden otomatikleşmez.

## Kota ve maliyet

- Belgelenen maliyet: `/sports` ve `/events` ücretsiz; `/odds` = pazar sayısı × bölge sayısı = **1 kredi** (h2h, eu);
  hiç maç dönmezse ücretsiz. Ücretsiz plan: **500 kredi/ay**.
- Her yanıtın `x-requests-remaining`, `x-requests-used`, `x-requests-last` başlıkları kalıcı olarak saklanır
  (`prediction_auto_provider`); restart, deploy, turnuva değişimi veya gece yarısı sıfırlamaz. Aybaşı yenilemesi
  varsayılmaz: yeni kalan kredi ücretsiz keşif yanıtlarının başlığından öğrenilir.
- Kalan − ölçülemeyen çağrılar − 1 < `CreditReserve` (50) ise ücretli çağrı yapılmaz (`QUOTA_PAUSED`); ücretsiz keşif ve
  manuel Öngörü devam eder.
- Zaman aşımı/bağlantı kopması gibi maliyeti bilinmeyen ücretli çağrı **harcanmış** sayılır; başlık gelmeyen yanıttan
  sonra ölçüm gelene kadar kör ücretli çağrı yapılmaz (en fazla bir tane).
- 429: `Retry-After` (1 dk – 1 saat sınırlı) kadar duraklama. 401/403: 6 saat tüm çağrılar durur (döngüde yeniden deneme
  yok). 5xx/zaman aşımı/bozuk yanıt: 1, 2, 4 … en fazla 30 dakika geri çekilme. HTTP katmanında gizli yeniden deneme yok.
- **Tahmini aylık tüketim** (varsayımlar: sezonda üç kulüp için ayda ~12 lig + ~6 Avrupa maçı ≈ 18 maç; aynı gün aynı
  organizasyondaki maçlar tek çağrıda; çoğu maç ilk denemede oranlı): ~15–25 kredi/ay; en kötü durumda (her maç 4 deneme,
  hepsi ayrı gün) ~70 kredi/ay. 500 kredilik ücretsiz planın ve 50 kredilik rezervin altında kalır. Doğrulama komutu
  ayrıca en fazla 25 kredi harcar.

## Teşhis

- `/bot status`: mod ve kapalıysa nedeni (ayar sorunu / anahtar yok / tek sunucu gerekli), son keşif ve sezondaki
  organizasyonlar, bugünkü maçlar (yayımlanan, gözlenen, bekleyen, atlanan), son bilinen kredi ve ölçüm zamanı, duraklama,
  son hata sınıfı, incelemedeki maç sayısı. Anahtarın değeri asla. Kanala uyarı gönderilmez (repo'da admin bildirim kanalı
  yok; yeni kanal uydurulmadı).
- `doctor`: mod, anahtar "set (value hidden)" / "NOT SET", ayar sorunları.
- `predictions football-check [--odds] [--budget N]`: salt-okunur sağlayıcı kontrolü (geçici veri dizini, sahte Discord,
  bot veritabanına yazmaz). Katalog, takip edilen maçlar (ev/deplasman, Türkiye saati), eşleşmeyen benzer adlar ve
  `--odds` ile en fazla N (varsayılan 5, en fazla 25) kredilik oran okuması: bookmaker'lar, h2h varlığı, seçilecek set veya
  neden.
- Loglar: `the_odds_api endpoint=… status=… remaining=…`, `auto_football_observed`, `auto_football_published`,
  `auto_football_skipped … code=…`, `auto_football_review` — URL ve anahtar asla.

## Bilinen sınırlar

- Sağlayıcı yalnızca **planlanan** başlangıç zamanı verir; "maç gerçekten başladı" bilgisi yoktur. Kilit planlanan
  zamandan 2 dakika öncedir; maç geç başlarsa katılım yine planlanan zamana göre kapanır.
- `h2h` pazarının normal süre (90 dk + uzatma dakikaları) olduğunu belgeler açıkça yazmaz; belgede "includes the draw for
  soccer" yazar ve standart futbol 1X2 pazarı böyledir. Kart kuralı normal süreyi açıkça belirtir; gerçek veride doğrulama
  bekliyor (PROVIDER_VERIFICATION.md).
- Türkiye Kupası ve Süper Kupa kapsam dışı. Hazırlık maçları desteklenen organizasyon anahtarlarında olmadığı için gelmez.
- Aynı maç için elle serbest metinle açılmış bir kart dış maç kimliği taşımadığından kesin tespit edilemez; başlık
  benzerliğinden hareketle hiçbir kullanıcı kartı silinmez veya iptal edilmez.
- Yalnızca tek izinli sunucu desteklenir.

## Üretim kontrol listesi

1. Ücretsiz The Odds API anahtarını al (hesap/abonelik sahibin işi; ücretli plan yok) ve secret olarak tanımla.
2. Yerelde `predictions football-check`, ardından `--odds --budget 5`; PROVIDER_VERIFICATION.md'yi gözlenen sonuçlarla güncelle.
3. Ayrı onayla deploy; `Mode = Observe` ile birkaç maç günü gözle (`/bot status`, loglar, `prediction_auto_event`).
4. Ayrı onayla `Mode = Live`; ilk otomatik kartı canlıda kontrol et (başlık, ev/deplasman, oranlar, kilit, kural).
