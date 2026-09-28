# TSQ Döviz & Altın

`/dolar`, `/euro` ve `/altın`, güncel USD/TRY, EUR/TRY ve gram altın **alış/satış** fiyatlarını döviz kanalında herkese
açık bir kartla gösterir. Argüman yoktur. Her gün 09:00'da (Türkiye saati) aynı kanala üçünü birlikte gösteren tek bir
günlük kart gönderilir. Her sunucuda varsayılan kapalıdır: `/modules enable currency` (listede **Döviz & Altın**).

Kendi tablosu ve migration'ı yoktur; API anahtarı/secret ve yapay zekâ kullanılmaz. Fiyatlar yalnızca gerektiğinde (bir
komut veya 09:00 kartı) alınır ve bellekte önbelleklenir; sürekli fiyat sorgulayan bir arka plan işi yoktur. Modül
kapalıyken hiçbir ağ isteği yapılmaz. Bot açılışında sağlayıcıya ya da Discord'a zorunlu istek atılmaz (sağlayıcı
kapalıyken de bot açılır).

## Döviz kanalı

Komutlar yalnızca **tek bir kanalda** çalışır: `Currency:ChannelId` = **`1242464361855848459`** (üretim varsayılanı;
yönetici komutu veya tablo yok). Başlangıçta doğrulanır (geçerli bir Discord snowflake olmalı).

- Döviz kanalında: mevcut kartlar **herkese açık** (ephemeral değil, ping yok).
- Başka bir kanalda: yalnızca komutu kullanan kişinin gördüğü (ephemeral) tek satır —
  "Bu komutu yalnızca <#1242464361855848459> kanalında kullanabilirsiniz." (tıklanabilir kanal etiketi; İngilizce
  sunucuda "You can only use this command in <#…>.") — ve **başka hiçbir şey**: fiyat sorgusu, sağlayıcı isteği, herkese
  açık onay veya defer yapılmaz. Kontrol üç komutta ortaktır (`CurrencyCommandFlow`).

## Komutlar

| Komut | Gösterir |
|---|---|
| `/dolar` | 💵 Amerikan Doları — alış, satış |
| `/euro` | 💶 Euro — alış, satış |
| `/altın` | 🪙 Gram Altın — alış, satış |

Kart: başlık, **Alış** ve **Satış** (`tr-TR`: `48,820 ₺`, `6.439,47 ₺`; sağlayıcının hassasiyeti korunur, 2–4 ondalık),
**Güncellendi** (sağlayıcının kendi güncelleme zamanı — botun istek zamanı değil — Discord zaman damgasıyla, her
izleyicinin kendi saat diliminde: "2 dakika önce · 28 Eylül 2026 14:49") ve altta **Kaynak**. Cevaplar ephemeral değildir
ve kimseyi etiketlemez. Önbellekteki fiyat anında gönderilir; önbellek boşsa komut önce herkese açık olarak onaylanır
(sağlayıcı + yedek, Discord'un 3 saniyelik süresini aşabilir) ve kart bu onayın yerine geçer.

`/altın` adı Türkçe noktasız **ı** ile kayıtlıdır: Discord komut adlarında her dilden küçük harfe izin verir
(`^[-_\p{L}\p{N}\p{sc=Deva}\p{sc=Thai}]{1,32}$`, harflerin küçük hâli); Discord.Net 3.20 aynı kuralı kayıttan önce
doğrular. Manifest doğrulayıcısı da artık bu kuralı uygular (önceden yalnızca ASCII kabul ediyordu).

## Sağlayıcılar ve yedek sırası

| Enstrüman | 1. (birincil) | 2. (yedek) | 3. | 4. |
|---|---|---|---|---|
| USD, EUR | Altınkaynak `static.altinkaynak.com/public/Currency` (`Kod` = `USD`/`EUR`) | TCMB `www.tcmb.gov.tr/kurlar/today.xml` (`ForexBuying`/`ForexSelling`) | son başarılı fiyat (≤ 15 dk) | kontrollü hata |
| Gram altın | Altınkaynak `static.altinkaynak.com/public/Gold` (**`Kod` = `GA`**, açıklama "Gram Altın") | Trunçgil `finans.truncgil.com/v4/today.json` (`GRA`, `Type` = `Gold`, `Name` = `GRAMALTIN`) | son başarılı fiyat (≤ 15 dk) | kontrollü hata |

- Altınkaynak'ın yayınladığı public JSON servisi kullanılır; HTML kazıma yoktur. Gold listesinde aynı "Gram Altın"
  açıklamalı ama farklı fiyatlı **`PGA`** kaydı da vardır; `/altın` yalnızca açıkça `GA` kodunu kullanır. `GA` kodu başka bir
  açıklamayla gelirse (anlamı değişmişse) kullanılmaz, yedeğe geçilir.
- **TCMB** günlük **gösterge kurudur**, anlık piyasa fiyatı değildir: kartta `Kaynak: TCMB — Gösterge Kuru`, "anlık piyasa
  fiyatı değildir" notu ve saat yerine **bülten tarihi** (`Tarih`, ör. `25.09.2026`) gösterilir.
- Yedek kullanıldığında kart normal görünür, kaynak açıkça yazılır ve "Birincil veri kaynağına ulaşılamadı." notu eklenir.
- Hiçbir sağlayıcı yanıt vermezse ve son başarılı fiyat en fazla 15 dakika önce alındıysa kart
  "⚠️ Veri kaynağına şu anda ulaşılamıyor. Son başarılı fiyat gösteriliyor." uyarısı, fiyatın kendi güncelleme zamanı ve
  **Son başarılı sorgu** zamanıyla gösterilir. Daha eskiyse fiyat gösterilmez: "Döviz/altın verisine şu anda ulaşılamıyor.
  Lütfen kısa süre sonra tekrar deneyin." + takip kodu (`TS-…`).

Sağlayıcı hatası sayılanlar (hepsi bir sonraki kaynağa geçirir, komutu düşürmez): zaman aşımı, DNS/ağ hatası, 200 dışı HTTP
durumu, boş yanıt, bozuk JSON/XML, enstrümanın olmaması veya birden fazla olması, alış/satışın okunamaması, `≤ 0` ya da
satışın alıştan küçük olması, anlamsız şema değişikliği (tarih biçimi, kök öğe, TCMB `Unit` ≠ 1, `GA` açıklaması).
Her başarısız istek tek bir yapılandırılmış uyarı satırı yazar: sağlayıcı, veri kümesi, enstrüman başına hata türü, HTTP
durumu, isteyen enstrüman ve sıradaki kaynak (`fallback=Tcmb` / `last-known-good`); yığın izi yoktur. Tam hata takip
koduyla loglanır.

## Ayrıştırma

- Tutarlar her yerde `decimal`'dır (float/double yok).
- Altınkaynak `tr-TR` sayı metni gönderir: `"48,820"` = 48,820 (48 bin değil), `"6.439,47"` = 6439,47. Ayırıcılar kodda
  sabittir (sunucunun yerel ayarına/ICU verisine bağlı değil). Ondalık virgülü olmayan değer (`"48.820"`) belirsiz olduğu için
  reddedilir; bozuk değer asla `0` olmaz.
- TCMB XML'i invariant noktalı ondalık okunur; DTD ve dış varlık çözümü kapalıdır.
- Trunçgil JSON sayıları doğrudan `decimal`'a okunur.
- Zamanlar (Altınkaynak `GuncellenmeZamani` `dd.MM.yyyy HH:mm:ss`, Trunçgil `Update_Date` `yyyy-MM-dd HH:mm:ss`, TCMB
  `Tarih`) saat dilimi içermez ve **Türkiye yerel saati** (`Europe/Istanbul`, Linux'ta IANA, Windows'ta ICU eşlemesi)
  olarak okunur; UTC sanılıp üç saat kaydırılmaz.

## Önbellek

| | Süre |
|---|---|
| Birincil sağlayıcının sorunsuz yanıtı | 60 sn (`Currency:FreshSeconds`) |
| Yedek sağlayıcı yanıtı, kısmi yanıt, başarısız sağlayıcı (bu süre boyunca atlanır) | 30 sn (`Currency:FallbackFreshSeconds`) |
| Son başarılı fiyatın gösterilebileceği en fazla yaş | 15 dk (`Currency:StaleMaxMinutes`) |
| İstek zaman aşımı | 5 sn (`Currency:TimeoutSeconds`) |

Önbellek komut başına değil **sağlayıcı yanıtı başına** tutulur: tek bir Altınkaynak Currency yanıtı hem `/dolar` hem
`/euro`'yu karşılar. Aynı veri kümesi için eşzamanlı istekler tek bir çekimi bekler (single flight): önbellek boşken 20
kişi aynı anda `/dolar` yazsa da sağlayıcıya tek istek gider. Başarısız birincil sağlayıcı 30 sn atlanır, sonra yeniden
denenir; böylece Altınkaynak geri geldiğinde en geç yarım dakika içinde tekrar kullanılır.

## Günlük 09:00 kartı

- **Ne zaman:** her gün **09:00 Europe/Istanbul** (sabit bir UTC saati değil; gün, Türkiye takvim günüdür). Zamanlayıcı her
  turda bir sonraki 09:00'ı saatten yeniden hesaplar (24 saatlik sabit bekleme yok; restart/deploy kaydırmaz).
- **Kaçırılan sabah (catch-up):** 09:00–09:30 arası (`Currency:DailyCatchUpMinutes`, varsayılan 30). 09:05 veya 09:28'de ilk
  kez açılan bot o günün kartını yollar; 09:31'de veya öğleden sonra açılan bot o günü kaçırılmış sayar, geç "sabah" kartı
  atmaz. Pencere içinde tur dakikada bir çalışır.
- **Tek kart:** "💱 Günlük Döviz & Altın" başlıklı tek embed; 💵 Amerikan Doları, 💶 Euro, 🪙 Gram Altın bölümleri. Her
  bölümde alış/satış (`48,900 ₺` biçimi, tek-komut kartlarıyla aynı `Price()`), **kendi kaynağı** ve zamanı
  (`Kaynak: Altınkaynak · Güncellendi: <t:…:R>`; TCMB'de `Kaynak: TCMB — Gösterge Kuru · Bülten: 25.09.2026` ve
  gösterge kuru notu; yedekte "(yedek kaynak)"; eski veride "⚠️ Son başarılı fiyat · son sorgu …"). Tek bir ortak
  "Kaynak" altbilgisi yoktur: üç enstrüman farklı sağlayıcılardan gelebilir.
- **Aynı fiyat servisi:** fiyatlar komutlarla aynı `MarketQuoteService`'ten gelir (birincil → yedek → son iyi fiyat, önbellek,
  single flight). USD ve EUR tek Altınkaynak Currency yanıtını paylaşır: normalde en fazla bir Currency ve bir Gold isteği.
- **Kısmi veri:** bir enstrüman alınamazsa o bölümde "Şu anda alınamadı" yazar, diğerleri gösterilir. Üçü de alınamazsa kart
  gönderilmez; pencere içinde sonraki turda yeniden denenir, 09:30'a kadar gelmezse o gün atlanır (uyarı logu) ve ertesi gün
  normal devam eder.
- **Teslim ve tekrar koruması:** kart doğrudan Discord'a gönderilmez; kalıcı **outbox**'a yazılır (yeni tablo/migration yok).
  Mantıksal anahtar: sunucu + `currency` + `day:<Türkiye tarihi>` + kanal + `currency-daily`
  (ör. `live|<sunucu>|currency|day:2026-09-29|1242464361855848459|currency-daily`). O gün için outbox'ta herhangi bir durumda
  satır varsa yeniden yazılmaz (gönderilmiş kart düzenlenmez de); eşzamanlı iki tur benzersiz anahtara takılır. Böylece
  09:03'teki bir restart/deploy ikinci kart üretmez. Kanal erişilemezse (403/404) mevcut outbox teslim kuralları işler;
  kart günün sabit son teslim zamanına kadar gönderilebilir: 09:00 + 30 dk catch-up + **en fazla 5 dk teslim payı** =
  **09:35** (Türkiye). Bu pay yalnızca 09:30'a yakın outbox'a yazılan kartın teslim edilebilmesi içindir; son yeni kart 09:30'da
  hazırlanır ve 09:35'ten sonra o günün kartı Discord'a hiç gönderilmez (outbox satırı `Expired` olur, yerine yenisi yazılmaz).
  Son teslim zamanı kartın ne zaman yazıldığından bağımsızdır (09:00'da da 09:29'da da 09:35).
- **Modül kapalıysa kart yok:** yalnızca modülü açık (ve izinli) sunuculara kart hazırlanır, onlar için bile fiyat ancak
  gerekirse alınır; teslim anında modül kapısı ayrıca denetlenir. 09:00–09:30 arasında açılan modül o günün kartını alır;
  daha sonra açılırsa geçmiş sabah kartı gönderilmez.
- **Ping yok:** `MentionPolicy.None`; @everyone, @here, rol veya kullanıcı etiketi yok.
- Loglar (`currency_daily_queued` / `_no_data` / `_skipped` / `_channel_unavailable` / `_window_passed`): yerel tarih, kanal,
  tur nedeni (`startup`/`scheduled`), enstrüman başına kaynak/erişilebilirlik ve outbox sonucu.

## Yapılandırma

`appsettings.json` → `Currency`: `ChannelId`, `DailyCatchUpMinutes`, üç taban URL (`AltinkaynakBaseUrl`, `TcmbBaseUrl`,
`TruncgilBaseUrl`) ve yukarıdaki dört süre.
Varsayılanlar üretim değerleridir; secret veya Railway değişkeni gerekmez. Başlangıçta doğrulanır: URL'ler mutlak `https`,
`/` ile biten, kimlik bilgisi/sorgu içermeyen adresler olmalı; hatalı bir değer botu açık bir `CONFIG: [currency] …`
satırıyla durdurur. HTTP, sağlayıcı başına adlandırılmış `IHttpClientFactory` istemcileriyle yapılır (yönlendirme ve cookie
yok, yanıt en fazla 1 MB, `User-Agent: TSQBot`).

## Durum

`/bot status` son fiyat isteğinin (komut veya günlük kart) nasıl karşılandığını gösterir (birincil / yedek / eski veri / veri yok); bunun için sağlayıcıya
istek atılmaz.

## Canlı sözleşme kontrolü (test paketinin parçası değil)

Birim testleri tamamen çevrimdışıdır (sahte HTTP, sahte saat). Sözleşme, geliştirme sırasında gerçek endpoint'lere salt-okunur
GET ile ayrıca doğrulanır (son: 2026-09-28): Altınkaynak Currency `USD`/`EUR`, Gold `GA` (+ ayrı fiyatlı `PGA`), TCMB
`USD`/`EUR` `ForexBuying`/`ForexSelling`, Trunçgil `GRA` + `Update_Date`.
