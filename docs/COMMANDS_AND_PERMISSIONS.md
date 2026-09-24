# Slash komutlar, yetkiler, intent'ler ve davet

Kaynak: kodla eşitliği test edilen [commands.manifest.json](commands.manifest.json). Tüm komutlar yalnızca sunucu
bağlamında (`contexts=[0]`, `integration_types=[0]`); DM komutu ve DM bildirimi yoktur (sunucu tarafında da reddedilir).

## Genel (herkes)

| Komut | Ne yapar | Yanıt |
|---|---|---|
| `/help` | Açık modüllerin, çağıranın yetkisine göre komutları | ephemeral |
| `/bot status` | Bağlantı, çalışma süresi, modül durumları, sağlayıcı sağlığı (secret/iç hata yok) | ephemeral |
| `/bot about` | Ad, sürüm + commit, lisans, işletmeci iletişimi, atıflar, "resmî BOT Greg değildir" | ephemeral |
| `/bot source` | Çalışan sürümün kaynak bağlantısı (`Bot:SourceUrl`), sürüm, commit, lisans notu | ephemeral |
| `/privacy export` | Çağıranın bu sunucudaki kayıtları (JSON dosyası) | ephemeral |
| `/privacy delete` | Önizleme → 5 dk geçerli, tek kullanımlık, kullanıcı+sunucuya bağlı onay düğmesi | ephemeral |

## Yönetici (`default_member_permissions = ManageGuild (32)` + sunucu tarafı yetki kontrolü)

| Komut | Ne yapar |
|---|---|
| `/setup` | Dil → zaman dilimi (Europe/Istanbul varsayılan) → modül adımları (esports: kanal, pingsiz önizleme, etkinleştir). Düğmeler yalnızca sihirbazı açan kişi için ve her tıklamada yeniden yetki kontrolü |
| `/modules list \| enable module: \| disable module:` | Modül durumları; kapatmak veri silmez; çekirdek kapatılamaz |

## Esports (modül açıkken)

| Komut | Seçenekler |
|---|---|
| `/esports matches` | `team` (autocomplete), `tournament` (autocomplete), `mine` |
| `/esports results` | `team`, `spoiler` (varsayılan: kişisel tercih) |
| `/esports events` | — |
| `/esports rankings` | `top` 1–50 (kaynak + yayın tarihi + alınma zamanı gösterilir) |
| `/esports team` | `team` (VRS eşleşmesi: kesin / alias / normalize / belirsiz / yok) |
| `/esports follow` / `unfollow` | `team` (autocomplete; "tüm maçlar" self-service rolü varsa listelenir) |
| `/esports subscriptions` | takipler, bot rolleri, spoiler tercihi düğmesi |

## Esports yönetici (`ManageGuild`; rol işlemleri ayrıca `ManageRoles` + hiyerarşi)

| Komut | Not |
|---|---|
| `/esports-admin configure` | `channel` (kanal seçici), `reminders`, `reminder_minutes` 1–120, `results`, `spoilers`. Kanal bu sunucuda ve metin kanalı olmalı; eksik bot izinleri uyarılır |
| `/esports-admin filters show \| team \| tournament \| tier \| vrs \| clear` | Kural: aynı tür VEYA, farklı türler VE; VRS verisi yoksa filtre durdurur |
| `/esports-admin roles list \| map \| unmap \| selfservice` | `map`: mevcut rolü ping hedefi yapar (managed/@everyone reddedilir). `selfservice`: ayrı onay, güvenlik denetimi, onaylayan rolden yukarıda olmalı |
| `/esports-admin panel` | Herkese açık takip düğmeleri (durumsuz custom id → yeniden başlatmada çalışır; modül kapalıysa reddedilir) |
| `/esports-admin preview` | Ping atmayan önizleme; pinglenecek rolleri metin olarak listeler |
| `/esports-admin pause` / `resume` | Resume, watermark'ı ileri alır: kaçanlar topluca gönderilmez |
| `/esports-admin doctor` | Kanal izinleri tek tek, rol ping'i/self-service güvenliği, sağlayıcı modu/durumu, VRS, 24 saatlik gönderim istatistiği |

Yönetici esports yapılandırması modül kapalıyken de yapılabilir (etkinleştirmeden önce kurulum için); kullanıcı komutları,
panel, autocomplete ve bildirimler modül kapalıyken çalışmaz.

## Bot hesabı, intent'ler, davet

- Resmî bot hesabı + bot token (self-bot/kullanıcı token'ı yok). **Administrator istenmez.**
- Gateway intent: yalnızca **Guilds** (ayrıcalıklı değil). Message Content / Presence / Guild Members **kapalı**; gerekmez
  (etkileşim yükü üyenin rollerini içerir, rol ekleme/çıkarma REST ile yapılır).
- OAuth2 kapsamları: `bot` ve `applications.commands`.
- İzinler (asgari, işleve göre):

| İzin | Bit | Neden | Gerekli mi |
|---|---|---|---|
| View Channel | 1024 | bildirim kanalını görmek | evet |
| Send Messages | 2048 | bildirim göndermek | evet |
| Embed Links | 16384 | embed'ler | evet |
| Read Message History | 65536 | belirsiz teslimat uzlaştırması (yalnızca kendi mesajlarını arar) | önerilir |
| Manage Roles | 268435456 | self-service bildirim rolleri | yalnızca self-service kullanılırsa |
| Mention Everyone | 131072 | bahsedilemez rolleri pinglemek | **önerilmez** — rolü "bahsedilebilir" yapın |

  Asgari izin tamsayısı: **84992**; self-service rollerle: **268520448**.
  Bot rolü, dağıtacağı self-service rollerin **üstünde** olmalıdır.
- Rate limit: Discord.Net yerleşik yönetimi (`RetryRatelimit`, Retry-After'a uyar). Ek agresif retry katmanı yok;
  outbox kendi sınırlı geri çekilmesini uygular.
