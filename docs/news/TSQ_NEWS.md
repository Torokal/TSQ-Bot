# TSQ Haber — Aurora · HLTV

Ayrı modül (`news`, `src/ToroSquad.Modules.News`). Aurora'nın **ana CS2 takımıyla** ilgili HLTV haberlerini resmî HLTV RSS
akışından okur ve yapılandırılmış **tek** haber kanalına, her haber için **bir kez**, başlık + bağlantı olarak iletir. Elle
haber veya bağlantı girişi yoktur; bir kez kanal seçmek ve canlıya almak yeterlidir.

Varsayılan **kapalıdır**: `News:Mode=Off` (akış isteği yok), her sunucuda modül kapısı kapalı (`/modules enable news`) ve
kanal seçilmeden hiçbir şey gönderilmez.

## Kaynak ve HLTV erişim istisnası

| | |
|---|---|
| Akış | `https://www.hltv.org/rss/news` (resmî RSS 2.0, sabit adres; kullanıcı URL'si yok) |
| İzin verilen | Yalnızca bu RSS akışının okunması |
| İzin verilmeyen | Haber/takım/oyuncu/maç HTML sayfaları, RSS'teki linki takip edip makaleyi almak, OpenGraph/görsel arama, Cloudflare veya erişim kısıtını aşmak, resmî olmayan HLTV kütüphaneleri, belgelenmemiş uç noktalar |

Maç bağlantısı özelliğinin HLTV'ye **hiç istek atmayan** davranışı değişmedi. Koddaki tek HLTV isteği `HltvRssClient`'tır
(mimari test `NewsArchitectureTests.The_only_hltv_request_is_the_official_rss_feed`).

İstek: `GET`, iletişim bilgili User-Agent (`TSQBot/… NewsModule (+https://github.com/Torokal/TSQ-Bot)`), gzip/br, çerez yok,
Authorization veya başka sağlayıcı başlığı yok, **yönlendirme takip edilmez** (3xx = hata), gövde sınırı (varsayılan 1 MiB),
15 sn zaman aşımı, satır içi yeniden deneme yok.

### Gerçek akışta gözlenenler (2026-09-30, yerel ağ, birkaç salt-okuma isteği)

- HTTP 200, son URL `https://www.hltv.org/rss/news` (yönlendirme yok), `Content-Type: application/rss+xml; charset=UTF-8`,
  Cloudflare arkasında.
- 10 öğe, yaklaşık 2 günlük aralık (28–30 Eylül); öğeler yayın zamanına göre tam sıralı değil.
- Öğe alanları: `title`, `description` (kısa, düz metin), `link` (`https://www.hltv.org/news/<id>/<slug>`),
  `guid` (`hltvnews<id>`, `isPermaLink="false"`), `pubDate` (RFC 1123, GMT), `media:content` (görsel; **kullanılmaz**).
- **Kategori, takım veya oyuncu etiketi yok.** Takım kimliğiyle filtreleme yapılamaz; eşleştirme yalnızca başlık ve kısa
  açıklamadaki adlarla yapılır.
- Kanal `<ttl>60</ttl>`; `ETag`, `Last-Modified`, `Cache-Control` başlığı **yok**.
- Railway ağından erişim: **NOT_VERIFIED** (Railway kabuğu için SSH anahtarı kaydı gerekir; yapılmadı).

## Kullanım koşulları — NOT_VERIFIED

HLTV Terms of Service (yürürlük 27 Ocak 2025, 2026-09-30'da okundu): kişisel, sınırlı, geri alınabilir lisans; içeriğin ticari
kullanımı, **veri madenciliği/web kazıma** ve lisanssız "business purposes" kullanımı yasak. RSS'e özel bir kullanım izni veya
açıklaması bulunamadı. RSS'in yayımlanması, içeriği yeniden yayımlama hakkına eşit sayılmaz.

TSQ yalnızca **orijinal başlığı, gerçek bağlantıyı ve yayın zamanını** "Kaynak: HLTV" ile paylaşır; tam makale, büyük alıntı
veya fotoğraf paylaşmaz ve ticari değildir. Bu değerlendirme bir hukuki uygunluk garantisi **değildir**: canlı gönderimi açmak
sahibinin kararıdır. HLTV'ye bu proje adına başvuru veya mesaj gönderilmedi.

## Kapsam

Hedef: **Aurora Gaming ana CS2 takımı** (XANTARES'in takımı). Referans kimlikler: PandaScore `131505` (kadroda XANTARES ile
doğrulandı), HLTV takım kimliği `11861` (önceki araştırmadan; HLTV'ye istek atılmadığı ve Liquipedia sayfasında HLTV kimliği
bulunmadığı için **NOT_VERIFIED** — RSS'te zaten kimlik olmadığı için eşleştirme buna dayanmaz).

**"RSS'in verdiği içerikle Aurora ile ilgili bulunan haberler"** kapsanır — "Aurora hakkındaki her HLTV haberi" değil:

- **Takım:** `Aurora`, `Aurora Gaming` başlıkta veya RSS açıklamasında, tam kelime olarak (büyük/küçük harf, Unicode NFKC,
  HTML entity ve kesme işareti normalize). Benzer adlı takımlar hedef sayılmaz: `CRUISER AURORA`, `Aurora Young Blood`/`YB`,
  `Aurora Academy`, `Aurora Female`/`fe`, `ex-Aurora`. Aynı metinde ayrıca açıkça Aurora geçiyorsa haber elenmez.
- **Oyuncu:** güncel aktif oyunculardan birinin adı **başlıkta** tam kelime olarak geçiyorsa (röportaj, oyuncu haberi).
  Kısa (<4 karakter) veya sıradan kelime olan adlar (ör. `ash`, `ace`) takım adı olmadan yeterli değildir. Yalnızca
  açıklamada geçen oyuncu adı yeterli değildir. Koç/analist gibi kadro dışı kişiler eşleşmez.
- Eski oyuncular (ör. yedeğe alınan MAJ3R) güncel kadroda olmadığı için yeni takımlarının haberleri Aurora haberi sayılmaz.

Kaçabilecek haberler (bilerek): Aurora'nın yalnızca **makale metninde** geçtiği haberler (ör. "Short news" derlemeleri, genel
turnuva haberleri); kadro bayatken yalnızca oyuncu adıyla gelen haberler; RSS'ten düşmüş eski haberler. RSS'te olmayan içerik
uydurulmaz, yapay zekâ ile tahmin edilmez. Gözlenmiş bir test kümesi olmadığı için başarı yüzdesi verilmez.

## Kadro güncelliği

Kaynak: takımın Liquipedia sayfası (`News:Roster:LiquipediaPage` = `Aurora_Gaming`), resmî MediaWiki API (`action=query`,
wikitext), yalnızca ilk sekmenin `===Active===` bölümü ve rolü olmayan kişiler (oyuncular). İletişim bilgili User-Agent.
İçerik CC BY-SA'dır; adlar yalnızca eşleştirme için kullanılır, gösterilmez.

**Otomatik istek sınırı:** iki otomatik kadro isteği arasında en az `News:Roster:RefreshHours` (varsayılan ve alt sınır
24 saat, en çok 168) — başarılı veya başarısız fark etmez. Daha uzun bir `Retry-After`'a uyulur; daha kısası 24 saati
kısaltmaz. Sonraki deneme zamanı istekten **önce** veritabanına yazılır: restart veya istek sırasında çökme sınırı sıfırlamaz.
Otomatik istek yalnızca en az bir sunucu haber alırken (modül açık, kanal ayarlı, duraklatılmamış) yapılır. Bu garanti
yalnızca botun otomatik akışı içindir: operatörün elle çalıştırdığı `news check --roster` her çalıştırmada ayrıca bir
istek yapar ve bu sınıra dahil değildir.

- Başarılı sonuç ve doğrulama zamanı `news_feed_state`'e yazılır; hata veya makul olmayan sonuç (3–10 oyuncu dışında) kadroyu
  **boşaltmaz**, önceki liste korunur.
- İlk senkron başarılı olana kadar tarihli başlangıç kadrosu kullanılır (`News:Roster:SeedPlayers`, Liquipedia'dan
  2026-09-30'da doğrulandı: XANTARES, woxic, Wicadia, Jimpphat, kyxsan).
- Her iki liste de `News:Roster:MaxAgeDays` (7 gün) sonra **bayat** sayılır: oyuncu adıyla eşleştirme kapanır, takım adıyla
  eşleştirme sürer (doctor/status bunu gösterir).
- PandaScore kadro verisi (takım 131505) 2026-09-30'da yedeğe alınmış MAJ3R'ı hâlâ gösterdiği için kadro kaynağı seçilmedi.

## Akış ve hata davranışı

- `News:Mode`: `Off` (varsayılan) / `DryRun` (kartlar outbox'ta simüle edilir, Discord'a gitmez, ayrı teslim kaydı) / `Live`.
  Mod değişikliği restart ile etkinleşir ve **kuyruktaki** kartlara da uygulanır (aşağıdaki tablo).

| Mod | Akış isteği | Yeni kart | Kuyruktaki canlı kart / canlı düzenleme | Kuyruktaki DryRun kartı |
|---|---|---|---|---|
| `Off` | yok | yok | gönderilmez: yeni gönderim **iptal** (`news_mode_off`), bekleyen düzenleme **atılır** | iptal |
| `DryRun` | var | DryRun kartı (simüle) | gönderilmez: iptal (`news_mode_dry_run`), bekleyen düzenleme atılır | simüle edilir |
| `Live` | var | canlı kart | gönderilir / düzenlenir | simüle edilir (Discord'a gitmez) |

  Kontrol `NewsDeliveryPolicy`'dedir: outbox her gönderim, düzenleme ve uzlaştırma sonrası yeniden gönderimden hemen önce
  (modül kapısından sonra, `IsDryRun` kontrolünden **önce**) çağırır; politika satırı değil türü gördüğü için DryRun kartları
  ayrı türle (`article-dry`) kuyruğa girer. İptal kalıcıdır: mod yeniden `Live` olduğunda iptal edilen kartlar gönderilmez
  (haberin teslim kaydı durur; aynı haber yeniden planlanmaz). Discord'a zaten ulaşmış bir mesaj `Off` ile geri alınmaz ve
  silinmez. Genel `Delivery:Mode` ayarı ve diğer modüller etkilenmez.
- Tek ortak istek; takım, oyuncu veya sunucu başına ayrı istek yok. Hiçbir sunucu haber almıyorsa (modül kapalı, kanal yok
  veya duraklatılmış) istek de yok.
- Aralık: proje varsayılanı `News:PollIntervalMinutes` (5 dk) **veya** akışın `<ttl>` değeri, hangisi uzunsa (bugün: 60 dk).
  Bu HLTV'nin belgelenmiş kotası değildir; `ttl` kaynağın önbellek isteğidir ve ona uyulur.
- `ETag`/`Last-Modified` gelirse sonraki istekte kullanılır; `304` "değişmedi" demektir (yalnızca baseline kurulduktan sonra).
- Hatalar ayrı tutulur ve **asla "yeni haber yok" sayılmaz**: `Blocked` (401/403, 6 saat bekler, aşılmaz), `RateLimited`
  (429, `Retry-After`'a uyulur), `ServerError`, `HttpError`, `Redirected`, `Timeout`, `TransportError`, `WrongContentType`
  (ör. HTML erişim sayfası), `TooLarge`, `Malformed`, `Empty`. Ardışık hatalarda üstel geri çekilme (en çok 6 saat).
- Sonraki kontrol zamanı ve hata sayacı veritabanındadır: restart geri çekilmeyi sıfırlamaz, ek istek üretmez; turlar üst üste
  binmez. Kaynak bozulduğunda yalnızca bu modül bekler; maç bildirimleri ve diğer modüller etkilenmez.
- Gecikme: haber **RSS'te göründükten sonraki kontrol turunda** (ttl 60 dk ile en çok ~1 saat) planlanır; yayın saniyesinde
  teslim garantisi yoktur. Yayın zamanı (`pubDate`), ilk görülme ve teslim zamanı ayrı tutulur.

## Güvenli RSS okuma

`DtdProcessing.Prohibit`, `XmlResolver = null` (dış varlık/dosya/bağlantı çözümü yok), karakter ve iç içe derinlik sınırı
(16), en fazla 200 öğe, gövde boyutu sınırı. CDATA ve entity'ler çözülür; açıklamadaki HTML düz metne çevrilir, içindeki
resim/script/iframe/bağlantılar **indirilmez**. Bozuk tek öğe (geçersiz bağlantı, boş başlık, başka haberi gösteren guid)
atlanır ve sayılır; hiç kullanılabilir öğe yoksa akış `Malformed`'dır. Gelecekteki (1 saatten fazla) veya geçersiz tarih
"yok" sayılır — ilk görülme zamanı yayın zamanı diye gösterilmez.

Bağlantılar yalnızca `https://www.hltv.org/news/<sayı>/<slug>` biçiminde kabul edilir (benzer hostlar, kullanıcı bilgisi,
port, başka yol reddedilir; sorgu ve parça atılır). Bağlantı varlığını doğrulamak için sayfa GET/HEAD ile **açılmaz**.

## Baseline, tekrar engelleme, düzeltme

- **Kimlik:** bağlantıdaki sayısal HLTV haber kimliği (başlık değil). Slug değişmesi yeni haber değildir.
- **Baseline:** ilk başarılı ve boş olmayan akıştaki tüm haberler baseline işaretlenir ve **hiç gönderilmez**. Başarısız veya
  boş ilk yanıt baseline kurmaz.
- **Teslim kaydı:** sunucu + kaynak + haber kimliği + mod (DryRun/Live). Kanal kimliği kimliğe **dahil değildir**: kanal
  değişince geçmiş haberler yeniden gönderilmez; yeni haberler yeni kanala gider, eski kanaldaki mesajlar silinmez/taşınmaz.
- **Yeni haber:** ilgiliyse sunucu başına bir kez planlanır; aynı haberde Aurora ve oyuncu birlikte geçse de **tek kart**.
  Tekrar yoklama veya restart ikinci kart üretmez.
- **Düzeltme:** aynı kimlikte başlık/bağlantı değişirse aynı outbox anahtarıyla yeniden planlanır → aynı mesaj **sessizce
  düzenlenir** (içerik aynıysa düzenleme yok). Moderatörün sildiği kart yeniden oluşturulmaz. Daha önce eşleşmemiş bir haberin
  akıştaki içeriği değişirse yeniden değerlendirilir (baseline'dakiler baseline kalır).
- **İlk aktivasyon / devam:** kanal ilk kez ayarlandığında veya `/tsq-admin modul:news islem:resume` sonrasında yalnızca o andan sonra
  **görülen ve yayımlanan** haberler gönderilir; duraklatma dönemi telafi edilmez.
- **Kesinti sonrası:** yalnızca son `News:CatchUpHours` (6 saat) içinde yayımlanmış haberler, tur başına en fazla
  `News:MaxCardsPerRound` (3) kart; kalanlar sonraki turlarda. Akıştan düşmüş haberler RSS ile geri getirilemez; son başarılı
  kontrol 6 saatten eskiyse doctor "kapsama boşluğu" uyarısı verir.
- **DryRun:** outbox'ta `dry|` anahtarlı ve ayrı teslim kaydıyla tutulur; canlı "gönderildi" durumunu etkilemez.
- **Tutma:** başlıklar `News:TextRetentionDays` (30 gün) sonra silinir; kimlikler ve teslim kayıtları
  `News:DedupRetentionDays` (365 gün) sonra silinir, silinen en büyük kimlik bir **filigran** olarak saklanır: bu kimlik veya
  altındaki bir haber akışta yeniden görünürse baseline sayılır ve tekrar gönderilmez. Tam makale, açıklama veya görsel saklanmaz.
- Tek transaction: haberler, akış doğrulayıcıları (ETag/Last-Modified), teslim kayıtları ve outbox satırları birlikte
  yazılır. Kayıt başarısızsa yeni doğrulayıcı da yazılmaz; haber bir sonraki turda yeniden işlenir.

## Belirsiz gönderim

Mevcut outbox uzlaştırması kullanılır: zaman aşımı gibi belirsiz gönderimde kör yeniden gönderim yoktur; son mesajlar içerik
parmak iziyle aranır. `MessageFingerprint` embed URL'sini içermez; bu yüzden haber bağlantısı açıklamada `[HLTV’de oku](…)`
satırı olarak da yer alır ve aynı başlıklı iki farklı haber farklı parmak izi taşır. Ortak parmak izi sözleşmesi değişmedi.
Sonuç belirlenemiyorsa satır "bilinmiyor" kalır; kesin "asla kopya olmaz" garantisi verilmez.

## Kart

```
📰 Aurora — HLTV            (başlık → haberin HLTV bağlantısı)
**<orijinal haber başlığı>**

HLTV’de oku                 (aynı bağlantı)
Kaynak: HLTV · <Discord native yayın zamanı>
```

Orijinal başlık korunur (çeviri, özet, yapay zekâ yok); sabit metinler Türkçe (İngilizce yedek). Başlık Discord sınırına göre
güvenli kısaltılır; RSS metni mention, markdown veya bağlantı üretemez (`DiscordText.Untrusted`). Ping yok
(`MentionPolicy.None`, düzenlemeler de ping'siz). Görsel, haber fotoğrafı, kadro, debug kimliği, skor alanı yok. PandaScore veya
Liquipedia bu haberin kaynağı gibi gösterilmez. Yayın zamanı yoksa zaman damgası da yoktur.

Not: haber başlıkları maç sonuçlarını açık edebilir; esports spoiler ayarı bu modüle devralınmaz (kanal ayarlanırken
hatırlatılır).

## Komutlar (Sunucuyu Yönet; tüm cevaplar yalnızca kullanana görünür)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:news islem:configure kanal:` | Haber kanalı (bu sunucuda, botun görebildiği metin/duyuru kanalı; View + Send + Embed Links denetlenir) |
| `/tsq-admin modul:news islem:pause` / `resume` | Gönderimi duraklatır / sürdürür (duraklatma dönemi telafi edilmez) |
| `/tsq-admin modul:news islem:preview` | En son eşleşen gerçek haberin veya açıkça **sentetik** bir örneğin kartını yalnızca yöneticiye gösterir; kanala gönderilmez |
| `/tsq-admin modul:news islem:status` | Mod, modül, kanal, son akış sonucu, son kart, kadro kaynağı/güncelliği, kapsam |
| `/tsq-admin modul:news islem:doctor` | Mod, modül, kanal ve izinler, akış (son başarı, HTTP, ardışık hata, sonraki kontrol), baseline, kapsama boşluğu, kadro, kapsam, koşullar |

Elle haber/bağlantı ekleme, silme veya kanal dışı hedef komutu **yoktur**. Komutlar modül kapalıyken de çalışır (önce kanal ve
doctor, sonra etkinleştirme). Salt-okuma CLI: `dotnet run --project src/ToroSquad.Bot -- news check [--roster]` — gerçek akışı
bir kez okur ve her öğe için gönderilir/gönderilmez kararını ve gerekçesini yazar; veritabanı açılmaz, hiçbir şey gönderilmez.

## Canlıya alma (sahibinin onayıyla)

DryRun'dan doğrudan Live'a geçmek, DryRun sırasında görülmüş ve son 6 saatte yayımlanmış bir haberi canlıya **bir kez**
taşır (kanal ilk ayarlandığında canlı ve DryRun başlangıcı birlikte kurulur; teslim kayıtları moda göre ayrıdır). Bu mükerrer
gönderim değildir, ama ilk canlı açılışta istenmez. Bu yüzden ilk canlı açılış şu sırayla yapılır (yeni tablo gerekmez;
`resume` başlangıç zamanlarını yeniler, `NewsModeTransitionTests` bunu doğrular):

1. PR'ı merge et (Railway deploy eder; `News:Mode=Off` olduğu için hiçbir şey değişmez).
2. `scripts/Sync-Commands.ps1` ile `/tsq-admin modul:news`'i ana sunucuya senkronla (önce dry-run).
3. `/tsq-admin modul:news islem:configure kanal:#haber` → `/modules enable news` → `/tsq-admin modul:news islem:doctor`.
4. İsteğe bağlı simülasyon: `TOROSQUAD_News__Mode=DryRun` (restart) → loglarda `[DRY-RUN]` kartlarını ve `/tsq-admin modul:news islem:doctor`'ı izle.
5. `/tsq-admin modul:news islem:pause`.
6. `TOROSQUAD_News__Mode=Live` (restart). Duraklatılmış tek sunucu varken akış isteği de yapılmaz.
7. `/tsq-admin modul:news islem:resume` → yalnızca bu andan sonra yayımlanan haberler canlıya gider; DryRun dönemindeki haberler atlanır.

Geri alma: `/tsq-admin modul:news islem:pause`, `/modules disable news` veya `TOROSQUAD_News__Mode=Off`.

## Doğrulama durumu

- **TESTED_OFFLINE:** ayrıştırıcı (CDATA, entity, HTML, DTD/XXE, derinlik, boyut, bozuk öğe), bağlantı politikası, HTTP
  sınıflandırması (304/403/429/5xx/zaman aşımı/yönlendirme/HTML), eşleştirici (takım, oyuncu, benzer takımlar, belirsiz adlar,
  bayat kadro), kadro ayrıştırıcı, baseline, tekrar engelleme, düzeltme düzenlemesi, DryRun ayrımı, duraklatma, kanal
  değişikliği, catch-up, tutma + filigran, modül kapısı, geri çekilmenin restart'ta korunması, atomik tur, belirsiz gönderim
  uzlaştırması, yetki (`NewsFeedTests`, `NewsMatchingTests`, `NewsTests`, `NewsArchitectureTests`).
- **Gerçek kaynak (yerel ağ, 2026-09-30):** RSS okuma ve ayrıştırma 10/10 öğe; Liquipedia kadro senkronu 5 oyuncu. O anda
  akışta Aurora haberi yoktu: eşleştirici gerçek bir Aurora haberiyle **doğrulanmadı** (sentetik testlerle TESTED_OFFLINE).
- **NOT_VERIFIED:** Railway ağından erişim, HLTV koşullarının RSS kullanımı için açık izni, HLTV takım kimliği 11861, canlı
  Discord kartı.
