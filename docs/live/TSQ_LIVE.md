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
- **Kesinti sonrası:** gözlem sürekli değilse (`Continuity` = en az 4 uzlaştırma / 3 dk; restart, sağlayıcı kesintisi,
  modül kapalıyken) yalnızca en fazla `LateAnnounceMinutes` (10 dk) önce başlamış bir yayın duyurulur; daha eskisi
  baseline'dır. Modül/gönderim kapalıyken başlayan oturum sonradan asla duyurulmaz.
- **Silinen kart:** bir düzenleme mesajı silinmiş bulursa (outbox `edit_target_deleted`) ve oturum sürüyorsa oturum başına
  **en fazla bir kez**, mention'sız (`@everyone` metni de yok) yedek kart gönderilir; yeni mesaj kimliği saklanır.
- **Geç teslim yok:** ilk duyuru `AnnouncementMaxDelayMinutes` (15 dk) içinde teslim edilemezse süresi dolar; geç bir
  `@everyone` atılmaz. Biten oturumun kartı hiçbir zaman yeni mesaj olarak gönderilmez (yalnızca düzenleme).

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
| Aynı olay kimliği / daha eski zaman damgası | yok sayılır (`DuplicateIgnored` / `StaleIgnored`); durum ve başlık ayrı sıralanır |

## Sağlayıcılar (resmî API, 2026-09-26 doğrulandı)

**Push/webhook yok — bilinçli karar.** Bot Railway'de public ağ girişi olmadan ve web sunucusu olmadan çalışır:

- Twitch EventSub **webhook** ve Kick webhook'ları (`livestream.status.updated`, `livestream.metadata.updated`) herkese
  açık bir HTTPS callback ister → bu dağıtımda **BLOCKED** (ikinci bir web sunucusu açılmadı).
- Twitch EventSub **WebSocket** kullanıcı erişim token'ı ister (app token ile abonelik başarısız olur); refresh token
  değişebilir ve saklanması gerekir → veritabanında dönen bir secret saklamak güvenlik kararıdır, sahip onayı olmadan
  yapılmadı (**DEFERRED**). Kick'in resmî WebSocket'i yoktur.

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
`started_at`, başlık + değişim zamanı, kategori, avatar, durum/metadata gözlem filigranları, son olay kimliği),
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
| `Live:LateAnnounceMinutes` | `10` | 0..60 |
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
