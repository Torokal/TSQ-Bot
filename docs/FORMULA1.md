# Formula 1 modülü

Modül kimliği `formula1` · sürüm 0.1.0 · **varsayılan kapalı** (kurulum + pingsiz önizleme + açık etkinleştirme gerekir).
Kod: [`src/ToroSquad.Modules.Formula1`](../src/ToroSquad.Modules.Formula1). Yalnızca paylaşılan TSQ katmanlarına
(Core, Infrastructure, Discord) bağlıdır; esports modülüne bağlı değildir ve esports da ona bağlı değildir (mimari testleri
`F1ArchitectureTests` bunu zorlar).

**Temel ilke:** yanlış bir F1 bildirimi, hiç bildirim olmamasından kötüdür. Emin olunamayan her durumda bildirim gönderilmez
ve durum dürüstçe raporlanır (`/f1-admin doctor`, `/f1 now`).

## Ne yapar

| Özellik | Kaynak | Not |
|---|---|---|
| Seans başladı kartı (Antrenman 1–3, Sprint, Yarış; Sıralama / Sprint Sıralaması modelli ama varsayılan kapalı) | **Canlı yaşam döngüsü** (OpenF1) | Program saati **asla** "başladı" demek değildir |
| Sonuç kartı (antrenman: tur zamanı/fark; sprint/yarış: tam sınıflandırma, DNF/DNS/DSQ) | OpenF1 `session_result` + `drivers` | Seans bitti ≠ sonuç hazır; sınıflandırma ancak **seansın katılımcı listesini tamamen kapsıyorsa** yayımlanır |
| Sonuç düzeltmeleri | aynı | 24 saat boyunca **aynı mesaj** pingsiz düzenlenir |
| Şampiyona puan durumu (sürücüler / takımlar) | Jolpica | Bot puan **hesaplamaz**; sağlayıcı tablosu gösterilir |
| Sprint/yarış kartına puan durumu ekleme | Jolpica | Tablo değişince aynı sonuç mesajı pingsiz düzenlenir (sınırlı bekleme penceresi) |
| Takvim, `/f1 next`, `/f1 schedule` | Jolpica | Discord zaman damgaları (`<t:…:F>`, `<t:…:R>`) herkesin kendi saat diliminde gösterilir |

Terminoloji (TR): Antrenman, Sprint Sıralaması, Sprint, Sıralama, Yarış, Sürücüler Şampiyonası, **Takımlar Şampiyonası**
(İngilizce "Constructors" için modül genelinde tek terim: "Takımlar"), Puan, Sıradaki Yarış.

## Sağlayıcı mimarisi

Tek bir dev "F1 sağlayıcısı" yoktur; her yetenek ayrı bir arayüzdür ve yapılandırmayla ayrı ayrı seçilir
(`Formula1:Provider:*`). Uygulamanın geri kalanı (planlayıcı, kalıcılık, komutlar, kartlar) yalnızca normalize domain
nesnelerini ve bu arayüzleri görür:

| Arayüz | Varsayılan | Yapılandırma | Ne sağlar |
|---|---|---|---|
| `IF1ScheduleProvider` | Jolpica | `Formula1:Provider:Schedule=Jolpica` | Sezon takvimi (toplantılar + seanslar + UTC saatleri) |
| `IF1LifecycleProvider` (+ `IF1LiveTransport`) | OpenF1 | `Formula1:Provider:Lifecycle=OpenF1` veya `None` | Seans başladı / durdu / bitti (canlı) |
| `IF1ResultsProvider` | OpenF1 | `Formula1:Provider:Results=OpenF1` | Seans sınıflandırması |
| `IF1StandingsProvider` | Jolpica | `Formula1:Provider:Standings=Jolpica` | Sürücüler / takımlar tablosu |

Yeni bir sağlayıcı (ör. ticari bir sağlayıcı) eklemek: arayüzü uygulayan sınıf + `Formula1Module.ConfigureServices`
içinde kayıt. Komutlar, planlayıcı, kalıcılık ve kart oluşturucu değişmez.

**Kimlik eşleme.** Sağlayıcıların oturum/toplantı kimlikleri ortak değildir. Normalize anahtar yalnızca sezon + tur + seans
tipinden türetilir (`2026-18-race`). Bir sağlayıcı oturumu takvime yalnızca aynı sezon + aynı tip + başlangıç saati
`SessionMatchToleranceHours` (vars. 6) içinde **tek** adayla eşleşirse bağlanır; aday yoksa veya birden fazlaysa eşleme
yapılmaz (fail closed). Eşlemeler `f1_session_snapshot` içinde kalıcıdır.

**Doğrulanmış sağlayıcı sözleşmeleri** (2026-09-25, resmî belgeler + tek seferlik salt-okuma örnekleri):

- *OpenF1*: `race_control` içinde `category="SessionStatus"` satırları `SESSION STARTED` / `SESSION ABORTED` (kırmızı
  bayrak vb. durdurma) / `SESSION FINISHED` iletir. **Sıralama formatlarında her segmentin (Q1/Q2/Q3) sonunda
  `SESSION FINISHED` gelir** (`qualifying_phase` 1, 2, 3); yalnızca 3. segmentin sonu seansı bitirir, segment numarası
  yoksa bitiş kabul edilmez. Boş sorgu `HTTP 404 {"detail":"No results found."}` döner → "henüz veri yok", hata değil.
  `session_result`: DNF/DNS/DSQ için `position=null`; `duration`/`gap_to_leader` sayı, sıralamada 3'lü dizi, turlanmış
  sürücüde `"+1 LAP"`. MQTT mesajları REST nesnesinin aynısı + `_id`, `_key`.
- *Jolpica*: Ergast uyumlu `MRData` zarfı; `FirstPractice`, `SecondPractice`, `ThirdPractice`, `Qualifying`, `Sprint`,
  `SprintQualifying` (2023: `SprintShootout`) alanları `date` + `time` (UTC). Saati olmayan seans atlanır (uydurulmaz).
  Puan durumu `StandingsLists[0]` (`season`, `round`).

## Yaşam döngüsü (durum makinesi)

```
Scheduled ──SESSION STARTED──► Started ──SESSION ABORTED──► Suspended
                                  ▲                            │
                                  └──────SESSION STARTED───────┘   (= RESUME, yeni "başladı" YOK)
Started/Suspended ──SESSION FINISHED──► FinishedPendingResults ──geçerli sınıflandırma──► Finalised
herhangi (bitmemiş) ──sağlayıcı iptali──► Cancelled
```

Kurallar (`F1LifecycleMachine`, testli): mantıksal başlangıç yalnızca Scheduled/Unknown → Started geçişidir ve
`StartedObservedAt` (sağlayıcı zaman damgası) bir kez yazılır; son uygulanan olaydan eski veya eşit zamanlı olaylar
(kopya, sıra dışı tekrar) yok sayılır; bitmiş/sonuçlanmış/iptal seans geç gelen mesajlarla yeniden açılmaz; başlangıç
görülmeden gelen FINISHED seansı bitirir ama asla "başladı" kartı üretmez.

## Bildirim akışı

```
OpenF1 MQTT/REST ─► normalize olay ─► Formula1Workflow (SQLite: f1_session_snapshot) ─► Formula1NotificationPlanner
                                                                                             │ (tek transaction)
                                                                   INotificationOutbox ◄─────┘
                                                                          │  (retry, modül kapısı, pause, kanal,
                                                                          ▼   uzlaştırma, allowed_mentions, pingsiz edit)
                                                                    Discord transport
```

Canlı dinleyici ve sağlayıcılar Discord'a **hiç** dokunmaz; outbox'a yalnızca planlayıcı yazar (mimari testli).

| Outbox türü | Ne zaman | Ping | Mantıksal anahtar |
|---|---|---|---|
| `started:<fp1\|fp2\|fp3\|sq\|sprint\|quali\|race>` | Canlı sağlayıcı ilk başlangıcı bildirdi, seans hâlâ sürüyor/durdurulmuş, başlangıç sunucunun watermark'ından sonra ve **sağlayıcı zamanından en fazla `StartFreshMinutes` (vars. 10) önce** | rol (varsa, `PingOnStarts`) | `live\|<guild>\|formula1\|<seans anahtarı>\|<kanal>\|started:race` |
| `result:<…>` | Geçerli sınıflandırma alındı (son `ResultStaleAfterMinutes` içinde çekilmiş) | rol (yalnızca `PingOnResults` açıksa; vars. kapalı) | `…\|result:race` |

- Düzeltme (ceza, DSQ, düzeltilmiş sınıflandırma) ve sonradan gelen puan durumu **aynı mesajı düzenler**; düzenlemeler asla
  ping atmaz (outbox kuralı). Kanonik sonuç hash'i sıralama/float gürültüsünden bağımsızdır.
- **Sonuç tamlığı:** OpenF1 sınıflandırması, aynı seansın `/drivers` listesiyle (katılımcı roster'ı) karşılaştırılır.
  Yayımlanabilir = roster'daki her sürücünün bir sonuç satırı var (DNF/DNS/DSQ satırları sayılır) ve roster dışı satır yok.
  Kesintisiz 1..10 sıralama, 20 kişilik roster varken **tam değildir**. Eksik/kopya/yabancı numara, boş roster veya
  sürücü listesi alınamaması (404) → yayımlanmaz, sınırlı geri çekilmeyle tekrar denenir. Grid boyutu sabitlenmez (18, 20,
  22, 24… kod/ayar değişmeden); `MinResultEntries` yalnızca ek bir alt sınırdır, tamlığın tanımı değildir. Satır uydurulmaz.
- Antrenman sonucu puan durumu iş akışını **tetiklemez**. Sprint/yarışta: sonuç hemen gönderilir ("puan durumu
  bekleniyor" satırıyla); tablo `StandingsSettleWindowMinutes` (vars. 180) boyunca 5→30 dk aralıklarla kontrol edilir.
- **Seans öncesi baseline kanıtlanabilir olmalı:** baseline, botun seansın kesim anından (`min(StartedObservedAt,
  ScheduledStartUtc)`) **önce** çektiği son tablodur (kalıcı `f1_standings_snapshot.FetchedAt`). "Şu anki en son tablo" asla
  baseline olmaz — kesintiden sonra bu zaten yarış sonrası tablo olabilir. Kanıtlanabilir bir seans öncesi tablo yoksa
  baseline bilinmez kalır: tablo eklenmez, kart dürüstçe "/f1 standings …" önerisine döner (sonuç yine yayımlanır).
- **Bir tablo ancak şu koşullarla "bu seanstan sonraki puan durumu" sayılır:** baseline'dan farklı; seansın kesim anından
  sonra çekilmiş; sağlayıcı tablosu en az bu turu kapsıyor (`round ≥ seansın turu`, önceki turun geç düzeltmesi eklenmez);
  **sprint** için tablo aynı turun yarışı başlamadan önce çekilmiş (yoksa yarışı da içerebilir); **sprint haftasonu yarışı**
  için sprint sonrası tablo, sprint bitişiyle yarış başlangıcı arasında gözlemlenmiş olmalı (tur numarası sprint sonrası ile
  yarış sonrasını ayırt edemez). Bu kanıt yoksa (ör. bot iki seans boyunca kapalıydı) tablo eklenmez — fail closed.
  Pencere eklemeden kapanırsa kart "sağlayıcı henüz güncellemedi — /f1 standings …" olarak düzenlenir.
- Kart boyutu: tam sınıflandırma tercih edilir (20–22 araç rahat sığar); açıklama 3000 karakter bütçesini aşarsa satır
  sınırında kesilir ve "kısaltıldı" notu eklenir. Puan durumu kartta ilk `CardStandingsRows` (vars. 10) satır +
  "tamamı: /f1 standings …".
- **Spoiler modu**: başlık ve renk sonuçtan bağımsızdır, küçük resim yoktur; sınıflandırma tek `||spoiler||` içinde,
  puan durumu alanları da spoiler içindedir. Sağlayıcı metni spoiler'ı kapatamaz (`DiscordText` ile etkisizleştirilir).
- Tüm sağlayıcı metinleri güvenilmezdir: @everyone/@here/rol biçimi, markdown ve `://` bağlantıları etkisizleştirilir;
  karta sağlayıcı URL'si konmaz.

### Bootstrap, watermark, kesinti

- İlk kurulum / yeni sezon / sağlayıcı değişimi: ilk görüldüğünde **planlanan bitişi geçmiş** her seans **baseline** olur ve
  asla duyurulmaz — sonucu dahil (sonuç `/f1 results` için yine çekilir). Fail closed: planlanan bitişten sonra ilk kez
  görülen seans hâlâ sürüyor olsa bile (uzun kırmızı bayrak) baseline'dır.
- Planlanan başlangıçtan sonra ama planlanan bitişten önce ilk kez görülen seans (ertelenmiş veya sürmekte) baseline
  **değildir**: canlı sağlayıcı ertelenmiş ilk başlangıcı sunucunun watermark'ından sonra ve taze olarak doğrularsa "başladı"
  kartı gider; seans modül açılmadan önce başlamışsa başlangıç kartı gitmez (watermark + tazelik). **Karar:** sonucu sunucunun
  watermark'ından sonra kesinleşen seansın sonucu yeni bilgidir ve gönderilir.
- Sunucu watermark'ı: kurulum, modülü (yeniden) açma, pause'dan dönme, kanal değiştirme ve bir bildirim türünü yeniden
  açma watermark'ı "şimdi"ye taşır; öncesindeki hiçbir şey gönderilmez.
- Kesinti sonrası: geç görülen başlangıç duyurulmaz (tazelik penceresi); sonuçlar yalnızca bitişi `ResultCatchUpHours`
  (vars. 6) içinde olan seanslar için ve çalıştırma başına sunucu başına en fazla `MaxCatchUpPerGuildPerRun` (vars. 3).
- Railway yeniden başlatması: tüm durum SQLite'tadır (anlık görüntüler, sağlayıcı eşlemeleri, son olay zamanı, outbox);
  yeniden başlayınca aynı olay/sonuç tekrar gelse de ikinci mesaj oluşmaz; sonraki düzeltmeler eski mesajı düzenler.

### Bayat veri ve sağlayıcı hataları

- Takvim `ScheduleStaleAfterHours` (48), sonuç `ResultStaleAfterMinutes` (90) eşiğini aşarsa planlayıcı **yeni bildirim
  ve düzenleme üretmez**; komutlar veriyi "⚠️ güncel değil" uyarısıyla gösterir.
- Bir sağlayıcı hatası asla "seans yok", "iptal" veya "bitti" olarak yorumlanmaz; son iyi veri korunur ve bayatlar.
- Yaşam döngüsü sağlayıcısı yoksa veya erişilemiyorsa: başlangıç bildirimi yoktur (programdan tahmin edilmez), `/f1 now`
  "canlı durum kullanılamıyor" der. Sonuçlar yine sonuç sağlayıcısından doğrulanmış veriyle gelebilir.
- Hatalar üstel geri çekilme ile tekrar denenir (`Retry-After` dikkate alınır); yapılandırma/kimlik hataları 30 dk'da bir.

## Yoklama stratejisi (durum farkındalıklı)

| Veri | Sıklık |
|---|---|
| Takvim | 6 saatte bir; 48 saat içinde seans varsa saatte bir. Mevcut sezon bitince (veya Aralık'ta) sonraki sezon da yüklenir — yıl kodda sabit değildir |
| Puan durumu | 6 saatte bir + sprint/yarış sonrası sınırlı bekleme penceresi |
| Yaşam döngüsü | Yalnızca seans penceresinde (başlangıçtan 35 dk önce → planlanan bitiş + 4 saat). Canlı akış (MQTT) açıkken REST yalnızca güvenlik ağı (2 dk) ve her yeniden bağlanmada; akış yoksa 60 sn |
| Sonuçlar | Sağlayıcı "bitti" dedikten sonra (kimlik bilgisi yoksa OpenF1 canlı penceresi + 5 dk sonra) 2→15 dk geri çekilmeyle en fazla 12 saat; sonra düzeltmeler için ilk 3 saat 20 dk'da bir, 24 saate kadar saatte bir |

Hiçbir sunucuda modül açık değilse hiçbir sağlayıcı çağrılmaz. Komutlar asla sağlayıcı çağırmaz (yalnızca önbellek;
mimari testli).

**Bütçeler:** Jolpica saatte 500'ün %50'si + saniyede 4; OpenF1 dakikada 30'un %50'si + saniyede 3 (sponsor hesabında
`RequestsPerMinute=60`, `RequestsPerSecond=6` yapılabilir). Her HTTP denemesi (retry dahil) yerel token-bucket'tan düşer.

**Canlı dinleyici (OpenF1 MQTT):** `mqtt.openf1.org:8883` (TLS), kullanıcı adı = OpenF1 hesabı, parola = OAuth2 erişim
token'ı (`POST https://api.openf1.org/token`, form `username`/`password`, 1 saat geçerli). Tek konu: `v1/race_control`.
Tek bağlantı (asla ikinci dinleyici yok), yalnızca seans penceresinde açık; kopunca 2 sn → 5 dk'ya kadar üstel geri
çekilme; kimlik reddinde 15 dk; token dolmadan temiz yeniden bağlanma; her bağlantıdan sonra REST uzlaştırması; kopya
olaylar hem dinleyicide hem durum makinesinde elenir. MQTT bağlanma/abone olma işlem zaman aşımıyla, nazik kopma 5 sn ile
sınırlıdır (iptali dinlemeyen bir ağ yolu bile beklemeyi uzatamaz; sonra soket kapatılır). Durdurma en fazla 10 sn bekler;
bağlantı hâlâ kapanmıyorsa dinleyici o döngüyü **unutmaz**: durum `Stopping` olur (doctor/sağlık uyarısı) ve eski döngü
gerçekten bitene kadar yeni bağlantı açılmaz. Host kapanışı bu yüzden takılmaz.

## Komutlar

Genel (`/f1`, modül açık olmalı; tüm yanıtlar ephemeral, kaynak + güncellik gösterir):

| Komut | Ne yapar |
|---|---|
| `/f1 next` | Sıradaki (veya süren) Grand Prix: tur, pist, yarış saati, tüm seanslar |
| `/f1 schedule [round]` | Hafta sonu programı (sprint/standart), seans durumları; canlı durum yoksa bunu açıkça belirtir |
| `/f1 results [session] [spoiler]` | Önbellekteki son sınıflandırma (`latest`, `race`, `sprint`, `qualifying`, `sprint-qualifying`, `fp1`–`fp3`) |
| `/f1 now` | Canlı sağlayıcıya göre süren seans; bilinmiyorsa "kullanılamıyor" (tahmin yok) |
| `/f1 standings drivers` / `/f1 standings constructors` | Sağlayıcının tablosu, "N. yarış sonrası", güncellik, bayatlık uyarısı |

Yönetici (`/f1-admin`, `default_member_permissions = ManageGuild` **ve** her servis çağrısında `Authorize.Require`; modül
kapalıyken de çalışır):

| Komut | Ne yapar |
|---|---|
| `/f1-admin configure channel channel:` | Bildirim kanalı (bu sunucuda olmalı; ViewChannel/SendMessages/EmbedLinks eksikse uyarı; başka kanala asla otomatik geçilmez) |
| `/f1-admin configure notifications …` | Antrenman/sprint/yarış başlangıç+sonuç, puan durumu; sıralama ve sprint sıralaması (vars. kapalı) |
| `/f1-admin configure role [ping_role] [ping_starts] [ping_results] [clear]` | İsteğe bağlı bildirim rolü (asla @everyone; yalnızca bu sunucunun rolü) |
| `/f1-admin configure spoilers enabled:` | Spoiler modu |
| `/f1-admin preview [card]` | TEST/DEMO sentetik kart, pingsiz (antrenman/yarış başladı, antrenman/yarış sonucu, yarış sonucu + puan durumu) |
| `/f1-admin status` | Sunucunun F1 ayarları, veri modu, canlı durum |
| `/f1-admin doctor` | İzinler, rol, her sağlayıcının güncelliği/son sonucu/geri çekilmesi, canlı bağlantı, gönderim istatistikleri |
| `/f1-admin pause` / `resume` | Duraklat / devam (kaçanlar gönderilmez) |

Ayrıca `/setup` sihirbazında F1 adımı: kanal → pingsiz önizleme → etkinleştir.

## Kurulum (yönetici)

1. `/setup` → Formula 1 adımı **veya** `/f1-admin configure channel` → `/f1-admin preview` → `/modules enable formula1`.
2. İsteğe bağlı: `/f1-admin configure role`, `/f1-admin configure spoilers`, `/f1-admin configure notifications`.
3. `/f1-admin doctor` ile sağlayıcı ve izin durumunu kontrol edin.

## Yapılandırma

`appsettings.json` yalnızca gizli olmayan varsayılanları içerir. Önemli anahtarlar (Railway'de `TOROSQUAD_` öneki ve `__`):

| Anahtar | Varsayılan | Not |
|---|---|---|
| `Formula1:Provider:Mode` | `Fixture` | `Live` = yalnızca gerçek ağ; hata durumunda fixture'a **dönüş yok** |
| `Formula1:Provider:Lifecycle` | `OpenF1` | `None` = başlangıç bildirimi tamamen kapalı |
| `Formula1:OpenF1:Username` / `Formula1:OpenF1:Password` | — (**secret**) | Yalnızca user-secrets veya ortam değişkeni; redaksiyon listesinde |
| `Formula1:OpenF1:RequestsPerMinute` / `RequestsPerSecond` | 30 / 3 | Sponsor hesabında 60 / 6 |
| `Formula1:Jolpica:UserAgent` | `TSQBot/0.1 (+https://github.com/Torokal/TSQ-Bot)` | Jolpica özel User-Agent ister |
| `Formula1:StartFreshMinutes` | 10 | Geç "başladı" kartı yok |
| `Formula1:ResultCorrectionHours` | 24 | Düzeltme penceresi |
| `Formula1:StandingsSettleWindowMinutes` | 180 | Puan durumu bekleme penceresi (≤ düzeltme penceresi) |
| `Formula1:ResultCatchUpHours` / `MaxCatchUpPerGuildPerRun` | 6 / 3 | Kesinti sonrası sınırlı telafi |

Tüm aralıklar başlangıçta doğrulanır (`Formula1Options.Validate`). OpenF1 kimlik bilgisinin **olmaması başlangıç hatası
değildir**: canlı yaşam döngüsü dürüstçe `NOT_CONFIGURED` olur, takvim/sonuç/puan durumu çalışmaya devam eder.

Yerel geliştirme (user-secrets, değerler asla depoya girmez):

```powershell
dotnet user-secrets set "Formula1:OpenF1:Username" "<hesap e-postası>" --project src\ToroSquad.Bot
dotnet user-secrets set "Formula1:OpenF1:Password" "<parola>" --project src\ToroSquad.Bot
```

### Fixture (TEST/DEMO) modu

`Formula1:Provider:Mode=Fixture` sentetik bir hafta sonunu ([`Fixtures/f1-demo-timeline.json`](../src/ToroSquad.Modules.Formula1/Fixtures/f1-demo-timeline.json):
"TSQ Test Grand Prix", "Test Driver 01" …) gerçek HTTP istemcileri ve ayrıştırıcılar üzerinden oynatır: olaylar ancak
zamanı gelince görünür, sonuçlar seans bittikten birkaç dakika sonra (öncesinde OpenF1 gibi 404), puan durumu sprint/yarış
sonrasında değişir. Zaman çizelgesi kalıcı bir anchor'a bağlıdır (yeniden başlatmada kaymaz). Fixture verisi yalnızca
yetkili test sunucularına gider, her kartta TEST/DEMO yazar ve gerçek bir kaynak adı taşımaz.

## Railway

Yeni servis gerekmez; F1 arka plan işi mevcut host içinde çalışır ve yalnızca uzun ömürlü host yolunda kayıtlıdır
(CLI fiilleri ve testler canlı dinleyici başlatmaz). Yeni ortam değişkenleri:

| Değişken | Gerekli mi | Değer |
|---|---|---|
| `TOROSQUAD_Formula1__Provider__Mode` | canlı veri için **evet** | `Live` |
| `TOROSQUAD_Formula1__OpenF1__Username` | canlı başlangıç bildirimleri için | OpenF1 hesabı (secret) |
| `TOROSQUAD_Formula1__OpenF1__Password` | canlı başlangıç bildirimleri için | OpenF1 parolası (secret) |
| `TOROSQUAD_Formula1__OpenF1__RequestsPerMinute`, `…__RequestsPerSecond` | yalnızca sponsor hesabında | `60`, `6` |

Mod `Fixture` kalırsa (varsayılan) üretimde hiçbir sağlayıcı çağrılmaz ve yetkili test sunucusu yoksa hiçbir F1 kartı
gönderilmez; modül zaten varsayılan kapalıdır.

## Sağlayıcı koşulları, atıf ve üretim sınırlamaları

| Sağlayıcı | Amaç | Canlı/tarihsel | Kimlik bilgisi | Sınırlama |
|---|---|---|---|---|
| **Jolpica F1** (`api.jolpi.ca/ergast/f1/`) | Takvim, sürücüler/takımlar puan durumu | yakın-gerçek zamanlı + tarihsel | Yok | TERMS.md (2025-08-27): **yalnızca ticari olmayan kullanım**, veri **CC BY-NC-SA 4.0**; ticari kullanım için admin@jolpi.ca. 4 istek/sn, 500 istek/saat; özel User-Agent zorunlu; gönüllü projedir, doğruluk/erişilebilirlik garantisi yok |
| **OpenF1** (`api.openf1.org`) | Canlı seans yaşam döngüsü (MQTT/REST), sonuçlar | canlı (**ücretli sponsor erişimi**) + tarihsel (2023+, ücretsiz) | Canlı için hesap (kullanıcı adı/parola → OAuth2 token) | Veri **CC BY-NC-SA 4.0**, "eğitim, kişisel proje, araştırma ve **ticari olmayan** hayran etkileşimi" için; resmî değildir, Formula 1 şirketleriyle bağlantısı yoktur. Ücretsiz: 3 istek/sn, 30 istek/dk; sponsor: 6/sn, 60/dk, 10 eşzamanlı MQTT |

- Her kartın altbilgisinde kaynak yazar ("Kaynak: OpenF1", puan durumu eklendiyse "· Puan durumu: Jolpica F1"); komutlar
  kaynağı ve güncelliği gösterir. Bot hiçbir yerde "resmî Formula 1 API" iddiasında bulunmaz. `/bot about` Jolpica F1,
  OpenF1 (CC BY-NC-SA 4.0) ve MQTTnet (MIT) atıflarını listeler.
- TSQ Bot'un mevcut kullanımı (tek sunuculu, ücretsiz, reklamsız hayran botu) ticari olmayan kullanım olarak
  değerlendirilmiştir. **İşletmeci sorumluluğu:** kullanım ticari hale gelirse (ücretli bot, reklam, sponsorluk) önce
  Jolpica (admin@jolpi.ca) ve OpenF1 ile lisans konuşulmalıdır. OpenF1 canlı erişimi ücretli bir sponsorluk gerektirir →
  bu bir **sahip onayı** konusudur; onaylanana kadar modül canlı başlangıç için dürüstçe `NOT_CONFIGURED` çalışır.
- formula1.com kazınmaz; resmî olmayan F1 Live Timing SignalR akışı kullanılmaz.

## Kalıcılık

Migration: `Formula1Module` (yalnızca eklemeli; esports tablolarına dokunmaz).

| Tablo | İçerik |
|---|---|
| `f1_guild_config` | Sunucu ayarları (kanal, pause, watermark, spoiler, rol, bildirim anahtarları, kanal sorunu) |
| `f1_session_snapshot` | Paylaşılan seans durumu: anahtarlar, program, yaşam döngüsü zaman damgaları, sağlayıcı eşlemeleri, baseline, sonuç/puan durumu iş akışı durumu |
| `f1_result_snapshot` | Son kanonik sınıflandırma (normalize JSON + hash, düzeltme sayısı) |
| `f1_standings_snapshot` | Farklı her puan tablosu (hash değişince yeni satır) |
| `f1_provider_state` | Sağlayıcı sağlığı (son deneme/başarı, sonuç, ardışık hata, ayrıntı) + önbelleklenmiş takvim |

Kullanıcıya ait veri tutulmaz; sunucu verisi saklama süresi sonunda silinir.

## Kapsam dışı (V1)

Canlı sıralama/tur tur mesajları, lastik stratejisi, telemetri, sektör karşılaştırması, tahmin, yapay zekâ özetleri,
fantezi puanları, bahis. Mimari şunları engellemez: sıralama bildirimleri (modelli, anahtarla açılır), pilot takip rolleri,
logolar, en hızlı tur, pit özetleri, güvenlik aracı / kırmızı bayrak bildirimleri (race_control zaten dinleniyor), alternatif
ticari sağlayıcılar.

## Durum

Uygulandı ve çevrimdışı test edildi (**TESTED_OFFLINE**). Canlı Discord ve canlı sağlayıcı ile doğrulanmadı
(**VERIFIED_LIVE değil**); OpenF1 canlı erişimi kimlik bilgisi/ücretli plan beklediği için **BLOCKED**.
