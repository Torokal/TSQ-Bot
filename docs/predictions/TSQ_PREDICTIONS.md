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

Kanal kuralları **yöneticiler için de** geçerlidir ve kanal ID'si **birebir** eşleşmelidir: DM, başka sunucu, başka kanal ve
izinli kanalın thread'leri reddedilir. Yanlış kanal cevabı yalnızca kullanana görünür, ör. "Bu komutu yalnızca
<#689814679056547857> kanalında kullanabilirsiniz." Reddedilen hiçbir çağrı cüzdan oluşturmaz, coin vermez, kayıt yazmaz veya
herkese açık mesaj göndermez. Kanal ve rol bağlantıları tıklanabilir ama kimseyi etiketlemez.

## Yetkiler

| İşlem | Kim |
|---|---|
| Öngörü **yaratma** | Yalnızca yaratıcı rolü. Administrator yetkisi bu rol olmadan yaratma hakkı vermez |
| Kart butonlarıyla **yönetme** (🔒 ✅ ↩️) | Kendi öngörüsünü: yaratıcı, rolü **hâlâ** varsa. Tüm öngörüleri: Administrator veya sunucu sahibi. Başka yaratıcının öngörüsü: hayır. Sunucuyu Yönet yetmez |
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
TSQ Öngörü #12 · Turnuva 3 · Oluşturan: Toro
```

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
- Katılımlar kartta birleştirilerek güncellenir (son 10 sn içinde düzenlendiyse bir sonraki worker turuna, ~10 sn, kalır); her
  katılımda yeni mesaj veya DM yok. Yönetim butonlarına basmak herkese açık mesaj üretmez; onaylar ve hatalar özeldir.

## 🎯 Tahmin Yap (katılım)

```
🎯 Tahmin Yap → form (Sonuç: seçim menüsü · Yatırılacak TSQ Coin: metin)
  → özel önizleme [✅ Onayla] [✏️ Düzenle] [Vazgeç] → atomik katılım → özel makbuz
```

- Form, sabitlenmiş Discord.Net 3.20.1'in modal içi String Select desteğini (TSQ LFG formuyla aynı "label" bileşeni) kullanır:
  2–25 sonuç, her biri "Galatasaray Kazanır — 1.10". Kartta paylaşılan bir seçim menüsü yoktur; formu kapatan üye 🎯 Tahmin Yap'a
  tekrar basıp **aynı sonucu doğrudan** seçebilir.
- **Düzenle** formu önceki seçim ve tutarla yeniden açar; gönderilince önceki önizleme geçersiz olur.
- Önizleme: sonuç, sabit oran, yatırılacak coin, kazanırsa toplam dönüş, net kazanç, işlem sonrası kullanılabilir bakiye,
  V1'de değiştirme/geri çekme olmadığı.
- Tahmin Yap, sonuç seçimi, formu açma/kapama, Düzenle, Vazgeç ve önizleme **hiçbir coin hareketi yapmaz**; coin yalnızca
  son **Onayla**'da düşülür. Onay tek yazma işleminde yeniden denetler: sunucu, kanal, modül, aktif turnuva, öngörü durumu,
  kilit zamanı (`şimdi ≥ kilit` ise ret — worker kartı güncellememiş olsa bile), sonucun bu öngörüye ait olması, bakiye, mevcut
  katılım ve onayın bu üyeye ait olması. İki önizleme açılmışsa yalnızca ilk başarılı onay katılım oluşturur (veritabanında
  öngörü/üye başına tek katılım).
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

`/ongoru yarat` tek bir form açar: **Başlık** (5–200), **Seçenekler ve oranlar** (her satır `Seçenek | oran`), isteğe bağlı
**Otomatik kilitlenme** (Türkiye saati `GG.AA.YYYY SS:DD`, ISO de kabul; boşsa elle kilitlenir), isteğe bağlı **Açıklama /
sonuçlandırma kuralı** (≤ 1000).

- 2–25 seçenek; boş satırlar yok sayılır; 26. seçenek reddedilir (kesilmez). Seçenek adı ≤ 80; boş/tekrar (kırpma, boşluk
  birleştirme ve Türkçe küçük harf sonrası) ve ad içinde `|` reddedilir.
- Oran `1.10`/`1,10`, en fazla iki ondalık, 1.01–1000.00; negatif, sıfır, NaN, Infinity, bilimsel gösterim reddedilir, asla
  varsayılana çevrilmez. Oran yazılmayan satır 2.00 alır ve önizleme bunu söyler.
- Kilit zamanı gelecekte olmalı (en az 1 dk, en fazla 1 yıl); belirsiz/var olmayan saatler reddedilir; yayımlarken tekrar denetlenir.

Form önce **özel önizleme** açar (`[📢 Yayımla] [✏️ Düzenle] [Vazgeç]`); hatalar alan ve satırıyla listelenir, Düzenle formu
girilen değerlerle yeniden açar. Taslaklar yalnızca bellekte (kullanıcı + sunucu + kanal + turnuva, 128 bit rastgele kimlik,
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
- İlk 10; az kişi varsa olanlar, kimse yoksa boş durum. Sıralama SQLite'ta `ORDER BY … LIMIT` ile yapılır; okumak cüzdan
  oluşturmaz. Liderlikte üyeler embed mention'ı olarak gösterilir (ping yok); kapanış duyurusu mention kullanmaz.

## Kalıcılık ve eşzamanlılık

Tablolar (additive migration `PredictionsModule`): `prediction_tournament`, `prediction_wallet` (görünen ad anlık görüntüsü
dahil), `prediction`, `prediction_outcome`, `prediction_entry`, `prediction_ledger`, `prediction_daily_claim`,
`prediction_standing` (tablo türü: coin/doğru).

| Garanti | Nasıl |
|---|---|
| Sunucu başına tek aktif turnuva | `prediction_tournament(GuildId) WHERE Status = 0` benzersiz |
| Turnuva/kullanıcı başına tek cüzdan | `prediction_wallet(TournamentId, UserId)` benzersiz |
| Öngörü/kullanıcı başına tek katılım | `prediction_entry(PredictionId, UserId)` benzersiz |
| Sunucu/kullanıcı/yerel gün başına tek günlük hak | `prediction_daily_claim(GuildId, UserId, LocalDay)` benzersiz |
| Ekonomik işlem başına tek hareket | `prediction_ledger(OperationKey)` benzersiz |
| Tek taslaktan tek öngörü | `prediction(PublishKey)` benzersiz |
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
verisi diğer modüllerle birlikte silinir. Kapanış duyurusu satırları teslimden 2 gün sonra outbox'tan silinir; taslak ve onay
token'ları yalnızca bellektedir.

## Operasyon ve denetim izi

Loglar yalnızca ID, sayı ve tutar içerir: `prediction_published`, `prediction_entry`, `prediction_locked`,
`prediction_settled` (kim, hangi öngörü, hangi sonuç, kazanan sayısı, ödeme), `prediction_cancelled`, `prediction_daily`,
`tournament_closed` (kim, hangi turnuva). `/bot status`: iki kanalın bot izinleri, yaratıcı rolü, doğrulanmayı bekleyen /
silinmiş / güncellenemeyen kart sayıları, bekleyen/gönderilemeyen duyurular, bekleyen coin tutarlılık denetimi.

## Kapsam dışı (V1)

Gerçek para, coin satışı/çekme, transfer, gerçek ödül, mağaza, kupon/parlay, cash-out, dinamik oran, otomatik sonuç/öngörü, web
paneli, katılım değiştirme/geri çekme, yeniden açma, yeniden sonuçlandırma, zorla turnuva bitirme, toplu otomatik iptal,
seri/saatlik/haftalık ödül, kullanıcı tarafından coin sıfırlama.
