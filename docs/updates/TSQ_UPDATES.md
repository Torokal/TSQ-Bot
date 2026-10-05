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
| Deadlock | `deadlock` | Steam (`steam`) | AppID `1422450` | `DeadlockUpdateClassifier` |
| World of Warcraft: Forever | `wow-forever` | Blizzard forumu (`blizzard`) | forum kategorisi `349` (ayar) | `WowForeverUpdateClassifier` |

Yalnızca gerçekten uygulanan oyunlar listelenir; arayüzde yer tutucu oyun yoktur. Her oyun sunucuda ayrı açılır.

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

## Deadlock (Steam, AppID 1422450)

Deadlock, Counter-Strike 2 ile **aynı** Steam sağlayıcısını kullanır (aynı adres, akış, sınırlar, önbellek, bağlantı
politikası); yalnızca AppID, sınıflandırıcı ve yama notu okuyucusu Deadlock'a özgüdür. Kimlik Steam'in duyuru kimliğidir
(`steam` + `deadlock` + GID; outbox anahtarı `steam:1422450:<gid>`), kart bağlantısı resmî Steam duyuru bağlantısıdır.

### Kaynak kararı: neden yalnızca Steam

Valve değişiklik günlüklerini ayrıca resmî forumda (`forums.playdeadlock.com`, "Changelog") yayımlıyor ve bazı küçük
düzeltmeleri oraya yalnızca **yanıt** olarak ekliyor. İki resmî kaynağı tek sağlayıcıda birleştirmek hedeflendi; ancak:

- Forumun `robots.txt` dosyası okunabiliyor ve `/forums/` ile `/threads/` yollarına izin veriyor (`/posts/` yasak).
- Buna rağmen forum, kendini açıkça tanıtan otomatik bir istemciye **her** istekte (konu listesi ve RSS dahil)
  `307` ile bir tarayıcı doğrulama sayfası döndürüyor ("Checking your browser"; geçmek çerez kabul edip doğrulama adımını
  tamamlamayı gerektiriyor). Gözlem: 2026-10-05, yerel ağ, birkaç salt-okuma isteği.
- Bu bir bot tespitidir. TSQ onu aşmaz, tamamlamaz ve tarayıcı gibi davranmaz. Forum bu yüzden **okunmuyor** (`BLOCKED`);
  yapısı, gönderi kimlikleri ve "Valve Developer" işareti doğrulanamadı.

Sonuç: Deadlock yalnızca Steam'den okunur. Steam'de duyurusu olmayan, foruma yalnızca yanıt olarak eklenen düzeltmeler
**görülmez** (bkz. Sınırlar). Forum bir gün doğrulamasız bir makine arayüzü sunarsa kaynak yeniden değerlendirilebilir.

Steam mağazasının olay listesi (`store.steampowered.com/events/ajaxgetpartnereventspageable`) Valve'ın kendi kategorisini
veriyor (14 = büyük güncelleme, 12 = yama notu, 13 = olağan güncelleme) ve `robots.txt` ile yasaklanmış değil; ancak
**belgelenmemiş** bir uç nokta ve kahraman tanıtımları da 13 olarak geliyor. Kullanılmıyor.

### Gerçek akışta gözlenenler (2026-10-05, 39 duyuru, 2024-10 → 2026-10)

| Tür | Örnek başlık | `patchnotes` etiketi | Metin |
|---|---|---|---|
| Küçük yama | "Minor Update - 09-16-2026" | var | "[ General ]" gibi bölümler + "- " ile başlayan satırlar |
| Oynanış güncellemesi | "Gameplay Update - 04-30-2026" | çoğunda **yok** | aynı düzen, yüzlerce satır |
| Başlıklı güncelleme | "Matchmaking Update" | **yok** | düz yazı ("This update includes …") |
| Adlandırılmış büyük güncelleme | "City Never Sleeps", "Old Gods, New Blood" | **yok** | düz yazı + oyunun kendi sitesindeki güncelleme sayfasına bağlantı |
| Kahraman tanıtımı | "Listen up, Crumbums! Your King is here." | yok | düz yazı; bazıları aynı güncelleme sayfasına bağlantı verir |

Yani ne etiket ne başlık tek başına yeterli: "City Never Sleeps" başlığında "update" yok, etiketi yok, değişiklik listesi
yok.

### Deadlock sınıflandırıcısı

Kapsamı sağlayıcı kurar (AppID 1422450'nin resmî duyurusu). TSQ'nun gönderdiği şey: oyunun oynanabilir hâlinde —
sistemlerinde, haritasında, modlarında, arayüzünde, eşleştirmesinde, kadrosunda, denge değerlerinde — gerçek değişiklik
yapan resmî duyuru. Tanıtım, kahraman hikâyesi, "yakında" duyurusu ve tek kahraman tanıtımı tek başına güncelleme değildir.
Sınıflandırıcı gönderinin kendisinden şu işaretleri okur (kahraman ya da güncelleme adı listesi yoktur):

| İşaret | Ne |
|---|---|
| etiket | Steam'in `patchnotes` etiketi |
| başlık | başlıkta kelime olarak "update", "patch", "hotfix" veya "changelog" |
| liste | Valve düzeninde en az 3 değişiklik satırı |
| beyan | metin sürümün kendisinden söz eder: "this update", "today's update", "today's … patch" (arada en çok 3 kelime) |
| sayfa | gönderi oyunun kendi sitesindeki (`https://www.playdeadlock.com/<sayfa>`) bir sayfaya bağlanır; başlık o sayfanın adıyla başlıyorsa gönderi **sayfasının adını taşır** ("Old Gods, New Blood" → `/oldgods`) |
| beyan edilen değişiklik | beyan, sürümün ne yaptığını söyleyerek devam eder: "today's update **adds** four new heroes to matchmaking", "this update **includes** …" |
| değişiklik cümleleri | oyundaki bir değişikliği yapılmış olarak bildiren cümleler: "has been updated", "has received a major overhaul", "replaces the existing …", "is now", "no longer" ("available to play now" bunlardan değildir) |
| başlıklı bölümler | gönderideki başlık satırları ("The Hideout", "Map Update", "[ General ]") |

| Karar | Koşul | Gerekçe kodu |
|---|---|---|
| `Update` | etiket + (başlık veya liste veya beyan) | `patch_notes` |
| `Update` | başlık + (liste veya beyan) | `titled_update` |
| `Update` | sayfasının adını taşıyor ve metinde "update" geçiyor — ya da beyan + sayfa bağlantısı | `named_update` |
| `Update` | beyan edilen değişiklik | `declared_update` |
| `Update` | en az 3 değişiklik cümlesi **ve** en az 2 başlıklı bölüm | `substantial_update` |
| `Ambiguous` | sayfasının adını taşıyor ama "coming soon" diyor | `named_but_upcoming` |
| `Ambiguous` | yalnızca bir işaret | `tagged_without_changes`, `update_title_only`, `declared_without_changes`, `list_only`, `change_statements_only` |
| `NotUpdate` | hiç işaret yok | `no_update_signal` |

**Gerçek akışta ölçüm (39 duyuru, 2024-10 → 2026-10; beklenen karar başlığa değil gönderi metnine bakılarak elle
belirlendi):**

| Beklenen | `Update` | `Ambiguous` | `NotUpdate` |
|---|---:|---:|---:|
| Güncelleme (27) | 27 | 0 | 0 |
| Güncelleme değil (12 kahraman tanıtımı) | 0 | 2 | 10 |

| İşaret (tek başına) | Güncellemede var | Güncelleme olmayanda var | Güncellemede yok |
|---|---:|---:|---:|
| etiket | 15 | 0 | 12 |
| başlık | 23 | 0 | 4 ("City Never Sleeps", "Old Gods, New Blood", "Six New Heroes", "Holliday, Vyper, Calico, and The Magnificent Sinclair") |
| liste | 20 | 2 (oy sayısı listeleri) | 7 |
| beyan | 6 | 0 | 21 |
| sayfa bağlantısı | 2 | 5 (kahraman tanıtımları aynı sayfaya bağlanıyor) | 25 |

Hiçbir işaret tek başına yetmiyor: başlık dört adlandırılmış güncellemeyi kaçırıyor, sayfa bağlantısı kahraman
tanıtımlarında da var, liste oy sayılarını da sayıyor. "Six New Heroes" başlıklı bölümler + değişiklik cümleleriyle,
"Holliday, Vyper, Calico, and The Magnificent Sinclair" beyan edilen değişiklikle yakalanır. İki `Ambiguous`: "Apollo - A
Cut Above" ve "You Can't Kill Victor" (oy sayısı listesi; gönderilmez). Yapay zekâ kullanılmaz.

Sınıflandırıcı gönderi metninin **sınırlı ilk bölümünü** (16.000 karakter) okur; adlandırılmış güncellemelerin işaretleri
metnin başındadır. Yalnızca bu sınırın ötesinde duran bir işaret görülmez (tahmin yürütülmez).

### Yama notu okuma ve kart alıntısı

Steam metni (BBCode) küçük, sınırlı bir ileri tarayıcıyla okunur: bilinen etiketler satır sonuna çevrilir ya da atılır,
görsel adresleri metin sayılmaz, bağlantı hedeflerine yalnızca **bakılır** (hiçbiri istenmez), `[ General ]` gibi satırlar
bölüm başlığı, "- " ile başlayan satırlar değişikliktir (hem yeni `[p]` düzeni hem eski düz satır düzeni). Girdi 400.000
karakter, bölüm 64, satır 2.000 karakterle sınırlıdır; bozuk işaretleme hata fırlatmaz.

Kart, mevcut `UpdateHighlights` modeliyle (yeni kolon/biçim yok) en çok 3 bölüm × 3 satır ve "… ve N değişiklik daha"
gösterir; değişiklik sayısı gönderinin **tamamından** sayılır. Değişiklik listesi olmayan bir güncellemede (adlandırılmış
büyük güncelleme, "Matchmaking Update") alıntı metnin ilk cümle benzeri satırıdır. Oyunun sitesindeki güncelleme sayfası
**istenmez** ve karta bağlantı olarak taşınmaz: karttaki tek bağlantı Steam duyurusudur.

Alıntı, gönderinin içerik özetine (`ContentHash`) dahildir: kartın gösterdiği bir şey değişirse — metnin sınırlı kopyasının
ötesindeki bir düzenleme değişiklik sayısını değiştirse bile — aynı kart düzenlenir. Alıntısı olmayan gönderilerin (CS2)
özeti eskisiyle birebir aynıdır.

### İstek maliyeti

Tur başına **1** istek (sakin, olağan, kesinti sonrası ve en kötü durum aynıdır; sayfalama yoktur). Steam cevabı ~60
dakika önbellekte tuttuğunu bildirdiği için fiilen saatte 1 istek; başlık yoksa modül aralığı (5 dk) geçerlidir. Kesinti
sonrası yetişme, cevabın her zaman en yeni 20 duyuruyu içermesiyle sağlanır (`CatchUpHours` içinde yayımlananlar gönderilir).

## Kaynak: Blizzard forumu (World of Warcraft: Forever)

Yalnızca **World of Warcraft: Forever** güncellemeleri: Blizzard'ın resmî World of Warcraft forumundaki (ABD, İngilizce)
kendi gönderileri. Retail, Classic ve diğer sürümler, genel haberler, oyuncu gönderileri, Known Issues, bakım ve tanıtım
içerikleri kart **üretmez**. Yapay zekâ kullanılmaz; her karar ve her özet deterministiktir.

| | |
|---|---|
| Taban adres | `https://us.forums.blizzard.com/en/wow` (sabit; Discourse JSON, anahtar ve giriş yok) |
| Yeni konular | `GET /latest.json?category=<kategori>&order=created[&page=N]` — kategorinin konuları, en yeni önce, sayfa başına 30 |
| Konu | `GET /t/<konu>.json` (ilk gönderiler) ve gerekirse `GET /t/<konu>/<gönderi no>.json` (o gönderinin çevresi) |
| Kart bağlantısı | `https://us.forums.blizzard.com/en/wow/t/<konu>/<gönderi no>` — forumun başlıksız biçimi; konuya yönlenir, konu yeniden adlandırılsa da değişmez |
| Kullanılmayan | `/groups/blizzard-tracker/posts.json` (aşağıya bakın), arama, RSS, HTML sayfaları, Battle.net sürüm uç noktası, Blizzard News, üçüncü taraf siteler |

**Tracker neden kullanılmıyor:** Blizzard'ın mavi gönderi akışı (`/groups/blizzard-tracker/posts.json`) çalışıyor ve aynı
gönderileri listeliyor; ancak forumun `robots.txt` dosyası `/en/wow/g` önekini yasaklıyor ve bu adres o önekin altında
(2026-10-05'te okundu). Aynı bilgi, yasaklı olmayan yollardaki resmî işaretlerle alınıyor: konu listesindeki
`first_tracked_post` ve konu JSON'undaki `tracked_posts` — ikisi de forumun kendi Blizzard-gönderisi işaretidir. Bir
gönderi yalnızca bu işaretlerle "Blizzard'ın" sayılır.

### Üç giriş yolu, tek liste

1. **İzlenen konular** (`Updates:WowForever:WatchedTopicIds`, varsayılan `2360696` — "WoW Forever Beta Development
   Notes"): Blizzard yeni build'leri yeni konu açmadan bu konuya **yanıt olarak** ekliyor (#1, #4, …) ve konu başlığını her
   seferinde "– Updated <tarih>" diye değiştiriyor. Konunun işaretli her Blizzard gönderisi her turda tam metniyle okunur.
   Gönderi başlığı olarak konunun "– Updated …" eki atılmış hâli kullanılır; böylece konu yeniden adlandırıldığında önceki
   build'lerin kartları değişmez (kartın tarihi gönderinin kendi zamanıdır).
2. **Yeni konular:** kategorinin en yeni konuları (`Updates:WowForever:ForumCategoryId`, varsayılan `349` — "WoW: Forever
   Beta Discussion"). **İlk gönderisi** Blizzard işaretli olan konu adaydır (ör. "Beta Client Update - September 22").
   Başlığı sınıflandırıcıya göre güncelleme olamayacak konular (Known Issues, bakım, …) **indirilmez**; yalnızca görüldü
   olarak kaydedilir. Oyuncu konuları ve oyuncu konusundaki Blizzard yanıtları aday bile olmaz.

3. **Takip edilen konular:** içinde bir gönderi `Update` olarak **doğrulanmış** konular (ör. ayrı açılmış bir Client
   Update veya Hotfix konusu). Böyle bir konu, son doğrulanmış güncellemesinden sonra `FollowThreadDays` (7 gün) boyunca
   izlenen konu gibi her turda okunur: Blizzard'ın konuya sonradan eklediği yeni yanıtlar ve ilk gönderideki düzeltmeler,
   konu "en yeni konular"dan düşmüş olsa da görülür. Liste ayrı bir tabloda tutulmaz; her turda kayıtlı gönderilerden
   türetilir (en yeni güncellemesi olan en çok `MaxFollowedThreads` = 5 konu), süre dolunca konu kendiliğinden listeden
   çıkar. Yalnızca Blizzard'ın açtığı konular takip edilir; yanıtlar yine `tracked_posts` işaretinden ve aynı
   sınıflandırıcıdan geçer (oyuncu yanıtı aday bile olmaz, Blizzard'ın tek satırlık notu `Ambiguous` kalır). Konunun ilk
   gönderisi yeni-konu yolundakiyle birebir aynı üretilir; takip, daha önce gönderilmiş kartı değiştirmez.

Üç yol aynı kimliği üretir (`<konu>:<gönderi no>`, outbox anahtarı `blizzard:<kategori>:<konu>:<gönderi no>`): aynı
gönderi birden fazla yoldan bulunsa da **tek** gönderidir.

### Hangi hata turu düşürür, hangisi düşürmez

| Ne okunamadı | Sonuç |
|---|---|
| Kategori listesi veya **izlenen** konu (oyunun ana kaynakları) | Tur kendi hata türüyle başarısız olur, hiçbir şey yazılmaz, geri çekilme başlar. (Artık var olmayan, başka kategoriye taşınmış veya Blizzard işareti taşımayan izlenen konu atlanır ve sayılır.) |
| **Takip edilen** bir konu (zaman aşımı, 5xx, JSON değil, konu şeklinde değil, çok büyük, yönlendirme) | Yalnızca o konu bu tur atlanır; izlenen konu, diğer takip edilen konular ve liste okunur. Tur **başarılıdır**, yetişme noktası ilerler. Sonraki turda yeniden denenir. |
| Listeden açılan **yeni** bir konu (aynı hata türleri) | Yalnızca o konu atlanır, diğer yeni konular okunur ve gönderilir. Tur başarılıdır ama **eksiktir**: yetişme noktası ilerlemez, böylece okunamayan konu sonraki turun baktığı pencereden düşemez. |
| Herhangi bir istekte **HTTP 429** | Forum ara istiyor: tur orada `RateLimited` olarak biter, o turda başka istek yapılmaz, `Retry-After` uygulanır. |

Atlanan her şey sayılır ve turun ayrıntısının **başına** yazılır (`partial: 1 followed thread unreadable; …`); `status`
böyle bir turu `SuccessPartial` olarak adlandırır, `doctor` ayrıntıyı gösterir. Başarılı turlar için ek log yoktur (konu
başına yalnızca Debug satırı).

### Kesintiden sonra yetişme

Liste olağan turda `DiscoveryLookbackHours` (6 saat) geriye okunur. Son **eksiksiz** turdan beri daha uzun süre geçmişse
(bot kapalıydı, forum cevap vermedi, geri çekilme uzadı) o ana kadar geriye okunur; modül bu noktayı
`Updates:CatchUpHours` (24 saat) ile sınırlar — daha eskisi zaten gönderilemez:

| Son eksiksiz tur | Geriye bakış |
|---|---|
| 15 dk – 6 saat önce | 6 saat (olağan; ek istek yok) |
| 10 saat önce | 10 saat |
| 30 saat önce | 24 saat |

`MaxListPages` (3 sayfa = 90 konu) her durumda kesin sınırdır. Kategori o kadar hareketliyse ki üç sayfa yetişme noktasına
ulaşmıyor, tur yine başarılıdır ve ayrıntısı `partial: list page bound reached before the catch-up point` der (daha fazla
sayfa hiçbir turda okunmayacağı için yetişme noktası bekletilmez). Başarısız bir tur (429 dahil) yetişme noktasını
ilerletmez; nokta veritabanındadır, restart'ta korunur.

### Uzun konularda Blizzard gönderileri

Bir konu ilk isteğe yalnızca ilk ~20 gönderisiyle cevap verir. Hangi Blizzard gönderilerinin eksik olduğunu forumun kendi
`tracked_posts` işaretleri söyler; tam olarak onlar istenir — en yenisi önce, bir cevap birden fazlasını getirebilecek
şekilde hedeflenerek, aynı yer iki kez sorulmadan — konu başına **en çok 3 istekle** (konu + 2). Tahminle sayfa
taranmaz. Bu sınırdan sonra hâlâ okunmamış işaretli gönderi kalırsa sayılır ve ayrıntıya yazılır
(`partial: 2 Blizzard posts not read`). Gözlem (2026-10-05): N numaralı gönderinin çevresi istenince forum N'den önceki 5
ve N'den itibaren 15 gönderiyi veriyor.

### Gerçek cevapta gözlenenler (2026-10-05, yerel ağ, salt-okuma)

- HTTP 200, `application/json`; `Cache-Control: no-cache, no-store`; hız sınırı başlığı yok. Var olmayan konu: 404 + JSON.
- Liste: `topic_list.topics[]` — `id`, `title`, `category_id`, `created_at`, `first_tracked_post{group, post_number}`,
  `excerpt`, `archetype`, `visible`; 30 konu yaklaşık 7 saati kapsıyordu. `/c/349/…` adresi kategori adına 301 ile
  yönleniyor; `latest.json?category=` addan bağımsız.
- Konu: `id`, `title`, `category_id`, `tracked_posts[]{post_number}`, `post_stream.posts[]` — `post_number`, `cooked`
  (işlenmiş HTML), `created_at`, `updated_at`, `post_type` (3 = moderatör işlem notu, metinsiz), `hidden`, `deleted_at`.
- Development Notes gönderileri: giriş paragrafı, "Change Log" başlığı, sonra ya kalın paragraf + liste ya da kalın
  başlıklı liste maddesi + iç liste; 5 düzeye kadar iç içe liste; build numarası **yok**. Client Update gönderisi:
  "WoW Forever 1.60.1 Build 69977" satırı + kısa liste.
- `updates check --game wow-forever` gerçek forumda: 30 konu listelendi, izlenen konudan 2 Blizzard gönderisi okundu, ikisi
  de `Update (development_notes)`.
- Railway ağından erişim ve Blizzard'ın forum koşullarının bu kullanım için açık izni: **NOT_VERIFIED**.

### Yoklama

Forum kendi önbellek ömrünü bildirmediği için sağlayıcının kendi alt sınırı geçerlidir:
`Updates:BlizzardForum:PollIntervalMinutes` (15 dk); modülün kendi aralığı daha uzunsa o geçerlidir. Aynı aralık bir
sonraki yoklama zamanını, geri çekilmeyi ve tur kaydedilemediğinde devreye giren bellek içi korumayı belirler (CS2: 5 dk,
WoW Forever: 15 dk). Liste sayfaları yalnızca geriye bakış penceresi içindeyken ve en çok `MaxListPages` (3) kadar
okunur; tur başına en çok `MaxThreadRequests` (6) aday konu açılır, kalanı sonraki tura kalır (özetinden hüküm verilmez).
Takip edilen konular (`MaxFollowedThreads` = 5) izlenen konulara **ek**tir; izlenen konu takip yeri harcamaz. Bunlar
proje tercihidir.

| Tur | İstek sayısı (varsayılan ayarlar, bir izlenen konu) |
|---|---|
| Sakin tur | 2 — izlenen konu + liste (izlenen konu 20 gönderiyi aşınca 3, yaklaşık 35'i aşınca 4) |
| Olağan tur | 2 + takip edilen konu sayısı (son 7 günde doğrulanmış güncelleme konuları; genelde 0–2) |
| Kesinti sonrası yetişme turu | olağan tur + 1–2 liste sayfası |
| En kötü durum (yetişme dahil) | **27** = izlenen konu 3 + takip edilen 5 × 3 + liste 3 sayfa + yeni konu 6 |
| Geçerli herhangi bir ayarla | **en çok 50** (doğrulama reddeder: `3 × (5 izlenen + MaxFollowedThreads) + MaxListPages + MaxThreadRequests ≤ 50`) |

15 dakikalık aralıkla en kötü durum saatte 108 istektir; olağan durumda saatte 8–16. İstekler arasında bekleme yoktur;
50'lik tavan, Discourse'un varsayılan IP sınırının (10 saniyede 50 istek) altında kalmak için konmuştur — Blizzard'ın kendi
sınırı belgelenmemiştir ve **NOT_VERIFIED**.

### İçerik okuma ve özet

`cooked` HTML'i küçük, sınırlı bir ileri tarayıcıyla okunur (genel bir HTML ayrıştırıcı değil, işaretleme üzerinde desen
eşleştirme de değil): başlıklar, paragraflar ve iç içe listeler düz metne çevrilir; alıntılar, gömüler, kod blokları,
tablolar ve betikler atlanır; varlıklar çözülür; hiçbir bağlantı veya görsel indirilmez. Bozuk işaretleme hata fırlatmaz.
Çıktı deterministiktir: aynı metnin farklı işaretlemeyle yeniden işlenmesi düzenleme sayılmaz.

Bölümler Blizzard'ın yazdığı gibi bulunur: kalın paragraf veya başlık + liste, ya da kendi metni ve iç listesi olan madde.
Sürüm ve build yalnızca "1.60.1 Build 69977" biçimindeki satırdan alınır; böyle bir satır yoksa gönderi yine geçerlidir.
Karttaki **özet** sınırlıdır ve gönderinin tamamı değildir (aşağıda "Kart").

### WoW Forever sınıflandırıcısı

Kapsamı sağlayıcı kurar (Blizzard işaretli gönderi + oyunun kategorisi); sınıflandırıcı gönderinin türünü birden fazla
sinyalle belirler:

1. **Başlık bir güncelleme türü adlandırmalı:** Development Notes → `development_notes`, Client Update →
   `client_update`, Patch/Update/Release Notes → `patch_notes`, Hotfix(es) → `hotfix`. Başka bir şeyi adlandıran başlık
   (known issues, maintenance, restart, downtime, service issue, feedback, bug report, podcast, deep dive, preview,
   interview, weekly, account actions, …) veya hiçbir tür adlandırmayan başlık `NotUpdate`'tir. Bu adım metne bakmaz.
2. Başlık başka bir WoW sürümünü de anıyorsa (Midnight, Cataclysm, Mists of Pandaria, Season of Discovery, Classic Era,
   Hardcore, Retail, …) `Ambiguous`.
3. Gönderi konusunu açan gönderi olmalı **ya da** gönderi gönderi okunan bir konuda (izlenen veya takip edilen) olmalı;
   başka yerdeki Blizzard yanıtı `Ambiguous`.
4. Metin başlığı desteklemeli: build numarası **veya** en az 2 maddelik değişiklik listesi. "Notlar yayımlandı" gibi bir
   işaret gönderisi `Ambiguous` kalır.

Karar yine `Update` / `NotUpdate` / `Ambiguous`'tur ve yalnızca `Update` gönderilir; güncellemenin gerekçe kodu türüdür.

### Ürün kodu ve CDN build izleyicisi

Battle.net sürüm uç noktası (`…/v2/products/wow_classic_beta/versions`) bugün Forever beta build'ini veriyor, ama **bu
sürümde okunmuyor**: build numarası neyin değiştiğini söylemez, tek başına kart üretemez ve yalnızca bir sonraki forum
yoklamasını öne çekmeye yarardı (en çok bir aralık kazanç). Ayrıca beta ürün kodunun canlıda aynı kalacağı garanti değil.
İleride eklenirse ürün kodu yapılandırma olmalı ve yalnızca forum yoklamasını tetiklemeli. Kodda ürün kodu yoktur.

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
  yok). Başarı: `SuccessItems`, `SuccessNoNewItems`, `SuccessEmpty`, `SuccessPartial` (geçerli tur, ama bir şey okunamadı —
  ayrıntı `partial: …` ile başlar). Ardışık hatalarda üstel geri çekilme (en çok 6 saat).
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

Tek kart. Ping yok (`MentionPolicy.None`, düzenlemeler de ping'siz); `@everyone`, rol pingi yok. Yapay zekâ, çeviri,
görsel, debug kimliği, ham kimlik veya sağlayıcı cevabı yok. Sağlayıcıdan gelen her metin (başlık, bölüm adı, madde, sürüm)
Discord sınırına göre kısaltılır ve mention, markdown veya bağlantı üretemez (`DiscordText.Untrusted`). Sabit metinler
Türkçe (İngilizce yedek). CS2 kartı yalnızca başlık + cümle + bağlantıdır (Steam özet vermez; değişmedi).

Sağlayıcı özet veriyorsa (Blizzard forumu) kart ayrıca sürüm/build satırını ve değişiklik listesinden **kısa bir alıntıyı**
gösterir:

```
🛠️ WoW: Forever Güncellemesi               (başlık → Blizzard forum bağlantısı)
**Beta Client Update - September 22**
1.60.1 · Build 69977                       (yalnızca gönderide varsa)

**Bug Fixes**                              (en çok 3 bölüm)
• …                                        (bölüm başına en çok 3 madde, madde en çok 160 karakter)
_… ve 150 değişiklik daha_                 (gönderide kalan değişiklik sayısı)

Yeni World of Warcraft: Forever güncellemesi yayınlandı.

Blizzard Forumunda Güncelleme Notlarını Gör
Kaynak: Blizzard · <Discord native yayın zamanı>
```

Alıntı deterministik kesilir (yaklaşık 1100 karakteri aşmadan durur), gönderinin tamamı hiçbir zaman karta konmaz ve resmî
bağlantı her zaman yer alır.

## Komutlar (Sunucuyu Yönet; tüm cevaplar yalnızca kullanana görünür)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:updates islem:configure kanal:` | Güncelleme kanalı (bu sunucuda, botun görebildiği metin/duyuru kanalı; View Channel + Send Messages + Embed Links denetlenir) |
| `/tsq-admin modul:updates islem:games` | Kayıtlı oyunlar ve bu sunucuda açık/kapalı durumu |
| `/tsq-admin modul:updates islem:game-enable` / `game-disable` | Bir oyunu açar / kapatır: yalnızca kayıtlı oyunları listeleyen özel bir seçim açılır (seçilene kadar hiçbir şey değişmez) |
| `/tsq-admin modul:updates islem:game-channel [kanal:]` | Bir oyuna **kendi kanalını** verir. `kanal:` verilirse sonra oyun seçilir; verilmezse önce kanal seçimi açılır (içinde "Ortak güncelleme kanalını kullan" düğmesiyle), sonra oyun. Oyun seçilene kadar hiçbir şey kaydedilmez |
| `/tsq-admin modul:updates islem:pause` / `resume` | Gönderimi duraklatır / sürdürür (duraklatma dönemi telafi edilmez) |
| `/tsq-admin modul:updates islem:preview` | Seçilen oyunun kayıtlı en son gerçek güncellemesinin veya açıkça **sentetik** (bağlantısız) bir örneğin kartını yalnızca yöneticiye gösterir |
| `/tsq-admin modul:updates islem:status` | Mod, modül, kanal, açık oyunlar; oyun başına son başarılı kontrol, son sonuç, sonraki kontrol, son bulunan güncelleme, Discord'a gerçekten ulaşan son kart |
| `/tsq-admin modul:updates islem:doctor` | Mod, modül, kanal ve izinler, oyunlar, kaynak sağlığı, baseline, son cevap (güncelleme/belirsiz/atlanan), kapsama boşluğu, kayıt durumu |

### Oyun başına kanal

Sunucunun **ortak güncelleme kanalı** (`islem:configure`) her oyunun varsayılanıdır. Bir oyuna `islem:game-channel` ile kendi
kanalı verilebilir; kendi kanalı olmayan oyun ortak kanala gönderir.

- Önce ortak kanal ayarlanmalıdır (oyunların geri düşeceği yer ve gönderim penceresinin sahibi odur).
- Kanal, oyun açılmadan önce de seçilebilir; oyunu açıp kapatmak kanalını değiştirmez.
- Bir oyunun kanalını değiştirmek (ya da ortak kanala geri almak) **hiçbir güncellemeyi yeniden göndermez**: gönderim kaydı
  sunucu + oyun + gönderi başınadır, kanaldan bağımsızdır. Sonraki güncelleme yeni kanala gider; önceki kartlar yerinde
  kalır ve artık düzenlenmez (düzeltmeler yalnızca oyunun **güncel** kanalındaki kartı düzenler).
- Kanal değiştiği anda kuyrukta bekleyen kart iptal edilir (ne eski ne yeni kanala gönderilir).
- Ortak kanalı değiştirmek, kendi kanalı olan oyunu taşımaz. Duraklatma, modül anahtarı ve mod bütün oyunlar için ortaktır.
- `doctor` her oyunun kendi kanalını ve izinlerini (View Channel + Send Messages + Embed Links) ayrıca denetler; o kanaldaki
  kalıcı gönderim sorunu o oyunun satırında görünür. `status` ve `games` oyunun kanalını gösterir.

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
| `updates_item` | Duyuru kimliği, bağlantı, başlık ve — sağlayıcı veriyorsa — sınırlı kart alıntısı (`Highlights`: sürüm, build, ilk bölümlerin ilk maddeleri; en çok 4000 karakter; ikisi de 90 gün), yayın zamanı, ilk/son görülme, içerik hash'i, karar + gerekçe |
| `updates_guild_config` | Sunucu, kanal, duraklatma, pencere zamanları, son planlanan mod — kullanıcı kimliği yok |
| `updates_subscription` | Sunucu + oyun, açık/kapalı, açılma zamanı |
| `updates_delivery` | Sunucu + duyuru + tür + kanal (365 gün) |

Duyurunun tam metni, yazar, etiketler ve ham API cevabı saklanmaz; saklanan tek türetilmiş metin yukarıdaki sınırlı kart
alıntısıdır (CS2 için boştur). Migration'lar: `UpdatesModule` (beş tablo) ve `UpdatesItemHighlights` (tek nullable kolon). Bot bir sunucudan ayrılıp saklama süresi dolduğunda o
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

**Sonradan bir oyun eklemek (ör. World of Warcraft: Forever):** yeni sürüm dağıtıldığında oyun kayıtlıdır ama hiçbir
sunucuda açık değildir; açılana kadar o oyunun kaynağına istek de yapılmaz ve mevcut oyunlar etkilenmez. Sıra: yedek →
merge/deploy (`UpdatesItemHighlights` migration'ı tek nullable kolon ekler; komut sync gerekmez) →
`/tsq-admin modul:updates islem:game-enable` → oyunu seç → `islem:doctor`. O oyunun ilk geçerli cevabı baseline'dır;
yalnızca açıldıktan sonra yayımlanan güncellemeler gönderilir. Mod zaten `Live` ise ayrıca DryRun gerekmez; temkinli yol
yine `pause` → aç → `doctor` ile baseline'ı gör → `resume`'dur (duraklatma tüm oyunları kapsar).

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
- CS2 kartında yama notu alıntısı yoktur (Steam cevabından özet çıkarılmaz); çeviri yoktur. Oyun başına kanal vardır
  (`islem:game-channel`); oyun başına ayrı duraklatma veya ayrı mod yoktur.
- **WoW Forever:** yalnızca ABD/İngilizce forum okunur. Ayrı açılmış bir güncelleme konusu, son doğrulanmış
  güncellemesinden sonra 7 gün takip edilir; daha sonra eklenen bir Blizzard yanıtı veya düzeltme görülmez (izlenen
  konuda süre sınırı yoktur). Aynı anda en çok 5 konu takip edilir. Güncelleme olarak doğrulanmamış bir konudaki
  (ör. `Ambiguous` kalan) Blizzard yanıtları takip edilmez. Alışılmadık başlıklı bir güncelleme (`untyped_title`)
  gönderilmez.
- **Deadlock — forumdaki düzeltmeler görülmez:** Valve'ın resmî Changelog forumuna yalnızca yanıt olarak eklediği küçük
  düzeltmeler (Steam duyurusu olmayanlar) bildirilmez; forum otomatik istemcilere tarayıcı doğrulaması döndürdüğü için
  okunmuyor. Valve Steam duyurusunu düzenleyip değişikliği oraya eklerse mevcut kart düzenlenir.
- **Deadlock — sınıflandırıcı sezgiseldir (kalan riskler):** gerçek akıştaki 39 duyurunun hepsi doğru sınıflanıyor, ama
  kurallar bu akıştan çıkarıldı. Gönderilmeyebilecek: başlığında "update" olmayan, sayfasının adını taşımayan, "this /
  today's update …" demeyen ve bölümlerinde değişiklik cümlesi kurmayan bir güncelleme (`Ambiguous` ya da `NotUpdate`).
  Yanlışlıkla gönderilebilecek: başlığı bir güncelleme sayfasının adıyla başlayıp o sayfaya bağlanan bir tanıtım, "coming
  soon" demeden yapılan adlandırılmış bir ön duyuru, ya da "today's update adds <kahraman>" diye yazılmış tek kahraman
  tanıtımı (gerçek akışta örnekleri yok). Tek kahraman tanıtımları Steam'de de "güncelleme" olarak dosyalanıyor; TSQ
  bunları bilerek göndermez.
- **Sınıflandırıcı bilerek temkinli (bilinen risk):** hariç tutulan bir kelime türden önce gelir — "Hotfixes and
  Maintenance", "Weekly Hotfixes", "Patch Notes Preview" gibi karışık başlıklar `NotUpdate` olur ve uyarı üretmez; tür
  adı geçmeyen bir Blizzard başlığı hiç indirilmez; build'siz tek maddelik bir hotfix `no_change_list` (Ambiguous) kalır.
  Bunlar kaçan gerçek güncelleme olabilir; politika değişikliği ayrı bir karardır.
- **Kategori geçişinden sonra eski konular (bilinen risk):** eski kategorinin son 7 günde doğrulanmış güncelleme konuları
  takip listesinde kalır; her tur en çok 5 boşa istek harcar ve "atlandı" olarak sayılır (başka kategoride oldukları için
  okunmazlar), 7 gün dolunca kendiliğinden çıkarlar. Outbox anahtarı kategoriyi içerdiğinden
  (`blizzard:<kategori>:<konu>:<gönderi>`) geçişten **önce** gönderilmiş kartlar sonraki düzeltmelerle artık
  düzenlenmez. Yinelenen kart oluşmaz (gönderim kaydı kategoriden bağımsızdır). Beta → canlı geçişinde ayrıca ele
  alınacak.
- İzlenen (ana) konunun kendisi veya kategori listesi okunamazsa tur bilerek başarısız olur: ana kaynağın kalıcı hatası
  "kısmi başarı" olarak gizlenmez. Bu sürede yeni konular da keşfedilmez; kaynak düzelince yetişme penceresi
  (`CatchUpHours`) içindekiler bulunur.
- Okunamayan **yeni** bir konu, yetişme noktasını en çok `CatchUpHours` kadar bekletir; bu sürede `status`'taki "son
  başarı" ilerlemez (tur sonucu `SuccessPartial`'dır). Konu `CatchUpHours`'tan eskiyince beklemeyi bırakır.
- **Kategori değişikliği (ör. beta → canlı):** `Updates:WowForever:ForumCategoryId` (ve izlenen konu) güncellenip bot
  yeniden başlatıldığında bu **açık bir kaynak geçişidir** ve başlangıçta, hiçbir istek yapılmadan önce kaydedilir. Yeni
  kategoride ikinci bir "ilk cevap baseline'ı" alınmaz; çizgi geçiş anıdır: o ana kadar yayımlanmış her şey geçmiştir ve
  gönderilmez, o andan sonra yayımlanan gerçek güncelleme — ilk başarılı cevaptan önce yayımlanmış olsa bile —
  gönderilir. Eski kategorinin kayıtları ve baseline'ı korunur; eski kaynağın geri çekilmesi sıfırlanır ve yeni kategori
  hemen sorulur. Bu yüzden ayarı, yeni kategoride ilk güncelleme yayımlanmadan **önce** değiştirin: geçişten önce
  yayımlanmış olan gönderilmez. (Henüz hiç cevap almamış bir oyun taşınırsa olağan ilk baseline alınır.)
- Ayar notu: `WatchedTopicIds` verilirse varsayılanın yerine geçer; verilmezse Development Notes konusu izlenir.

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
- **WoW Forever — TESTED_OFFLINE:** HTML okuyucu (başlık/paragraf/iç içe liste, varlıklar, atlananlar, bozuk işaretleme,
  sınırlar), bölüm/sürüm/build çıkarımı, sınırlı alıntı ve saklama biçimi; sınıflandırıcı (türler, hariç tutulanlar, başka
  sürümler, izlenmeyen konudaki yanıt, yalnızca-özet, değişiklik listesi yok); sağlayıcı (istekler, izlenen konu, yeni
  konular, indirilmeyenler, iki yoldan tek gönderi, sayfalama, sınırlar, tüm hata türleri, bağlantı politikası); uçtan uca
  (ilk çalıştırma, izlenen konuya yeni Blizzard yanıtı, ayrı Client Update konusu, kart üretmeyenler, düzenleme, uzun
  liste, zararlı metin, hata sonrası kayıpsız devam, CS2 ile yan yana, alıntının saklanıp silinmesi; doğrulanmış konunun
  takibi — konu en yeni konulardan düştükten sonra gelen Blizzard yanıtı tek kart, oyuncu yanıtı hiç, sayı sınırı ve
  süre dolumu, izlenen konu takip yeri harcamaz; kategori geçişi — olağan restart, eski içerik gönderilmez, geçişten
  sonraki ilk güncelleme ilk başarılı cevaptan önce yayımlansa da gönderilir; hata yalıtımı — takip edilen konu 500 /
  zaman aşımı / bozuk JSON / çok büyük cevap verse de yeni güncelleme bulunur, bir hafta boyunca kalıcı hata keşfi
  durdurmaz, 429 turu düşürür ve yetişme noktasını ilerletmez, okunamayan yeni konu diğerlerini durdurmaz ve sonra
  gönderilir; yetişme — 8, 10 (restart ile) ve 20 saatlik aradan sonra açılmış konu bulunur, 30 saatte 24 saat ve 3
  sayfa sınırı, başarısız turlar yetişme noktasını ilerletmez; uzun konuda birbirinden uzak iki Blizzard yanıtı tek turda;
  en kötü tur 27 istek, geçerli hiçbir ayar 50'yi aşamaz; kaydedilemeyen tur oyunun kendi aralığından önce yinelenmez)
  (`UpdatesForumContentTests`, `UpdatesWowForeverClassifierTests`, `UpdatesBlizzardForumProviderTests`,
  `UpdatesWowForeverTests`). Gerçek kaynak (yerel ağ, 2026-10-05): `updates check --game wow-forever` başarılı. Canlıda
  **doğrulanmadı**: oyun bir sunucuda açılana kadar (`islem:game-enable`) foruma istek yapılmaz; canlı kart yok.
- **Deadlock — TESTED_OFFLINE:** sınıflandırıcı (küçük yama, oynanış/başlıklı güncelleme, adlandırılmış büyük
  güncelleme, yeni sistemlerle gelen kahraman dağıtımı, kadroya toplu ekleme, tek kahraman tanıtımları, adlandırılmış ama
  değişiklik içermeyen tanıtım, tek işaretli belirsizler, sahte site bağlantıları, bozuk/kesik metin, sınırlı kopya), yama notu
  okuyucusu (iki düzen, kaçışlı köşeli parantez, liste, görsel, bağlantı, sınırlar), kart alıntısı (tam metinden sayım),
  uçtan uca (ilk çalıştırma, yeni yama tek kart, adlandırılmış güncelleme kartı, kahraman tanıtımı kart değil, düzenleme
  aynı kartı düzenler, kozmetik işaretleme değişikliği düzenleme üretmez, çok uzun yamanın sonundaki düzenleme karttaki
  sayıyı günceller, uzun liste, zararlı metin, kesinti ve 429,
  tur başına tek istek, üç oyun yan yana ve her biri kendi kanalında, CS2 kartı birebir aynı)
  (`UpdatesDeadlockClassifierTests`, `UpdatesDeadlockTests`). Gerçek kaynak (yerel ağ, 2026-10-05, salt-okuma):
  `updates check --game deadlock` — 20/20 duyuru kullanılabilir; 17 `Update`, 1 `Ambiguous` (Apollo - A Cut Above),
  2 `NotUpdate`; akışın tamamı (39 duyuru) sınıflandırıcıdan geçirildi: 27 güncellemenin 27'si `Update`, 12 tanıtımın
  hiçbiri `Update` değil. Resmî forum: **BLOCKED** (tarayıcı doğrulaması). Canlıda **doğrulanmadı**.
- **NOT_VERIFIED:** Railway ağından erişim; canlı Discord kartı (ancak canlıya alındıktan sonra yayımlanan gerçek bir
  güncellemeyle doğrulanabilir — eski bir güncellemeyi test için göndermek bu doğrulama sayılmaz).
