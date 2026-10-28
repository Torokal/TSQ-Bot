# TSQ Profil Görseli

Bir üyenin profil fotoğrafını (`/pfp`) veya profil banner'ını (`/banner`) sohbette **büyük** gösterir. Sonuç kartı
**herkese açıktır** (komutun kullanıldığı kanalda) ve kimseyi etiketlemez. Her sunucuda varsayılan kapalıdır:
`/modules enable pfp` (listede **TSQ Profil Görseli**). Yetki gerekmez; modül açıkken herkes kullanabilir. Komutlar
yalnızca sunucuda çalışır (guild-only kayıt + çalışma anında `[ToroModule]` guild denetimi); DM'de kayıtlı değildir.

Durumsuzdur: tablo, migration, arka plan işi, yapılandırma, yeni paket, yeni intent, görsel işleme, AI veya önbellek
yoktur. Tek G/Ç, botun mevcut Discord.Net REST istemcisiyle yapılan Discord API okumalarıdır.

## Komutlar

```text
/pfp [uye] [tur]
/banner [uye] [tur]
```

| Seçenek | Tip | Zorunlu | Davranış |
|---|---|---|---|
| `uye` | Discord'un yerleşik **USER** seçeneği (tip 6, kişi seçici) | Hayır | Boşsa komutu kullanan kişi. Seçilen kişinin **ID'si** kullanılır; ad yazma/ayrıştırma/autocomplete yok. Sunucudaki bot hesapları da seçilebilir |
| `tur` | Sabit seçenekli metin: `Otomatik` (`otomatik`) · `Genel` (`genel`) · `Sunucu` (`sunucu`) | Hayır | Boşsa `otomatik` |

Örnekler:

```text
/pfp
/pfp uye:@Yusuf
/pfp uye:@Yusuf tur:genel
/pfp uye:@Yusuf tur:sunucu

/banner uye:@Yusuf
/banner uye:@Yusuf tur:genel
/banner uye:@Yusuf tur:sunucu
```

## Görsel seçimi

"Sunucu", seçilen kişinin **komutun kullanıldığı sunucudaki** kendi üye profilidir; başka bir sunucunun profiline
bakılmaz.

| Komut / tür | Gösterilen görsel |
|---|---|
| `/pfp otomatik` | Bu sunucuya özel avatar; yoksa genel avatar; o da yoksa Discord'un varsayılan avatarı |
| `/pfp genel` | Genel avatar; yoksa Discord'un varsayılan avatarı (sunucu avatarı **asla**) |
| `/pfp sunucu` | Yalnız bu sunucuya özel avatar |
| `/banner otomatik` | Bu sunucuya özel profil banner'ı; yoksa genel banner |
| `/banner genel` | Yalnız genel kullanıcı banner'ı |
| `/banner sunucu` | Yalnız bu sunucuya özel profil banner'ı |

### Geçiş sözleşmesi (V1 kararı, 2026-10-28)

İlk sözleşmedeki "bir sonraki kaynağa yalnız doğrulanmış yoklukta geçilir" koşulu V1 için şu sınırlarla güncellendi:

- Sunucu görseli **doğrulanmış yoksa** (None) Otomatik genel görsele sessizce geçer.
- Sunucu görseli Discord tarafından **bildirilmediyse** (NotReported), yalnız `otomatik` türde genel görsele geçilir. Kart
  başlığı fiilen kullanılan kaynağı yazar (ör. "Genel profil fotoğrafı") ve açıklama satırı bunu görünür biçimde söyler:
  "Discord bu sunucuya özel bir profil fotoğrafı bildirmedi; genel profil fotoğrafı gösteriliyor." (banner için "… profil
  banner’ı bildirmedi; genel banner gösteriliyor.").
- Açıkça `sunucu` seçildiğinde genel görsele **hiç geçilmez**; `genel` sunucu görseline hiç bakmaz.
- HTTP hataları (403, 429, 5xx, zaman aşımı, ağ), sunucu veya kullanıcı 404'ü, bozuk JSON ve Discord biçimine uymayan hash
  NotReported'a çevrilmez: hepsi "Profil bilgisi şu anda alınamadı" olur. Üye 404'ü "üye değil" olarak kalır.
- Genel görsel de doğrulanamıyorsa sahte başarı veya kesin yokluk iddiası üretilmez. Genel avatar alanı gönderilmediyse
  "alınamadı" denir. Genel banner bildirilmediyse not "Discord … bildirmedi" der, "banner yok" demez.
- Varsayılan avatar yalnız genel avatarın doğrulanmış yokluğunda (her zaman gönderilen `user.avatar` alanı `null`) kullanılır.
- İsteğe bağlı bir alanın `null` gönderilmesiyle hiç gönderilmemesi **hâlâ ayırt edilmiyor** (Discord.Net 3.20.1; ayrıntı:
  [Eksik alan, null ve "görsel yok"](#eksik-alan-null-ve-görsel-yok)). V1'de ham JSON okuyan ayrı istemci,
  tüm REST trafiğini saran `IRestClient` veya SDK fork'u yoktur.

Sunucunun kendi kapak görseli (guild banner) ve profil
vurgu rengi (accent color) hiçbir zaman kullanılmaz; yeni görsel üretilmez. Banner için varsayılan avatar gösterilmez.

### Görsel bulunamadığında

Yanıt, aynı herkese açık cevabın kısa bir notla tamamlanmasıdır:

| Durum | Mesaj |
|---|---|
| `/pfp sunucu`, Discord özel avatar bildirmedi | Discord bu üye için bu sunucuya özel bir profil fotoğrafı bildirmedi. Genel fotoğrafı için türü ‘Genel’ seçebilirsin. |
| `/banner sunucu`, Discord özel banner bildirmedi | Discord bu üye için bu sunucuya özel bir profil banner’ı bildirmedi. Genel banner’ı için türü ‘Genel’ seçebilirsin. |
| `/banner genel`, Discord genel banner bildirmedi | Discord bu kullanıcının genel profili için bir banner görseli bildirmedi. |
| `/banner otomatik`, Discord iki banner'ı da bildirmedi | Discord bu kullanıcı için ne genel profilde ne de bu sunucuya özel profilde bir banner görseli bildirmedi. |
| Get Guild Member 404 döndü | Bu kişi bu sunucunun üyesi değil (sunucudan ayrılmış olabilir), bu yüzden profil görseli gösterilemiyor. |
| Okuma başarısız (zaman aşımı, 403, 429, 5xx, ağ, Get Guild veya Get User 404, bozuk/beklenmeyen veri, gönderilmemiş genel avatar alanı) | Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene. |

Notlar Discord'un **ne bildirdiğini** söyler, görselin kesin olarak olmadığını iddia etmez (aşağıya bakın). Bir okuma hatası
hiçbir zaman "görsel yok" diye gösterilmez.

## Kart

Tek embed: başlıkta kişinin bu sunucudaki görünen adı ve **fiilen kullanılan** kaynak (ör. `Yusuf — Genel profil
fotoğrafı`), görsel embed'in **image** alanında (büyük; yalnız thumbnail değil), TSQ turuncusu, altında tek
**Tam boy aç** bağlantı düğmesi — görselle **aynı URL**. Kaynak etiketleri: Genel profil fotoğrafı · Sunucu profil
fotoğrafı · Varsayılan profil fotoğrafı (kısa açıklama satırıyla) · Genel banner · Sunucu profili banner’ı. Ad düz metindir
(`DiscordText.UntrustedPlain`: mention/`@everyone`/bağlantı oluşturmaz) ve tüm cevaplar `allowed_mentions` boş gönderilir.

## Görsel URL'leri ve animasyon

Görsel Discord CDN URL'siyle gösterilir; bot görseli indirmez, yeniden yüklemez veya işlemez. URL'ler yalnız API'den gelen
kimlik ve hash'lerden, Discord "Image Formatting" belgesindeki resmî yollarla kurulur (`PfpCdn`); kullanıcıdan URL alınmaz,
Discord hash biçimine (`(a_)?` + 32 hex) uymayan değer URL'ye girmez:

| Görsel | Yol |
|---|---|
| Genel avatar | `avatars/{user_id}/{hash}` |
| Sunucu avatarı | `guilds/{guild_id}/users/{user_id}/avatars/{hash}` |
| Genel banner | `banners/{user_id}/{hash}` |
| Sunucu profili banner'ı | `guilds/{guild_id}/users/{user_id}/banners/{hash}` |

- Statik: `.png?size=2048`. Hareketli (`a_` önekli hash): `.webp?size=2048&animated=true` — Discord belgesi, WebP/AVIF
  olarak yüklenmiş hareketli görsellerin GIF'e düzgün dönüşmediğini ve hareketli görsellerin WebP istenmesini önerir.
- Neden SDK yardımcısı değil: Discord.Net 3.20.1'de `ImageFormat.Auto` hareketli hash için `.gif` üretir (`animated=true`
  eklemez) ve üye avatarı/banner'ı için CDN yardımcıları public değildir. `GetDisplayAvatarUrl` sunucu avatarından genel
  avatara düştüğü için hiç kullanılmaz.
- 2048 istenen boyuttur; Discord görseli en fazla kendi saklı çözünürlüğünde verir, istemcinin ekranda göstereceği boyut
  garanti edilmez. Varsayılan avatarın URL'si SDK'nın `GetDefaultAvatarUrl()` yardımcısından gelir (yeni ve eski kullanıcı
  adı sistemi) ve büyütülmez (Discord'un varsayılan avatar boyutu sabittir).

## Profil verisi nasıl okunur

- **Üye**: her komutta `DiscordRestClient.GetGuildAsync(guildId, withCounts: false)` (Get Guild) ve ardından
  `RestGuild.GetUserAsync(userId)` (Get Guild Member). Bu, Discord.Net'in `GetGuildUserAsync` yönteminin gönderdiği iki
  isteğin aynısıdır; ayrı adımlar olarak yapılır, çünkü Discord.Net bu rotalardaki **her** 404'ü `null`'a çevirir ve sunucu
  okunamadığında da "üye değil" denmemelidir. Buradan: üyelik, görünen ad, bu sunucunun avatar ve banner hash'leri, iç içe
  kullanıcı nesnesindeki genel avatar hash'i.
- **Genel banner**: yalnız gerektiğinde `DiscordRestClient.GetUserAsync(userId)` → `RestUser.BannerId` (Get User). Üye
  nesnesindeki kullanıcının banner'ı veya gateway önbelleği kullanılmaz.
- Üye listesi indirilmez, tarama yapılmaz, yeni intent açılmaz.

| Komut / tür | Get Guild | Get Guild Member | Get User |
|---|---|---|---|
| `/pfp` otomatik · genel · sunucu | 1 | 1 | — |
| `/banner otomatik`, sunucu banner'ı var | 1 | 1 | — |
| `/banner otomatik`, sunucu banner'ı yok/bildirilmedi | 1 | 1 | 1 |
| `/banner genel` | 1 | 1 | 1 |
| `/banner sunucu` | 1 | 1 | — |

Get Guild sunucuya erişemediğinde (`null`) üye okunmaz; üye 404 ise Get User'a geçilmez. Tablo, gerçek Discord.Net 3.20.1
ile yalnız HTTP katmanı taklit edilerek sayılır (`DiscordPfpProfileContractTests`); Discord.Net'in 429 sonrası kendi
yeniden denemeleri bu sayılara dahil değildir.

- Hatalar: Get Guild Member 404 (Discord.Net'te `null`) → "üye değil"; Get Guild veya Get User 404 (`null`),
  `HttpException` (403, 5xx…), `RateLimitedException`, zaman aşımı/iptal, ağ hataları → "alınamadı"; bozuk JSON gibi
  beklenmeyen istisnalar da akışta "alınamadı" olur. Discord.Net'in mevcut rate-limit davranışı (`RetryMode.RetryRatelimit`)
  dışında retry yoktur. Komutun tüm okumaları 10 saniyelik bir bütçeyle sınırlıdır. Loglarda yalnız kimlikler, tür, sonuç ve
  Discord hata kodları bulunur (istisna metni, URL, interaction veya bot token'ı yok).

## Eksik alan, null ve "görsel yok"

Discord belgelerinde bir alanın tipindeki `?` "null olabilir", adındaki `?` "gönderilmeyebilir" demektir. İlgili alanlar:

| Alan | Uç nokta | Belgedeki tip | Hash | `null` | Hiç yok |
|---|---|---|---|---|---|
| `user.avatar` (genel avatar) | Get Guild Member → `user` | `avatar: ?string` (her zaman gönderilir) | Gösterilir | Avatar yok → varsayılan avatar | Belgeye aykırı; Discord.Net bunu `null`'dan ayıramaz → varsayılan avatar sayılır |
| `avatar` (sunucu avatarı) | Get Guild Member | `avatar?: ?string` | Gösterilir | **Bildirilmedi** | **Bildirilmedi** |
| `banner` (sunucu banner'ı) | Get Guild Member | `banner?: ?string` | Gösterilir | **Bildirilmedi** | **Bildirilmedi** |
| `banner` (genel banner) | Get User | `banner?: ?string` | Gösterilir | **Bildirilmedi** | **Bildirilmedi** |

- Get User ve Get Guild Member belgeleri yalnız "kullanıcı/üye nesnesi döndürür" der; isteğe bağlı alanların bu uç
  noktalarda **her zaman** gönderildiğini söylemez. Bu yüzden isteğe bağlı bir alanda hash olmaması doğrulanmış yokluk sayılmaz.
- Bilginin kaybolduğu yer: Discord.Net 3.20.1'in internal API modelleri bu alanları `Optional<string>` olarak tutar, ama
  `RestUser.Update` / `RestGuildUser.Update` yalnız gönderilmiş alanları kopyalar; gönderilmemiş alan da `null` kalır. Modeller
  ve ham API istemcisi internal olduğundan bu ayrım SDK'nın public API'siyle okunamaz.
- Adaptör (`DiscordPfpProfileSource.ToProfile`) bunu üç durumla taşır (`PfpHashState`): **Present** (hash),
  **None** (her zaman gönderilen alan `null`), **NotReported** (isteğe bağlı alan, hash yok). Bugünkü adaptör isteğe bağlı
  alanlar için hiçbir zaman None üretmez.
- Sonuç: Otomatik seçimde NotReported bir sunucu görselinden genel görsele geçilir ama kart bunu söyler; açık `sunucu`/`genel`
  seçimlerinde ve banner notlarında mesaj "Discord … bildirmedi" der. Gönderilmemiş/`null` alan hiçbir zaman "görsel kesin yok"
  diye sunulmaz; genel avatar alanı ise belgeye göre her zaman geldiği için `null` doğrudan varsayılan avatardır.
- Kesin yokluğu ayırmak için ham JSON gerekir. Bu, ya botun tüm REST trafiği için Discord.Net `RestClientProvider` /
  `IRestClient` sarmalayıcısı ya da ayrı bir HTTP istemcisi demektir. İkisi de bu modülün sınırını aşan bir karardır ve
  yapılmadı.

## Yanıt akışı

1. Hızlı kontroller: guild + modül (`[ToroModule]`), `tur` değeri, ortak cooldown. Bunların reddi **yalnızca kullanana**
   (ephemeral) ve herhangi bir onaydan önce gider.
2. Ardından tek bir **herkese açık defer** (REST okumaları Discord'un 3 saniyelik süresini aşabilir).
3. İşlem sonunda aynı özgün yanıt kartla veya kısa notla **bir kez** tamamlanır; beklenmeyen bir istisna da "alınamadı"
   notuyla tamamlanır, "düşünüyor" durumu asılı kalmaz. Public defer edilmiş yanıt sonradan ephemeral yapılmaya çalışılmaz.
   Yanıt gönderilemezse (ör. token süresi dolmuşsa) mevcut interaction hata yolu loglar; başka kanala mesaj gönderilmez.

Gönderim yolu: `PfpCommandFlow.RunAsync` → `PfpCommands` (`IPfpResponder`) → `ToroInteractionModule.SendAsync` /
`SendEphemeralAsync` → Discord.Net `RespondAsync` (henüz yanıt yoksa) ya da `FollowupAsync` (defer sonrası), her ikisinde de
`allowed_mentions` boş (`MentionPolicy.None`). Defer sonrası ilk followup, Discord belgesine göre yeni mesaj oluşturmaz, yükleniyor
mesajını düzenler ve defer'in ephemeral durumunu korur. Belge bu davranışı geriye uyumluluk için koruyor ama **deprecated** olarak
işaretliyor; önerilen yol "Edit Original Interaction Response"tır. Bu ortak yardımcı diğer modüllerde de (ör. Currency) kullanıldığı
için PFP'de ayrı bir gönderim yolu açılmadı; geçiş ayrı bir ortak altyapı bakım işidir ([STATUS.md](../STATUS.md), Known
limitations). PFP'nin bu yolla canlı yanıtı doğrulanmadı.

**Cooldown**: `/pfp` ve `/banner` ortak; anahtar `guildId + invokingUserId` (seçilen hedef kişi değil), **5 saniye** (bellek içi; yeniden başlatmada
sıfırlanır). Mesaj: "Biraz bekle: N saniye sonra tekrar deneyebilirsin."

## Veri saklama

Uygulama görsel, URL, profil veya kullanım geçmişi **arşivlemez**: veritabanına, diske, önbelleğe yazmaz; yalnız cooldown için
5 saniye boyunca bellekte (sunucu ID, üye ID) tutulur. Ancak kanala gönderilen Discord mesajı (kart ve görsel bağlantısı) normal
bir Discord mesajıdır ve kanal geçmişinde kalır; kendiliğinden silinmez. Discord'un kendi CDN/önbellek davranışı da ayrıca
geçerlidir.

## Bilinen sınırlar

- İsteğe bağlı görsel alanlarında yokluk doğrulanamaz (yukarıdaki bölüm). Bu yüzden sunucu görseli olmayan her üyede
  Otomatik kart bir açıklama satırı taşır ve boş-sonuç mesajları "bildirmedi" der. Bu, V1 için kabul edilen davranıştır
  (2026-10-28, [Geçiş sözleşmesi](#geçiş-sözleşmesi-v1-kararı-2026-10-28)); sınır ancak ham JSON okuyan bir yolla kalkar ve bu
  V1 kapsamında değildir.
- Üye için alınan her 404 "üye değil" sayılır (Discord.Net 404 kodunu göstermez; Unknown Member ile Unknown User ayırt
  edilemez). Sunucu için alınan 404 ise "alınamadı"dır.
- Her komut ayrıca Get Guild isteği gönderir (Discord.Net'in üye okuma yolu da aynısını yapar); bunu azaltmak için ayrı bir
  HTTP altyapısı eklenmedi.
- Hareketli WebP'nin tüm Discord istemcilerinde hareketli göründüğü canlıda doğrulanmadı.

## Canlı doğrulama kontrol listesi (sahip; komut senkronu ve modül açıldıktan sonra)

- [ ] `uye` alanında Discord'un yerleşik kişi seçicisi açılıyor; bir bot hesabı da seçilebiliyor.
- [ ] Genel ve sunucu avatarı **farklı** bir üye: `/pfp tur:genel` genel, `/pfp tur:sunucu` ve `/pfp` sunucu avatarını
      gösteriyor; başlıklar doğru.
- [ ] Hareketli avatar/banner'ı olan bir üye: kartta ve **Tam boy aç** bağlantısında animasyon oynuyor (masaüstü + mobil).
- [ ] Banner'ı olmayan bir üye: `/banner`, `/banner tur:genel`, `/banner tur:sunucu` "Discord … bildirmedi" notlarını veriyor (hata değil).
- [ ] Sunucu avatarı olmayan bir üye: `/pfp` genel avatarı gösteriyor ve kartta "Discord bu sunucuya özel bir profil fotoğrafı
      bildirmedi" satırı var.
- [ ] Sunucu banner'ı olan bir üye: `/banner tur:sunucu` onu gösteriyor.
- [ ] Profil fotoğrafı olmayan bir hesap: "Varsayılan profil fotoğrafı".
- [ ] **Tam boy aç** düğmesi karttaki görselin aynısını açıyor; görsel büyük (image alanı) görünüyor.
- [ ] Kart kimseyi etiketlemiyor; 5 saniye içinde ikinci komut yalnızca kullanana "Biraz bekle" diyor.
