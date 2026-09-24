# Bildirimler, filtreler, roller ve spoiler

## Bildirim türleri

| Tür | Ne zaman | Not |
|---|---|---|
| `reminder` | Planlanan başlangıçtan `ReminderLeadMinutes` (vars. 15; kurulumda seçilebilir) önce, başlangıç + 10 dk'ya kadar | "Planlanan saat hatırlatması" — maçın başladığını iddia etmez. Başlangıç saati değişirse **aynı mesaj** ping'siz düzenlenir: "Başlangıç saati güncellendi (önceki: …)". Tahmini saatli (dateexact=0) maçlar için hatırlatma yok |
| `result` | Kaynak `finished=1` bildirdiğinde (ilk görülme = `FinishedObservedAt`) | Skor düzeltmeleri 24 saat boyunca aynı mesajı ping'siz düzenler. Oynanmadı (not played) maçlar duyurulmaz |
| "canlı" | **Yok** | Liquipedia doğrulanmış canlı durum sunmuyor (docs/PROVIDERS.md) |

## Yeniden başlatma / ilk bağlantı / geri dönüş politikası

- **İlk çalıştırma (bootstrap)**: snapshot tablosu boşken görülen bitmiş maçlar `IsBaseline` olur → hiç duyurulmaz.
- **Watermark**: modül etkinleştirildiğinde veya `resume` edildiğinde ayarlanır. Watermark'tan önce başlamış maçın hatırlatması,
  watermark'tan önce bitmiş maçın sonucu gönderilmez.
- **Kesinti sonrası (gap)**: son başarılı fetch 3 poll aralığından eskiyse sonuçlar yalnızca son 6 saatte başlamış maçlar
  için ve sunucu başına poll başına en fazla 5 adet gönderilir.
- **Bayat veri** (vars. 30 dk) yeni bildirim üretmez.

## Sunucu filtreleri

| Boyut | Geçme kuralı |
|---|---|
| Takım | İki takımdan **en az biri** seçili |
| Turnuva | Maçın turnuva sayfası veya üst turnuva sayfası seçili |
| Seviye | Liquipedia tier (1=S … 5=D) seçili; tier bilinmiyorsa **geçmez** |
| VRS Top-N | En az bir takım **güvenilir biçimde** eşleşmiş ve sıralaması ≤ N. VRS verisi yoksa **hiçbir maç geçmez** (fail-closed) ve doctor "VRS filtresi bildirimleri durdurdu" der |

Aynı boyuttaki değerler **VEYA**, etkin farklı boyutlar **VE** ile birleşir. Filtre yoksa tüm CS2 maçları duyurulur.
**Kişisel takipler sunucu filtresini genişletmez**: bildirim yalnızca sunucu filtresinden geçen maçlar için yapılır; takip
yalnızca hangi rollerin pingleneceğini ve `/esports matches mine` görünümünü etkiler.

### VRS takım eşleştirme <a id="vrs"></a>
1) ad/kısa ad birebir (büyük/küçük harf, aksan ve Türkçe İ/ı duyarsız) → 2) `Esports:TeamAliases` (takım anahtarı → VRS
adı) → 3) "team/esports/gaming/clan/club/gg" ayıklanmış ad **tek adaya** denk geliyorsa. Birden fazla aday = **belirsiz**
(filtrelerde kullanılmaz, `/esports team` adayları gösterir). VRS asla "yıldız puanı" olarak yeniden adlandırılmaz.

## Roller

- **Ping hedefi** (`roles map`): mevcut bir rol; @everyone ve managed/entegrasyon rolleri reddedilir. Kapsam: tüm duyurulan
  maçlar veya belirli takım; hatırlatma ve sonuç ping'i ayrı ayrı seçilir.
- **Self-service** (`roles selfservice`): ayrı onay. Rol **@everyone'ın sahip olmadığı hiçbir sunucu izni vermemeli**,
  **hiçbir kanalda "izin ver" üzerine yazması olmamalı** (özel kanal açamaz), managed/@everyone olmamalı, botun en yüksek
  rolünün altında olmalı; onaylayan yöneticinin en yüksek rolü de bu rolün üstünde olmalı. **Her rol verişinde yeniden
  değerlendirilir.**
- Takip → rol: istenen durum modeli. Üye rolü zaten taşıyorsa bot "önceden vardı" diye kaydeder ve **asla kaldırmaz**.
  Paylaşılan rol, ilgili son takip bitene kadar kaldırılmaz. Discord çağrısı öncesi `PendingAdd/PendingRemove` yazılır;
  başarısızlık `Failed` olarak kalır ve arka planda sınırlı sayıda yeniden denenir. Bot rol oluşturmaz ve "bahsedilebilir"
  ayarını değiştirmez.
- **Panel**: `tsq:esp:panel:<mappingId>` durumsuz düğmeler; tıklayanın sunucusuna bağlı arama (başka sunucunun eşleştirme
  kimliği işe yaramaz), yalnızca tıklayanın kendi takibi değişir.

## Mesaj güvenliği

- `allowed_mentions = { parse: [], roles: [izinli roller] }`; kullanıcı ve @everyone/@here ping'i asla. Önizleme,
  düzenleme ve tekrar denemeler ping atmaz.
- Sağlayıcı metinleri güvenilmez kabul edilir: mention sözdizimi etkisizleştirilir, markdown kaçışlanır, spoiler sınırları
  kırılamaz; bağlantılar yalnızca izinli https host'larına (liquipedia.net, twitch.tv, youtube.com, kick.com, github.com).
- **Spoiler modu**: skor, kazanan ve harita sonuçları yalnızca `||…||` içinde; başlık iki takımı kaynak sırasıyla yazar;
  renk kazanana göre değişmez; mesaj içeriği yalnızca rol ping'lerinden oluşur.
- Her mesajda kaynak bağlantısı, "Kaynak: Liquipedia (CC BY-SA 3.0)" ve "Son veri değişikliği" zamanı; demo veride
  `[TEST/DEMO]` başlık ve footer etiketi.

## Teslimat

Ayrıntı: [adr/0004-outbox-delivery.md](adr/0004-outbox-delivery.md). Özet: tekil mantıksal anahtar, tek transaction,
InFlight-önce-commit, belirsiz teslimatta marker ile sınırlı uzlaştırma, 429'da Retry-After, kalıcı hatalarda retry yok ve
kanal işaretlenir, gönderimden hemen önce kapı/pause kontrolü, exactly-once iddiası yok.
