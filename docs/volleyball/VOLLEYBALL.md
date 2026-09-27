# Voleybol modülü — Filenin Sultanları

Modül kimliği `volleyball` · sürüm 0.1.0 · **varsayılan kapalı** (kurulum + pingsiz önizleme + açık etkinleştirme gerekir).
Kod: [`src/ToroSquad.Modules.Volleyball`](../../src/ToroSquad.Modules.Volleyball). Yalnızca paylaşılan TSQ katmanlarına
(Core, Infrastructure, Discord) bağlıdır; esports/F1 modüllerine bağlı değildir ve onlar da buna bağlı değildir
(`VbArchitectureTests`).

**Kapsam: yalnızca Türkiye Kadın A Milli Takımı.** Erkek milli takımı, U17/U19/U21/U23/genç takımlar, kulüpler (Sultanlar
Ligi dahil) ve Türkiye'nin oynamadığı maçlar **kapsam dışıdır** ve kodla reddedilir. Başka takım seçtiren bir ayar yoktur.

**Temel ilke:** veri göndermeye yetecek kadar güvenilir değilse hiçbir şey gönderilmez. Biraz geç gelen doğru bildirim,
hızlı gelen yanlış skordan iyidir.

## Veri sağlayıcı

**FIVB VIS** web servisi (resmî, herkese açık veri, anahtar gerekmez). Seçim gerekçesi, gerçek 2026 doğrulaması, reddedilen
adaylar (API-Sports Free 2026'yı okuyamıyor, CEV/TVF yalnızca HTML, live-volleyball-api doğrulanamadı):
[PROVIDER_RESEARCH.md](PROVIDER_RESEARCH.md).

Akış (sağlayıcı değiştirmek bildirim kodunu değiştirmez):

```
FIVB VIS ──► FivbVisClient/Parser ──► VolleyballMatch (normalize) ──► TrackedTeamIdentity (filtre)
   (tek, paylaşılan fetch)                                                   │
                                          VolleyballWorkflow ◄───────────────┘   (SQLite: vb_match_snapshot)
                                          MatchProgress (durum makinesi)
                                                   │
                           VolleyballNotificationPlanner ──► INotificationOutbox ──► OutboxProcessor ──► Discord
```

| Yetenek (`VbCapabilities`) | FIVB VIS | Not |
|---|---|---|
| Fixtures / Results | ✅ | Tek istek: tarih penceresi + `TournamentGenders=W` + kıdemli turnuva türleri (sunucu tarafı filtre) |
| LiveMatchState / SetScores / CurrentSetScore | ✅ belgelenmiş, **canlı doğrulanmadı** | `NoMatches` + `Version` (artımlı); canlı gözlem: PROVIDER_RESEARCH.md |
| Venue | ✅ | salon + şehir |
| Broadcast | ❌ | VIS'te yok → kartta yayın satırı **hiç** gösterilmez (tahmin yok, eski maçtan taşınmaz) |
| TeamLogo | ❌ | Logo yok → Unicode bayraklar (🇹🇷 🇮🇹 …) |
| Postponed / Cancelled | ❌ | VIS'te bu durumlar yok. Alan modeli ve kartlar hazır; VIS ile bu kartlar **üretilmez** (tarih değişikliği hatırlatmayı günceller) |

Yeni sağlayıcı: `IVolleyballDataProvider` uygulaması + `VolleyballModule.ConfigureServices` içinde tek satır + `VbProviderName`.
**Fallback yok**: iki kaynak çelişirse rastgele seçim yapılamaz; doğrulanmış ikinci kaynak da yok. Canlı mod hata verince
fixture veriye **asla** dönülmez.

## Takım kimliği (filtre)

`TrackedTeamIdentity.TurkeyWomenSenior()` — ad eşleşmesi ("Turkey", "Turkey W", "Türkiye") **asla** tek başına kullanılmaz.

| Takım | Karar |
|---|---|
| Ülke `TUR` + kadın + senior + milli takım (VIS: turnuva `gender=1`, tür kıdemli izin listesinde, adda yaş işareti yok) | **Kabul** |
| Türkiye erkek, U17/U19/U21 kadın ("U19", "Girls", "Junior", "Youth" işaretleri), Türk kulübü | **Red** |
| Türkiye'nin olmadığı maç (ör. İtalya–Brezilya) | **Red** |
| Türkiye ama cinsiyet/seviye/tür bilinmiyor veya çelişkili; iki taraf da "Türkiye"; yapılandırılmış sağlayıcı kimliği çelişen veriyle | **Red** (belirsiz) + log `ambiguous_team_identity` + doctor uyarısı |
| Turnuva adı yok veya adında "test" / "club" geçiyor (VIS "VNL … (TEST ONLY)" turnuvaları çalıştırıyor) — türü kıdemli olsa bile | **Red** |

VIS'in takım numaraları turnuvaya özeldir (VNL'de 8632, EuroVolley'de 9283) — kalıcı kimlik değildir; bu yüzden varsayılan
`Volleyball:ProviderTeamIds` boştur. Kalıcı kimliği olan bir sağlayıcı eklenirse oraya yazılır; kimlik yine de çelişen
veriye karşı güvenilmez. VIS'in TEST turnuvası (`VNL 2026 - WOMEN (TEST ONLY)`), U17 kız şampiyonası ve erkek EuroVolley
maçları `TUR` kodunu paylaşır — gerçek kayıtlarla test edilir (`VbProviderContractTests`).

## Durum makinesi (`MatchProgress`, saf ve testli)

```
Scheduled ──(sağlayıcı: oynanıyor)──► Live ──(set sayısı artar + set puanları var)──► set N ──► … ──(sağlayıcı: bitti)──► Finished
Scheduled ──► Postponed (bir kez) ──► Scheduled (yeni tarih)         herhangi ──► Cancelled (bir kez, son)
```

- **İlk gözlem = baseline**: o andaki durum kaydedilir, hiçbir geçiş üretilmez (maç 2-1'deyken açılan bot başladı/set 1-2-3
  göndermez).
- "Başladı" yalnızca sağlayıcı "oynanıyor/bitti" dediğinde, **bir kez** (saat gelince değil).
- Set bitti = **tamamlanan set sayısı** artışı + o setin puanları; "skor alanı değişti" değildir. Her set numarası bir kez.
- Geriye gidiş (2-1 → 1-1 → 2-1), setin kazananının değişmesi, "canlı"nın "planlandı"ya dönmesi: **yok sayılır**, durum düşmez,
  log `provider_state_conflict`. Biten/iptal maç yeniden açılmaz.
- Doğrulama (FIVB, 5 setlik maç): her taraf 0..3 set, bitmiş maç yalnızca 3-0 / 3-1 / 3-2; 1–4. setler 25'te (5. set 15'te)
  2 farkla, hedef aşılırsa tam 2 farkla (26-24, 31-29) biter; set kazananları sayılarla uyumlu. Aksi hâlde gözlem **tamamen**
  reddedilir (log `provider_state_conflict`).
- **Bayat veri:** VIS için sağlayıcı güncelleme zamanı, maçın VIS `version` değerinin **son değiştiği an**dır. Canlı bir
  maçın sürümü `LiveStaleAfterMinutes` (20) boyunca değişmezse akış donmuş sayılır: hiçbir şey uygulanmaz, süreklilik kopar
  (donma sonrası gelen set duyurulmaz).
- **İki gözlemle teyit:** yeni bir geçiş (başladı, set, sonuç, ertelendi, iptal) ilk görüldüğünde yalnızca "bekliyor" olarak
  kaydedilir; **bir sonraki gözlem aynı durumu (veya daha ilerisini) gösterirse** uygulanır. Gerekçe gerçek gözlemdir: VIS
  2026-09-26'da canlı bir maç için art arda "1. set 15-14" ve "planlandı, skor yok" döndü. Tek bir ileri sıçrama asla
  duyurulmaz; bedeli bir yoklama aralığı (60 sn) gecikmedir. Teyit, ilk görülmenin zamanı ve sürekliliğiyle değerlendirilir
  (kesinti sonrası ilk gözlem teyitle "sürekli" hâle gelmez). Bekleyen gözlemi olan maç, pencere dışında olsa bile canlı
  yoklamayla hemen teyit edilir.
- **Canlı skor düzeltmesi:** geri gidiş normalde yok sayılır; sağlayıcı **aynı farklı geçmişi 3 kez art arda** bildirirse
  (skorer düzeltmesi, dalgalanma değil) durum sessizce düzeltilir — kart yok, duyurulmuş set numarası tekrar duyurulmaz,
  maç donmaz. "Planlandı"ya dönüş asla düzeltme sayılmaz.
- **Kararı veren set** (bir taraf 3 sete ulaştı) ayrı set kartı almaz: VIS "set bitti" durumundan sonra "bitti"ye geçer ve
  sonuç kartı o seti zaten gösterir. Set geçişi durum alanından değil set sayısı + set puanlarından türetilir (VIS'te durum
  alanının skorun gerisinde kaldığı gözlendi: "1. set oynanıyor" derken skor 1-0 ve 1. set 25-23).
- **Süreklilik**: iki gözlem arası `ContinuityMinutes`'tan (10 dk) uzunsa (yeniden başlatma, kesinti) arada olan "başladı"/set
  geçişleri kaydedilir ama **kart üretmez** (catch-up spam yok). Maç sonucu yine duyurulabilir (sınırlı).
- Aynı gözlemde birden çok geçiş: yalnızca en yenisi duyurulur (sonuç setleri, son set öncekileri ve "başladı"yı kapsar).

## Bildirimler (düşük spam)

| Tür (outbox `Kind`) | Ne zaman | Ping | Mantıksal anahtar |
|---|---|---|---|
| `reminder:<yyyyMMdd>` | Planlanan başlangıçtan `ReminderLeadMinutes` (15) önce, maç başlamadan, taze fikstürle (`FixtureStaleAfterHours`) | rol (varsa, `PingOnReminder`, vars. açık) | `live\|<guild>\|volleyball\|fivb:<maç>\|<kanal>\|reminder:20270603` |
| `started` | Sağlayıcı maçın oynandığını ilk kez bildirdi, süreklilik var, watermark sonrası, en fazla `LiveFreshMinutes` (15) önce | yok | `…\|started` |
| `set:<1..5>` | Set tamamlandı (yukarıdaki kurallar) | yok | `…\|set:3` |
| `final` | Sağlayıcı "bitti" + geçerli skor. İzlenmeden (kesinti) biten maçın sonucu "telafi" sayılır: başlangıcı son `ResultCatchUpHours + LiveTrailingHours` (6+5) saat içinde olmalı, çalıştırma başına sunucu başına en fazla `MaxCatchUpPerGuildPerRun` (2), ve maç bitmeden önceki son gözlem sunucunun watermark'ından **sonra** olmalı (modülü yeniden açan sunucuya geçmiş sonuç gitmez) | rol yalnızca `PingOnFinal` açıksa (vars. kapalı) | `…\|final` |
| `postponed` / `cancelled` | Sağlayıcı açıkça bildirirse, bir kez, yakın/gelecek maçlar için (VIS bunları vermez) | yok | `…\|postponed` |

- Tekillik outbox'ın **kalıcı** benzersiz anahtarıyla (SQLite) sağlanır — RAM'de değil; yeniden başlatma, tekrar gelen aynı
  veri veya iki poller turu ikinci mesaj üretemez. Aynı gün içinde saat değişirse hatırlatma **aynı mesajı pingsiz düzenler**;
  başka güne ertelenen maç yeni tarihte yeni bir hatırlatma alır. Sonuç düzeltmesi (VIS "Corrected") `FinalCorrectionHours`
  (6) içinde aynı mesajı pingsiz düzenler. Set/başladı kartları düzenlenmez (pencere dışı).
- **Yok:** her sayı, ace, blok, mola, teknik mola, oyuncu değişikliği, challenge, servis değişimi, istatistikler.
- Sunucu watermark'ı (kurulum, modülü açma, kanal değiştirme, bir türü yeniden açma, pause'dan dönme) öncesindeki hiçbir şey
  gönderilmez. Kapatmak veri silmez.
- Hatırlatma yalnızca **son başarılı fikstür listesinde hâlâ bulunan** maç için gider (VIS'ten silinen/yeniden numaralanan
  maça hatırlatma yok); saat "kesin değil"e dönerse eski başlangıç saati silinir.
- Telafi sınırını aşan sonuç "süresi dolmuş" olarak kaydedilir ve sonradan (pingle) canlanmaz.

### Kart düzeni (mobilde okunur, TSQ kompakt stili)

```
🇹🇷 Türkiye 2-1 İtalya 🇮🇹          ← başlık: her zaman Türkiye önce, skor Türkiye perspektifinden
🇹🇷 SET TÜRKİYE'NİN! · 3. set       ← tek durum satırı (Türkiye kaybederse: "3. seti İtalya aldı")
**1. Set: 25-21**                   ← Türkiye'nin kazandığı setler kalın
2. Set: 22-25
**3. Set: 25-19**
🏆 Women's Volleyball Nations League 2026
Kaynak: FIVB · <zaman>              ← kaynak yalnızca altbilgide
```

Hatırlatma: `⏳ Maç <t:…:R> başlıyor` + `🕒 <t:…:F>` + turnuva + salon (Discord zaman damgaları herkesin kendi saat
diliminde; arka uç UTC saklar, sabit +03 yoktur). Sonuç: `🏆 Filenin Sultanları kazandı!` / `Maç sona erdi · <rakip>
kazandı`. Renkler: hatırlatma mavi, canlı/Türkiye seti kırmızı, galibiyet altın, kayıp/nötr gri, erteleme amber, iptal kırmızı.

**Görsel/logo politikası:** TVF/FIVB/CEV/Volleyball World logosu kopyalanmaz, kazınmaz, hotlink edilmez. Varsayılan: Unicode
bayraklar (metin; lisans/host sorunu yok). Ortak `ThumbnailPolicy` (Core; esports takım logoları da onu kullanır) yalnızca
sağlayıcının izinli görsel host'undan HTTPS PNG/JPG/WebP/GIF kabul eder; voleybol için izinli host listesi **boştur** (VIS logo
vermez). Logo yoksa/uygunsuzsa kart aynen çalışır. TEST/DEMO kartlarında logo olmaz.

Sağlayıcı metinleri güvenilmezdir: mention/markdown/bağlantı etkisizleştirilir; bilinen ülke kodları yerelleştirilmiş adla
(İtalya, Sırbistan…), bilinmeyenler sağlayıcı adıyla ve bayraksız gösterilir.

## Yoklama (kota dostu, tek paylaşılan fetch)

| Veri | Sıklık |
|---|---|
| Fikstür/sonuç keşfi | 6 saatte bir; 48 saat içinde maç varsa saatte bir. Pencere: 3 gün geri, 60 gün ileri |
| Canlı durum | Yalnızca maç penceresinde (başlangıçtan 30 dk önce → "bitti" görülene kadar, en fazla 5 saat), **60 sn**'de bir, yalnızca takip edilen maç numaraları (`NoMatches`), artımlı `Version`; her 5. istek tam istek |
| Maç olmayan gün | ≈ 4 istek/gün |

volleyballworld.com'un kendi canlı widget'ı 30 sn aralıkla yoklar; 60 sn bu sağlayıcı için yeterli ve nazik bir aralıktır
(`LivePollSeconds`, 20–600). Hata: üstel geri çekilme + jitter, `Retry-After`'a uyulur; 429 satır içinde tekrar denenmez;
yapılandırma/yetki hataları 30 dk'da bir. Yerel bütçe: dakikada 10 istek (FIVB limit yayımlamıyor). **Hiçbir sunucuda modül
açık değilse sağlayıcı hiç çağrılmaz.** Sunucu sayısı istek sayısını artırmaz (testli: 10 sunucu = 1 sunucu kadar istek).
Komutlar sağlayıcıyı asla çağırmaz (yalnızca önbellek; mimari testli).

## Komutlar

| Komut | Kim | Ne yapar |
|---|---|---|
| `/volleyball next` | herkes (modül açık) | Sıradaki (veya sağlayıcıya göre süren) maç: rakip, saat, turnuva, salon, canlı set skoru, güncellik |
| `/volleyball schedule` | herkes (modül açık) | Yaklaşan maçlar ve son sonuçlar |
| `/volleyball-admin configure channel` | Sunucuyu Yönet | Bildirim kanalı |
| `/volleyball-admin configure notifications` | Sunucuyu Yönet | `match_reminder_15m`, `match_started`, `set_finished`, `match_finished`, `match_postponed_cancelled` |
| `/volleyball-admin configure role` | Sunucuyu Yönet | İsteğe bağlı rol (`ping_role`, asla @everyone), `ping_reminder`, `ping_final`, `clear` |
| `/volleyball-admin preview [card]` | Sunucuyu Yönet | Pingsiz TEST/DEMO kart (hatırlatma, başladı, set kazanıldı/kaybedildi, sonuç galibiyet/mağlubiyet, ertelendi, iptal) |
| `/volleyball-admin status` · `doctor` · `pause` · `resume` | Sunucuyu Yönet | Ayarlar · tanı · duraklat/devam (kaçanlar gönderilmez) |

`/setup` sihirbazında voleybol adımı: kanal → pingsiz önizleme → etkinleştir. `/bot status` voleybol fikstür/canlı sağlığı ve
sıradaki Türkiye maçını gösterir. `doctor`: modül, kanal/izinler, rol, kapsam, veri modu, sağlayıcı sağlığı (Healthy /
Degraded / RateLimited / Unauthorized / SchemaChanged / Unavailable / NotConfigured), son veri yaşı, ardışık hata, yetenekler,
kimlik reddi sayısı, son 24 saatteki tutarsız veri sayısı, sıradaki maç, gönderim istatistiği. Secret hiçbir yerde gösterilmez.

## Yapılandırma

`appsettings.json` yalnızca gizli olmayan varsayılanları içerir (Railway'de `TOROSQUAD_` öneki ve `__`).

| Anahtar | Varsayılan | Not |
|---|---|---|
| `Volleyball:Provider:Mode` | `Fixture` | `Live` = yalnızca gerçek ağ; hata durumunda fixture'a **dönüş yok** |
| `Volleyball:Provider:Name` | `FivbVis` | `None` = sağlayıcı yok (dürüstçe NOT_CONFIGURED) |
| `Volleyball:Fivb:AppId` | boş | FIVB uygulama kimliği (isteğe bağlı; yalnızca başlıkta, loglanmaz) |
| `Volleyball:Fivb:RequestsPerMinute` | 10 | Yerel bütçe |
| `Volleyball:LivePollSeconds` | 60 | 20–600 |
| `Volleyball:ContinuityMinutes` | 10 | ≥ 3 canlı yoklama |
| `Volleyball:ReminderLeadMinutes` | 15 | `LiveLeadMinutes` (30) bundan büyük olmalı |
| `Volleyball:LiveFreshMinutes` | 15 | Geç "başladı"/set kartı yok |
| `Volleyball:ResultCatchUpHours` / `MaxCatchUpPerGuildPerRun` | 6 / 2 | Kesinti sonrası sınırlı telafi |
| `Volleyball:FinalCorrectionHours` | 6 | Sonuç düzeltme penceresi |

Tüm aralıklar başlangıçta doğrulanır (`VolleyballOptions.Validate`, `FivbVisOptions.Problem`). Kıdemli turnuva türü izin
listesi bilerek **yapılandırılamaz** (güvenlik açısından kritik; `FivbVisParser.SeniorTournamentTypes`).

Railway için canlı veri: `TOROSQUAD_Volleyball__Provider__Mode=Live` (secret gerekmez). İsteğe bağlı:
`TOROSQUAD_Volleyball__Fivb__AppId`. Yeni servis gerekmez; arka plan işi mevcut host içinde çalışır.

### Fixture (TEST/DEMO) modu

Varsayılan mod. Sentetik "TSQ Demo Cup" maçı (Türkiye – "Testland"; kalıcı anchor'dan +20 dk başlangıç, setler +45/+70/+95,
+120'de 3-1) **gerçek VIS istemcisi ve ayrıştırıcısı** üzerinden oynatılır; aynı ülke kodlu bir U19 tuzak maçı da vardır ve
reddedilir. Demo verisi yalnızca yetkili test sunucularına gider, her kartta TEST/DEMO yazar, gerçek kaynak adı taşımaz.
Demo satırları ayrı sağlayıcı kimliğiyle (`fivb-demo`) saklanır: `Live`'a geçildiğinde planlayıcı, komutlar ve canlı yoklama
yalnızca `fivb` satırlarını görür — demo maçı gerçek FIVB verisi gibi asla görünmez. Demo zaman çizelgesi 24 saatte bir
yeni maç numarasıyla yenilenir.

## Kalıcılık

Migration `VolleyballModule` (yalnızca eklemeli).

| Tablo | İçerik |
|---|---|
| `vb_guild_config` | Sunucu ayarları (kanal, pause, watermark, rol, ping anahtarları, bildirim anahtarları, kanal sorunu) |
| `vb_match_snapshot` | Paylaşılan maç durumu: kimlik, meta veri, set sayıları/puanları, geçiş bayrakları, kaydedilen geçişler (`EventsJson`), baseline, son gözlem, son sorun |
| `vb_provider_state` | Sağlayıcı sağlığı (fikstür/canlı: son deneme/başarı, sonuç, ardışık hata) + fixture anchor |

Kullanıcıya ait veri tutulmaz; sunucu ayarları saklama süresi sonunda silinir.

## Gözlemlenebilirlik (yapılandırılmış log)

`volleyball match_discovered`, `baseline_established`, `match_started`, `set_completed`, `match_finished`, `match_postponed`,
`match_cancelled`, `notification_enqueued`, `notification_deduplicated` (debug), `provider_rate_limited`,
`provider_schema_error`, `provider_state_conflict`, `provider_state_corrected`, `provider_stale`, `ambiguous_team_identity`, `continuity gap`. Her
yoklama INFO seviyesinde loglanmaz.

## Bilinen sınırlamalar

- VIS'te **hazırlık maçları** (ör. Fransa, Antalya, Ağustos 2026) ve Akdeniz Oyunları yok → duyurulamaz.
- VIS'in canlı durumu Türkiye maçında **henüz gözlenmedi** (sıradaki maç 2027) → "başladı"/set kartları **NOT_VERIFIED**;
  canlı değişiklik yoksa kart da yoktur (sonuç yine gelir).
- Yayın bilgisi yok (VIS'te alan yok; TVF duyuruları makine tarafından okunabilir değil).
- VIS'in yayımlanmış kullanım koşulları/limiti yok; ticari kullanım için FIVB yazılı izni gerekir (işletmeci sorumluluğu).
- Erteleme/iptal VIS'te durum olarak yok.

## Durum

Uygulandı ve çevrimdışı test edildi (**TESTED_OFFLINE**). FIVB VIS'ten gerçek 2026 Türkiye Kadın verisi okunması
**VERIFIED_LIVE** (2026-09-26, anonim istek; kayıtlar test fixture'ı). Gerçek Discord teslimi ve bir Türkiye maçında canlı
akış **doğrulanmadı** (sıradaki maç VIS'te henüz yok) → canlı bildirimler **DEFERRED**.
