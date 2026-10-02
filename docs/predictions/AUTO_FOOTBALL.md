# TSQ Öngörü · Otomatik futbol öngörüleri

Dört takip hedefinin — **Galatasaray, Fenerbahçe, Beşiktaş** (erkek A futbol takımları) ve **Türkiye erkek A millî
futbol takımı** — desteklenen organizasyonlardaki maçları için, maç günü otomatik olarak **sabit oranlı** bir TSQ Öngörü
açan, mevcut Öngörü modülünün bir uzantısı (millî takım için ayrı worker, kart veya ekonomi yok). Yeni ekonomi, yeni cüzdan
veya ayrı bot yoktur. Açılan kart normal bir öngörüdür: aynı katılım/değiştirme/geri çekme, aynı sonuçlandırma ve
iptal/iade, aynı turnuva. **Sonuç otomatik girilmez**; ödemeyi her zaman yönetici karttan yapar.

Varsayılan: **Disabled**. Sağlayıcı kapsamı ve gerçek veri doğrulaması: [PROVIDER_VERIFICATION.md](PROVIDER_VERIFICATION.md)
(2026-09-30: üç kulüp için gerçek ücretsiz anahtarla salt-okunur doğrulandı — Süper Lig, Şampiyonlar Ligi ve Avrupa
Ligi maçları ve eksiksiz h2h setleri gözlendi; Türkiye millî takımı Uluslar Ligi'nde "Turkey" adıyla gözlendi;
Live yalnızca Pinnacle setini kullanır — aşağıda "Pazar kuralı kapısı").

## Kapsam

| Otomatik | Elle kalan |
|---|---|
| Desteklenen organizasyonlarda maç keşfi (dört hedefin iç saha, deplasman ve tarafsız saha maçları) | Sonuç girme (✅ Sonuçlandır) |
| Maçın Türkiye takvim gününde, 09:00'da (erken maçta başlangıçtan 2 saat önce) kart açma | İptal / iade (↩️) |
| Gerçek sağlayıcı verisinden normal süre 1-X-2 oranı, yayında sabitlenir | Saat değişince/maç kaybolunca kilitlenen kartın incelenmesi |
| Başlangıçtan 2 dakika önce otomatik kilit (mevcut kilit worker'ı ve işlem içi saat kontrolü) | Belirsiz teslimi incelemek (`DELIVERY_UNKNOWN`) |

Kapsam dışı: otomatik sonuçlandırma/ödeme, canlı/dinamik oran, handikap/alt-üst/golcü/kupon, AI ile oran veya sonuç,
gerçek para, bahis sitesine yönlendirme, ücretli API, ikinci sağlayıcı, yeni admin paneli veya slash komutu.

**Takip listesi (tek yerde: `TrackedTeams`):**

| Kod | Gösterim | Kapsam | Eşleşen sağlayıcı adları | Dayanak |
|---|---|---|---|---|
| GS | Galatasaray | kulüp | Galatasaray, Galatasaray SK, Galatasaray AS | "Galatasaray" gerçek veride gözlendi (2026-09-30) |
| FB | Fenerbahçe | kulüp | Fenerbahce, Fenerbahçe, Fenerbahce SK, Fenerbahçe SK | "Fenerbahce" gözlendi |
| BJK | Beşiktaş | kulüp | Besiktas, Beşiktaş, Besiktas JK, Beşiktaş JK | "Besiktas JK" gözlendi |
| TR | Türkiye | millî | Turkey, Türkiye, Turkiye | **"Turkey" gözlendi** (2026-09-30, Uluslar Ligi); "Türkiye" resmî ad, "Turkiye" ASCII biçimi (gözlenmedi) |

**Organizasyonlar (açık allow-list, kapsamıyla):**
- Kulüp: `soccer_turkey_super_league`, `soccer_uefa_champs_league`, `soccer_uefa_champs_league_qualification`,
  `soccer_uefa_europa_league`, `soccer_uefa_europa_conference_league`.
- Millî: `soccer_uefa_nations_league`, `soccer_uefa_euro_qualification`, `soccer_uefa_european_championship`,
  `soccer_fifa_world_cup_qualifiers_europe`, `soccer_fifa_world_cup` (anahtarlar belgedeki katalogda var).

Eşleşme **birebir** ad (boşluk/harf büyüklüğü/Türkçe harf katlanır: "Beşiktaş JK" = "besiktas jk") **ve aynı kapsam**:
kulüp adları yalnızca kulüp organizasyonlarında, "Turkey" yalnızca millî organizasyonlarda. "Turkey" hiçbir zaman kulüp
değildir (Türk kulüplerini ülke filtresiyle takip yok), kulüp adı hiçbir zaman millî takım değildir. "Fenerbahçe U19",
"Galatasaray W", "Turkey U21", "Turkey U19", "Turkey Women", "Turkey W", futsal/plaj futbolu adları veya "Fener" asla
eşleşmez. Sağlayıcı takım ID'si vermez (yalnızca ad); böyle bir alan uydurulmadı. Yeni bir yazım gerçek veride görülünce
listeye eklenir (`football-check` benzer ama eşleşmeyen adları `REVIEW:` satırıyla gösterir). İki takip edilen kulübün
derbisi **tek maç, tek öngörü**dür ("GS,FB"); millî takımın tarafsız saha maçında sağlayıcının ev/deplasman sırası korunur.

Katalog (`/v4/sports?all=true`) her allow-list anahtarı için ayırır: **sezonda** (maç listesi okunur), **destekleniyor ama
sezonda değil** (okunmaz; sonraki katalog yenilemesinde aktifleşince kendiliğinden girer), **katalogda yok** (destek dışı).
`has_outrights` işaretli girişler (ör. `soccer_fifa_world_cup_winner`, "turnuvayı kim kazanır") hiçbir zaman okunmaz;
yalnızca belirli bir maçın 1-X-2'si kullanılır, "turu geçen" veya "kupayı kazanan" pazarı değil. Katalogdaki sezon
başlığı ("UEFA Euro 2024" gibi) koda yazılmaz, yıl tahmin edilmez. **Hazırlık maçları**: sağlayıcının belgelenmiş
kataloğunda hazırlık/dostluk maçı anahtarı yok → kapsam dışı (başka bir anahtara eşlenmez, gizli endpoint aranmaz).
**Türkiye Kupası ve Türkiye Süper Kupası** belgelenmiş anahtarlar arasında yok → kapsam dışı (elle açılabilir). Bir
takımın hangi organizasyonda oynadığı tahmin edilmez; desteklenen organizasyonlarda gerçekten dönen maçlar takım
filtresinden geçer.

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

Gerçek ev/deplasman sırası kullanılır (Türkiye her zaman öne alınmaz); takip edilen takımlar kendi Türkçe adıyla,
Türkiye'nin güncel birkaç millî rakibi Türkçe kartta Türkçe adıyla ("Belgium" → "Belçika", "Italy" → "İtalya",
"France" → "Fransa"; ülke veritabanı değil), diğer rakipler sağlayıcının yazdığı gibi (etkisizleştirilmiş, ping'siz)
yazılır. Bu yalnız görünümdür: oranlar sağlayıcının ham adlarıyla eşlenir. Kanal: `Predictions:ChannelId` (ikinci bir kanal ayarı yok). Kartta TSQ turnuvası
görünmez. Logo, promosyon, affiliate veya "bahis yap" bağlantısı yoktur. Kilit bir "maç başladı" bildirimi değildir.

## Yetki, ekonomi, tekillik

- Otomatik öngörünün insan yaratıcısı yoktur: `Origin = AutoFootball`, `CreatorUserId = 0`. Bota ya da ayarlayan admine
  cüzdan, başlangıç coini veya liderlik uygunluğu verilmez (öngörü oluşturmak kimseye uygunluk vermez). Gerçek oyuncular
  tahminleri sonuçlandığında mevcut kurala göre uygun olur.
- Yönetim: **sonuçlandırma** yaratıcı rolü, Administrator veya sunucu sahibi; **kilitleme ve iptal** yalnızca Administrator
  veya sunucu sahibi (yaratıcı rolü otomatik kartları kilitleyemez/iptal edemez).
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
- Yokluk sayacı yalnızca **başarılı, eksiksiz** (hiç düşürülen/bozuk öğe olmayan) ve **kendi organizasyonuna ait** bir
  listede, maç sorgunun zaman aralığındaysa ilerler. Zaman aşımı, 429, 401/403, 5xx, bozuk JSON, yarım liste veya
  katalog/organizasyon hatası yokluk sayılmaz; bir organizasyonun listesi başka organizasyondaki kayıtlar için
  kullanılmaz. Maç yeniden görünürse sayaç sıfırlanır; kilitlenmiş kart yeniden açılmaz.
- Yetkilinin manuel kilidi geri açılmaz; sonuçlanmış/iptal kart yeniden otomatikleşmez.

## Pazar kuralı kapısı (Live için)

`h2h` ve üç sonuç görülmesi, pazarın **normal süre** (90 dakika + hakemin eklediği süre; uzatma devreleri ve penaltılar
hariç) olduğunun kanıtı değildir. Bu yüzden Live yalnızca **kendi resmî kuralı normal süre olarak doğrulanmış**
bookmaker'ların setini kullanır (`AutoFootballOptions.RuleVerifiedBookmakers`; her giriş için kanıt
PROVIDER_VERIFICATION.md'de). Liste bugün yalnız **`pinnacle`** (Pinnacle betting rules, Soccer madde 1), dar kapsamla:
The Odds API, standart maç öncesi `h2h` (ev / `Draw` / deplasman), erkek A takımları, allow-list organizasyonları.
1xBet ve diğerleri onaysız. Sonuç:
- **Live:** yalnız eksiksiz, taze, geçerli bir Pinnacle seti kart açar. Pinnacle yoksa veya seti geçersizse başka bir
  kaynağa düşülmez; maç `BOOKMAKER_NOT_APPROVED` (onaysız bir set vardı) veya ilgili neden (`NO_ODDS`, `STALE_ODDS`…) ile
  bekler, süresi dolunca atlanır. Onaylı liste boşalırsa Live yine ücretli çağrı yapmaz (`MARKET_RULE_UNVERIFIED`).
- **Observe ve `football-check`:** veri görülebilir — onaysız kaynağın seti "aday" olarak, neden
  (`MARKET_RULE_UNVERIFIED` / `BOOKMAKER_NOT_APPROVED`) ile kaydedilir; hiçbir zaman yayımlanmaz.
- **Yeniden değerlendirme:** onaylı kaynak yokken `MARKET_RULE_UNVERIFIED` ile gözlenmiş bir maç, onay varken ve son
  yayın anından önce yeniden beklemeye alınır (kullanılmış deneme sayısı korunur, yayımlanmış sayılmaz, Live tekilliğini
  etkilemez). Eski oran seti kayıt için durur ama yeniden kullanılmaz (bu karar için çekilmemiştir; `last_update`
  değiştirilmez) — yeni bir çağrı gerekir. Denemeleri bitmişse sayaç sıfırlanmaz: satır `ATTEMPTS_EXHAUSTED` ile atlanır.
- Bir kaynağın kuralı doğrulanınca listeye kodla (kanıt bağlantısıyla, test ve inceleme ile) eklenir; ayarla açılamaz.

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
- **Tahmini aylık tüketim** — varsayımlar: sezonda üç kulüp için ayda ~12 lig + ~6 Avrupa maçı ≈ 18 kulüp maçı; Türkiye
  için uluslararası arada ayda 0–2 (yıllık ~10–12, turnuva yazında birkaç ek) → ortalama ~1–2 millî maç/ay; aynı gün aynı
  organizasyondaki maçlar tek çağrıda (derbi tek maç); çoğu maç ilk denemede oranlı; Türkiye'nin oynamadığı millî maçlar
  için **hiç** oran çağrısı yapılmaz (keşif ücretsiz). Tipik: ~16–28 kredi/ay; en kötü durumda (her maç 4 deneme, hepsi ayrı
  gün, ~22 maç) ~90 kredi/ay; 500 kredilik ücretsiz planın ve 50 kredilik rezervin altında. Doğrulama komutu toplamda en
  fazla 25 kredi (bu turda sınır 5) harcar. Keşif organizasyon başına ücretsiz bir liste çağrısıdır; sezonda olmayan
  organizasyonun listesi okunmaz.
- Kota yenilemesi: resmî SSS "Usage credits are automatically reset on the first of every month" der; uygulama bunu
  varsaymaz, kalan krediyi her zaman sağlayıcının başlıklarından okur. Rezerv engeli yalnızca yeni ölçüm (ör. ücretsiz
  keşif yanıtındaki yenilenmiş `x-requests-remaining`) ile kalkar ve **yalnızca** kota engelini kaldırır: Disabled, kapalı
  modül veya doğrulanmamış pazar kuralı gibi engeller aynen kalır. Anahtar değişimi yeni kredi varsayımı üretmez.
  Otomasyon ve `football-check` aynı anahtarı kullanır; kontrol komutu kalıcı kota durumuna yazmaz, bu yüzden kendi
  bütçesi ve rezerv kontrolüyle sınırlıdır.

## Teşhis

- `/bot status`: mod ve kapalıysa nedeni (ayar sorunu / anahtar yok / tek sunucu gerekli), son keşif ve sezondaki
  organizasyonlar, bugünkü maçlar (yayımlanan, gözlenen, bekleyen, atlanan), son bilinen kredi ve ölçüm zamanı, duraklama,
  son hata sınıfı, incelemedeki maç sayısı. Anahtarın değeri asla. Kanala uyarı gönderilmez (repo'da admin bildirim kanalı
  yok; yeni kanal uydurulmadı).
- `doctor`: mod, anahtar "set (value hidden)" / "NOT SET", ayar sorunları.
- `predictions football-check [--odds] [--budget N] [--days D] [--focus GS|FB|BJK|TR] [--preview]`: salt-okunur
  sağlayıcı kontrolü (geçici veri dizini, sahte Discord, bot veritabanına yazmaz). Ortam, istemci türü, host ve "canlı HTTP"
  bilgisi; tam katalog (sezonda / sezon dışı / katalogda yok / outright), önümüzdeki D gündeki (varsayılan 7, en fazla 30)
  takip edilen maçlar (tam event ID, ev/deplasman, UTC ve Türkiye saati), eşleşmeyen benzer adlar; `--odds` ile toplamda
  en fazla N (varsayılan 5, en fazla 25) kredi — `--focus` takımının organizasyonu önce: dönen bookmaker'lar, üç sonuç,
  seçilen/aday kaynak, ham ve sabit oranlar, pazar zamanı ve yaşı, KABUL/RET nedeni; `--preview` ile ilk maçın kartının
  yerel metin önizlemesi (hiçbir şey gönderilmez veya saklanmaz).
- Loglar: `the_odds_api endpoint=… status=… remaining=…`, `auto_football_observed`, `auto_football_published`,
  `auto_football_skipped … code=…`, `auto_football_recheck … code=REOPENED|ATTEMPTS_EXHAUSTED`, `auto_football_review` —
  URL ve anahtar asla.

## Bilinen sınırlar

- Sağlayıcı yalnızca **planlanan** başlangıç zamanı verir; "maç gerçekten başladı" bilgisi yoktur. Kilit planlanan
  zamandan 2 dakika öncedir; maç geç başlarsa katılım yine planlanan zamana göre kapanır.
- `h2h` pazarının normal süre olduğunu sağlayıcı belgesi yazmaz; Live yalnız kuralı normal süre olan Pinnacle'ın setini
  kullanır. Sağlayıcının bu eşlemesi bağımsız denetlenmedi. Pinnacle seti olmayan maç (ör. yalnız 1xBet) açılmaz.
- Sağlayıcı resmî fikstürdeki her maçı hemen listelemez (2026-09-30: İtalya – Türkiye 05.10 henüz yok); listelenmeyen maç
  için kart açılmaz, sahte maç eklenmez.
- Türkiye Kupası, Süper Kupa ve hazırlık/dostluk maçları kapsam dışı (belgelenmiş anahtar yok).
- Aynı maç için elle serbest metinle açılmış bir kart dış maç kimliği taşımadığından kesin tespit edilemez; başlık
  benzerliğinden hareketle hiçbir kullanıcı kartı silinmez veya iptal edilmez.
- Yalnızca tek izinli sunucu desteklenir.

## Üretim kontrol listesi

1. Ekran görüntüsünde görünen anahtarı sağlayıcı panelinden iptal et, yenisini al ve yerelde değeri shell geçmişine
   yazmadan tanımla (PowerShell):
   ```
   $k = Read-Host "Yeni The Odds API anahtarı" -AsSecureString
   dotnet user-secrets set "Predictions:Automation:TheOddsApi:ApiKey" ([System.Net.NetworkCredential]::new('', $k).Password) --project src/ToroSquad.Bot
   Remove-Variable k
   ```
   User-secrets yalnızca **Development** ortamında yüklenir; bu yalnızca yerel kontrol içindir. Production'da (Railway)
   anahtar ortam değişkeniyle verilir: `TOROSQUAD_Predictions__Automation__TheOddsApi__ApiKey`; production için
   `DOTNET_ENVIRONMENT=Development` kullanılmaz.
2. Yerelde (Development) `predictions football-check --days 30`, ardından `--odds --budget 5 --focus TR --preview`;
   PROVIDER_VERIFICATION.md'yi gözlenen sonuçlarla güncelle.
3. Pinnacle dar onayı eklendi (kanıt PROVIDER_VERIFICATION.md). Başka bir bookmaker ancak resmî kuralı aynı şekilde
   kanıtlanırsa eklenir.
4. Ayrı onayla deploy ve `Mode = Observe` (Railway anahtarı ortam değişkeniyle).
5. Ayrı onayla `Mode = Live`; ilk otomatik kartı canlıda kontrol et (başlık, ev/deplasman, oranlar, kilit, kural).
