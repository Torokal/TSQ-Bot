# The Odds API · sağlayıcı doğrulaması

Otomatik futbol öngörüleri ([AUTO_FOOTBALL.md](AUTO_FOOTBALL.md)) için tek sağlayıcı **The Odds API**'dir
(https://the-odds-api.com). Odds-API.io farklı bir servistir ve kullanılmaz.

**Durum (2026-09-30): PROVIDER_VERIFIED_READ_ONLY** (kısmi; aşağıdaki NOT_OBSERVED satırları hariç). Ücretsiz planlı
gerçek anahtarla, yerelde `predictions football-check --days 16 --odds --budget 5` salt-okunur çalıştırıldı (Discord'a
hiçbir şey gönderilmedi, bot veritabanına yazılmadı, anahtar hiçbir çıktıda görünmedi). Gerçek API okuması Discord
kartının canlı doğrulaması değildir; testlerdeki yanıtlar SENTETİKTİR ve ücretsiz kapsamın kanıtı değildir.

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

## Hâlâ gözlenmeyenler

- `h2h` pazarının normal süre (90 dk + uzatma dakikaları) anlamı — belge açıkça yazmıyor, veriden anlaşılamaz.
- Canlı/başlamış maç yanıtı, erteleme veya saat değişikliği (sağlayıcıda durum alanı yok; yalnızca `commence_time`).
- Ay dönümünde kota yenilenmesi; 401/403/429/5xx yanıtları.
- ŞL eleme ve Konferans Ligi'nde takip edilen kulüp maçı; Türkiye Kupası/Süper Kupa (anahtar yok, okunmuyor).
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
dotnet run --project src/ToroSquad.Bot -- predictions football-check --days 16
dotnet run --project src/ToroSquad.Bot -- predictions football-check --days 16 --odds --budget 5
```

User-secrets yalnızca Development ortamında yüklenir: komutu `DOTNET_ENVIRONMENT=Development` ile (veya anahtarı
`TOROSQUAD_Predictions__Automation__TheOddsApi__ApiKey` ortam değişkeniyle) çalıştırın.

## Alternatif kaynak politikası

The Odds API kapsamı yeterliyse orada kalınır; başka sağlayıcı istemcisi yazılmadı. Gerçek ücretsiz testte önemli bir
açık görülürse kısa karar raporu: eksik olan maç keşfi mi, oran mı, güncel sezon erişimi mi, belirli kupa mı, kota mı?
Sonraki adaylar (ücretsiz planları o aşamada yeniden doğrulanır; API-Football'un güncel sezon erişimi varsayılmaz):
fikstür/durum için Highlightly, fikstür+oran için API-Football veya OddsPapi. Sessizce ikinci sağlayıcıya veya ücretli
plana geçilmez.
