# The Odds API · sağlayıcı doğrulaması

Otomatik futbol öngörüleri ([AUTO_FOOTBALL.md](AUTO_FOOTBALL.md)) için tek sağlayıcı **The Odds API**'dir
(https://the-odds-api.com). Odds-API.io farklı bir servistir ve kullanılmaz.

**Durum:**

| Hedef | Organizasyon | Gerçek maç gözlendi mi | Geçerli 1-X-2 | Seçilen kaynak | Normal süre kanıtı | Sonuç |
|---|---|---|---|---|---|---|
| Galatasaray | Süper Lig, Şampiyonlar Ligi | evet (2026-09-30) | evet | Süper Lig: pinnacle; ŞL (Barcelona): yalnız onexbet görüldü | pinnacle: Soccer madde 1 | PROVIDER_VERIFIED_READ_ONLY · onexbet onaysız |
| Fenerbahçe | Süper Lig, Şampiyonlar Ligi | evet (2026-09-30) | evet | Süper Lig: pinnacle; ŞL (Aston Villa): yalnız onexbet görüldü | pinnacle: Soccer madde 1 | PROVIDER_VERIFIED_READ_ONLY · onexbet onaysız |
| Beşiktaş | Süper Lig, Avrupa Ligi | evet (2026-09-30) | evet | pinnacle | pinnacle: Soccer madde 1 | PROVIDER_VERIFIED_READ_ONLY |
| Türkiye (erkek A millî) | Uluslar Ligi (gözlendi); EURO elemeleri, EURO, DK Avrupa elemeleri, DK sezon dışı | **evet** (Belgium – Turkey); İtalya – Türkiye sağlayıcıda **henüz yok** | evet | pinnacle (**KABUL**, 2026-09-30 22:37Z) | pinnacle: Soccer madde 1 | PROVIDER_VERIFIED_READ_ONLY · COVERAGE_INCOMPLETE |

Genel, ayrı ayrı:
- **Kaynak uygunluğu:** yalnız `pinnacle`, dar kapsamla onaylı (aşağıda "Normal süre kuralı"); `onexbet` ve diğerleri onaysız.
- **Sağlayıcı erişimi:** PROVIDER_VERIFIED_READ_ONLY (dört hedef; gerçek maç keşfi + eksiksiz h2h seti).
- **Kapsam bütünlüğü:** COVERAGE_INCOMPLETE — resmî fikstürdeki her maç sağlayıcıda (henüz) yok; tüm Türkiye maçlarının
  kapsandığı söylenemez.
- **Discord canlı doğrulaması:** yok (hiçbir otomatik kart gönderilmedi).

2026-09-30 kontrolü: ücretsiz planlı gerçek anahtarla, yerelde (Development, `TheOddsApiClient`, host
`api.the-odds-api.com`, canlı HTTP, fixture/cache yok) `predictions football-check --days 16 --odds --budget 5`
salt-okunur çalıştırıldı; Discord'a hiçbir şey gönderilmedi, bot veritabanına yazılmadı, anahtar hiçbir çıktıda
görünmedi. O kontrolde millî organizasyonlar henüz allow-list'te değildi (sorgulanmadı). Gerçek API okuması Discord
kartının canlı doğrulaması değildir; testlerdeki yanıtlar SENTETİKTİR ve ücretsiz kapsamın kanıtı değildir.

**Anahtar yenileme (2026-09-30):** o kontrolde kullanılan anahtar bir ekran görüntüsünde göründü; sahibi yenisini tanımladı
(yerel secrets dosyası 01:11 TR'de güncellendi; değer hiçbir yerde gösterilmedi). Eski anahtarın panelden iptali sahibin
işidir. Yeni anahtarla ilk ölçüm `x-requests-remaining=500`.

## Türkiye millî takımı (2026-09-30 22:12Z, yeni anahtar)

Ortam Development, istemci `TheOddsApiClient`, host `api.the-odds-api.com`, canlı HTTP (fixture/cache yok), kontrol
`predictions football-check --days 30` (ücretsiz) ve `--days 30 --odds --budget 5 --focus TR --preview` (1 kredi).

| Konu | Gözlenen |
|---|---|
| Millî organizasyonlar (katalog `all=true`) | `soccer_uefa_nations_league` **sezonda**; `soccer_uefa_euro_qualification`, `soccer_uefa_european_championship`, `soccer_fifa_world_cup_qualifiers_europe`, `soccer_fifa_world_cup` **destekleniyor, sezon dışı**; `soccer_fifa_world_cup_winner` outright (okunmadı). Kulüplerde ŞL elemesi sezon dışı. |
| Sağlayıcının Türkiye adı | **"Turkey"** (gözlendi). "Türkiye"/"Turkiye" gözlenmedi (alias olarak kalır). |
| Maç | Uluslar Ligi, event `429e47b7c49fba99d5cbcba70b9c48c5`, **Belgium (ev) – Turkey (deplasman)**, 2026-10-02 18:45Z / **02.10.2026 21:45 TR**; 30 günde başka Türkiye maçı yok |
| Oran çağrısı | 1 çağrı, `x-requests-last=1`, kalan 500 → **499** |
| Dönen bookmaker'lar (h2h) | onexbet, betclic_fr, winamax_de, winamax_fr, betfair_ex_eu, suprabets, matchbook, mybookieag, williamhill, pinnacle, marathonbet, unibet_se, leovegas_se, pmu_fr, sport888, nordicbet, betsson, betonlineag (18) |
| Sonuçlar | Belgium / Draw / Turkey (adla eşlendi) |
| Aday set | pinnacle: ham 1.5 / 4.93 / 5.99 → sabit 1.50 / 4.93 / 5.99; pazar güncelleme 22:12:16Z (okumada 14 sn) |
| Karar | **RED: MARKET_RULE_UNVERIFIED** — geçerli veri, bizim kural kapımız reddetti (sağlayıcı oran veriyor) |
| Yerel kart önizlemesi | başlık "Belgium - Türkiye maç sonucu ne olur?" (sağlayıcı sırası), "Belgium kazanır 1.50 · Beraberlik 4.93 · Türkiye kazanır 5.99", yayın 02.10.2026 09:00 TR, kilit 21:43 TR, altbilgi "TSQ Öngörü #… · Otomatik · Sabit oran", dört buton; gönderilmedi, saklanmadı |

Dört hedef içindeki **ilk uygun maç: 02.10.2026 21:45 TR, Belgium – Türkiye** (yayın hedefi 09:00 TR). Sonraki kulüp maçı
09.10 Galatasaray – Kasimpasa SK.

### Pinnacle onayı sonrası (2026-09-29 22:37Z = 30.09 01:37 TR)

`football-check --days 30 --odds --budget 1 --focus TR --preview` (Development, canlı HTTP, 1 kredi, kalan 499 → **498**):
Belgium – Turkey `pinnacle` ham 1.5 / 4.93 / 5.99 → sabit 1.50 / 4.93 / 5.99, pazar 22:36:58Z (14 sn) → **KABUL**.
Yerel önizleme (gönderilmedi): "Belçika - Türkiye maç sonucu ne olur?" · "Belçika kazanır 1.50 · Beraberlik 4.93 ·
Türkiye kazanır 5.99", oran kaynağı "Pinnacle", yayın 02.10.2026 09:00 TR, kilit 21:43 TR. Oranlar maç gününe kadar
değişir; bunlar yalnız o anın gözlemidir.

### İtalya – Türkiye (resmî fikstür: 05.10.2026 21:45 TR)

TFF ve UEFA fikstüründe 02.10 Belçika – Türkiye ve 05.10 İtalya – Türkiye var. Ücretsiz ham `events` çağrısı
(`soccer_uefa_nations_league`, zaman filtresi yok, 2026-09-29 22:28Z, 0 kredi): **25 maç, hepsi 01.10–04.10 arasında**;
Türkiye maçı yalnız Belgium – Turkey; 05.10 ve sonrası için hiçbir maç yok (France – Italy 02.10 var). Bizim kodda
eleme yok: pencere now−3 sa … now+30 gün (UTC), doğru anahtar, eventIds filtresi yok, takım başına First/Take yok,
maçlar yalnız event id ile ayrılır, önbellek yok. Sonuç: **resmî fikstürde var, mevcut sağlayıcı yanıtında gözlenmedi**
(sağlayıcı ikinci maç gününü henüz listelemiyor). Sahte maç/oran eklenmedi; sağlayıcı listeleyince normal keşif
(48 saat ufuk) onu bulur — bunu testler iki farklı günde aynı takımın iki maçıyla doğrular.

## Belgeden doğrulananlar (2026-09-29)

Kaynaklar: [v4 rehberi](https://the-odds-api.com/liveapi/guides/v4/),
[sporlar](https://the-odds-api.com/sports-odds-data/sports-apis.html),
[pazarlar](https://the-odds-api.com/sports-odds-data/betting-markets.html),
[bookmaker'lar](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html),
[SSS](https://the-odds-api.com/manage/faqs.html), [koşullar](https://the-odds-api.com/terms-and-conditions.html),
[ana sayfa/planlar](https://the-odds-api.com/).

| Konu | Belgede | Gözlendi |
|---|---|---|
| Host | `https://api.the-odds-api.com` (IPv6: `ipv6-api.the-odds-api.com`) | `api.the-odds-api.com` ile çalıştı |
| Anahtar | yalnızca `apiKey` sorgu parametresi | çalıştı; loglarda/çıktıda görünmedi |
| Ücretsiz plan | 500 kredi/ay; "All sports", "Most bookmakers", "All betting markets" | başlangıçta `x-requests-remaining=500` |
| Kota yenileme | "Usage credits are automatically reset on the first of every month" (kod bunu varsaymaz; başlıktan okur) | NOT_OBSERVED (ay dönümü görülmedi) |
| `/v4/sports` | ücretsiz; alanlar `key, group, title, description, active, has_outrights` | 200, `x-requests-last=0`; Süper Lig, Şampiyonlar Ligi, Avrupa Ligi, Konferans Ligi **active**; ŞL elemesi **aktif değil** |
| `/v4/sports/{sport}/events` | ücretsiz; `id, sport_key, sport_title, commence_time, home_team, away_team`; `commenceTimeFrom/To` ISO 8601 | 200, `x-requests-last=0`; zaman aralığı filtresi doğru çalıştı (7 gün: 0 maç — ilk maçlar 9 Ekim'de; 16 gün: dönen maçlar) |
| `/v4/sports/{sport}/odds` | maliyet = pazar × bölge; "If no events are returned, the request will not count against the usage quota"; `eventIds` filtresi | 200; `h2h` + `eu` çağrısı başına `x-requests-last=1`; `eventIds` ile yalnızca istenen maçlar; 3 çağrı = 3 kredi (500 → 497) |
| Canlı maçlar | başlamış maçlar da dönebilir (`commence_time` < şimdi ise in-play); ayrıca bir durum alanı yok | durum alanı yok (yanıtta yalnızca planlanan `commence_time`); canlı maç NOT_OBSERVED |
| Kullanım başlıkları | `x-requests-remaining`, `x-requests-used`, `x-requests-last` | üç başlık da ücretsiz ve ücretli yanıtlarda geldi |
| `last_update` | pazar düzeyindeki alan: sistemin o pazarı bookmaker'da son gördüğü an; askıya alınan pazar ~15 dk sonra kalkar; bookmaker düzeyindeki alan kullanımdan kalkmış | pazar düzeyinde geldi; okuma anından birkaç saniye önce (taze) |
| `h2h` | "Bet on the winning team or player of a game (includes the draw for soccer)"; normal süre/uzatma ayrımı **yazılmıyor** | her takip edilen maçta tam 3 sonuç (ev sahibi, `Draw`, deplasman; adlar maçın takımlarıyla birebir). Normal süre anlamı veride görülemez → **NOT_OBSERVED** (belge sessiz) |
| Diğer pazarlar | `h2h_lay` (yalnız exchange), `h2h_3_way`, `draw_no_bet` ayrı anahtarlar — kullanılmaz | istenmedi; Betfair Exchange (`betfair_ex_eu`) `h2h` ile de dönüyor — öncelik listesinde olmadığı için seçilmedi |
| Organizasyon anahtarları | `soccer_turkey_super_league`, `soccer_uefa_champs_league`, `soccer_uefa_champs_league_qualification`, `soccer_uefa_europa_league`, `soccer_uefa_europa_conference_league` | Süper Lig, ŞL ve Avrupa Ligi'nde takip edilen kulüp maçları gözlendi; Konferans Ligi'nde takip edilen kulüp yok; ŞL elemesi sezon dışı |
| Türkiye Kupası / Süper Kupa | listede **yok** | kod bu maçları okumuyor; ayrıca teyit edilmedi |
| Millî organizasyonlar (2026-09-30 belge) | `soccer_uefa_nations_league` ("UEFA Nations League"), `soccer_uefa_euro_qualification` ("UEFA Euro Qualification"), `soccer_uefa_european_championship` (belgede "UEFA Euro 2024" başlığıyla — başlık koda yazılmadı), `soccer_fifa_world_cup_qualifiers_europe`, `soccer_fifa_world_cup` | NOT_OBSERVED (aktif/pasif durumu gerçek katalogda görülmedi) |
| Şampiyonluk pazarı | `soccer_fifa_world_cup_winner` ayrı bir outright anahtarı — maç değil, okunmaz | — |
| Hazırlık/dostluk maçları | belgelenmiş katalogda anahtar **yok** | kapsam dışı |
| `eu` bookmaker'ları | 1xBet, 888sport, Betclic (FR), BetAnySports, BetOnline.ag, Betsson, Codere (IT), Bet Victor, Coolbet, Everygame, GTbets, LeoVegas (SE), Marathon Bet, MyBookie.ag, NordicBet, Pinnacle ("public website … may incur a delay"), PMU (FR), Suprabets, Tipico (DE), Unibet (FR/IT/NL/SE), William Hill, Winamax (DE/FR); exchange: Betfair Exchange, Matchbook | — |
| Hata kodları | yalnızca 429 (rate limit) belgelenmiş; 401/403/422/5xx kod tarafından ayrıca sınıflandırılır | hata görülmedi (NOT_OBSERVED) |
| Kullanım koşulları | UI'da gösterim (ticari dahil) serbest, veriyi bağımsız ürün olarak yeniden satmak yasak; atıf zorunlu değil; saklama serbest; "as is", doğruluk garantisi yok | — |

## Gözlenen maçlar (2026-09-30, 16 günlük pencere)

| Organizasyon | Maç (sağlayıcının yazımıyla, ev – deplasman) | Başlangıç (TR) | Seçilecek set |
|---|---|---|---|
| Süper Lig | Galatasaray – Kasimpasa SK | 09.10 20:00 | pinnacle 1.22 / 6.57 / 11.76 |
| Süper Lig | Çaykur Rizespor – Fenerbahce | 10.10 19:00 | pinnacle 4.37 / 4.03 / 1.74 |
| Süper Lig | Besiktas JK – Kocaelispor | 11.10 19:00 | pinnacle 1.23 / 6.31 / 11.80 |
| Şampiyonlar Ligi | Galatasaray – Barcelona | 13.10 22:00 | onexbet 9.25 / 6.19 / 1.34 (Pinnacle yoktu) |
| Şampiyonlar Ligi | Aston Villa – Fenerbahce | 14.10 22:00 | onexbet 1.79 / 3.94 / 4.93 (Pinnacle yoktu) |
| Avrupa Ligi | TSG Hoffenheim – Besiktas JK | 15.10 22:00 | pinnacle 1.91 / 4.04 / 3.56 |

Oranlar okuma anındaki değerlerdir (yayımlanmadı, hiçbir yere yazılmadı). Süper Lig maçlarında 13, ŞL maçlarında 19,
Avrupa Ligi maçında 16 bookmaker `h2h` verdi. Kulüp adları ("Galatasaray", "Fenerbahce", "Besiktas JK") mevcut listeyle
birebir eşleşti; eşleşmeyen benzer ad (`REVIEW:`) çıkmadı; kadın/genç takım görülmedi.

## Normal süre kuralı — üç ayrı kanıt

| Kanıt | Durum |
|---|---|
| A) The Odds API pazar açıklaması (https://the-odds-api.com/sports-odds-data/betting-markets.html, okundu 2026-09-30) | `h2h`: "Bet on the winning team or player of a game (includes the draw for soccer)". `h2h_lay` yalnız borsalar için ayrı bir anahtar, `draw_no_bet` ayrı bir anahtar. Normal süre / uzatma / penaltı ayrımı `h2h` için **yazılmıyor**. |
| B) Bookmaker'ın resmî kuralı | **Pinnacle** — https://www.pinnacle.com/en/future/betting-rules, bölüm **Soccer, madde 1** (kontrol 2026-09-30). Özet: aksi belirtilmedikçe futbol maç pazarları planlanan 90 dakika ve hakemin eklediği süre üzerinden sonuçlanır; uzatma devreleri ve penaltı atışları dahil değildir. Erişim notu: sayfa bu çalışma ortamının araçlarıyla açılamadı (doğrudan okuma bağlantı sıfırlandı; yerleşik tarayıcı bu alan adını güvenlik kısıtıyla açmıyor) ve erişim engeli aşılmadı; URL, bölüm ve içerik proje sahibinin resmî sayfadan aktarımıdır. **1xBet**: resmî kural okunmadı → onaysız. |
| C) Gerçek API'de gözlenen yapı | 2026-09-30 22:37Z, Uluslar Ligi Belgium – Turkey: 17 bookmaker; `pinnacle` altında tek `h2h` pazarı, tam 3 sonuç Belgium / `Draw` / Turkey (maçın takım adlarıyla birebir). 1X2 ile tutarlı; bu, The Odds API'nin Pinnacle kuralını `h2h`'ye doğru eşlediğinin **bağımsız denetimi değildir**. |

Sonuç: **dar onay** — `RuleVerifiedBookmakers = ["pinnacle"]`, yalnızca şu kapsamda: sağlayıcı The Odds API; bookmaker
`pinnacle`; erkek A futbol takımları, allow-list organizasyonları; standart maç öncesi `h2h` 1-X-2 (ev / `Draw` /
deplasman, tam üç sonuç); normal süre + hakemin eklediği süre. Diğer bütün kontroller aynen sürer (maç/takım eşleşmesi,
eksiksiz üçlü, tek bookmaker, oran sınırları, tazelik, yayın penceresi, sunucu/modül/mod kapıları, kota, tekillik).
`h2h_lay`, `draw_no_bet`, devre sonucu, tur atlama, outright ve başka spor pazarları okunmaz. Pinnacle'ın iptal/ertelenme
kuralları alınmadı: otomatik kartın sonucu ve iptali yine yöneticinin elindedir. Pinnacle seti yoksa veya geçersizse Live
başka (onaysız) bir kaynağa **düşmez**, kart açılmaz. Genel `MARKET_RULE_UNVERIFIED` kapısı kodda kalır (onaylı liste
boşalırsa Live yine hiçbir şey açmaz). Genel bahis kaynaklarındaki "90 dakika kuralı" anlatımları (üçüncü taraf) kanıt
sayılmadı.

Bu politikanın etkisi: daha önce ŞL'de yalnız `onexbet` seti görülen maçlar (Galatasaray – Barcelona 13.10, Aston Villa –
Fenerbahce 14.10) maç günü Pinnacle seti gelmezse **açılmayabilir**; Pinnacle'ın o gün oran vereceği garanti değildir.

The Odds API'ye sorulabilecek soru (taslak; kullanıcı onayı olmadan gönderilmez, team@the-odds-api.com):

> Hello, for soccer events, does the `h2h` market always represent the full-time result (90 minutes plus stoppage time,
> excluding extra time and penalty shoot-outs) for every bookmaker — in particular `pinnacle` and `onexbet` — including
> knockout matches (e.g. UEFA Champions League knockouts, World Cup)? Or can a bookmaker's `h2h` be a "to qualify"/
> "including extra time" price in knockout rounds? Thank you.

## Hâlâ gözlenmeyenler

- The Odds API'nin Pinnacle kuralını `h2h`'ye eşlemesi bağımsız denetlenmedi (sağlayıcı belgesi `h2h` için süre yazmıyor).
- İtalya – Türkiye (05.10) sağlayıcı yanıtında henüz yok.
- Canlı/başlamış maç yanıtı, erteleme veya saat değişikliği (sağlayıcıda durum alanı yok; yalnızca `commence_time`).
- Ay dönümünde kota yenilenmesi; 401/403/429/5xx yanıtları.
- ŞL eleme ve Konferans Ligi'nde takip edilen kulüp maçı; Türkiye Kupası/Süper Kupa (anahtar yok, okunmuyor).
- EURO elemeleri, EURO, DK Avrupa elemeleri ve DK'da Türkiye maçı (şu an sezon dışı).
- Discord'da gerçek otomatik kart (canlı doğrulama değil).

## Doğrulama listesi (tekrar çalıştırmak için)

Her biri için sonuç **gözlendi / NOT_OBSERVED** olarak yazılacak; o gün ilgili maç yoksa "desteklenmiyor" sonucu
çıkarılmaz, yakın tarih aralığı kontrol edilir ve gözlenemeyen kısım NOT_OBSERVED kalır.

1. Katalogda beş anahtarın `active` durumu (sezonda olan/olmayan).
2. Önümüzdeki 7 günde Galatasaray, Fenerbahçe, Beşiktaş maçları: doğru erkek A takımı mı, ev/deplasman sırası doğru mu,
   sağlayıcının kulüp yazımları ne (ör. "Fenerbahce", "Besiktas JK") — `REVIEW:` satırları alias listesine eklenmeli mi?
3. `/odds` (en fazla 25 kredi; varsayılan 5): aynı maçta eksiksiz ev/beraberlik/deplasman `h2h` var mı, hangi
   bookmaker'lar dönüyor, öncelik listesinden hangisi seçiliyor, pazar `last_update` taze mi.
4. Başlıklardan gerçek kalan kota, `x-requests-last` değeri (1 bekleniyor).
5. Gözlenen organizasyonlar (Süper Lig ve varsa Avrupa kupası).
6. Kupa maçlarının (Türkiye Kupası) hiçbir anahtarda görünmediğinin teyidi.

Komutlar (Discord'a hiçbir şey göndermez, bot veritabanına yazmaz, anahtarı basmaz):

```
dotnet run --project src/ToroSquad.Bot -- predictions football-check --days 30
dotnet run --project src/ToroSquad.Bot -- predictions football-check --days 30 --odds --budget 5 --focus TR --preview
```

Bu tur için bütçe toplamda en fazla **5 kredi**; Türkiye maçı bulunursa onun organizasyonu önce, üç kulübün maçları
yeniden sorgulanmak zorunda değil. İlk uygun gözlem/yayın maçı gerçek keşiften hesaplanır (2026-09-30 kontrolünde yalnız
kulüp organizasyonları okunmuştu; en erken takip edilen maç 09.10 Galatasaray – Kasimpasa SK idi; millî maçlar bilinmiyor).

User-secrets yalnızca Development ortamında yüklenir: komutu `DOTNET_ENVIRONMENT=Development` ile (veya anahtarı
`TOROSQUAD_Predictions__Automation__TheOddsApi__ApiKey` ortam değişkeniyle) çalıştırın.

## Alternatif kaynak politikası

The Odds API kapsamı yeterliyse orada kalınır; başka sağlayıcı istemcisi yazılmadı. Gerçek ücretsiz testte önemli bir
açık görülürse kısa karar raporu: eksik olan maç keşfi mi, oran mı, güncel sezon erişimi mi, belirli kupa mı, kota mı?
Sonraki adaylar (ücretsiz planları o aşamada yeniden doğrulanır; API-Football'un güncel sezon erişimi varsayılmaz):
fikstür/durum için Highlightly, fikstür+oran için API-Football veya OddsPapi. Sessizce ikinci sağlayıcıya veya ücretli
plana geçilmez.
