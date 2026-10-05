# TSQ Bot Updates — oyun güncelleme bildirimleri

Ayrı modül (`updates`, `src/ToroSquad.Modules.Updates`). Takip edilen bir oyun **resmî bir güncelleme / yama notu**
yayımladığında yapılandırılmış **tek** güncelleme kanalına, her güncelleme için **bir kez**, sade bir kart gönderir. Elle
güncelleme veya bağlantı girişi yoktur.

Varsayılan **kapalıdır**: `Updates:Mode=Off` (kaynak isteği yok), her sunucuda modül kapısı kapalı
(`/modules enable updates`), kanal seçilmemiş ve **hiçbir oyun açık değil**. Dördü de sağlanmadan hiçbir şey gönderilmez.

## Desteklenen oyunlar ve sağlayıcılar

| Oyun | Anahtar | Sağlayıcı | Sağlayıcıdaki kimlik | Sınıflandırıcı |
|---|---|---|---|---|
| Counter-Strike 2 | `cs2` | Steam (`steam`) | AppID `730` | `Cs2UpdateClassifier` |

Bugün gerçekten uygulanan tek oyun budur; arayüzde yer tutucu oyun yoktur.

## Genel mimari

```
Steam HTTP cevabı ─► SteamNewsParser ─► GameUpdateCandidate (normalize) ─► oyunun sınıflandırıcısı
                                                                               │
                          UpdatesPlanner ◄─────────────────────────────────────┘
                          tek SQLite transaction: duyurular + kaynak durumu + teslim kayıtları + outbox
                                   │
                          OutboxProcessor ─► UpdatesDeliveryPolicy (mod / kanal / duraklatma / oyun) ─► Discord
```

- **Oyun** bir `GameUpdateDefinition` kaydıdır: anahtar, ad, sağlayıcı, sağlayıcıdaki kimlik, sınıflandırıcı.
- **Sağlayıcı** bir `IGameUpdateProvider`'dır: yalnızca okur; sunucuları, outbox'ı ve Discord'u görmez.
- **Sınıflandırıcı** bir `IGameUpdateClassifier`'dır: saf fonksiyon (I/O ve saat yok).
- Alan modeli, planlayıcı, kalıcılık ve komutlar Steam'e veya CS2'ye özgü hiçbir tipi görmez; sağlayıcı DTO'ları
  renderer'a ulaşmaz (`UpdatesArchitectureTests`).

Yeni bir **Steam oyunu** eklemek: `Domain/Games/` altına bir tanım + sınıflandırıcı ve `UpdatesModule`'de tek satır kayıt.
Yeni bir **sağlayıcı** eklemek: `Providers/` altına bir `IGameUpdateProvider` ve kayıt; Steam kodu ve planlayıcı
değişmez. Testler bunu uydurma ikinci bir oyun + sağlayıcıyla kanıtlar. Kayıt hatası (yinelenen anahtar, kayıtsız
sağlayıcı, geçersiz anahtar) başlangıç hatasıdır.

## Kaynak: Steam Web API (ISteamNews)

| | |
|---|---|
| Uç nokta | `GET https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/` (sabit adres; kullanıcı URL'si yok) |
| Parametreler | `appid=730`, `count=20` (`Updates:ItemsPerRequest`), `maxlength=0`, `feeds=steam_community_announcements`, `format=json` |
| API anahtarı | **Gerekmez.** Authorization başlığı, çerez, Steam girişi veya kimlik bilgisi yok |
| Kullanılmayan | `ISteamNews/GetNewsForAppAuthed` (`partner.steam-api.com`, yayıncı anahtarı ister) — hiç çağrılmaz |
| Resmî doküman | <https://partner.steamgames.com/doc/webapi/ISteamNews> ve API'nin kendi yöntem listesi `ISteamWebAPIUtil/GetSupportedAPIList` (okundu: 2026-10-05) |

İstek: `GET`, iletişim bilgili User-Agent (`TSQBot/… UpdatesModule (+https://github.com/Torokal/TSQ-Bot)`), gzip/br,
**yönlendirme takip edilmez** (3xx = hata), gövde sınırı (2 MiB), 15 sn zaman aşımı, satır içi yeniden deneme yok. Sorguya
yalnızca oyunun sayısal AppID'si ve sınırlı `count` girer.

`maxlength=0` tam duyuru metnini ister: başka her değerde Steam işaretlemesi sökülmüş bir özet üretir ve sınıflandırıcının
okuduğu yapı (`[ MAPS ]` gibi bölüm başlıkları, listeler) kaybolur. Metin yalnızca bellekte sınıflandırma girdisidir
(en fazla 16 000 karakter); **saklanmaz, loglanmaz, karta konmaz**.

### Gerçek cevapta gözlenenler (2026-10-05, yerel ağ, birkaç salt-okuma isteği)

- HTTP 200, `Content-Type: application/json; charset=UTF-8`, gzip; 20 duyuruluk tam metinli cevap ≈ 35 KB (gzip ≈ 9 KB).
- Biçim: `appnews { appid, newsitems [ … ], count }`. Duyuru alanları: `gid`, `title`, `url`, `is_external_url`, `author`,
  `contents`, `feedlabel`, `date` (Unix saniye), `feedname`, `feed_type`, `appid` ve — varsa — `tags`.
- `feeds` filtresi olmadan basın akışları da gelir (ör. `PC Gamer`, `GamingOnLinux`); filtreyle 150 duyurunun tamamı
  `feedname=steam_community_announcements` (oyunun kendi resmî duyuruları).
- `tags`: yama notlarında Steam'in kendi `patchnotes` etiketi. `GetSupportedAPIList` çıktısında belgelenen `tags`
  parametresi de bu etiketi örnek verir.
- `is_external_url` Steam'in kendi duyurularında da `true`: ayırt edici **değil**, kullanılmaz.
- `url`: `https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/<gid>` — Steam'in haber
  yönlendiricisi; `steamcommunity.com` üzerindeki duyuruya 302 ile gider. Aynı yol `store.steampowered.com` üzerinde de
  aynı yönlendirmeyi verir (bir duyuruyla denendi).
- Önbellek: cevap `Expires` başlığını `Date` + 60 dk olarak taşır; aynı adrese bir dakika sonra yapılan istekte `Expires`
  **değişmedi** (cevap kaynak tarafında yaklaşık bir saat önbellekli). `ETag`, `Last-Modified`, `Cache-Control` ve hız
  sınırı başlığı **yok**.
- Bilinmeyen AppID: HTTP 403 ve `{}` gövdesi (boş liste değil).
- 150 duyuruda `gid` yayın sırasıyla artıyordu; bu belgelenmiş bir garanti olmadığı için **hiçbir yerde varsayılmaz**.
- Railway ağından erişim: **NOT_VERIFIED**.

### Kullanım koşulları

Steam Web API Terms of Use (<https://steamcommunity.com/dev/apiterms>, 2026-10-05'te okundu): günde 100 000 çağrı sınırı;
Steam verisi uygulamanın Valve/Steam tarafından onaylandığı izlenimini vermemeli. Bu modül oyun başına saatte en fazla
12, kaynak önbelleği nedeniyle fiilen yaklaşık 1 istek yapar. Kart yalnızca **orijinal başlığı, Steam bağlantısını ve
yayın zamanını** "Kaynak: Steam" ile gösterir; yama metni, görsel veya Steam/Valve markası TSQ Bot'a aitmiş gibi
kullanılmaz. Bu değerlendirme bir hukuki uygunluk garantisi değildir.

## CS2 güncelleme sınıflandırıcısı

Yanlış bildirim, bir güncellemeyi kaçırmaktan **daha kötü** sayılır: kesin olmayan her şey `Ambiguous`'tur ve gönderilmez.
Kurallar resmî duyuru akışındaki 150 duyuruya (2024-11 → 2026-10) bakılarak yazıldı; 108'i "Counter-Strike 2 Update"ti.

1. **Güçlü başlık** (normalize edilmiş tam eşleşme): `Counter-Strike 2 Update`, `Counter-Strike 2 Pre-Release Update`
   → `Update` (`strong_title`).
2. Aksi halde üç bağımsız sinyal sayılır: **güncelleme benzeri başlık** (son kelimesi "Update", ya da "Release Notes" /
   "Patch Notes" ile başlar), Steam'in **`patchnotes` etiketi**, metnin **yama notu yapısı** (boşluklu ve büyük harfli en
   az bir `[ BÖLÜM ]` başlığı **ve** en az bir liste maddesi). İki veya daha fazlası `Update`; tam biri `Ambiguous`;
   hiçbiri `NotUpdate`.
3. Başlık bir **etkinlik, turnuva, eşya veya atölye** duyurusuna işaret ediyorsa (major, playoffs, champions, finals,
   tournament, sticker, capsule, case, collection, charm, music kit, pass, sale, merch, souvenir, medal, workshop,
   pick'em) 2. kural onu `Update` yapamaz: sinyal varsa `Ambiguous` (`conflicting_signals`), yoksa `NotUpdate`.

Başlıkta bir yerde "update" kelimesinin geçmesi sinyal **değildir**; tek bir köşeli parantez bölüm sayılmaz; `[p]`,
`[list]`, `[url=…]` gibi küçük harfli işaretleme etiketleri bölüm değildir. Kesik veya boş metin ikinci sinyali veremez.

| Gerçek akıştaki örnek başlık | Karar |
|---|---|
| Counter-Strike 2 Update · Counter-Strike 2 Pre-Release Update | `Update` (`strong_title`) |
| Rush Hour · Cologne 2026: Ranked Series | `NotUpdate` (`no_update_signal`) |
| Call II Arms-ory (yalnızca `patchnotes` etiketi, yama notu yapısı yok) | `Ambiguous` (`tagged_without_patch_notes`) |
| CS2 Workshop Update (etiketsiz) | `Ambiguous` (`conflicting_signals`) |

Karar ve kısa gerekçe kodu `updates_item`'da saklanır; `status`/`doctor` ve `updates check` gösterir. Kullanıcıya giden
kartta gerekçe **gösterilmez**. Baseline sonrası `Ambiguous` çıkan yeni bir duyuru bir kez Warning olarak loglanır.

## Mod, akış ve hata davranışı

`Updates:Mode`: `Off` (varsayılan) / `DryRun` / `Live`. Restart ile değişir. Host'un genel `Delivery:Mode` ayarı `Send`
değilse `Live` fiilen `DryRun`'dır (planlamada **ve** gönderim anında: kuyruktaki canlı kart bekletilmez, iptal edilir).
Etkin mod, **kuyruktaki** kartlara da uygulanır:

| Mod | Kaynak isteği | Yeni kart | Kuyruktaki canlı kart / canlı düzenleme | Kuyruktaki DryRun kartı |
|---|---|---|---|---|
| `Off` | yok | yok | gönderilmez: gönderim **iptal** (`updates_mode_off`), bekleyen düzenleme **atılır** | iptal |
| `DryRun` | var | DryRun kartı (simüle) | gönderilmez: iptal (`updates_mode_dry_run`), bekleyen düzenleme atılır | simüle edilir |
| `Live` | var | canlı kart | gönderilir / düzenlenir | simüle edilir (Discord'a gitmez) |

Kontrol `UpdatesDeliveryPolicy`'dedir: outbox her gönderim, düzenleme ve uzlaştırma sonrası yeniden gönderimden hemen önce
çağırır. Aynı yerde sunucunun **o anki** durumu da denetlenir: kanal yok, duraklatılmış, oyun kapatılmış veya kanal
değişmişse kart iptal edilir. İptal kalıcıdır. Discord'a ulaşmış bir mesaj hiçbir durumda geri alınmaz veya silinmez.
Diğer modüller ve genel outbox etkilenmez.

**Mod değişikliği güvenlidir:** her sunucu en son hangi modda planlandığını saklar (`PlannedMode`). Çalışan mod farklıysa
o modun penceresi **şimdi** başlar (başlangıçta — `Off` iken de — ve her planlamadan önce). Mod kaydedilemezse
kaydedilene kadar yeniden denenir; o zamana kadar ne istek yapılır ne plan. Sonuç: DryRun'da görülmüş veya `Off` iken
yayımlanmış bir güncelleme, mod `Live` olduğunda **gönderilmez**. Aynı modla yeniden başlatma pencereyi değiştirmez;
kesinti telafisi çalışır.

- **Tek ortak istek:** oyun (sağlayıcı + kimlik) başına tek istek; sunucu başına istek yok. Sonuç o oyunu takip eden tüm
  sunuculara dağıtılır. Bir oyunu hiçbir sunucu almıyorsa (modül kapalı, kanal yok, duraklatılmış veya oyun kapalı) o oyun
  için istek de yapılmaz.
- **Aralık:** proje varsayılanı `Updates:PollIntervalMinutes` (5 dk) **veya** kaynağın cevabıyla bildirdiği önbellek ömrü
  (`Cache-Control: max-age`, yoksa `Expires − Date`), hangisi uzunsa — en çok 2 saat. Steam bugün yaklaşık 60 dk bildirir:
  daha erken sormak aynı önbellekli cevabı döndürür. Bu Steam'in belgelenmiş bir kotası değildir.
- **Hatalar asla "güncelleme yok" sayılmaz** ve ayrı tutulur: `RateLimited` (429, `Retry-After`'a uyulur, yoksa 30 dk),
  `Timeout`, `ServerError` (5xx), `HttpError` (diğer durumlar, yönlendirme dahil), `TransportError`, `TooLarge`,
  `Malformed` (JSON değil / bozuk / çok derin), `UnexpectedSchema` (beklenen biçim değil, başka AppID, kullanılabilir duyuru
  yok). Başarı: `SuccessItems`, `SuccessNoNewItems`, `SuccessEmpty`. Ardışık hatalarda üstel geri çekilme (en çok 6 saat).
- Sonraki kontrol zamanı ve hata sayacı veritabanındadır: restart geri çekilmeyi sıfırlamaz, ek istek üretmez; turlar üst
  üste binmez. Bir oyunun veya sağlayıcının hatası diğer oyunu, diğer modülleri ve host'u etkilemez; kapanış iptali
  normal çalışır.
- **Tek transaction:** duyurular, sınıflandırma, kaynak durumu, teslim kayıtları ve outbox satırları birlikte yazılır.
  Kayıt başarısızsa hiçbiri yazılmaz ve tur bir sonraki aralıkta yinelenir.
- **Gecikme:** güncelleme, Steam cevabında göründükten sonraki kontrol turunda planlanır (kaynak önbelleğiyle en çok
  yaklaşık 1 saat). Yayın saniyesinde teslim garantisi yoktur.

## Güvenli okuma ve bağlantı politikası

JSON derinliği en çok 16, cevap başına en çok 100 duyuru, gövde boyutu sınırı. Cevap istenen AppID için olmalıdır (aksi
`UnexpectedSchema`). Bir duyuru ancak şu koşullarla kullanılır: `feedname` resmî duyuru akışı, kendi `appid`'i (varsa)
eşleşiyor, `gid` yalnızca rakam, başlık boş değil, bağlantı politikadan geçiyor. Diğerleri atlanır ve sayılır; hiç
kullanılabilir duyuru yoksa cevap hatadır. Gelecekteki (1 saatten fazla) veya eksik tarih "yok" sayılır.

Kabul edilen tek bağlantı biçimi: `https://{steamstore-a.akamaihd.net | store.steampowered.com}/news/externalpost/
steam_community_announcements/<gid>` — https, varsayılan port, kullanıcı bilgisi/sorgu/parça yok ve `<gid>` duyurunun
kendi kimliği. Kartta her zaman `store.steampowered.com` biçimi gösterilir. Başka host, başka akışın duyurusu veya başka
bir kimlik reddedilir; renderer da sağlayıcının tanımadığı bir bağlantıyı çizmez. Sayfa GET/HEAD ile **açılmaz**.

## Baseline, kimlik, düzeltme

- **Kimlik:** sağlayıcı + oyun + sağlayıcının duyuru kimliği (Steam `gid`; outbox anahtarı `steam:730:<gid>`). Başlık
  kimlik **değildir**: aynı başlıklı iki güncelleme iki ayrı karttır.
- **Baseline:** bir oyunun ilk geçerli ve boş olmayan cevabındaki tüm duyurular baseline işaretlenir ve **hiç
  gönderilmez** (hangi modda kurulursa kurulsun tektir). Başarısız, bozuk, başka AppID'ye ait veya boş ilk cevap baseline
  kurmaz.
- **Teslim kaydı:** sunucu + sağlayıcı + oyun + duyuru + tür (`update:<oyun>` / `update-dry:<oyun>`). Kanal kimliğe
  **dahil değildir**: kanal değişince eski güncellemeler yeniden gönderilmez, yeni güncellemeler yeni kanala gider, eski
  mesajlar taşınmaz/silinmez. Tekrar yoklama, restart ve redeploy ikinci kart üretmez.
- **Pencere:** bir sunucu yalnızca penceresi başladıktan sonra **hem ilk kez görülen hem yayımlanan** güncellemeleri alır.
  Pencere şunlarda yeniden başlar: kanalın ilk seçilmesi, `resume`, modülün yeniden açılması, mod değişikliği; ayrıca her
  oyunun açıldığı an o oyun için alt sınırdır. Duraklatma/kapalı dönemi telafi edilmez.
- **Yayın zamanı zorunlu:** sağlayıcı zamanı olmayan bir duyuru otomatik gönderilmez; ilk görülme zamanı yayın zamanı
  yerine kullanılmaz veya gösterilmez.
- **Düzeltme:** aynı kimlikte başlık, bağlantı veya yayın zamanı değişirse, sunucunun **şu anki kanalındaki** kartı aynı
  outbox anahtarıyla o turda yeniden planlanır → aynı mesaj **sessizce düzenlenir**. Yalnızca metin değiştiyse kart aynıdır
  ve düzenleme yapılmaz. Düzeltme hiçbir zaman yeni kart oluşturmaz: eski kanaldaki kart olduğu gibi kalır, moderatörün
  sildiği veya outbox kaydı kalmamış kart yeniden oluşturulmaz.
- **Sonradan güncelleme sayılan duyuru** (ör. etiket geç eklendi): baseline değilse ve pencere/catch-up kurallarını
  geçiyorsa **bir kez** gönderilir.
- **Artık güncelleme sayılmayan duyuru:** gönderilmiş kart düzenlenmez ve silinmez; durum kaydedilir, `doctor` gösterir.
- **Belirsiz gönderim:** mevcut outbox uzlaştırması; bağlantı açıklamada da yer aldığı için aynı başlıklı iki kart farklı
  parmak izi taşır.

## Kesinti sonrası ve tutma

- **Catch-up:** yalnızca son `Updates:CatchUpHours` (24 saat) içinde yayımlanmış güncellemeler, tur ve sunucu başına en
  fazla `Updates:MaxCardsPerRound` (3) kart; kalanlar sonraki turlarda. 3 saatlik kesintide yayımlanan iki güncellemenin
  ikisi de gelir; 3 aylık kesinti kanalı doldurmaz. Bunlar proje tercihidir, Steam kotası değildir.
- **Tutma:** başlıklar `Updates:TextRetentionDays` (90 gün) sonra silinir; kimlikler ve teslim kayıtları
  `Updates:DedupRetentionDays` (365 gün) sonra. Yalnızca catch-up penceresinde yayımlanmış duyurular planlandığı için
  silinmiş eski bir duyuru yeniden görünse de gönderilemez; ek olarak silinenlerin en yeni yayın zamanı filigran olarak
  tutulur ve o tarihe kadar yayımlanmış duyurular baseline sayılır. Kimliklerin sıralı olduğu varsayılmaz.

## Kart

```
🛠️ CS2 Güncellemesi                       (başlık → Steam bağlantısı)
**Counter-Strike 2 Update**                (sağlayıcının orijinal başlığı)

Yeni Counter-Strike 2 güncellemesi yayınlandı.

Steam'de Güncelleme Notlarını Gör          (aynı bağlantı)
Kaynak: Steam · <Discord native yayın zamanı>
```

Tek kart. Ping yok (`MentionPolicy.None`, düzenlemeler de ping'siz); `@everyone`, rol pingi yok. Yama metni, özet, yapay
zekâ, çeviri, görsel, debug kimliği, ham `gid` veya sağlayıcı cevabı yok. Başlık Discord sınırına göre kısaltılır ve
mention, markdown veya bağlantı üretemez (`DiscordText.Untrusted`). Sabit metinler Türkçe (İngilizce yedek).

## Komutlar (Sunucuyu Yönet; tüm cevaplar yalnızca kullanana görünür)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:updates islem:configure kanal:` | Güncelleme kanalı (bu sunucuda, botun görebildiği metin/duyuru kanalı; View Channel + Send Messages + Embed Links denetlenir) |
| `/tsq-admin modul:updates islem:games` | Kayıtlı oyunlar ve bu sunucuda açık/kapalı durumu |
| `/tsq-admin modul:updates islem:game-enable` / `game-disable` | Bir oyunu açar / kapatır. Tek kayıtlı oyun varken doğrudan ona uygulanır; birden fazlaysa yalnızca kayıtlı oyunları listeleyen özel bir seçim açılır |
| `/tsq-admin modul:updates islem:pause` / `resume` | Gönderimi duraklatır / sürdürür (duraklatma dönemi telafi edilmez) |
| `/tsq-admin modul:updates islem:preview` | Kayıtlı en son gerçek güncellemenin veya açıkça **sentetik** (bağlantısız) bir örneğin kartını yalnızca yöneticiye gösterir |
| `/tsq-admin modul:updates islem:status` | Mod, modül, kanal, açık oyunlar; oyun başına son başarılı kontrol, son sonuç, sonraki kontrol, son bulunan güncelleme, Discord'a gerçekten ulaşan son kart |
| `/tsq-admin modul:updates islem:doctor` | Mod, modül, kanal ve izinler, oyunlar, kaynak sağlığı, baseline, son cevap (güncelleme/belirsiz/atlanan), kapsama boşluğu, kayıt durumu |

Genel kullanıcı komutu yoktur (ilk sürüm otomatik bildirim + yönetimdir). Komutlar modül kapalıyken de çalışır. Yeni slash
komutu eklenmez: `/tsq-admin` şeması değişmez, işlemler autocomplete ile görünür.

Salt-okuma CLI: `dotnet run --project src/ToroSquad.Bot -- updates check [--game cs2]` — gerçek kaynağı oyun başına bir
kez okur ve her duyuru için kimlik, yayın zamanı, karar + gerekçe ve başlığı yazar. Veritabanı açılmaz, Discord'a
bağlanılmaz, API anahtarı istenmez, duyuru metni yazdırılmaz. Otomatik test paketi internete çıkmaz.

## Gizlilik ve veri

Kişisel veri tutulmaz. Tablolar (yalnızca ekleme yapan `UpdatesModule` migration'ı):

| Tablo | İçerik |
|---|---|
| `updates_source_state` | Sağlayıcı + oyun başına yoklama durumu (baseline, son sonuç, hata sayacı, sonraki kontrol, filigran) |
| `updates_item` | Duyuru kimliği, bağlantı, başlık (90 gün), yayın zamanı, ilk/son görülme, içerik özeti (hash), karar + gerekçe |
| `updates_guild_config` | Sunucu, kanal, duraklatma, pencere zamanları, son planlanan mod — kullanıcı kimliği yok |
| `updates_subscription` | Sunucu + oyun, açık/kapalı, açılma zamanı |
| `updates_delivery` | Sunucu + duyuru + tür + kanal (365 gün) |

Duyuru metni, yazar, etiketler ve ham API cevabı saklanmaz. Bot bir sunucudan ayrılıp saklama süresi dolduğunda o
sunucunun kanal, oyun ve teslim kayıtları diğer sunucu verileriyle birlikte silinir; duyurular ve kaynak durumu sunucu
verisi değildir.

## Canlıya alma (sahibinin onayıyla)

Migration ekleme yapar; yine de önce yedek doğrulanır.

1. Veritabanı yedeğini doğrula (`/data/backups`, son günlük yedek).
2. PR'ı merge et → Railway deploy eder. `Updates:Mode=Off` olduğu için davranış değişmez.
3. Loglarda migration'ın (`UpdatesModule`) başarıyla uygulandığını gör.
4. `scripts/Sync-Commands.ps1` ile önce dry-run: slash şeması değişmediği için "Nothing to change" beklenir.
5. `/tsq-admin modul:updates islem:configure kanal:#kanal` → `/modules enable updates` →
   `/tsq-admin modul:updates islem:game-enable` → `islem:doctor`.
6. `TOROSQUAD_Updates__Mode=DryRun` (restart). İlk geçerli cevap baseline'dır. `doctor`'da kaynak sağlığını ve baseline'ı,
   loglarda `[DRY-RUN]` kartlarını izle.
7. `/tsq-admin modul:updates islem:pause`.
8. `TOROSQUAD_Updates__Mode=Live` (restart). Duraklatılmış tek sunucu varken kaynak isteği de yapılmaz.
9. `/tsq-admin modul:updates islem:resume` → yalnızca bu andan sonra yayımlanan güncellemeler canlıya gider.

7–9 arası sıra temkinli yoldur; mod değişikliği kuralı sayesinde DryRun'dan doğrudan `Live`'a geçmek de DryRun'da
görülen güncellemeleri göndermez (`UpdatesModeTransitionTests`).

Geri alma: `/tsq-admin modul:updates islem:pause`, `/modules disable updates` veya `TOROSQUAD_Updates__Mode=Off`.

## Sınırlar

- Yalnızca resmî duyuru akışında yayımlanan güncellemeler görülür; duyurusu olmayan bir istemci güncellemesi görülmez.
- Gecikme en çok yaklaşık 1 saat (kaynak önbelleği) + bir yoklama aralığı.
- Sınıflandırıcı temkinlidir: alışılmadık başlıklı ve etiketsiz bir güncelleme `Ambiguous` kalıp gönderilmeyebilir
  (`doctor` ve loglar gösterir).
- Kuyruktayken iptal edilen (duraklatma, kanal değişikliği, oyun/modül kapatma, mod) veya outbox denemeleri tükenip
  kalıcı hataya düşen (ör. kanalda izin yok, uzun Discord kesintisi) bir kart sonradan yeniden gönderilmez; kanal sorunu
  `doctor`'da görünür.
- Düzeltme yalnızca değişikliğin görüldüğü turda ve sunucu o anda etkinken uygulanır; duraklatma sırasında gelen düzeltme
  sonradan uygulanmaz.
- Bir oyun açıldıktan sonra ama ilk başarılı cevaptan önce yayımlanan güncelleme baseline'da kalır ve gönderilmez.
- 24 saatten uzun kesintide daha eski güncellemeler gönderilmez.
- Yama notu önizlemesi, özet, çeviri ve oyun başına ayrı kanal yoktur.

## Doğrulama durumu

- **TESTED_OFFLINE:** Steam isteği (adres, AppID, anahtar yok), tüm sonuç sınıfları, boyut/derinlik/şema/AppID
  doğrulaması, bağlantı politikası; sınıflandırıcı (güçlü, olumsuz, belirsiz, Unicode, kesik metin, yanıltıcı "update");
  baseline, tekrar/restart, düzeltme (yalnızca düzenler, eski kanala dokunmaz), silinen kart, yeniden sınıflandırma,
  duraklatma, kanal/oyun/modül değişiklikleri, tek ortak istek, catch-up, tutma (hâlâ listelenen duyuru silinmez), sunucu
  verisi temizliği, geri çekilmenin restart'ta korunması, atomik tur, hata yalıtımı; mod geçişleri (Off, DryRun, Live,
  kuyruktaki gönderim/düzenleme/uzlaştırma, DryRun→Live, pause→Live→resume, Off→Live, gönderimi kapalı host, mod
  kaydının yeniden denenmesi); kart güvenliği; ikinci uydurma oyun + sağlayıcı; yetki (`UpdatesSteamProviderTests`, `UpdatesClassifierTests`, `UpdatesTests`,
  `UpdatesModeTransitionTests`, `UpdatesArchitectureTests`, `TsqAdminCommandTests`).
- **Gerçek kaynak (yerel ağ, 2026-10-05):** `updates check` — HTTP 200, 20/20 duyuru kullanılabilir, 0 atlandı; 17
  `Update`, 1 `Ambiguous` (Call II Arms-ory), 2 `NotUpdate` (Rush Hour, Cologne 2026: Ranked Series).
- **NOT_VERIFIED:** Railway ağından erişim; canlı Discord kartı (ancak canlıya alındıktan sonra yayımlanan gerçek bir
  güncellemeyle doğrulanabilir — eski bir güncellemeyi test için göndermek bu doğrulama sayılmaz).
