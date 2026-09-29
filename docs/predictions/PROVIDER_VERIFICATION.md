# The Odds API · sağlayıcı doğrulaması

Otomatik futbol öngörüleri ([AUTO_FOOTBALL.md](AUTO_FOOTBALL.md)) için tek sağlayıcı **The Odds API**'dir
(https://the-odds-api.com). Odds-API.io farklı bir servistir ve kullanılmaz.

**Durum (2026-09-29): PROVIDER_VERIFICATION_PENDING.** Yerel güvenli yapılandırmada bu sağlayıcıya ait API anahtarı
yok; gerçek hesapla hiçbir istek yapılmadı. Aşağıdaki "belgede" sütunu resmî sayfalardan okundu; "gözlendi" sütunu
anahtar tanımlanıp `predictions football-check` çalıştırılınca doldurulacak. Testlerdeki yanıtlar SENTETİKTİR ve
ücretsiz kapsamın kanıtı değildir.

## Belgeden doğrulananlar (2026-09-29)

Kaynaklar: [v4 rehberi](https://the-odds-api.com/liveapi/guides/v4/),
[sporlar](https://the-odds-api.com/sports-odds-data/sports-apis.html),
[pazarlar](https://the-odds-api.com/sports-odds-data/betting-markets.html),
[bookmaker'lar](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html),
[SSS](https://the-odds-api.com/manage/faqs.html), [koşullar](https://the-odds-api.com/terms-and-conditions.html),
[ana sayfa/planlar](https://the-odds-api.com/).

| Konu | Belgede | Gözlendi |
|---|---|---|
| Host | `https://api.the-odds-api.com` (IPv6: `ipv6-api.the-odds-api.com`) | — |
| Anahtar | yalnızca `apiKey` sorgu parametresi | — |
| Ücretsiz plan | 500 kredi/ay; "All sports", "Most bookmakers", "All betting markets" | — |
| Kota yenileme | "Usage credits are automatically reset on the first of every month" (kod bunu varsaymaz; başlıktan okur) | — |
| `/v4/sports` | ücretsiz; alanlar `key, group, title, description, active, has_outrights` | — |
| `/v4/sports/{sport}/events` | ücretsiz; `id, sport_key, sport_title, commence_time, home_team, away_team`; `commenceTimeFrom/To` ISO 8601 | — |
| `/v4/sports/{sport}/odds` | maliyet = pazar × bölge; "If no events are returned, the request will not count against the usage quota"; `eventIds` filtresi | — |
| Canlı maçlar | başlamış maçlar da dönebilir (`commence_time` < şimdi ise in-play); ayrıca bir durum alanı yok | — |
| Kullanım başlıkları | `x-requests-remaining`, `x-requests-used`, `x-requests-last` | — |
| `last_update` | pazar düzeyindeki alan: sistemin o pazarı bookmaker'da son gördüğü an; askıya alınan pazar ~15 dk sonra kalkar; bookmaker düzeyindeki alan kullanımdan kalkmış | — |
| `h2h` | "Bet on the winning team or player of a game (includes the draw for soccer)"; normal süre/uzatma ayrımı **yazılmıyor** | — |
| Diğer pazarlar | `h2h_lay` (yalnız exchange), `h2h_3_way`, `draw_no_bet` ayrı anahtarlar — kullanılmaz | — |
| Organizasyon anahtarları | `soccer_turkey_super_league`, `soccer_uefa_champs_league`, `soccer_uefa_champs_league_qualification`, `soccer_uefa_europa_league`, `soccer_uefa_europa_conference_league` | — |
| Türkiye Kupası / Süper Kupa | listede **yok** | — |
| `eu` bookmaker'ları | 1xBet, 888sport, Betclic (FR), BetAnySports, BetOnline.ag, Betsson, Codere (IT), Bet Victor, Coolbet, Everygame, GTbets, LeoVegas (SE), Marathon Bet, MyBookie.ag, NordicBet, Pinnacle ("public website … may incur a delay"), PMU (FR), Suprabets, Tipico (DE), Unibet (FR/IT/NL/SE), William Hill, Winamax (DE/FR); exchange: Betfair Exchange, Matchbook | — |
| Hata kodları | yalnızca 429 (rate limit) belgelenmiş; 401/403/422/5xx kod tarafından ayrıca sınıflandırılır | — |
| Kullanım koşulları | UI'da gösterim (ticari dahil) serbest, veriyi bağımsız ürün olarak yeniden satmak yasak; atıf zorunlu değil; saklama serbest; "as is", doğruluk garantisi yok | — |

## Gözlenmesi gerekenler (anahtar gelince)

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
dotnet run --project src/ToroSquad.Bot -- predictions football-check
dotnet run --project src/ToroSquad.Bot -- predictions football-check --odds --budget 5
```

## Alternatif kaynak politikası

The Odds API kapsamı yeterliyse orada kalınır; başka sağlayıcı istemcisi yazılmadı. Gerçek ücretsiz testte önemli bir
açık görülürse kısa karar raporu: eksik olan maç keşfi mi, oran mı, güncel sezon erişimi mi, belirli kupa mı, kota mı?
Sonraki adaylar (ücretsiz planları o aşamada yeniden doğrulanır; API-Football'un güncel sezon erişimi varsayılmaz):
fikstür/durum için Highlightly, fikstür+oran için API-Football veya OddsPapi. Sessizce ikinci sağlayıcıya veya ücretli
plana geçilmez.
