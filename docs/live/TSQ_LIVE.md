# TSQ Live — Yayın Takibi (Twitch + Kick)

Ayrı modül (`live`, `src/ToroSquad.Modules.Live`). Yapılandırılmış yayıncıların Twitch/Kick yayınları başladığında
belirlenen Discord kanalına **tek** bir canlı yayın kartı gönderir; aynı kart yayın boyunca güncellenir.

V1 yayıncıları (`appsettings.json` → `Live:Creators`; ortam değişkeniyle değiştirilebilir, kullanıcı girdisi değildir):

| Yayıncı (mantıksal) | Twitch | Kick |
|---|---|---|
| `lordtoro` (LORDTORO) | `lordtoro` | `lordtoro` |
| `nasilyani69` (NASILYANI69) | `nasilyani69` | `nasilyani69` |

Kapsam dışı (V1): YouTube/TikTok, sohbet, takipçi/abone bildirimi, izleyici sayısı, klip, analiz paneli, ses kanalı veya
kanal adı yönetimi, kullanıcıların kendi yayınlarını eklemesi.

## Davranış

- **Oturum** mantıksal yayıncı başınadır: herhangi bir platform canlıysa yayıncı canlıdır; tüm platformlar kapanınca
  yeniden bağlanma toleransı (`ReconnectGraceSeconds`, varsayılan 120 sn) başlar. Oturum ancak tolerans bittiğinde **ve**
  izlenen her platform tolerans bitiminden *sonra* yapılmış bir sağlayıcı cevabıyla kapalı doğrulandığında biter.
- **Yeni oturum → tek `@everyone`** (içerik `@everyone 🔴 **LORDTORO** yayında!`, `allowed_mentions.parse=["everyone"]`).
- İkinci platformun açılması, başlık/kategori değişmesi, bir platformun kapanması, oturumun bitmesi → **aynı mesaj
  düzenlenir**, düzenlemeler asla ping atmaz (outbox + transport `allowed_mentions` boş gönderir). Yeniden bağlanma
  toleransı sırasında kart hiç değiştirilmez (flap düzenleme bile üretmez).
- Kart: başlık (en son değişen platform başlığı), `🔴 CANLI · Twitch + Kick`, kategori, Discord göreli başlangıç zamanı,
  profil resmi (yalnızca `static-cdn.jtvnw.net` / `*.kick.com`, `ThumbnailPolicy`), yalnızca canlı platformlar için
  "Twitch'te İzle" / "Kick'te İzle" link butonları (URL'ler yapılandırmadan, sağlayıcıdan değil). Sağlayıcı metni
  (başlık/kategori) güvenilmez kabul edilir: mention, markdown ve link etkisizleştirilir.
- Oturum bitince kart "⚫ yayını sona erdi" olarak düzenlenir (süre, kullanılan platformlar; buton yok, ping yok).
- **İlk açılış (bootstrap):** bir kanal ilk kez gözlemlendiğinde zaten canlıysa baseline kaydedilir, duyurulmaz
  (`AnnounceExistingLiveOnBootstrap=false`, varsayılan).
- **Kesinti sonrası (restart, deploy, sağlayıcı kesintisi):** zaman sınırı yoktur, karar kalıcı durum geçişine dayanır.
  Kanal en son güvenilir gözlemde **kapalı** görüldüyse ve sağlayıcının `started_at` değeri o gözlemden **sonraysa**, yayın
  bot bakmıyorken başlamış gerçek yeni bir oturumdur → duyurulur (ör. bot 20 dk kapalı, yayın 18 dk önce başladı → tek
  `@everyone`). `started_at` yoksa veya son kapalı gözlemden önceyse baseline'dır.
- **Bilinçli kapatma:** modül kapısı kapalıyken başlayan oturum sonradan asla duyurulmaz (izleme sürer, oturum
  `delivery_off` olarak kaydedilir). `Live:Enabled=false` ile açılış, bilinen tüm kanal durumlarını "hiç gözlemlenmedi"ye
  döndürür ve açık oturumları kapatır → tekrar açıldığında ilk gözlem yeniden baseline'dır (bayat duyuru yok).
- **Silinen kart:** bir düzenleme mesajı silinmiş bulursa (outbox `edit_target_deleted`) ve oturum sürüyorsa oturum başına
  **en fazla bir kez**, mention'sız (`@everyone` metni de yok) yedek kart gönderilir; yeni mesaj kimliği saklanır.
- **Geç teslim yok:** ilk duyuru algılandıktan sonra `AnnouncementMaxDelayMinutes` (15 dk) içinde teslim edilemezse
  (ör. Discord kesintisi) süresi dolar; geç bir `@everyone` atılmaz. Biten oturumun kartı hiçbir zaman yeni mesaj olarak
  gönderilmez (yalnızca düzenleme).

## @everyone en fazla bir kez (oturum başına)

- `@everyone` yalnızca oturumun **ilk** outbox satırının (`announce`) ilk gönderiminde olabilir; yedek kartlar
  (`announce-r1`), düzenlemeler, restart, uzlaştırma ve başlık/platform değişiklikleri asla mention üretmez.
- Kesin başarısız olduğu bilinen denemeler (429, bağlantı yokken, 4xx) normal şekilde tekrar denenir.
- Discord'a ulaşmış olabilecek belirsiz denemeler (zaman aşımı, yanıt kaybı, **her** 5xx, gönderim sırasında çökme →
  restart'ta `DeliveryUnknown`) mesaj içerik parmak iziyle son mesajlarda aranır (uzlaştırma). Bulunursa o mesaj
  benimsenir; bulunamazsa **tek** yeniden gönderim `@everyone`'sız yapılır — outbox, uzlaştırmadan geçmiş bir satırda
  `@everyone`'ı her zaman kaldırır (kesin garanti), planlayıcı ayrıca `@everyone` metnini de çıkarır. Çok nadir bir
  durumda ping'in hiç gitmemesi, ikinci bir ping'den daha iyidir.
- Discord'un `nonce` + `enforce_nonce` özelliği kullanılmıyor: kullanılan Discord.Net 3.20.1 `enforce_nonce`'u hiç
  desteklemiyor (`nonce` yalnızca kütüphanenin iç istek modelinde var, public `SendMessageAsync` ile ayarlanamıyor) ve
  kütüphaneyi atlayan ikinci bir Discord REST istemcisi yazılmadı. Nonce zaten yalnızca birkaç dakikalık bir pencere
  sağlar; değişmez (invariant) kural yukarıdaki "belirsizlikten sonra asla mention" kuralıyla sağlanır.
- DryRun ↔ Send geçişi sırasında devam eden oturum ikinci kez duyurulmaz.

## Durum makinesi

Platform: `Unknown → Offline ⇄ Live`. Yayıncı: `Offline → Live ⇄ ReconnectGrace → Offline`
(`Domain/LiveStateMachine.cs`, saf; saat enjekte edilir).

| Gözlem | Sonuç |
|---|---|
| Bir platform canlı, yayıncı `Offline` | yeni oturum; duyuru kararı: `announce` / `bootstrap` / `gap` / `delivery_off` |
| İkinci platform canlı, yayıncı `Live` | oturuma katılır, kart düzenlenir, ping yok |
| Platform kapandı, diğeri canlı | kart düzenlenir, oturum sürer |
| Son platform kapandı | `ReconnectGrace` |
| Tolerans içinde yeniden canlı (`started_at` < tolerans sonu) | aynı oturum, ping yok |
| Tolerans sonrasında başlamış yayın | eski oturum biter, yeni oturum (duyurulabilir) |
| İki gözlem arasında görülmeden yeniden başlama (`started_at` > önceki gözlem) | toleranstan kısa → aynı oturum; uzun → yeni oturum |
| Sağlayıcının aynı yayın kimliği (Twitch stream id) geri geldi | her zaman aynı oturum; oturum bitmişse yeniden açılır (aynı mesaj canlıya döner, ping yok) |
| Kesinti sonrası ilk gözlem: canlı, `started_at` son "kapalı" gözlemden sonra | bot bakmıyorken başlamış gerçek yeni oturum → duyurulur (yaş sınırı yok) |
| Aynı olay kimliği / daha eski zaman damgası | yok sayılır (`DuplicateIgnored` / `StaleIgnored`); durum ve başlık ayrı sıralanır |

## Sağlayıcılar (resmî API, 2026-09-26 doğrulandı)

**V1'de push/webhook yok — bilinçli karar (DEFERRED).**

- Twitch EventSub **webhook** ve Kick webhook'ları (`livestream.status.updated`, `livestream.metadata.updated`) herkese
  açık bir HTTPS callback endpoint'i ister. Mevcut TSQ Bot dağıtımı şu anda bir HTTP callback endpoint'i sunmuyor (generic
  host worker, web sunucusu yok). Bu yüzden webhook aktarımı V1 için ertelendi; bu **bir Railway platform kısıtı değildir**
  (Railway public HTTPS networking/domain destekler). Sırf bunun için bot HTTP sunucusuna dönüştürülmedi.
- Twitch EventSub **WebSocket**: resmî gereksinim olarak abonelik oluşturmak **kullanıcı erişim token'ı (user access
  token)** ister; app token ile WebSocket abonelikleri başarısız olur. Refresh token değişebilir ve saklanması gerekir.
  V1, yalnızca TSQ Live için bir refresh-token yaşam döngüsü ve kalıcılığı eklemekten bilinçli olarak kaçınır; bu yüzden
  V1 mekanizması Helix polling'dir. Kick'in resmî WebSocket'i yoktur.

Bu yüzden her iki platformda da **resmî API uzlaştırması (reconciliation)** hem tetikleyici hem doğruluk kaynağıdır:
`ReconciliationIntervalSeconds` (30 sn) başına sağlayıcı başına **tek toplu istek**; kaçan online/offline ve başlık
değişikliği bir sonraki turda düzelir. Koordinatör olay kimliği/zaman damgası ile sıralama ve tekilleştirme yaptığı için
ileride EventSub/webhook eklenirse aynı boru hattına bağlanır.

| | Twitch | Kick |
|---|---|---|
| Kimlik | client credentials app token (`POST id.twitch.tv/oauth2/token`), başlangıçta ve saatlik `oauth2/validate` | client credentials app token (`POST id.kick.com/oauth/token`) |
| Durum + başlık | `GET helix/streams?user_login=…&user_login=…` (≤100): listede `type=live` → canlı; başarılı cevapta yoksa → kapalı | `GET public/v1/channels?slug=…&slug=…` (≤50): `stream.is_live`, `stream_title`, `category.name`, `stream.start_time`; cevapta olmayan slug → bilinmiyor (kapalı DEĞİL) |
| Profil resmi | `GET helix/users?login=…` (6 saatte bir) | `GET public/v1/users?id=…` (6 saatte bir) |
| Limit | app token puan kovası (dakikalık, `Ratelimit-*` başlıkları; 429 → `Ratelimit-Reset`) — 30 sn'de 1 istek çok altında | genel limit dokümante değil; 30 sn'de 1 istek |

Hata politikası: 5xx/zaman aşımı/ağ hatası sınırlı tekrar (1) + üstel geri çekilme (en çok 15 dk, `Retry-After`/`Ratelimit-Reset`
önceliklidir); 401 → token bir kez yenilenir; kimlik hatası 5 dk'da bir denenir. **Başarısız istek asla "kapalı"
sayılmaz**: önceki bilinen durum korunur, tolerans içindeki oturum doğrulama gelene kadar bitmez. Bir sağlayıcının
hatası diğerini etkilemez (ayrı, eşzamanlı istekler). Token ve secret'lar yalnızca başlıkta taşınır; URL'ye, loga,
doctor'a veya veritabanına yazılmaz.

## Kalıcılık ve restart

Ek (additive) migration `LiveModule`: `live_creator_state` (oturum no, faz, tolerans, başlangıç/bitiş, duyuru kararı,
duyuru türü, guild/kanal/mesaj kimliği, duyuru zamanı, yedek sayısı), `live_platform_state` (durum, yayın kimliği,
`started_at`, başlık + değişim zamanı, kategori, avatar, durum/metadata gözlem filigranları — kesinti sonrası kararın
dayandığı son güvenilir gözlem zamanı —, son olay kimliği),
`live_provider_state` (son deneme/başarı/sonuç/hata). Diğer modüllerin tablolarına dokunulmaz.

Oturum durumu ve outbox satırı **tek SQLite işleminde** yazılır. Duyuru mesajı outbox'ın tekil mantıksal anahtarıyla
(`guild|live|<yayıncı>:<oturum>|kanal|announce`) gönderilir: restart, tekrar planlama ve belirsiz gönderim (zaman aşımı)
ikinci mesaj üretemez (belirsiz gönderim içerik parmak izi ile uzlaştırılır, kör tekrar yok). Restart sonrası canlı
yayın aynı oturum olarak devam eder → yeni mesaj yok, `@everyone` yok.

## Yapılandırma

| Anahtar | Varsayılan | Açıklama |
|---|---|---|
| `Live:Enabled` | `false` | ana anahtar; kapalıyken hiçbir sağlayıcı isteği yok |
| `Live:DiscordChannelId` | `0` | duyuru kanalı (açıkken zorunlu; tahmin edilmez) |
| `Live:GuildId` | `0` | `0` = `Discord:AllowedGuildIds` içindeki tek sunucu |
| `Live:ReconciliationIntervalSeconds` | `30` | 15..300 |
| `Live:ReconnectGraceSeconds` | `120` | 30..1800 |
| `Live:AnnounceExistingLiveOnBootstrap` | `false` | |
| `Live:AnnouncementMaxDelayMinutes` | `15` | 2..120 |
| `Live:Creators:<key>:DisplayName` / `:Twitch` / `:Kick` | V1 listesi | |
| `Live:Twitch:ClientId` / `:ClientSecret` | — | **secret**, yalnızca ortam değişkeni / user-secrets |
| `Live:Kick:ClientId` / `:ClientSecret` | — | **secret**, yalnızca ortam değişkeni / user-secrets |

Ayrıca sunucuda modül kapısı: `/modules enable live` (diğer isteğe bağlı modüller gibi varsayılan kapalı). Modül veya
`Live:Enabled` kapalıyken başlayan yayın, sonradan açılsa bile duyurulmaz.

Railway değişkenleri: `TOROSQUAD_Live__Enabled`, `TOROSQUAD_Live__DiscordChannelId`, `TOROSQUAD_Live__Twitch__ClientId`,
`TOROSQUAD_Live__Twitch__ClientSecret`, `TOROSQUAD_Live__Kick__ClientId`, `TOROSQUAD_Live__Kick__ClientSecret`
(isteğe bağlı: `TOROSQUAD_Live__GuildId`, `TOROSQUAD_Live__ReconnectGraceSeconds`, …).

## Discord izinleri

Duyuru kanalında bot: View Channel, Send Messages, Embed Links, **Mention Everyone** (`@everyone`'ın gerçekten bildirim
göndermesi için; davet izinleri 84992 bunu içermez — kanal/rol izniyle verilmelidir), Read Message History (belirsiz
gönderimlerin uzlaştırılması için, önerilir).

## Tanı ve loglar

- `/live-admin doctor` (Manage Server): `Live:Enabled`, modül kapısı, hedef kanal ve izinler (Mention Everyone dahil),
  gönderim modu, platform başına yetkilendirme durumu ve son başarılı uzlaştırma, olay aktarımı, her yayıncının faz /
  platform durumu / oturum / duyuru mesaj bağlantısı / başlık, 24 saatlik gönderim istatistiği. Sağlayıcıya istek atmaz.
- `/bot status`: Twitch/Kick uzlaştırma sağlığı. CLI `doctor`: `Live` yapılandırması ve kimlik bilgilerinin varlığı
  (değerler gösterilmez).
- Yapılandırılmış loglar: `live creator session started`, `platform joined active session`, `title changed`,
  `platform went offline`, `reconnect grace entered`, `reconnect within grace`, `session ended`,
  `announcement created/updated`, `announcement message missing`, `provider request failed/recovered`,
  `duplicate event ignored`, `stale … statement ignored`. Rutin turlar yalnızca Debug (her 30 sn "hâlâ kapalı" logu yok);
  hata akışı başına bir uyarı (ve her 20. denemede bir).

## Doğrulama durumu

- **IMPLEMENTED / TESTED_OFFLINE**: durum makinesi, çoklu yayın tekilleştirme, başlık senkronu, restart/flap/sağlayıcı
  hatası güvenliği, silinen kart yedeği, bootstrap, sağlayıcı sözleşmeleri (belgelenmiş cevap şekilleriyle), mimari
  sınırlar (`LiveStateMachineTests`, `LiveAnnouncementTests`, `LiveProviderContractTests`, `LiveArchitectureTests`).
- **NOT VERIFIED_LIVE**: gerçek Twitch/Kick API çağrısı (kimlik bilgisi yok), gerçek Discord gönderimi, gerçek `@everyone`
  (sahip onayı gerektirir).
