# TSQ Öngörü

Sunucu üyelerinin tamamen **sanal TSQ Coin** ile topluluk öngörülerine katıldığı, **sabit oranlı** bir eğlence modülü.
Gerçek para, coin satışı, para çekme, üyeler arası transfer, gerçek değeri olan ödül, mağaza, kupon/parlay, cash-out, dinamik
oran, otomatik maç sonucu ve otomatik öngörü oluşturma **yoktur**. Sonuçlar yetkili tarafından elle belirlenir; ödeme ve
iadeler otomatik ve atomiktir. Spora bağlı değildir: başlık ve sonuçlar serbesttir (futbol, CS2, F1, oyun etkinlikleri…).

Her sunucuda varsayılan kapalıdır: `/modules enable predictions` (listede **TSQ Öngörü**). Modül kimliği `predictions`,
proje `ToroSquad.Modules.Predictions`, komut kökü `/ongoru`.

## Final komut yapısı

| Komut | Kanal | Kim | Görünürlük |
|---|---|---|---|
| `/ongoru yarat` | öngörü kanalı | yaratıcı rolü (Administrator tek başına yetmez) | form + özel önizleme; kart herkese açık |
| `/ongoru cuzdan` | komut kanalı | herkes | özel |
| `/ongoru gunluk` | komut kanalı | herkes | özel |
| `/ongoru tahminlerim` | komut kanalı | herkes | özel (10'ar sayfa, ◀ ▶) |
| `/ongoru liderlik` | komut kanalı | herkes | herkese açık, ping'siz |
| `/ongoru turnuva durum` | komut kanalı | herkes | herkese açık, ping'siz |
| `/ongoru turnuva bitir` | komut kanalı | **yalnızca Administrator veya sunucu sahibi** | özel önizleme + onay; kapanış duyurusu herkese açık |

Öngörü **yönetimi slash komutla değil, kartın butonlarıyladır**: 🎯 Tahmin Yap · 🔒 Kilitle · ✅ Sonuçlandır ·
↩️ İptal / İade. `/ongoru kilitle`, `/ongoru sonuclandir`, `/ongoru iptal`, ayrı bir `/ongoru-admin` grubu ve herhangi bir
coin sıfırlama komutu (`reset`, `sifirla`…) **yoktur** (manifest testleriyle doğrulanır).

`/ongoru` grubu `default_member_permissions` taşımaz (aksi hâlde üyeler cüzdan/günlük/liderlik komutlarını göremezdi); her
alt komut, form gönderimi, önizleme onayı ve buton tıklaması yetkiyi sunucu tarafında **yeniden** denetler. Discord her
etkileşimde üyenin o anki rollerini ve izinlerini gönderir; form açıldıktan sonra kaldırılan rol hemen görülür.

## Kanallar, rol ve yapılandırma

Tüm değerler tek yapılandırma bölümünden okunur (`Predictions`, ortam değişkeni önekiyle `TOROSQUAD_Predictions__…`);
başlangıçta doğrulanır, hatalı değer botu açık bir CONFIG satırıyla durdurur.

| Ayar | Varsayılan (ana sunucu) | Anlamı |
|---|---|---|
| `Predictions:ChannelId` | `1048525775919390840` | Öngörülerin oluşturulduğu ve kartların bulunduğu **tek** kanal |
| `Predictions:CommandsChannelId` | `689814679056547857` | Üye komutlarının ve `/ongoru turnuva bitir`'in **tek** kanalı; kapanış duyurusu buraya gider |
| `Predictions:CreatorRoleId` | `1233057768408350741` | Öngörü yaratabilen rol (yalnızca yaratma ve kişinin kendi öngörülerini yönetme) |
| `Predictions:InitialBalanceCoins` | `1000` | Her turnuvanın başlangıç bakiyesi |
| `Predictions:DailyMinCoins` / `DailyMaxCoins` | `10` / `100` | Günlük ödül aralığı (iki uç dahil) |
| `Predictions:DefaultOdds` | `2.00` | Oranı yazılmayan seçeneğin oranı (1.01–1000.00, en fazla iki ondalık) |
| `Predictions:MaxOutcomes` | `25` | Öngörü başına en fazla seçenek (2–25) |
| `Predictions:TerminalCardRetentionHours` | `12` | Sonuçlanmış/iptal edilmiş öngörünün kartı bu kadar saat sonra kanaldan kaldırılır (1–168; yalnız Discord mesajı) |
| `Predictions:WeeklyLeaderboard:Enabled` | `true` | Haftalık otomatik liderlik paylaşımı |
| `Predictions:WeeklyLeaderboard:DayOfWeek` | `Sunday` | Gün (`Monday`–`Sunday`) |
| `Predictions:WeeklyLeaderboard:LocalTime` | `20:00` | Yerel saat (`HH:mm`) |
| `Predictions:WeeklyLeaderboard:TimeZone` | `Europe/Istanbul` | IANA saat dilimi (sunucunun saat dilimi kullanılmaz) |
| `Predictions:WeeklyLeaderboard:CatchUpHours` | `12` | Bot o anda kapalıysa en fazla bu kadar saat geç gönderilir (1–72) |

Örnek (Railway ortam değişkeni): `TOROSQUAD_Predictions__WeeklyLeaderboard__LocalTime=21:00`,
`TOROSQUAD_Predictions__TerminalCardRetentionHours=12`. Saat/gün değişikliği için yeni komut veya yönetim ekranı yoktur.

Kanal kuralları **yöneticiler için de** geçerlidir ve kanal ID'si **birebir** eşleşmelidir: DM, başka sunucu, başka kanal ve
izinli kanalın thread'leri reddedilir. Yanlış kanal cevabı yalnızca kullanana görünür, ör. "Bu komutu yalnızca
<#689814679056547857> kanalında kullanabilirsiniz." Reddedilen hiçbir çağrı cüzdan oluşturmaz, coin vermez, kayıt yazmaz veya
herkese açık mesaj göndermez. Kanal ve rol bağlantıları tıklanabilir ama kimseyi etiketlemez.

## Yetkiler

| İşlem | Kim |
|---|---|
| Öngörü **yaratma** | Yalnızca yaratıcı rolü. Administrator yetkisi bu rol olmadan yaratma hakkı vermez |
| Kart butonlarıyla **yönetme** (🔒 ✅ ↩️) | Kendi öngörüsünü: yaratıcı, rolü **hâlâ** varsa. Tüm öngörüleri: Administrator veya sunucu sahibi. Başka yaratıcının öngörüsü: hayır. Sunucuyu Yönet yetmez |
| **Otomatik futbol** öngörülerini yönetme | Yalnızca Administrator veya sunucu sahibi (insan yaratıcısı yok; yaratıcı rolü yetmez) — [AUTO_FOOTBALL.md](AUTO_FOOTBALL.md) |
| **Turnuva bitirme** (`/ongoru turnuva bitir`) | Yalnızca Administrator veya sunucu sahibi (yaratıcı rolü de, Sunucuyu Yönet de yetmez) |
| 🎯 Tahmin Yap, cüzdan, günlük ödül, tahminlerim, liderlik, turnuva durumu | Herkes (botlar hariç) |

Kartın butonları herkese görünür (Discord public mesajda bileşenleri kişiye göre gizleyemez); yetkisiz biri yönetim
butonuna basarsa hiçbir şey değişmez ve yalnızca ona "Bu öngörüyü yönetme yetkiniz yok." yazılır.

## Kart

```
🟢 Katılım Açık
## Galatasaray - Fenerbahçe Maç Sonucu Ne Olur?

1️⃣ Galatasaray Kazanır
Oran: 1.10

2️⃣ Beraberlik
Oran: 2.30

3️⃣ Fenerbahçe Kazanır
Oran: 3.10

👥 Katılım: 14 katılımcı · 🪙 2450 TSQ Coin yatırıldı      ⏳ Kilitlenme: <32 dakika sonra> <tam tarih>
📜 Sonuçlandırma kuralı (varsa)
[🎯 Tahmin Yap]
[🔒 Kilitle] [✅ Sonuçlandır] [↩️ İptal / İade]
TSQ Öngörü #12 · Oluşturan: Toro · Sabit oran
```

- Public kartta **turnuva gösterilmez** (adı, numarası, ID'si yok). Öngörü veride turnuvasına bağlı kalır: ödeme o turnuvanın
  cüzdanına yazılır, liderlik ve turnuva kapanışı onu kullanır; `/ongoru turnuva durum|bitir`, kapanış duyurusu,
  `/ongoru cuzdan` ve `/ongoru tahminlerim` turnuvayı göstermeye devam eder.

- Kartın en büyük metni öngörünün **başlığıdır**; seçenekler 1️⃣…🔟, sonra **11.** … ile numaralanır. Uzun/çok biçimli
  metinde düzen kod bloğuna, o da sığmazsa alanlara geçer — hiçbir şey kesilmez; hiçbir durumda sığmayacak bir öngörü formda
  reddedilir.
- **Açık:** `[🎯 Tahmin Yap]` / `[🔒 Kilitle] [✅ Sonuçlandır] [↩️ İptal / İade]`.
- **Kilitli** (🔒 Katılım Kapandı): Tahmin Yap devre dışı; `[✅ Sonuçlandır] [↩️ İptal / İade]`.
- **Sonuçlandı** (✅): kazanan 🏆 ile işaretli; "🏆 Galatasaray Kazanır — 1.10", kazanan sayısı, toplam ödeme ve toplam katılım;
  **buton yok**.
- **İptal** (⚠️ Öngörü İptal Edildi): iptal nedeni ve tam iade; **buton yok**.
- Kilitlenme zamanı Discord zaman damgasıdır (her saniye düzenleme yok). Tüm gönderim/düzenlemeler `allowed_mentions` = boş.
- Kart butonlarının custom id'si **yalnızca öngörü numarasını** taşır (`tsq:pred:enter:12`, `…:lock:12`, `…:settle:12`,
  `…:cancel:12`); restart sonrası da çalışır. Her tıklamada öngörü veritabanından yeniden okunur ve **sunucu + kanal + kartın
  kendi mesajı** doğrulanır: başka sunucudan, başka kanaldan, başka bir mesaja kopyalanmış butondan veya elle yazılmış bir
  numarayla gelen tıklama "bulunamadı" görünür.
- Kartın 👥 katılımcı ve 🪙 toplam sayıları yalnızca **aktif** tahminleri sayar (geri çekilen düşer; tutar değişikliği farkı
  kadar, sonuç değişikliği hiç değiştirmez). Katılım, değişiklik ve geri çekme kartta birleştirilerek güncellenir (son 10 sn
  içinde düzenlendiyse bir sonraki worker turuna, ~10 sn, kalır); hiçbirinde yeni mesaj veya DM yok. Yönetim butonlarına basmak herkese açık mesaj üretmez; onaylar ve hatalar özeldir.

## 🎯 Tahmin Yap (katılım), değiştirme ve geri çekme

```
🎯 Tahmin Yap → form (Sonuç: seçim menüsü · Yatırılacak TSQ Coin: metin) → Submit
  → tahmin DOĞRUDAN kaydedilir (atomik) → özel makbuz [✏️ Tahminimi Değiştir] [↩️ Tahminimi Geri Çek]
```

- **İkinci bir onay yoktur:** formun Submit'i son karardır. Formu açmak, sonuç seçmek veya formu kapatmak coin hareketi
  yapmaz; coin yalnızca geçerli bir Submit'in yazma işleminde düşer. Submit tek yazma işleminde denetler: sunucu, kanal, modül,
  aktif turnuva, öngörü durumu, kilit zamanı (`şimdi ≥ kilit` ise ret — worker kartı güncellememiş olsa bile), sonucun bu
  öngörüye ait olması (oran istemciden değil, kayıtlı sonuçtan alınır), mevcut aktif tahmin ve bakiye; sonra katılımı, coin
  düşümünü, hareket kaydını ve kart sayılarını birlikte yazar. Geçersiz Submit hiçbir şeyi değiştirmez.
- Özel makbuz: "✅ Tahminin kaydedildi!", 🎯 sonuç, 📈 oran, 🪙 yatırılan, 💰 olası toplam dönüş, 👛 kullanılabilir bakiye ve
  "Öngörü kilitlenene kadar tahminini değiştirebilir veya geri çekebilirsin." Kanala kimin ne yatırdığını söyleyen mesaj gitmez.
- Özel mesaj kaybolsa da çıkmaz yok: karttaki **🎯 Tahmin Yap** her zaman giriş noktasıdır; aktif tahmini olan üye yeni form
  yerine veritabanından okunan **🎯 Mevcut Tahminin**'i (sonuç, oran, yatırılan) ✏️ / ↩️ butonlarıyla görür. Aynı form iki kez
  gelirse (veya ikinci bir form açıldıysa) ikinci Submit hiçbir şeyi değiştirmez ve mevcut tahmini gösterir.
- **✏️ Tahminimi Değiştir** (yalnızca öngörü açıkken): form mevcut sonuç ve tutarla dolu açılır; Submit değişikliğin kendisidir.
  Sonuç ve tutar değişebilir. Yalnızca **fark** hareket eder: 100 → 150 yalnız 50 düşer, 150 → 100 50 iade eder, aynı tutarla
  başka sonuç bakiyeyi değiştirmez. Sonuç değişirse oran snapshot'ı yeni sonucun kayıtlı oranı olur. Fark bakiyeyi aşarsa
  ("Bu değişiklik için 50 TSQ Coin daha gerekiyor, ancak kullanılabilir bakiyen 20 TSQ Coin.") **hiçbir şey** değişmez (sonuç
  bile).
- **↩️ Tahminimi Geri Çek** (yalnızca öngörü açıkken): ek "emin misin?" sorusu yok; yatırılan **ana para** (olası kazanç değil)
  tamamen iade edilir ("↩️ Tahminin geri çekildi. 🪙 100 TSQ Coin bakiyene iade edildi. 👛 Yeni bakiyen: …",
  `[🎯 Tekrar Tahmin Yap]`). Geri çekilen tahmin geçmişte kalır (`Withdrawn`): kart sayılarına, ödemeye, iptal iadesine, doğru/
  yanlış sayısına ve bekleyen coin'e girmez, ama üyenin bu turnuvada tahmin yaptığını gösterir (liderlik uygunluğu kalır).
  Öngörü açıksa üye yeniden tahmin yapabilir (aynı satır `Withdrawn → Pending`); öngörü/üye başına **en fazla bir aktif tahmin**
  (veritabanında öngörü/üye başına tek satır).
- Öngörü **kilitlendiğinde, sonuçlandığında, iptal edildiğinde** veya kilit zamanı geldiğinde (worker çalışmamış olsa bile)
  değiştirme ve geri çekme reddedilir: "Bu öngörü artık kilitlendiği için tahminini değiştiremez veya geri çekemezsin."
- Kilitleme, sonuçlandırma ve iptal ile yarışan değiştirme/geri çekme aynı `BEGIN IMMEDIATE` yazma kilidinde sıralanır: önce
  commit olan geçerlidir (değişiklik/geri çekme önce ise tamamlanır, sonra kilit/sonuç/iptal onu görür; aksi hâlde reddedilir).
  Aynı tutar hem ödeme hem geri çekme iadesi, hem iptal iadesi hem geri çekme iadesi alamaz.
- Hareket kayıtları ayrı türlerdedir ve her işlem bir kez yazılır: `stake:e3` (ilk katılım), `stake-up:e3:r1` (artış),
  `stake-down:e3:r2` (azalış iadesi), `withdraw:e3:r3` (geri çekme iadesi), `stake:e3:r4` (geri çekmeden sonra yeniden
  katılım), `payout:e3`, `refund:e3`.
- En az 1 TSQ Coin, en fazla iki ondalık (`100`, `12.5`, `12,50`); bakiye üstü ve negatif bakiye yok; botlar katılamaz.

## Kart yönetimi

**🔒 Kilitle** → yalnızca basana görünen onay: "Bu öngörüyü kilitlemek istediğinize emin misiniz? Kilitledikten sonra yeni
tahmin kabul edilmeyecek ve V1'de tekrar açılamayacak." `[🔒 Kilitle] [Vazgeç]`. Onayda sunucu, öngörü, aktif turnuva, durum ve
yetki yeniden denetlenir; `Open → Locked` bir kez olur (aynı anda iki onay: biri kilitler, diğeri "zaten kilitli"). Kart aynı
mesajda "🔒 Katılım Kapandı" olur. V1'de `Locked → Open` yoktur.

**✅ Sonuçlandır** → özel sonuç seçimi (tek seçimlik menü, "Galatasaray Kazanır — 1.10") → özel önizleme: "“Galatasaray Kazanır”
(1.10) kazanan sonuç olarak işaretlenecek.", kazanan tahmin, kaybeden tahmin, toplam ödeme, "Bu işlem geri alınamaz."
`[✅ Sonuçlandır] [Vazgeç]`. Onayda her şey yeniden denetlenir (terminal durumda değil mi, sonuç bu öngörüye mi ait, aktif
turnuva aynı mı, yetki hâlâ geçerli mi); durum, kazanan, ödemeler, cüzdanlar, hareketler ve istatistikler tek işlemde
commit olur. Açık öngörü onayla birlikte kapanır. Kazananı kimsenin seçmemiş olması geçerli sonuçtur (ödeme de iade de yok).
Sonuçlanmış öngörüde tekrar Sonuçlandır: "Bu öngörü zaten sonuçlandırılmış." — **ikinci ödeme asla yapılmaz** (onay tekrar
gönderilse bile).

**↩️ İptal / İade** → iptal nedeni formu (3–300 karakter; etkisizleştirilir, ping atmaz) → özel önizleme: "Bu öngörü iptal
edilecek. Toplam 1250 TSQ Coin yatırımı oyunculara tam olarak iade edilecek. Bu işlem geri alınamaz."
`[↩️ İptal Et ve İade Et] [Vazgeç]`. Onayda yetki, durum ve turnuva yeniden denetlenir; her bekleyen katılımın **yatırılan
tutarı** (olası kazancı değil: 100 @ 3.00 iptalde 100 döner) tek işlemde, tam ve bir kez iade edilir. Kart aynı mesajda
"⚠️ Öngörü İptal Edildi" + "İptal nedeni: …" olur. Sonuçlanmış öngörü iptal edilemez; iptal edilmiş öngörü tekrar iptal
edilemez; ikinci iade asla olmaz. İptaller doğru/yanlış istatistiklerine girmez.

Kilit/sonuçlandırma onay butonları yalnızca öngörü (ve sonuç) numarasını taşır ve her şeyi yeniden denetler; iptal onayı
nedeni tuttuğu için kısa ömürlü (10 dk), bu üyeye bağlı, tek kullanımlık bir bellek tokenıdır (restart'ta biter; karttaki
buton her zaman yeniden başlatır). Aynı anda gelen sonuçlandırma ve iptalden yalnızca biri geçerli olur.

## Oluşturma

`/ongoru yarat` tek bir **🔮 Öngörü Oluştur** formu açar (beş alan, Discord sınırı):

| Alan | Açıklama (formda) | Örnek metin | Kural (arka planda) |
|---|---|---|---|
| Başlık | Öngörünün sorusunu kısa ve net yaz. | Galatasaray - Fenerbahçe maç sonucu ne olur? | zorunlu, 5–200 karakter |
| Seçenekler ve oranlar | Her seçeneği yeni satıra yaz. Oran eklemek için \| kullan. Oran yazmazsan 2.00 kullanılır. | üç satırlık gerçek örnek (`Galatasaray Kazanır \| 1.10` …) | zorunlu, aşağıdaki kurallar |
| Kilitlenme tarihi | Boş bırakırsan öngörü manuel olarak kilitlenir. | 05.10.2026 | isteğe bağlı, `GG.AA.YYYY` (ISO `YYYY-AA-GG` de kabul) |
| Kilitlenme saati | Türkiye saati. | 20:00 | isteğe bağlı, `SS:DD` (`20.00` de kabul) |
| Sonuç kuralı | Sonucun nasıl belirleneceğini gerekiyorsa belirt. | Normal süre sonucu geçerlidir; uzatmalar dahil değildir. | isteğe bağlı, ≤ 1000 karakter; kartta gösterilir |

Formda karakter sınırı veya biçim ayrıntısı gösterilmez; bunlar yalnızca bir değer geçersiz olduğunda, alanı ve satırı
belirten Türkçe hata mesajında söylenir (ör. "**Seçenekler**: 3. satırdaki oran geçerli değil. Örnek: 2.30 (en az 1.01, en
fazla 1000.00).", "**Seçenekler**: en az 2 seçenek girmelisin; …"). Varsayılan oran yapılandırmadan gelir (`Predictions:DefaultOdds`).

- 2–25 seçenek; boş satırlar yok sayılır; 26. seçenek reddedilir (kesilmez). Seçenek adı ≤ 80; boş/tekrar (kırpma, boşluk
  birleştirme ve Türkçe küçük harf sonrası) ve ad içinde `|` reddedilir.
- Oran `1.10`/`1,10`, en fazla iki ondalık, 1.01–1000.00; negatif, sıfır, NaN, Infinity, bilimsel gösterim reddedilir, asla
  varsayılana çevrilmez. Oran yazılmayan satır 2.00 alır ve önizleme bunu söyler.
- Tarih ve saat ikisi de boşsa öngörü elle kilitlenir. Yalnız biri girilirse tahmin edilmez, reddedilir: "Kilitlenme tarihi
  girdiysen saat de girmelisin." / "Kilitlenme saati girdiysen tarih de girmelisin." İkisi birlikte Europe/Istanbul duvar saati
  olarak okunur ve UTC saklanır.
- Kilit zamanı gelecekte olmalı (en az 1 dk, en fazla 1 yıl); belirsiz/var olmayan saatler reddedilir; yayımlarken tekrar denetlenir.

Form önce **özel önizleme** açar (`[✅ Yayımla] [✏️ Düzenle] [❌ Vazgeç]`); önizlemedeki kartta ⏳ Kilitlenme, tarih ve
saatten oluşan tek zaman olarak (göreli + tam Discord zaman damgası) ya da "Manuel" olarak görünür. Hatalar alan ve satırıyla
listelenir; Düzenle formu tarih ve saat dahil girilen beş değerle yeniden açar, hiçbir girdi kaybolmaz. Taslaklar yalnızca bellekte (kullanıcı + sunucu + kanal + turnuva, 128 bit rastgele kimlik,
30 dk). **Yayımla** taslağı tek seferlik alır (çift tıklama tek kayıt; benzersiz `PublishKey` yedektir), her şeyi yeniden
denetler (eski taslak yeni turnuvaya taşınmaz), satırı `Publishing` olarak kaydeder, yaratıcının bu turnuvadaki cüzdanını
açar ve kartı gönderir. Kesin reddedilen gönderim satırı siler ve taslağı geri verir; belirsiz gönderim son mesajlarda aranır,
10 dk içinde bulunamazsa `Abandoned` olur — **asla ikinci kart gönderilmez**. Yayımlanan içerik değiştirilemez.

## Coin ve ödeme matematiği

- `long` tam sayı alt birim: **1 TSQ Coin = 100 birim**; oranlar ×100 tam sayı. Float/double yok; çarpım 128 bit, toplama/çıkarma
  `checked`.
- **toplam dönüş = yatırılan × sabit oran** (küsurat aşağı), **net kazanç = toplam dönüş − yatırılan**; önizleme, makbuz ve ödeme
  aynı fonksiyonu kullanır. 1000 → 100 yatır (1.10) → 900; kazanırsa +110 → 1010. Kaybedince ikinci düşüm yok; iptalde yalnızca
  ana para döner. Havuz paylaşımı, komisyon, vergi yok. Her katılımda oran ve olası ödeme snapshot'lanır.
- Hareket defteri: başlangıç, günlük ödül, katılım düşümü, kazanç ödemesi, iptal iadesi — her biri ekonomik işleme özgü benzersiz
  anahtarla (`initial:w1`, `daily:c7`, `stake:e3`, `payout:e3`, `refund:e3`).

## Günlük ödül

`/ongoru gunluk`: Europe/Istanbul takvim gününe göre günde bir (yeni hak 00:00'da), 10–100 eşit olasılıklı tam sayı
(`RandomNumberGenerator`). "Günlük ödülün: 47 TSQ Coin / Yeni bakiyen: 1047 TSQ Coin". Hak anahtarı sunucu + kullanıcı + yerel
gün (turnuvadan bağımsız: aynı gün turnuva bitse de ikinci ödül yok). Çift tıklama/paralel komut tek ödeme; tekrar deneme tutarı
yeniden çekmez. Günlük ödül cüzdanı **sıfırlamaz** ve liderlik uygunluğu **sağlamaz**.

## Normal kullanıcı coinini sıfırlayamaz

- Hiçbir kullanıcı komutu bakiyeyi, kayıpları veya istatistikleri sıfırlamaz; `reset`/`sifirla` benzeri komut yoktur.
- Cüzdan bir turnuvada **bir kez** açılır (ilk katılım, yayımlanan öngörü veya günlük ödülde) ve başlangıç bakiyesi benzersiz
  `initial:w{id}` hareketiyle bir kez yazılır. Cüzdanı tekrar tekrar görüntülemek, restart, sunucudan çıkıp girmek, günlük ödül,
  iptal (yalnızca yatırılan iade) ve sonuç (normal ödeme) başlangıç bakiyesini geri getirmez.
- `/privacy delete` Öngörü ekonomisini **silmez/sıfırlamaz** (bkz. Gizlilik).
- 1000 TSQ Coin'e dönüş **yalnızca** `/ongoru turnuva bitir` ile eski turnuva kapanıp yeni turnuva başladığında olur (yeni
  turnuvada yeni cüzdan).

## Turnuva

Sunucu başına tek aktif turnuva (kısmi benzersiz indeks). İlk kullanımda Turnuva 1 tek sefer oluşturulur. Katılımın cüzdanı ile
öngörüsünün aynı turnuvaya ait olması bileşik yabancı anahtarla garanti edilir.

`/ongoru turnuva bitir` (yalnızca komut kanalı; yalnızca Administrator veya sunucu sahibi):

1. Terminal olmayan (Publishing, Open, Locked) öngörü varsa: "Turnuvayı bitirmeden önce sonuçlanmamış öngörüleri
   sonuçlandırmalı veya iptal etmelisiniz." + `#12 · Galatasaray - Fenerbahçe · Kilitli — <kart bağlantısı>` listesi (özel).
   Turnuva kapanmaz, yeni turnuva açılmaz, cüzdan başlangıcı/snapshot/duyuru oluşmaz.
2. Uygun katılımcı yoksa kapatılmaz.
3. Özel önizleme: turnuva numarası, başlangıç tarihi, uygun katılımcı sayısı, öngörü sayısı, sonuçlandırılmış öngörü sayısı,
   En Çok TSQ Coin ilk 3, En Çok Doğru Tahmin ilk 3, yeni turnuvanın 1000 TSQ Coin ile başlayacağı; `[🏁 Turnuvayı Bitir] [Vazgeç]`.
4. Onay bu **yöneticiye**, bu **turnuva ID'sine** bağlı, 5 dk ömürlü, tek kullanımlık. Onayda her şey yeniden denetlenir;
   önizlemeden sonra açılan öngörü kapanışı engeller; başka yönetici önce kapattıysa eski onay yeni turnuvayı kapatmaz.
5. Tek atomik işlem: iki ilk 3 (coin ve doğru) görünen adlarla `prediction_standing`'e dondurulur, eski turnuva kapanır
   (final sayılar), yeni turnuva **bir kez** açılır, duyuru outbox'a yazılır. Eşzamanlı iki onaydan yalnızca biri kapatır.
6. Duyuru komut kanalına herkese açık, **mention'sız**:
   ```
   🏁 TSQ Öngörü · Turnuva 3 Sona Erdi
   💰 En Çok TSQ Coin: 🥇 Toro — 2840 TSQ Coin …
   🎯 En Çok Doğru Tahmin: 🥇 Toro — 12 doğru / 15 sonuçlanan (%80) …
   🔄 Yeni Turnuva Başladı: Herkes yeni turnuvaya 1000 TSQ Coin ile başlar.
   ```
   Duyuru dondurulmuş snapshot'tan bir kez render edilir; gönderim başarısız olursa outbox aynı içeriği tekrar dener (turnuva
   tekrar kapatılmaz; yeni 1000'lik bakiyeler "final" gösterilmez).

Eski cüzdanlar **UPDATE ile 1000'e çekilmez**: yeni turnuvada cüzdan yoktur, herkes ilk kullanımda 1000 ile başlar; eski
turnuvanın cüzdanları, katılımları, sıralaması arşiv olarak değişmeden kalır.

## Sonuçlanmış/iptal kartların kaldırılması

Sonuçlanmış (`Settled`) veya iptal edilmiş (`Cancelled`) bir öngörünün herkese açık kartı, sonuçlandırma/iptal işleminin
**commit edildiği andan** (`SettledAt` / `CancelledAt`; kart düzenleme zamanı değil) **12 saat** sonra kanaldan kaldırılır —
ör. 10:30'da sonuçlanan kart 22:30'da veya worker'ın sonraki ilk turunda (≈10 sn). Açık, kilitli veya yayımlanmakta olan
öngörünün kartı **kaldırılmaz**. Yeniden denenen sonuçlandırma (ikinci ödeme zaten reddedilir) veya geç biten kart
güncellemesi süreyi yeniden başlatmaz.

- **Yalnızca Discord mesajı kaldırılır.** Öngörü, sonuçlar, katılımlar, coin hareketleri, cüzdanlar, doğru/yanlış
  istatistikleri, turnuva ilişkisi, liderlik uygunluğu, otomatik futbol maç bağlantısı ve denetim alanları **kalır**;
  `/ongoru tahminlerim`, `/ongoru liderlik`, `/ongoru turnuva durum` ve `/ongoru turnuva bitir` aynen çalışır.
- Worker her turda veritabanından "terminal + kartı duruyor + süresi dolmuş" kayıtları bulur (kart başına zamanlayıcı yok):
  restart, deploy veya çökme süreyi kaybettirmez; 15 saat önce sonuçlanmış kart ilk turda kaldırılır. Bir turda en fazla 10.
- Silme Discord işlem dışındadır ve hemen önce durum yeniden denetlenir (hâlâ terminal, süre dolmuş, aynı mesaj ID'si).
  Başarılı silme veya "mesaj zaten yok" (404) → `CardRemovedAt` yazılır, bir daha denenmez. 429/5xx/zaman aşımı ve izin
  sorunu → artan bekleme (1 dk, 5 dk, 15 dk, 1 sa, 6 sa), en fazla 6 deneme; sonra `/bot status` "kart temizliği" satırında
  görünür. Bot kendi mesajını sildiği için Manage Messages gerekmez.
- **Kaldırılan kart arşivlenmiştir:** bir daha düzenlenmez ve **yedek kart gönderilmez**. Yedek kart yalnızca açık/kilitli
  öngörünün kartı beklenmedik şekilde silinince gönderilir (değişmedi). Sonuçlanmış kart 12 saat dolmadan elle silinirse de
  yedek gönderilmez (bugünkü davranış); 12 saatte temizlik bunu "zaten yok" olarak kapatır.
- Kart kaldırılınca otomatik futbol maç anahtarı korunur: aynı maç için ikinci otomatik öngörü açılmaz.

## Haftalık otomatik liderlik

Aktif turnuvanın liderliği **haftada bir** komut kanalına (`Predictions:CommandsChannelId`, `/ongoru liderlik`'in kanalı)
otomatik gönderilir; varsayılan **her Pazar 20:00 (Europe/Istanbul)**.

- `/ongoru liderlik` ile **aynı sorgular ve aynı görünüm**: aynı uygunluk (bu turnuvada en az bir katılım veya bir öngörü;
  yalnız cüzdan veya günlük ödül yetmez), 💰 En Çok TSQ Coin ve 🎯 En Çok Doğru Tahmin, her biri **en fazla ilk 10**. Başlık
  "🏆 TSQ Öngörü · Haftalık Liderlik", açıklama "Güncel aktif turnuva sıralaması (Turnuva N)". Tek kompakt mesaj; üyeler embed
  mention'ı olarak görünür, **ping yok** (`allowed_mentions` boş).
- **Her sunucu ve hafta için en fazla bir otomatik paylaşım:** hafta anahtarı, slotun yerel tarihinin ISO haftasıdır
  (`yyyyww`); `prediction_weekly_board(GuildId, WeekKey)` benzersizdir ve outbox anahtarı da haftaya bağlıdır (turnuvaya
  değil). Plan satırı ve outbox kaydı aynı işlemde commit olur; restart, iki worker veya Discord zaman aşımı ikinci kart
  üretmez; teslim, yeniden deneme ve 6 saatlik geçerlilik outbox'ındır.
- **Geç gönderim:** bot slot anında kapalıysa en fazla `CatchUpHours` (12) saat içinde bir kez gönderilir (Pazar 23:00'te açılan
  bot gönderir; Pazartesi 08:00'den sonra veya Salı açılan bot geçen Pazar'ı göndermez, sonraki Pazar'ı bekler). Eski
  haftalar topluca gönderilmez.
- **Boş liderlik:** uygun kimse yoksa (veya aktif turnuva yoksa) mesaj gönderilmez, hafta "atlandı" olarak işaretlenir; o hafta
  sonradan oynayan biri için geç kart gönderilmez.
- Her çalışmada **o anki** aktif turnuva kullanılır: turnuva 19:59'da biterse 20:00 kartı yeni turnuvadandır (tek kart).
- Modül kapalıysa veya `WeeklyLeaderboard:Enabled=false` ise gönderilmez; modül pencere içinde yeniden açılırsa o hafta bir kez
  gönderilebilir. Kanal/izin sorunu ekonomiye dokunmaz; outbox'ın sınırlı yeniden denemesi ve `/bot status` duyuru satırı geçerlidir.
- Elle `/ongoru liderlik` bağımsızdır: haftalık paylaşımı atlatmaz, haftalık paylaşım onu engellemez.

## Liderlik ve uygunluk

Tek tanım (`PredictionStore.EligibleWallets`): aktif turnuvada **en az bir katılım** yapmış **veya en az bir öngörü
yayımlamış** (açık, kilitli, sonuçlanmış ya da iptal) üye. Yalnızca cüzdanı görüntülemek, günlük ödül almak veya tembel
oluşturulan cüzdan uygunluk sağlamaz; önceki turnuvadaki aktivite yeni turnuvaya taşınmaz. Aynı tanım `/ongoru liderlik`,
`/ongoru turnuva durum` (katılımcı sayısı), turnuva sonu önizlemesi, dondurulan ilk 3 ve kapanış duyurusu tarafından kullanılır.

- **💰 En Çok TSQ Coin** = kullanılabilir + bekleyen ana para (olası kazanç sayılmaz). Eşitlik: toplam coin → doğru sayısı →
  kullanıcı ID.
- **🎯 En Çok Doğru Tahmin** = doğru sonuçlanan benzersiz öngörü sayısı (yaratmak puan vermez). Gösterim "12 doğru / 15
  sonuçlanan (%80)"; hiç sonuçlanmış tahmini olmayan (ör. yalnızca öngörü yaratan) "0 doğru · Henüz sonuçlanmış tahmini yok"
  (yanıltıcı %0 yok). Eşitlik: doğru sayısı → başarı yüzdesi (sonuçlanmışı olmayan en sonda) → toplam coin → kullanıcı ID.
- Her sıralamada **en fazla ilk 10** (sayfa yok); az kişi varsa yalnızca olanlar, kimse yoksa boş durum. Sıralama SQLite'ta
  `ORDER BY … LIMIT 10` ile yapılır; okumak cüzdan oluşturmaz. Geri çekilen tahmin uygunluğu korur ama coin'e (yalnız aktif
  ana para bekleyen sayılır) ve doğru/sonuçlanan sayısına girmez. Turnuva kapanışındaki ilk 3 değişmedi. Liderlikte üyeler embed mention'ı olarak gösterilir (ping yok); kapanış duyurusu mention kullanmaz.

## Kalıcılık ve eşzamanlılık

Tablolar (additive migration `PredictionsModule`): `prediction_tournament`, `prediction_wallet` (görünen ad anlık görüntüsü
dahil), `prediction`, `prediction_outcome`, `prediction_entry`, `prediction_ledger`, `prediction_daily_claim`,
`prediction_standing` (tablo türü: coin/doğru); additive migration `PredictionsCardRetentionWeeklyBoard`: `prediction`
kart temizliği sütunları (`CardRemovedAt`, `CardRemovalAttempts`, `CardRemovalNextAt`) ve `prediction_weekly_board`.

| Garanti | Nasıl |
|---|---|
| Sunucu başına tek aktif turnuva | `prediction_tournament(GuildId) WHERE Status = 0` benzersiz |
| Turnuva/kullanıcı başına tek cüzdan | `prediction_wallet(TournamentId, UserId)` benzersiz |
| Öngörü/kullanıcı başına tek katılım | `prediction_entry(PredictionId, UserId)` benzersiz |
| Sunucu/kullanıcı/yerel gün başına tek günlük hak | `prediction_daily_claim(GuildId, UserId, LocalDay)` benzersiz |
| Ekonomik işlem başına tek hareket | `prediction_ledger(OperationKey)` benzersiz |
| Tek taslaktan tek öngörü | `prediction(PublishKey)` benzersiz |
| Sunucu/hafta başına tek otomatik haftalık liderlik | `prediction_weekly_board(GuildId, WeekKey)` benzersiz |
| Katılımın sonucu kendi öngörüsüne, öngörüsü ve cüzdanı aynı turnuvaya ait | bileşik yabancı anahtarlar |
| Negatif bakiye yok, geçerli tutar/oran | CHECK kısıtları |

Her ekonomik değişiklik tek bir SQLite `BEGIN IMMEDIATE` işlemidir (denetimler başka yazarın değiştiremeyeceği durumu görür;
süreç içi kilide değil veritabanına dayanır). `SQLITE_BUSY`/`LOCKED` ve iyimser çakışma sınırlı (4) yeniden denenir; denenen
işlem hiçbir şey commit etmemiştir. İşlem içinde Discord çağrısı yok.

## Discord hataları ve modül kapatma

- **Silinen kart**: kesin silinme (Unknown Message/Channel) ile 429/5xx/zaman aşımı/izin kaybı karıştırılmaz. Kesin silinmede
  katılım durur (`Locked`, neden `CardMissing`), yatırımlar korunur ve — yönetim kartta olduğu için — worker aynı kanala bir
  **yedek yönetim kartı** gönderir (kilitli, ✅ Sonuçlandır · ↩️ İptal / İade). Belirsiz gönderimde önce son mesajlarda aranır;
  en fazla 10 dakikada bir denenir. Eski kartın (silinmiş) butonları yeni kaydı yönetemez. Kanal da silindiyse `/bot status`
  sorunu sayar.
- **Modül kapatma**: tüm `/ongoru` komutları ve kart butonları durur; cüzdanlar ve yatırımlar korunur; worker süresi dolanları
  kilitlemeye devam eder; teslim edilmemiş kapanış duyurusu ortak kapı gereği iptal edilir.
- Bot izinleri: öngörü kanalı View Channel, Send Messages, Embed Links, Read Message History; komut kanalı View Channel,
  Send Messages, Embed Links. Add Reactions veya Administrator gerekmez; yeni privileged intent yok.

## Gizlilik

`/privacy export` bu modülün kayıtlarını içerir (turnuva başına cüzdan ve görünen ad, katılımlar, hareketler, günlük ödüller,
ilk 3 dereceleri, oluşturulan/yönetilen öngörüler, kapatılan turnuvalar).

`/privacy delete` **Öngörü ekonomisini silmez veya sıfırlamaz**: bakiye, katılım, hareket, günlük ödül ve derece kayıtları
ortak bir yarışmanın oyun kayıtlarıdır (diğer üyelerin sıralaması onlara bağlıdır) ve silinmeleri "kaybet → sil → tekrar 1000
al" açığını doğururdu. Bu kayıtlar korunur ve üyeye önizlemede ve silme sonrasında açıkça söylenir; kayıtlı görünen ad anlık
görüntüleri (cüzdan, derece, oluşturulan öngörü) kaldırılır, kartta oluşturan "—" görünür. Gerçek bir yasal silme talebi
operatör tarafından oyun dışında ele alınmalıdır (ürün/hukuk kararı). Bot sunucudan çıkarıldığında modülün o sunucudaki tüm
verisi diğer modüllerle birlikte silinir. Kapanış duyurusu ve haftalık liderlik satırları teslimden 2 gün sonra outbox'tan
silinir; taslak ve onay token'ları yalnızca bellektedir. Sonuçlanmış/iptal kartın 12 saat sonra kanaldan kaldırılması
**veri silme değildir**: yalnızca Discord mesajı gider, öngörü geçmişi veritabanında kalır.

## Operasyon ve denetim izi

Loglar yalnızca ID, sayı ve tutar içerir: `prediction_published`, `prediction_entry`, `prediction_entry_changed`,
`prediction_entry_withdrawn`, `prediction_locked`,
`prediction_settled` (kim, hangi öngörü, hangi sonuç, kazanan sayısı, ödeme), `prediction_cancelled`, `prediction_daily`,
`tournament_closed` (kim, hangi turnuva), `prediction_weekly_leaderboard` (hafta, durum, turnuva, katılımcı sayısı), kart
kaldırma satırları. `/bot status`: iki kanalın bot izinleri, yaratıcı rolü, doğrulanmayı bekleyen / silinmiş / güncellenemeyen
kart sayıları, kaldırılamayan sonuçlanmış/iptal kartlar, bekleyen/gönderilemeyen duyurular, bekleyen coin tutarlılık denetimi ve otomatik
futbol durumu (mod, keşif, bugünkü maçlar, kredi, son hata, inceleme).

## Otomatik futbol öngörüleri

Galatasaray, Fenerbahçe, Beşiktaş ve Türkiye erkek A millî takımının desteklenen organizasyonlardaki maçları için maç günü otomatik açılan sabit
oranlı öngörüler (The Odds API; varsayılan **Disabled**, Observe ve Live modları; sonuç yine elle girilir):
[AUTO_FOOTBALL.md](AUTO_FOOTBALL.md), sağlayıcı doğrulaması: [PROVIDER_VERIFICATION.md](PROVIDER_VERIFICATION.md).
Otomatik kartın altbilgisi "TSQ Öngörü #42 · Otomatik · Sabit oran"; ayrıca planlanan başlama ve oran kaynağı gösterilir.
Otomatik öngörü kimseye cüzdan veya liderlik uygunluğu vermez.

## Kapsam dışı (V1)

Gerçek para, coin satışı/çekme, transfer, gerçek ödül, mağaza, kupon/parlay, cash-out, dinamik oran, otomatik sonuç, web
paneli, kilitlendikten sonra katılım değiştirme/geri çekme, yeniden açma, yeniden sonuçlandırma, zorla turnuva bitirme, toplu otomatik iptal,
seri/saatlik/haftalık ödül, kullanıcı tarafından coin sıfırlama.
