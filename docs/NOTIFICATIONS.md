# Bildirimler, filtreler, roller ve spoiler

## Bildirim türleri

| Tür (outbox) | Ne zaman | Ping | Not |
|---|---|---|---|
| `reminder` | Planlanan başlangıçtan `ReminderLeadMinutes` önce, başlangıç + 10 dk'ya kadar | hatırlatma rolleri | "Planlanan saattir; maçın başladığı doğrulanmamıştır." Saat değişirse **aynı mesaj** ping'siz düzenlenir |
| `started` | Sağlayıcı maçı **running** bildirdiğinde, daha önce planlandı/ertelendi olarak **görülmüşse** | hatırlatma rolleri | Saatin gelmesi başlama değildir. Liquipedia hiç "running" vermez → bu kart yalnızca PandaScore ile |
| `result` | Maç bitti (ilk görülme `FinishedObservedAt`) veya hükmen | sonuç rolleri | Düzeltmeler 24 saat aynı mesajı ping'siz düzenler; iptal (oynanmadı) sonuç değildir |
| `postponed` | Planlandı → ertelendi (yeni tarih bilinmiyor) | **yok** | "Yeni tarih henüz açıklanmadı." |
| `rescheduled-<yyyyMMddHHmm>` | Sağlayıcı `rescheduled=true` ve saat ≥ `RescheduleThresholdMinutes` (vars. 15) kaydı; ya da ertelenmiş maça yeni tarih | **yok** | Yeni saat başına bir kart; "Yeni Saat" sunucunun saat diliminde (vars. Europe/Istanbul) |
| `cancelled` | Planlandı/ertelendi/oynanıyor → iptal (hükmen değil) | **yok** | Sağlayıcı hatası asla iptal sayılmaz |

Başladı/ertelendi/saat değişti/iptal kartları **hatırlatma anahtarına** bağlıdır (`/esports-admin configure reminders`),
hem planlamada hem gönderimden hemen önce. Bu kartlar yalnızca geçiş **iki bilinen sağlayıcı durumu arasında gözlendiğinde**
ve `LifecycleFreshMinutes` (vars. 90) içinde gönderilir. `Unknown` durum son bilinen durumu silmez, geçiş üretmez.
Ayrıntı: [adr/0006-pandascore-lifecycle-and-match-links.md](adr/0006-pandascore-lifecycle-and-match-links.md).

## Kart düzeni (sade, BOT Greg benzeri)

```
Natus Vincere [0] - [2] Aurora          ← sonuçta skor başlıkta; diğer kartlarda "A vs B"
🏆 Aurora maçı kazandı                  ← tek durum satırı (▶️ / ⏸️ / 🕒 / ❌ / 🏳️ / ⏰)
[Maç Sayfası](https://www.hltv.org/…)   ← yalnızca güvenli bir bağlantı varsa
Etkinlik: StarLadder StarSeries Fall 2026   Format: bo3   (Yeni Saat: 25/09/2026 22:00)
Kaynak: PandaScore · <zaman damgası>
```

Yok: harita skorları, yayın listesi, aşama, "son veri" satırı, iç kimlikler, "yıldız" (güvenilir kaynak yok → DEFERRED).
Renkler: başladı/hatırlatma mavi, sonuç yeşil (kazanana göre değişmez), ertelendi/saat değişti amber, iptal kırmızı.

## Maç Sayfası bağlantısı

Öncelik: **doğrulanmış HLTV** → resmî organizatör sayfası → sağlayıcı sayfası (izinli host) → **hiç** (yer tutucu yok).
HLTV bağlantısı yalnızca `https://www.hltv.org/matches/<sayı>/<ad>` biçiminde ve güvenilir bir kaynaktan
(`Esports:VerifiedMatchLinks`) gelir; sayfa indirilmez, kimlik tahmin edilmez, HLTV olmayan bağlantı "HLTV" diye
etiketlenmez. PandaScore herkese açık maç sayfası vermediği için PandaScore maçlarında bağlantı ancak bu listeden gelir.
Demo kartları hiçbir yere bağlantı vermez.

## Yeniden başlatma / ilk bağlantı / geri dönüş politikası

- **İlk çalıştırma (bootstrap)**: bir sağlayıcının hiç anlık görüntüsü yokken görülen bitmiş/iptal maçlar `IsBaseline` olur → hiç duyurulmaz. Sağlayıcı bazındadır: Liquipedia → PandaScore geçişi yeni sağlayıcının geçmişini duyurmaz. İlk görülen maçta hiçbir yaşam döngüsü geçişi kaydedilmez.
- **Watermark**: modül etkinleştirildiğinde, `resume` edildiğinde, bildirim **kanalı değiştirildiğinde** veya hatırlatma/sonuç bildirimleri **yeniden açıldığında** ayarlanır (aksi hâlde son sonuçlar yeni kanala pinglerle tekrar gönderilirdi). Watermark'tan önce başlamış maçın hatırlatması,
  watermark'tan önce bitmiş maçın sonucu gönderilmez.
- **Kesinti sonrası (gap)**: son başarılı fetch 3 poll aralığından eskiyse sonuçlar yalnızca son 6 saatte başlamış maçlar
  için ve sunucu başına en fazla 5 adet gönderilir; limiti aşanlar outbox'a **süresi dolmuş** olarak kaydedilir ve sonraki normal poll'larda da gönderilmez.
- **Bayat veri** (vars. 30 dk) yeni bildirim üretmez.

## Sunucu filtreleri

| Boyut | Geçme kuralı |
|---|---|
| Takım | İki takımdan **en az biri** seçili |
| Turnuva | Maçın turnuva sayfası veya üst turnuva sayfası seçili |
| Seviye | Tier 1=S … 5=D (Liquipedia tier; PandaScore s/a/b/c/d aynı ölçeğe eşlenir, "unranked" = bilinmiyor) seçili; tier bilinmiyorsa **geçmez** |
| VRS Top-N | En az bir takım **güvenilir biçimde** eşleşmiş ve sıralaması ≤ N. VRS verisi yoksa **hiçbir maç geçmez** (fail-closed) ve doctor "VRS filtresi bildirimleri durdurdu" der |

Aynı boyuttaki değerler **VEYA**, etkin farklı boyutlar **VE** ile birleşir. Filtre yoksa tüm CS2 maçları duyurulur.
**Kişisel takipler sunucu filtresini genişletmez**: bildirim yalnızca sunucu filtresinden geçen maçlar için yapılır; takip
yalnızca hangi rollerin pingleneceğini ve `/esports matches mine` görünümünü etkiler.

### VRS takım eşleştirme <a id="vrs"></a>
1) ad/kısa ad birebir (büyük/küçük harf, aksan ve Türkçe İ/ı duyarsız) → 2) `Esports:TeamAliases` (takım anahtarı → VRS
adı) → 3) "team/esports/gaming/clan/club/gg" ayıklanmış ad **tek adaya** denk geliyorsa. Birden fazla aday = **belirsiz**
(filtrelerde kullanılmaz, `/esports team` adayları gösterir). VRS asla "yıldız puanı" olarak yeniden adlandırılmaz.

## Roller

- **Ping hedefi** (`roles map`): mevcut bir rol; @everyone ve managed/entegrasyon rolleri reddedilir. Rol bahsedilebilir
  değilse eşleştiren yöneticinin kendisinin de **Herkesten Bahset** izni olmalıdır (bot, yöneticinin pingleyemeyeceği rolü pinglemez). Kapsam: tüm duyurulan
  maçlar veya belirli takım; hatırlatma ve sonuç ping'i ayrı ayrı seçilir.
- **Self-service** (`roles selfservice`): ayrı onay. Rol **@everyone'ın sahip olmadığı hiçbir sunucu izni vermemeli**,
  **hiçbir kanalda "izin ver" üzerine yazması olmamalı** (özel kanal açamaz), managed/@everyone olmamalı, botun en yüksek
  rolünün altında olmalı; onaylayan yöneticinin en yüksek rolü de bu rolün üstünde olmalı. **Her rol verişinde yeniden
  değerlendirilir.**
- Takip → rol: istenen durum modeli. Üye rolü zaten taşıyorsa bot "önceden vardı" diye kaydeder ve **asla kaldırmaz**.
  Paylaşılan rol, ilgili son takip bitene kadar kaldırılmaz. Discord çağrısı öncesi `PendingAdd/PendingRemove` yazılır;
  başarısız ekleme `Failed` kalır ve üyenin bir sonraki etkileşiminde (mevcut rolleri bilinirken) yeniden denenir; arka
  planda yalnızca bot tarafından verildiği kesin rollerin kaldırılması yeniden denenir. Başarısız/belirsiz eklemeler hiçbir
  zaman Discord çağrısıyla geri alınmaz (rol bu arada elle verilmiş olabilir). Bot rol oluşturmaz ve "bahsedilebilir"
  ayarını değiştirmez.
- **Panel**: `tsq:esp:panel:<mappingId>` durumsuz düğmeler; tıklayanın sunucusuna bağlı arama (başka sunucunun eşleştirme
  kimliği işe yaramaz), yalnızca tıklayanın kendi takibi değişir.

## Mesaj güvenliği

- `allowed_mentions = { parse: [], roles: [izinli roller] }`; kullanıcı ve @everyone/@here ping'i asla. Önizleme,
  düzenleme ve tekrar denemeler ping atmaz.
- Sağlayıcı metinleri güvenilmez kabul edilir: mention sözdizimi etkisizleştirilir, markdown kaçışlanır, spoiler sınırları
  kırılamaz, metindeki `scheme://` tıklanabilir bağlantıya dönüşemez; bağlantılar yalnızca doğrulanmış **https** adreslere (Maç Sayfası kuralları yukarıda; sağlayıcı bağlantıları izinli host listesiyle).
- **Spoiler modu**: başlık yalnızca iki takımı kaynak sırasıyla yazar ("A vs B", skor yok); kazanan, skor ve hükmen bilgisi
  **tek, sabit düzenli** bir `||spoiler||` satırının içindedir; kazanan satırı ve harita ayrıntısı yoktur; renk kazanana göre
  değişmez; Maç Sayfası bağlantısı kalabilir; mesaj içeriği yalnızca rol ping'lerinden oluşur.
- Kaynak atfı: PandaScore verisinde "Kaynak: PandaScore" (PandaScore koşulları md. 6.4), Liquipedia verisinde
  "Kaynak: Liquipedia (CC BY-SA 3.0)"; demo veride `[TEST/DEMO]` başlık ve "sentetik demo verisi" footer'ı, bağlantı yok.

## Teslimat

Ayrıntı: [adr/0004-outbox-delivery.md](adr/0004-outbox-delivery.md). Özet: tekil mantıksal anahtar, tek transaction,
InFlight-önce-commit, belirsiz teslimatta marker ile sınırlı uzlaştırma, 429'da Retry-After, kalıcı hatalarda retry yok ve
kanal işaretlenir, gönderimden hemen önce kapı/pause kontrolü, exactly-once iddiası yok.
