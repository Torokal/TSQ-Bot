# TSQ Letterboxd — aktivite bildirimleri

Takip edilen **tek** Letterboxd üyesinin (`Letterboxd:Username`, varsayılan `lordtoro`) yeni **incelemelerini**, **puanlı
günlük kayıtlarını** ve **yeniden izlemelerini** (puansız ve incelemesiz olanlar dahil) üyenin resmî, herkese açık RSS
akışından okuyup yapılandırılmış kanala **bir kez** gönderen ayrı modül (`letterboxd`). Aynı kayda sonradan eklenen
inceleme ya da değişen puan/rewatch işareti **aynı kartı düzenler**.

> **Kapsam KISMİ.** Günlük kaydı oluşturmadan verilen yıldızlar (film sayfasında "yalnız puan") ve bunların değişimi
> **desteklenmez**: bu hareketler akışta yoktur ve izinli başka bir kaynak bulunamadı (aşağıda). Bu eksik `status`,
> `doctor` ve `/bot status` içinde görünür; "tüm istenen özellikler tamamlandı" denemez.

Varsayılan: `Letterboxd:Mode=Off`, modül her sunucuda kapalı, kanal yok. Kanal kodda veya `appsettings.json`'da
**yoktur**; sunucu verisidir (`/tsq-admin modul:letterboxd islem:configure`). Hedef kanal (sahibinin isteği) ana sunucuda
`689861386108665941`; ayar sahibinin onayıyla yapılır.

## Kaynak doğrulaması (2026-10-29, yerel ağ, salt-okuma)

Railway ağından erişim sonradan canlıda doğrulandı (aşağıda "Canlı durum"). Yerelde birkaç salt-okuma isteği: RSS (curl + .NET istemcisi `letterboxd check`, 2 istek),
robots.txt, iki herkese açık kayıt sayfası (yalnız viewing id kanıtı için, bir kez), bir poster `HEAD`, resmî API erişim
sayfası ve Kullanım Şartları (2026-10-29, ikinci kontrol). Erişim engeli olan sayfalar (aşağıda 403) zorlanmadı.

| Yetenek | Kaynak | Giriş/anahtar | Sonuç | Durum |
|---|---|---|---|---|
| A. İnceleme / günlük kaydı | `https://letterboxd.com/lordtoro/rss/` | yok | 200 `application/rss+xml`, 50 öğe, ~43 KB, `Cache-Control: no-store`, ETag/Last-Modified **yok**, `<ttl>` yok | **Supported** |
| B. Puansız + incelemesiz rewatch | aynı akış | yok | World War Z (izleme 2026-10-21): `memberRating` öğesi yok, `rewatch=Yes`, açıklama yalnız "Watched on …" | **Supported** |
| C. Günlükten bağımsız puan ve değişimi | — | — | Akışta yalnız `letterboxd-watch-N` / `letterboxd-review-N` öğeleri var; resmî API erişim sayfası akışı "yeni günlük kayıtları, incelemeler ve listeler" olarak tanımlar (yalnız yıldız yok) ve API'ye kişisel projeler için erişim verilmediğini yazar; HTML sayfalarını otomatik okumak Kullanım Şartları 6.11'e aykırı | **Blocked** |

**Gerçek yanıt şekli (öğe başına):** `title` ("Film, Yıl - ★★½", spoiler'lı incelemede sonuna " (contains spoilers)"),
`link` (`https://letterboxd.com/lordtoro/film/<slug>/`, aynı filmin sonraki kayıtları `/<slug>/1/` gibi), `guid`
(`letterboxd-watch-<id>` veya `letterboxd-review-<id>`, `isPermaLink=false`), `pubDate` (RFC 822, `+1300` gibi ofsetle),
`letterboxd:watchedDate` (yyyy-MM-dd), `letterboxd:rewatch` (Yes/No), `letterboxd:filmTitle`, `letterboxd:filmYear`,
`letterboxd:memberRating` (0.5–5.0; puan yoksa **öğe hiç yok**: 50 öğenin 9'unda yok), `letterboxd:memberLike`,
`tmdb:movieId` veya `tmdb:tvId`, `description` (CDATA HTML: poster `<img>` + "Watched on …" ya da inceleme paragrafları;
spoiler'lı incelemede önce `<em>This review may contain spoilers.</em>`), `dc:creator` (görünen ad). Kanal `<link>` üyenin
profili. Namespace'ler: `https://letterboxd.com`, `https://themoviedb.org`.

**Kimlik:** guid'deki sayı **viewing id**'dir: inceleme sayfasında `viewing-1400163932`, rewatch sayfasında
`viewing-1548125398` olarak görülür. Bu yüzden `watch-N` → `review-N` dönüşümü aynı kayıttır (önek kırpma değil, aynı
sayının ikisinde de viewing id olduğu kanıtı). Dönüşüm 2026-10-29'da canlıda da **gözlendi**: üyenin mevcut bir izleme
kaydına eklediği inceleme aynı viewing id ile geldi, yeni kayıt açılmadı ve tek inceleme kartı gönderildi (aşağıda). Film kimliği (slug, TMDB id) kayıt kimliği değildir; başlık hiçbir zaman kimlik değildir.

**Pencere:** 2026-10-29 kontrolünde bu hesabın akışı en yeni **50** öğeyi verdi (en eskisi 2025-09-26). Bu, o kontrolde
**gözlenen** penceredir; Letterboxd'un tüm hesaplar ve gelecek için verdiği bir garanti değildir. Modül pencereyi her
okumada ölçer (`MaxItemCount`) ve sabit bir sayıya dayanmaz. Sayfalama yoktur.

**Erişilemeyenler:** `letterboxd.com/api-beta/`, `/faq/` ve `api-docs.letterboxd.com` bu ağdan JavaScript/çerez
challenge'ı (403 "Just a moment…") döndürdü; aşılmadı. `letterboxd.com/api-beta/access/` ise okunabildi (200).

## RSS kullanım dayanağı

- **Resmî API erişim sayfası** (`/api-beta/access/`, 2026-10-29 okundu): API yalnız başvuruyla; "private or personal
  projects" için şu an erişim verilmiyor; makinece okunur hesap verisi için içe/dışa aktarma ve "every member profile has
  an RSS feed of new diary entries, reviews and lists" diye profil RSS'ine yönlendiriyor.
- **Resmî yardım (FAQ)** — "Can I auto-publish my reviews to other sites?": sahibinin verdiği referansa göre profil
  RSS'indeki günlük, inceleme ve liste içeriğinin üçüncü taraf hizmetlere otomatik yayımlanmasını tarif ediyor. Bu sayfa
  bu ortamdan **okunamadı** (403 challenge, aşılmadı) ve arama dizininde de bulunamadı; içeriğini kendim doğrulamadım.
- Bu nedenle RSS ile otomatik paylaşım, Letterboxd'un **kendi belgelerinde tarif ettiği bir kullanımdır** — HLTV'deki
  "RSS'e dair hiçbir resmî ifade yok" durumuyla aynı değildir.
- Sınırlar: bu **HTML sayfalarını tarama izni değildir** (Kullanım Şartları 6.11 otomatik toplamayı, açık izin olmadıkça
  yasaklar); TSQ için **özel bir onay veya hukukî uygunluk garantisi değildir**; günlükten bağımsız puanlama hâlâ **BLOCKED**.
  Canlıya alma sahibinin kararıyla yapıldı (2026-10-29).
- Yapılmayanlar: HTML sayfası, `/by/` sıralama yolları, film/liste/puan sayfaları, API, görsel indirme, challenge/çerez,
  tarayıcı taklidi, proxy, üçüncü taraf ayna.

## Davranış

| Kayıt | Kart |
|---|---|
| İnceleme (puanlı veya puansız, rewatch olsun olmasın) | `🎬 LordToro değerlendirdi` |
| İncelemesiz rewatch (puan isteğe bağlı; WWZ: puansız) | `🔁 LordToro yeniden izledi` |
| İncelemesiz, rewatch olmayan puanlı kayıt | `⭐ LordToro puan verdi` |
| Puansız, incelemesiz ilk izleme | **kart yok** (rewatch ile karıştırılmaz) |
| Liste, watchlist, takip, beğeni, başka kullanıcıya yorum | kart yok (akışta kayıt değil; sayılıp atlanır) |

Puan + inceleme + rewatch tek kayıttaysa **tek kart**. Kart: film adı + yıl, **bu kaydın** puanı (yoksa alan hiç
gösterilmez, asla 0/5), rewatch "Evet" (yalnız akış Yes diyorsa), izleme tarihi, inceleme alıntısı (en çok 800 karakter,
kelime sınırında "…", devamı için kayıt bağlantısı), kayda bağlantı, "Kaynak: Letterboxd", yayın zamanı, poster
(yalnız `https://a.ltrbxd.com/resized/…`, `?v=` atılır; yoksa kart yine gider). İnceleme **her zaman** spoiler etiketi
içinde; akış spoiler bildiriyorsa başlık "⚠️ spoiler içerir" der, bildirmiyorsa durum "bilinmiyor" sayılır (spoiler yok
sayılmaz). Tüm metin `DiscordText.Untrusted` ile kaçırılır (mention, markdown, bağlantı, `||`); `MentionPolicy.None`.
Çeviri, özet veya AI yok.

## Tekilleştirme, düzenleme, silinme

- Kimlik `(hesap, viewing id)`. Aynı filmin yeni rewatch'ı yeni kayıttır → **ayrı kart**; aynı film aynı gün iki kayıt da
  iki karttır.
- Kart görünür alanlarının hash'i (`ContentHash`) değişirse gönderilmiş kartın **aynı mesajı** sessizce düzenlenir;
  gözlem zamanı hash'te yoktur (her turda edit yok).
- **Üyenin eylemi** (`MemberHash`: puan, rewatch, izleme tarihi, inceleme) değişirse ve kayıt artık kart almaya değerse,
  kartı olmayan bir kayıt da (baseline'daki eski bir kayıt dahil) kart alır — örn. eski kayda bugün inceleme eklenmesi.
  Yalnız **meta veri** (film adı, yıl, poster, bağlantı) değişimi asla yeni kart açmaz, yalnız mevcut kartı düzenler.
- Puan kaldırılırsa kart puansız hale düzenlenir ("🎞️ izledi"); 0 yazılmaz.
- Akıştan kaybolan kayıt **silinmiş sayılmaz** (pencereden çıkmış olabilir); hiçbir kart silinmez/düzenlenmez. Bozuk,
  boş veya hatalı yanıt hiçbir kaydı "puansız/silinmiş" yapmaz.
- Geçersiz değer (ör. `memberRating=3.25`) o öğeyi **atlar**; kayıt eski haliyle kalır (yanlış "puan kaldırıldı" yok).
- **Kimlik çakışması:** yalnız `letterboxd-watch-N` ve `letterboxd-review-N` kabul edilir (`letterboxd-list-N` gibi
  başka türler aynı sayı alanına hiç girmez). Aynı akışta aynı N farklı içerikle iki kez gelirse **ikisi de** atılır;
  saklanmış bir N başka bir filmi gösterirse (TMDB id, yoksa slug farklı) öğe yok sayılır, kayıt ve kartı değişmez,
  `doctor` uyarır. Aynı TMDB id ile değişen slug/başlık ise yeniden adlandırmadır: aynı kayıt, kart düzenlenir.

## Baseline, restart, mod

- **Mod etkinleşmesi:** etkin mod (Off/DryRun/Live; Live + `Delivery:Mode=DryRun` = DryRun) ve **etkinleştiği an**
  (`ModeSince`) host başlarken ve her turdan önce, **ilk HTTP isteğinden önce** veritabanına yazılır. Mod değişince her
  sunucu penceresi o ana çekilir. Aynı modla restart yeni geçiş değildir (`ModeSince` korunur). Off istek yapmadan kaydedilir.
- **Geçmiş:** bir modun ilk başarılı okumasında (ilk kurulum dahil) ilk kez görülen kayıt, akış onu (`pubDate`) mod
  etkinleşmesinden **önce** tarihliyorsa geçmiştir ve gönderilmez; **aynı anda veya sonra** tarihliyse yenidir — ilk
  başarılı okuma başarısız istekler veya restart yüzünden gecikse bile. Tarihsiz kayıt geçmiştir. Sınır eşitliği:
  `pubDate == ModeSince` yeni sayılır (akış saniye hassasiyetinde). İzleme tarihi bu kararda **kullanılmaz**.
- **Kaynak sınırı:** akışta düzenleme zamanı yoktur. Bir modun ilk başarılı okumasında, zaten bilinen bir kayıttaki
  değişikliğin Off/DryRun sırasında mı yoksa Live başladıktan sonra mı yapıldığı **ayırt edilemez**: böyle bir değişiklik
  mevcut kartı düzenler ama **yeni kart açmaz** (toplu taşıma olmasın diye). Live sırasında, ilk okumadan sonra görülen
  değişiklikler normal şekilde kart açar. `pubDate`'in Letterboxd'da kaydın oluşturulma anı olduğu gözleme dayanır
  (izleme tarihinden sonra, kayıt sırasıyla uyumlu); belgelenmiş bir garanti değildir ve saat farkı birkaç saniyelik
  sınır kaymasına yol açabilir.
- İlk kurulum: ilk başarılı, boş olmayan okuma **baseline**'dır; o andaki (etkinleşmeden önce tarihli) kayıtlar
  gönderilmez. Hata, boş akış veya baseline'sız 304 baseline kurmaz.
- Yeni kart: yeni (veya üyenin eylemiyle değişen) kayıt, sunucu penceresinden (configure, resume, modül açma, mod
  etkinleşmesi) sonra, `CatchUpHours` (24 sa) içinde yayımlanmış/değişmiş ve tur başına en çok `MaxCardsPerRound` (3).
  İzleme tarihi eski olabilir (bugün girilen 2019 izlemesi gönderilir).
- **Kartı olmayan kayıt:** Live sırasında görülen puansız, incelemesiz, rewatch olmayan bir kayıt kart almaz; sonraki bir
  okumada aynı viewing id'ye inceleme, günlük puanı veya rewatch eklenirse **ilk kartı** oluşturulur (olmayan mesaj
  düzenlenmez), eski `pubDate`/izleme tarihi buna engel olmaz. Yalnız film adı/yıl/poster değişimi kart açmaz. "Görülmüş
  id" tek başına kalıcı engel değildir. Bu, günlük kaydının puanıdır — **bağımsız yıldız verme değildir**.
- Restart baseline'ı sıfırlamaz, tekrar göndermez; bekleyen outbox satırı korunur; sıradaki kontrol, backoff, kimlikler ve
  teslim geçmişi veritabanındadır. DryRun teslimleri ayrı kayıttır, canlı sayılmaz ve geçişte silinmez.
- Pause/resume, kanal değişimi ve modülün kapatılması mevcut TSQ sözleşmesine uyar: kuyruktakiler gönderim anında iptal
  edilir, kanal değişimi eski kayıtları yeniden göndermez, aradaki dönem telafi edilmez.
- `status`/`doctor` üç ayrı durumu ayrı satırda söyler, biri diğerini ima etmez:
  - **Kaynak bayat:** son başarılı okuma `CatchUpHours`'tan eski (veya hiç yok). Kayıp **kesin değildir**; o dönemin
    kayıtları henüz görülmedi, kapsam belirsizdir.
  - **Olası kapsama boşluğu:** yeni akış (gözlenen pencere boyunda) daha önce bilinen hiçbir kayıtla örtüşmedi. Kayıp
    **ihtimalidir**; kaç kaydın kaçtığını tek başına kanıtlamaz.
  - **Bağımsız puanlama BLOCKED:** kaynak yokluğu; bayatlık veya boşlukla ilgisi yoktur.

## HTTP ve maliyet

Tek istek: `GET https://letterboxd.com/<username>/rss/` (adres yalnız doğrulanmış kullanıcı adından), tanımlayıcı
User-Agent, yönlendirme izlenmez, çerez tutulmaz (akışın CSRF çerezi atılır), 15 sn zaman aşımı, 2 MiB açılmış gövde
sınırı, DTD yasak, `XmlResolver=null`, derinlik/öğe/karakter sınırları, regex'siz sınırlı HTML okuyucu. 304 yalnız
baseline'dan sonra anlamlı; doğrulayıcılar yalnız içerik DB'ye yazıldıktan sonra (aynı transaction) ilerler.
403/404 → 6 sa bekleme (aşılmaz), 429 → Retry-After (yoksa 30 dk; 5 dk–6 sa arası), diğer hatalar üstel backoff (en çok 6 sa).
200 HTML/challenge sayfası `WrongContentType`, XML diye gelen HTML `Malformed`; DNS/bağlantı `TransportError`
(`HttpRequestError` ile ayrılır), zaman aşımı `Timeout` — hiçbiri "kayıt yok" sayılmaz.

| Durum | İstek |
|---|---|
| Normal tur | 1 (15 dk'da bir → en çok 96/gün) |
| Yetişme turu (kesinti sonrası) | 1 (akış sayfalanmaz; ek istek yok) |
| Hard maximum | tur başına 1, turlar üst üste binmez, aralık ≥ 10 dk (yapılandırma sınırı) → ≤ 144/gün |

Kayıt başına, film başına veya sayfalı istek **yoktur**. Kanalı olan, açık ve duraklatılmamış sunucu yoksa istek yoktur.

## Veri ve saklama

`letterboxd_guild_config` (sunucu, hesap, kanal, duraklatma, pencere zamanları — Discord kullanıcı ID'si yok),
`letterboxd_entry` (viewing id, film slug/TMDB id, film adı/yıl, bu kaydın puanı, rewatch, izleme/yayın zamanı, en çok 800
karakterlik alıntı — akışta son görülmesinden 30 gün sonra silinir —, spoiler işareti, bağlantı, poster adresi, hash'ler),
`letterboxd_delivery` (sunucu + kayıt + kanal + mod), `letterboxd_source_state` (akış durumu, doğrulayıcılar, backoff,
baseline, pencere, boşluk). Kimlikler ve teslim kayıtları 365 gün; silinen kimlikler bir filigranla (`PrunedBelowViewingId`)
kapsanır — geri gelirse baseline sayılır. Ham XML/HTML, tam inceleme veya inceleme geçmişi saklanmaz; inceleme metni
loglanmaz. Yalnız herkese açık akış: özel/taslak içerik okunmaz.

## Komutlar (Sunucuyu Yönet; tüm cevaplar yalnız kullanana görünür)

| İşlem | Ne yapar |
|---|---|
| `/tsq-admin modul:letterboxd islem:configure kanal:` | Kanal (View Channel + Send Messages + Embed Links; Read Message History isteğe bağlı: belirsiz gönderim uzlaştırması) |
| `islem:pause` / `resume` | Duraklatır / sürdürür (aradaki dönem atlanır) |
| `islem:preview` | Kart alacak son gerçek kaydın veya açıkça sentetik örneğin kartı (kanala gitmez) |
| `islem:status` | Hesap, mod, modül, kanal, RSS sağlığı, baseline ve mod etkinleşmesi, kapsam uyarıları (bayat / olası boşluk / kimlik çakışması, ayrı ayrı), bağımsız puan kaynağı (BLOCKED), yetenekler, kapsam |
| `islem:doctor` | Mod, modül, hesap, kanal/izinler, akış, sıradaki kontrol, baseline, mod etkinleşmesi, bayatlık, olası boşluk, çakışma, yetenekler, kapsam (KISMİ), kaynak dayanağı |

Ayrı `/letterboxd-admin` kökü yoktur. Komut manifesti değişmez (yalnız `modul` önerisine `letterboxd` eklenir) →
Discord senkronu gerekmez. CLI: `dotnet run --project src/ToroSquad.Bot -- letterboxd check` (salt-okuma; DB yok, gönderim yok,
inceleme metni yazdırılmaz).

## Canlı durum (2026-10-29, saatler İstanbul, UTC+3)

Sahibinin onayıyla, kısmi kapsam bilinerek doğrudan `Live`'a alındı (DryRun ara adımı atlandı).

| Saat | Olay | Kanıt | Durum |
|---|---|---|---|
| 14:35 | PR #77 merge (`6d6fce6`), Railway deploy `e0e833f2` | Başlangıç logu: migration uygulandı, manifest değişmedi (31 komut) | VERIFIED_LIVE |
| 14:40 | `TOROSQUAD_Letterboxd__Mode=Live` → deploy `f06003d0`; mod etkinleşmesi 14:40:36 | Temiz başlangıç, "Letterboxd is off" satırı yok | VERIFIED_LIVE |
| 14:52–14:53 | Sahibi `configure` (kanal `689861386108665941`) + `/modules enable letterboxd` | AdminRouter logu | VERIFIED_LIVE |
| 14:57:07 | Railway'den ilk RSS okuması: baseline, 50 kayıt, kart yok | "letterboxd baseline with 50 feed entries" | VERIFIED_LIVE |
| 15:12:07 | Sahibi mevcut Hope kaydına inceleme ekledi → aynı viewing id (`1559817559`), "0 new", `card=Review` | Planner logu | VERIFIED_LIVE |
| 15:12:12 | Kart kanala teslim edildi; sahibi kanalda doğru göründüğünü onayladı | Outbox logu + sahibinin gözlemi | VERIFIED_LIVE |

Henüz canlıda görülmeyenler: gönderilmiş bir kartın sonradan düzenlenmesi (aynı mesajın edit'i), puansız rewatch kartı,
yalnız puanlı kayıt kartı, 429/403 gibi kaynak hataları, olası kapsama boşluğu uyarısı. Geri dönüş: Railway'de
`TOROSQUAD_Letterboxd__Mode=Off` (kuyruktakiler iptal edilir) veya `/modules disable letterboxd`.

## Bilinen sınırlar

- **Günlükten bağımsız puan ve değişimi: BLOCKED.** Snapshot yöntemi (profil film listesi) erişim dayanağı olmadığı için
  uygulanmadı; uygulansa bile iki kontrol arasında eski değere dönen hareketleri ve gerçek işlem zamanını veremezdi.
- Akış penceresi (gözlenen: 50 öğe) aşılırsa aradakiler RSS ile geri getirilemez (olası boşluk raporlanır).
- `watch-N → review-N` dönüşümü bir kez canlıda gözlendi (yukarıda); bu tek bir gözlemdir, her durum için garanti
  değildir. Diğer dönüşümler (ör. inceleme silinmesi) yalnız sentetik testlerle denenmiştir.
- Bir modun ilk okumasında bilinen kayıttaki değişikliğin zamanı belirlenemez (yukarıda): yeni kart açmaz.
- Pencereden çıkmış bir kayda yapılan düzenleme görülmez.
- Gönderilmiş kartın canlıda düzenlenmesi henüz gözlenmedi (yalnız çevrimdışı test edildi).
