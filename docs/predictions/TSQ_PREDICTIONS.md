# TSQ Öngörü

Sunucu üyelerinin tamamen **sanal TSQ Coin** ile topluluk öngörülerine katıldığı, **sabit oranlı** bir eğlence modülü.
Gerçek para, coin satışı, para çekme, üyeler arası transfer, gerçek değeri olan ödül, mağaza, kupon/parlay, cash-out, dinamik
oran, otomatik maç sonucu ve otomatik öngörü oluşturma **yoktur**. Sonuçlar yetkili tarafından elle belirlenir; ödeme ve
iadeler otomatik ve atomiktir. Spora bağlı değildir: başlık ve sonuçlar serbesttir (futbol, CS2, F1, oyun etkinlikleri…).

Her sunucuda varsayılan kapalıdır: `/modules enable predictions` (listede **TSQ Öngörü**). Modül kimliği `predictions`,
proje `ToroSquad.Modules.Predictions`, komut kökü `/ongoru`.

## Kanallar, rol ve yapılandırma

Tüm değerler tek yapılandırma bölümünden okunur (`Predictions`, ortam değişkeni önekiyle `TOROSQUAD_Predictions__…`);
başlangıçta doğrulanır, hatalı değer botu açık bir CONFIG satırıyla durdurur.

| Ayar | Varsayılan (ana sunucu) | Anlamı |
|---|---|---|
| `Predictions:ChannelId` | `1048525775919390840` | Öngörülerin oluşturulduğu, yönetildiği ve kartların bulunduğu **tek** kanal |
| `Predictions:CommandsChannelId` | `689814679056547857` | Üye komutlarının (cüzdan, günlük, tahminlerim, liderlik, turnuva) **tek** kanalı; turnuva kapanış duyurusu buraya gider |
| `Predictions:CreatorRoleId` | `1233057768408350741` | Öngörü yaratabilen rol |
| `Predictions:InitialBalanceCoins` | `1000` | Her turnuvanın başlangıç bakiyesi |
| `Predictions:DailyMinCoins` / `DailyMaxCoins` | `10` / `100` | Günlük ödül aralığı (iki uç dahil) |
| `Predictions:DefaultOdds` | `2.00` | Oranı yazılmayan seçeneğin oranı (1.01–1000.00, en fazla iki ondalık) |
| `Predictions:MaxOutcomes` | `25` | Öngörü başına en fazla seçenek (2–25; Discord seçim menüsü sınırı 25) |

Kanal kuralları **yöneticiler için de** geçerlidir ve kanal ID'si **birebir** eşleşmelidir: DM, başka sunucu, başka kanal
ve izinli kanalın thread'leri reddedilir (thread'in parent kanalına bakılmaz). Yanlış kanal cevabı yalnızca kullanana
görünür, ör. "Bu komutu yalnızca <#689814679056547857> kanalında kullanabilirsiniz." Kanal ve rol bağlantıları tıklanabilir
ama kimseyi etiketlemez (`allowed_mentions` boş). Reddedilen hiçbir çağrı cüzdan oluşturmaz, coin vermez, kayıt yazmaz veya
herkese açık mesaj göndermez.

## Yetkiler

| İşlem | Kim |
|---|---|
| Öngörü **yaratma** | Yalnızca yaratıcı rolüne sahip üyeler. Administrator yetkisi bu rol olmadan yaratma hakkı **vermez** |
| Öngörü **yönetme** (kilitle, sonuçlandır, iptal) | Kendi öngörüsünü: yaratıcı, rolü **hâlâ** varsa. Tüm öngörüleri: Administrator veya sunucu sahibi. Bir yaratıcı başkasınınkini yönetemez |
| Turnuva **bitirme** | Yalnızca Administrator veya sunucu sahibi (yaratıcı rolü yetmez; Sunucuyu Yönet de yetmez) |
| Katılım, cüzdan, günlük ödül, tahminlerim, liderlik, turnuva durumu | Herkes (rol gerekmez) |

`/ongoru` grubu `default_member_permissions` taşımaz: aksi hâlde üyeler cüzdan/günlük/liderlik komutlarını göremezdi. Her
alt komut, form gönderimi, önizleme onayı ve bileşen tıklaması yetkiyi sunucu tarafında **yeniden** denetler (Discord her
etkileşimde üyenin o anki rollerini ve izinlerini gönderir; form açıldıktan sonra kaldırılan rol hemen görülür). Custom
id'ler yetki, tutar veya oran taşımaz; yalnızca rastgele bir token, bir numara veya sayfa numarası taşır. Botlar yaratamaz,
katılamaz, ödül alamaz.

## Komutlar

Öngörü kanalında:

| Komut | Ne yapar |
|---|---|
| `/ongoru yarat` | Oluşturma formunu açar (yalnızca yaratıcı rolü) |
| `/ongoru kilitle ongoru:<hedef>` | Yeni katılımı hemen durdurur |
| `/ongoru sonuclandir ongoru:<hedef>` | Kazanan sonucu seçme + önizleme + onay |
| `/ongoru iptal ongoru:<hedef> gerekce:<metin>` | Gerekçe (3–300 karakter) + iade önizlemesi + onay |

Kullanıcı komut kanalında:

| Komut | Görünürlük | Ne yapar |
|---|---|---|
| `/ongoru cuzdan` | yalnızca kullanan | Kullanılabilir, bekleyen ve toplam coin; doğru/sonuçlanan; bugünkü günlük ödül durumu |
| `/ongoru gunluk` | yalnızca kullanan | Günlük ödülü alır |
| `/ongoru tahminlerim` | yalnızca kullanan | Aktif turnuvadaki katılımlar (10'ar sayfalı, ◀ ▶) |
| `/ongoru liderlik` | herkese açık, ping'siz | En Çok TSQ Coin + En Çok Doğru Tahmin (ilk 10) |
| `/ongoru turnuva durum` | herkese açık, ping'siz | Turnuva numarası, başlangıç, katılımcı, öngörü ve sonuçlanmamış öngörü sayısı |
| `/ongoru turnuva bitir` | yalnızca kullanan | Önizleme + tehlikeli işlem onayı; başarılı kapanış duyurusu komut kanalına herkese açık gider |

Hedef: kartın altbilgisindeki numara (`#12`, otomatik tamamlama başlık ve durumu gösterir), kartın mesaj bağlantısı
(yalnızca aynı sunucu) veya kartın mesaj ID'si. Başka sunucunun kaydı veya normal bir mesaj "bulunamadı" görünür.
Otomatik tamamlama yalnızca yönetebileceğin açık/kilitli öngörüleri önerir.

## Oluşturma

`/ongoru yarat` tek bir form açar:

1. **Başlık** — zorunlu, 5–200 karakter.
2. **Seçenekler ve oranlar** — zorunlu; her satır `Seçenek adı | oran`. Örnek:
   ```
   Galatasaray Kazanır | 1.10
   Beraberlik | 2.30
   Fenerbahçe Kazanır | 3.10
   ```
3. **Otomatik kilitlenme** — isteğe bağlı, Türkiye saati `GG.AA.YYYY SS:DD` (ISO `YYYY-AA-GG SS:DD` de kabul edilir).
   Boşsa öngörü elle kilitlenir.
4. **Açıklama / sonuçlandırma kuralı** — isteğe bağlı, en fazla 1000 karakter.

Kurallar (hiçbiri sessizce düzeltilmez veya kesilmez; her sorun alanı ve satırıyla birlikte söylenir):

- 2–25 seçenek; boş satırlar yok sayılır; 26. seçenek **reddedilir** (kesilmez).
- Seçenek adı en fazla 80 karakter; boş ad ve (kırpma, boşluk birleştirme ve Türkçe küçük harfe çevirme sonrası) aynı
  seçenek reddedilir; ad içinde `|` kullanılamaz.
- Oran: `1.10` ve `1,10` kabul edilir; en fazla iki ondalık; aralık 1.01–1000.00. Negatif, sıfır, NaN, Infinity, bilimsel
  gösterim ve bozuk oranlar reddedilir, **asla** varsayılana çevrilmez. Oran hiç yazılmazsa (`Beraberlik`) varsayılan oran
  (2.00) kullanılır ve önizleme bunu söyler. Oranlar yeniden hesaplanmaz veya normalize edilmez.
- Kilit zamanı gelecekte (en az 1 dk, en fazla 1 yıl) olmalı; belirsiz veya Türkiye saatinde var olmayan zamanlar reddedilir.
  Yayımlama anında tekrar denetlenir.
- Kart her durumda (açık, kilitli, her sonuçla sonuçlanmış, en uzun gerekçeyle iptal) Discord sınırlarına sığmalıdır;
  sığmayan öngörü kısaltılması istenerek reddedilir.

Form gönderilince hemen yayımlanmaz: yalnızca yaratıcının gördüğü bir **önizleme** gelir (kartın aynısı, "Sabit oran"
bilgisi, varsa varsayılan oran notu) ve **[📢 Yayımla] [✏️ Düzenle] [Vazgeç]** butonları. Hata varsa sorunlar listelenir ve
**Düzenle** formu girilen değerlerle yeniden açar (modal gönderimine doğrudan modal gönderilmez; yeniden açma özel
mesajdaki butondandır). Taslaklar veritabanına yazılmaz: kullanıcı + sunucu + kanal + turnuvaya bağlı, 128 bit rastgele
kimlikli, kullanıcı başına ve toplamda sınırlı, son kullanımdan 30 dk sonra düşer; restart'ta kaybolması kabul edilir.

**Yayımla** taslağı tek seferlik alır (çift tıklama tek kayıt; benzersiz `PublishKey` veritabanı yedeğidir), her şeyi yeniden
denetler (rol, kanal, modül, kilit zamanı, **turnuvanın hâlâ formun açıldığı turnuva olması** — eski taslak yeni turnuvaya
sessizce taşınmaz), satırı `Publishing` durumunda kaydeder ve kartı gönderir. Discord kartı kesin almadıysa (4xx/429) satır
silinir ve taslak geri verilir. Gönderim belirsizse (zaman aşımı/5xx) kanalın son mesajlarında aynı içerik aranır; bulunamazsa
satır `Publishing` kalır, worker 1 dk sonra tekrar arar, 10 dk içinde bulunamazsa `Abandoned` olur (kimse katılamadığı için
iade gerekmez). **Hiçbir durumda ikinci kart gönderilmez**; kartı henüz doğrulanmamış öngörüye coin yatırılamaz.

Yayımlanan başlık, sonuçlar, oranlar ve kural değiştirilemez; hatalı öngörü iptal edilip yeniden oluşturulur.

## Kart

Tek kart, ömrü boyunca **düzenlenir** (her katılımda yeni mesaj veya DM yok). En büyük metin öngörünün **başlığıdır**
(açıklamada `## ` başlık; kullanıcı metni önce etkisizleştirilir). Küçük başlık satırı durumu söyler (🟢 Katılım açık /
🔒 Katılım kapandı / ✅ Sonuçlandı / ❌ İptal edildi); "TSQ Öngörü" etiketi altbilgidedir:
`TSQ Öngörü #12 · Turnuva 1 · Oluşturan: <görünen ad> · Sabit oran`.

- Numaralı seçenekler ve sabit oranları (uzun/çok biçimlendirmeli metinde kod bloğuna, o da sığmazsa alanlara geçer —
  hiçbir şey kesilmez).
- 👥 Katılım: katılımcı sayısı ve yatırılan toplam coin.
- ⏳ Kilitlenme: Discord zaman damgası (kalan süre + tam tarih; her saniye düzenleme yok) ya da "Manuel kilitlenecek".
- 📜 Sonuçlandırma kuralı (varsa).
- Açıkken tek seçimlik **seçim menüsü** (her sonuç bir seçenek: `1. Galatasaray Kazanır` · `Oran 1.10`); kilitlenince menü
  devre dışı; sonuçlanınca/iptalde menü kalkar. Sonuçlanan kart kazananı (🏆) ve ödeme özetini, iptal kartı gerekçeyi ve
  iadeyi gösterir.
- Tüm gönderim ve düzenlemeler `allowed_mentions` = boş.

Hızlı ardışık katılımlar birleştirilir: kart son 10 sn içinde düzenlendiyse yeni düzenleme worker'ın bir sonraki turuna
(~10 sn) kalır; aynı kartın düzenlemeleri sırayla uygulanır (eski görüntü yeniyi ezmez). Oluşturanın adı yayımlama anındaki
görünen addır (düz metin); ad bulunamadıysa "eski kullanıcı" denmez.

## Katılım

```
Karttaki menüden sonuç seçimi → TSQ Coin tutarı formu → özel önizleme → [✅ Onayla] / [Vazgeç] → atomik kayıt → özel makbuz
```

Önizleme: öngörü ve seçilen sonuç, sabit oran, yatırılacak coin, kazanırsa toplam dönüş, net kazanç, işlem sonrası
kullanılabilir bakiye ve V1'de katılımın değiştirilemeyeceği/geri çekilemeyeceği.

- Üye başına öngörü başına **tek** katılım (veritabanında benzersiz indeks); ek coin, sonuç değiştirme ve kullanıcı kaynaklı
  çekilme yok.
- En az 1 TSQ Coin, en fazla iki ondalık (`100`, `12.5`, `12,50`); bakiyenin üzerinde katılım ve negatif bakiye yok.
- Form açılınca coin düşülmez; coin **yalnızca Onayla'da** düşülür.
- Onayda tek yazma işlemi içinde yeniden denetlenir: kanal, modül, turnuva, öngörü durumu, seçilen sonucun bu öngörüye ait
  olması, **kilit zamanı** (`şimdi ≥ kilit` ise ret), mevcut katılım ve bakiye. Kilitten önce açılıp sonra onaylanan form
  reddedilir; eski turnuvadan kalan onay yeni turnuvada harcama yapamaz. Onay tokenı tek kullanımlık, kullanıcı ve sunucuya
  bağlı, 10 dk ömürlüdür.

## Coin ve ödeme matematiği

- Bakiyeler ve hareketler `long` tam sayı alt birimdir: **1 TSQ Coin = 100 birim**; oranlar ×100 tam sayıdır (1.10 = 110).
  Para hesabında float/double yoktur; çarpım 128 bit yapılır, toplama/çıkarma `checked`'tır (taşma sessizce sarmaz).
- **toplam dönüş = yatırılan × sabit oran**, en küçük birimin altındaki küsurat **aşağı** yuvarlanır;
  **net kazanç = toplam dönüş − yatırılan**. Önizleme, makbuz ve gerçek ödeme aynı fonksiyonu kullanır.
- Örnek: 1000 ile başla, 100 yatır (1.10) → katılım sonrası 900; kazanırsa **110** eklenir → 1010 (net +10). Kaybedince
  ikinci kez düşülmez; iptalde yalnızca yatırılan ana para iade edilir.
- Havuz paylaşımı değildir: kazananın ödemesi kaybedenlere bağlı değildir. Komisyon, vergi veya gizli kesinti yoktur.
- Her katılımda oran ve olası ödeme **snapshot** olarak saklanır; ödeme sonradan değişen yapılandırmaya bağlı değildir.
- Bir cüzdanın bakiyesi + bekleyen tüm olası ödemeleri 10^16 TSQ Coin'i aşamaz (taşmaya karşı; pratikte erişilmez).

Hareket defteri (`prediction_ledger`): **başlangıç**, **günlük ödül**, **katılım düşümü**, **kazanç ödemesi**, **iptal
iadesi** — her biri tutar, sonrası bakiye ve ekonomik işleme özgü benzersiz anahtarla (`initial:w1`, `daily:c7`, `stake:e3`,
`payout:e3`, `refund:e3`). Cüzdan değişikliği, hareket ve katılım/durum kaydı aynı işlemde commit olur.

## Günlük ödül

`/ongoru gunluk` (komut kanalı): Europe/Istanbul takvim gününe göre günde bir kez; yeni hak Türkiye saatiyle 00:00'da doğar
(24 saatlik kayan pencere değil). 10–100 (iki uç dahil) eşit olasılıklı tam sayı, `RandomNumberGenerator` ile (testlerde
değiştirilebilir `IPredictionRandom`).

```
🎁 Günlük ödülün: 47 TSQ Coin
💰 Yeni bakiyen: 1047 TSQ Coin
Sonraki hak: <Discord zaman damgası>
```

Tekrar denemede bugünkü ödülün alındığı, tutarı ve sonraki hak zamanı gösterilir. Hak anahtarı **sunucu + kullanıcı + Türkiye
yerel tarihidir** (turnuvaya bağlı değil): aynı gün turnuva bitse bile ikinci ödül yok. Ödül seçimi/kaydı ve bakiye artışı tek
işlemdir; çift tıklama, paralel komut ve belirsiz interaction cevabı ikinci ödeme yapmaz, tekrar deneme tutarı yeniden
çekmez. Otomatik herkese ödeme, seri (streak), saatlik/haftalık ödül yoktur.

## Kilit, sonuçlandırma, iptal

Yaşam döngüsü: `Draft (bellek) → Publishing → Open → Locked → Settled`; `Open/Locked → Cancelled`; doğrulanamayan kart
`Publishing → Abandoned`. Terminal durumlar tek yönlüdür; her değişiklik yazma işleminde saklı durumu yeniden denetler.

- **Kilit**: kilit tarihi öngörünün tamamına uygulanır. Worker (~10 sn) süresi dolanları kilitler; restart sırasında
  dolanlar ilk turda ele alınır. Katılım güvenliği worker'a bağlı değildir (her onay saati kendi işleminde karşılaştırır).
  `/ongoru kilitle` hemen kilitler. V1'de tekrar açma yoktur.
- **Sonuçlandırma**: yetkili öngörüye ait sonuçlardan birini seçer; önizleme kazananı, kazanan/kaybeden sayısını ve toplam
  ödemeyi gösterir; **açık onay olmadan ödeme yapılmaz**. Açık bir öngörü onayla birlikte kapatılıp sonuçlandırılır (aradaki
  katılım yarışı yazma kilidiyle güvenlidir). Kazananı kimsenin seçmemiş olması geçerli sonuçtur: ödeme yapılmaz, iade de
  yapılmaz. Her kazanan tam olarak saklı olası ödemesini alır; her üye bir öngörüden en fazla bir "doğru" alır.
- **İptal**: gerekçe ve onay ister; tüm yatırımlar tam ve **bir kez** iade edilir; iptaller doğru/yanlış istatistiklerine
  girmez.
- Sonuçlandıktan sonra V1'de yeniden sonuçlandırma, kazanan değiştirme veya sonradan iptal/iade **yoktur** (açık hata
  mesajı verilir). Aynı anda gelen sonuçlandırma ve iptal onaylarından yalnızca biri geçerli olur.

## Turnuva

Sunucu başına aynı anda tek aktif turnuva (veritabanında kısmi benzersiz indeks). İlk kullanımda (ilk form veya günlük
ödül) **Turnuva 1** güvenle ve tek sefer oluşturulur. Tüm cüzdanlar, öngörüler ve katılımlar bir turnuvaya bağlıdır;
katılımın cüzdanı ile öngörüsünün aynı turnuvaya ait olması veritabanında bileşik yabancı anahtarla garanti edilir.

`/ongoru turnuva bitir`:

1. Kanal (komut kanalı) ve Administrator/sunucu sahibi kontrolü.
2. Sonuçlanmamış (açık, kilitli veya yayımlanması belirsiz) öngörü varsa: "Turnuvayı bitirmeden önce sonuçlanmamış
   öngörüleri sonuçlandırmalı veya iptal etmelisiniz." + numara ve kart bağlantılarıyla liste. Zorla bitirme ve toplu iptal yok.
3. Hiç katılımcı yoksa kapatılmaz (gereksiz kapanış/yeni turnuva döngüsü yok).
4. Önizleme: güncel ilk 3 ve bakiyesi yenilenecek kullanıcı sayısı + **[⚠️ Turnuvayı bitir]**.
5. Onay tokenı bu **turnuva ID'sine** ve bu **yöneticiye** bağlı, 5 dk ömürlü ve tek kullanımlıktır. Başka yönetici önce
   kapattıysa eski onay yeni turnuvayı kapatmaz.
6. Tek atomik işlem: koşullar yeniden denetlenir, ilk 3 final değerleriyle (`prediction_standing`) dondurulur, eski turnuva
   kapanır (final katılımcı/öngörü sayıları), yeni turnuva **bir kez** açılır, duyuru outbox'a yazılır.
7. Duyuru komut kanalına herkese açık, ping'siz gider: turnuva numarası, ilk 3 (final coin ve doğru sayısı) ve "Yeni turnuva
   başladı. Bakiyeler 1000 TSQ Coin olarak yenilendi." Duyuru donmuş snapshot'tan bir kez render edilir; gönderim başarısız
   olursa outbox aynı içeriği tekrar dener (turnuva tekrar kapatılmaz; yenilenmiş bakiyeler "final" diye gösterilmez).

Yeni turnuvada eski cüzdanlar **körlemesine 1000'e güncellenmez**: yeni turnuvanın henüz cüzdanı yoktur, her üye ilk
kullanımında 1000 ile başlar; eski turnuvanın cüzdanları, katılımları ve hareketleri arşiv olarak değişmeden kalır. Eski
turnuvaya ait worker, ödeme, form veya outbox işlemleri yeni cüzdanlara coin yazamaz (her işlem aktif turnuvayı ve kaydın
turnuvasını karşılaştırır; veri modeli de bunu zorlar).

## Liderlik ve istatistikler

`/ongoru liderlik` tek herkese açık cevapta iki sıralama (ilk 10; veri azsa olanlar):

- **En Çok TSQ Coin** = kullanılabilir bakiye + bekleyen katılımların **ana parası** (olası kazanç eklenmez; kartta belirtilir).
  Eşitlik: toplam coin → doğru sayısı → kullanıcı ID (küçük önce).
- **En Çok Doğru Tahmin** = doğru sonuçlanan benzersiz öngörü sayısı (yatırılan veya kazanılan tutar değil); gösterim: doğru,
  sonuçlanan ve başarı yüzdesi (aşağı yuvarlanmış tam yüzde). Yalnızca en az bir doğru tahmini olanlar listelenir; hiç yoksa
  "Henüz doğru sonuçlanmış tahmin yok." Eşitlik: doğru sayısı → başarı yüzdesi (eşit doğru sayısında daha az sonuçlanan) →
  toplam coin → kullanıcı ID.

İptaller ve sonuçlanmamışlar yüzdeye girmez. Sıralama SQLite'ta `ORDER BY … LIMIT` ile, yalnızca ilgili sunucunun aktif
turnuvasından hesaplanır (tüm veri belleğe çekilmez; başka turnuva karışmaz). Liderliği veya cüzdanı okumak cüzdan/başlangıç
ödülü üretmez. Üyeler embed içinde mention olarak gösterilir (Discord güncel adı gösterir, ping atılmaz).

## Kalıcılık ve eşzamanlılık

Tablolar (additive migration `PredictionsModule`): `prediction_tournament`, `prediction_wallet`, `prediction`,
`prediction_outcome`, `prediction_entry`, `prediction_ledger`, `prediction_daily_claim`, `prediction_standing`.

Veritabanı garantileri:

| Garanti | Nasıl |
|---|---|
| Sunucu başına tek aktif turnuva | `prediction_tournament(GuildId) WHERE Status = 0` benzersiz |
| Turnuva/kullanıcı başına tek cüzdan | `prediction_wallet(TournamentId, UserId)` benzersiz |
| Öngörü/kullanıcı başına tek katılım | `prediction_entry(PredictionId, UserId)` benzersiz |
| Sunucu/kullanıcı/yerel gün başına tek günlük hak | `prediction_daily_claim(GuildId, UserId, LocalDay)` benzersiz |
| Ekonomik işlem başına tek hareket | `prediction_ledger(OperationKey)` benzersiz |
| Tek taslaktan tek öngörü | `prediction(PublishKey)` benzersiz |
| Katılımın sonucu kendi öngörüsüne, öngörüsü ve cüzdanı aynı turnuvaya ait | bileşik yabancı anahtarlar (`OutcomeId, PredictionId`), (`PredictionId, TournamentId`), (`WalletId, TournamentId`) |
| Negatif bakiye yok | CHECK `BalanceMinor >= 0`, `PendingMinor >= 0`, hareket sonrası bakiye ≥ 0 |
| Geçerli tutar/oran | CHECK en az 1 coin, oran 1.01–1000.00, olası ödeme ≥ yatırılan |

Her ekonomik değişiklik tek bir SQLite yazma işlemidir (`BEGIN IMMEDIATE`: yazma kilidi okumadan önce alınır; denetimler
başka hiçbir yazarın değiştiremeyeceği durumu görür — süreç içi `SemaphoreSlim`'e değil veritabanına dayanır). `SQLITE_BUSY`/
`LOCKED` ve iyimser eşzamanlılık çakışması sınırlı sayıda (4) yeniden denenir; denenen işlem hiçbir şey commit etmemiştir ve
benzersiz anahtarlar çift uygulamayı zaten imkânsız kılar. İşlem içinde Discord HTTP isteği beklenmez. Birlikte commit olanlar:
bakiye kontrolü + katılım + düşüm + hareket; günlük hak + ödül + bakiye; sonuçlandırma + ödemeler + istatistikler +
hareketler; iptal + tüm iadeler; turnuva snapshot'ı + kapanış + yeni turnuva + duyuru.

## Discord hataları ve modül kapatma

- **Silinen kart**: kesin silinme (Unknown Message/Channel) ile 429/5xx/zaman aşımı/izin kaybı karıştırılmaz. Kart kesin
  silindiyse yeni katılım durur (öngörü `Locked`, neden `CardMissing`), yatırımlar korunur ve öngörü numarasıyla
  sonuçlandırılabilir/iptal edilebilir kalır; `/bot status` sayısını gösterir. Botun mesaj olaylarını dinlemediği (yalnızca
  Guilds intent) için açık kartlar ~10 dakikada bir okunarak denetlenir. Diğer hatalar en fazla 8 kez yeniden denenir.
- **Tıklamadan kurtarma**: gönderim onayı kaybolmuş bir kart üzerinden gelen seçim, kartı kaydeder ve öngörüyü açar.
- **Modül kapatma** (`/modules disable predictions`): tüm `/ongoru` komutları ve bileşenler durur (yeni yaratma, katılım ve
  günlük ödül dahil). Cüzdanlar ve yatırımlar silinmez/sıfırlanmaz. Worker süresi dolan öngörüleri kilitlemeye ve kartları
  güncellemeye devam eder (coin hareketi yok). Kapalıyken teslim edilmemiş kapanış duyurusu ortak kapı gereği iptal edilir
  (turnuva kapanmış olarak kalır). Tekrar açıldığında geçmiş kilit zamanları zaten uygulanmıştır.

Bot izinleri — öngörü kanalı: View Channel, Send Messages, Embed Links, Read Message History (belirsiz gönderim için kartı
bulmak ve kartın varlığını okumak); komut kanalı: View Channel, Send Messages, Embed Links. **Add Reactions veya Administrator
gerekmez**; yeni privileged intent yoktur. Mevcut davet izinleri (84992 = View Channel, Send Messages, Embed Links, Read
Message History) yeterlidir.

## Gizlilik

`/privacy export` bu modülün kayıtlarını içerir: turnuva başına cüzdanlar, katılımlar, coin hareketleri, günlük ödüller,
ilk 3 dereceleri, oluşturulan öngörüler (o anki görünen ad), kilitlenen/sonuçlandırılan/iptal edilen öngörüler ve kapatılan
turnuvalar.

`/privacy delete` (tek yazma işlemi): cüzdanlar, katılımlar, hareketler, günlük ödül kayıtları ve ilk 3 satırları silinir.
**Bekleyen** bir katılım yatırımıyla birlikte silinir — sonradan ödeme veya iade yapılmaz (gideceği cüzdan yoktur) — ve
öngörünün canlı katılımcı sayısı ile toplamı düşürülür (kart yeniden çizilir; silinmiş kişinin coinleri gösterilmez).
Sonuçlanmış/iptal edilmiş öngörülerin tarihsel toplamları (kişisel veri içermeyen sayılar) kalır. Oluşturduğu öngörüler
kalır; oluşturan ID'si ve adı temizlenir (kart "—" gösterir). Yönetici ID'leri (kilitleyen, sonuçlandıran, iptal eden,
turnuvayı kapatan) temizlenir. Liderlikte artık görünmez.

**Bilinen sonuç (yeni saklama politikası uydurulmadı):** cüzdan ve günlük hak kaydı silindiği için üye silmeden sonra aktif
turnuvada yeniden 1000 ile başlar ve aynı gün günlük ödülü tekrar alabilir. Bunu engellemek, silinen kimliklerin süresiz
listesini tutmayı gerektirir; bu bir ürün/gizlilik kararıdır (sahip onayı gerekir).

Kapanış duyurusu satırları (ilk 3 mention'ı içerir) teslimden/bitişten 2 gün sonra outbox'tan silinir. Taslak ve onay
token'ları yalnızca bellektedir (veritabanına hiç yazılmaz; 5–30 dk ömür, restart'ta silinir).

## Operasyon ve denetim izi

- Loglar yalnızca ID, sayı ve tutar içerir (başlık, gerekçe, seçenek adı, form içeriği veya token yazılmaz):
  `prediction_published`, `prediction_entry`, `prediction_locked`, `prediction_settled` (kim, hangi öngörü, hangi sonuç,
  kazanan sayısı, toplam ödeme), `prediction_cancelled` (kim, iade sayısı ve toplamı), `prediction_daily`,
  `tournament_closed` (kim, hangi turnuva). Aynı bilgiler satırlarda da saklanır (`SettledByUserId`, `SettledAt`,
  `CancelledByUserId`, `CancelReason`, `ClosedByUserId` …).
- `/bot status`: modül açık sunucularda iki kanalın bot izinleri ve yaratıcı rolünün varlığı; doğrulanmayı bekleyen, silinmiş
  ve güncellenemeyen kart sayıları; bekleyen/gönderilemeyen duyurular; tutarlılık denetimi (her cüzdanın bekleyen coini =
  bekleyen yatırımlarının toplamı).

## Kapsam dışı (V1)

Gerçek para, coin satışı/çekme, kullanıcılar arası transfer, gerçek ödüle dönüştürme, mağaza, kupon/parlay, cash-out, dinamik
oran, otomatik maç sonucu, otomatik öngörü oluşturma, web paneli, katılım değiştirme/geri çekme, yeniden açma, yeniden
sonuçlandırma, zorla turnuva bitirme, toplu otomatik iptal, seri/saatlik/haftalık ödül.
