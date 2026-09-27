# TSQ LFG — Oyuncu Bul

Ayrı modül (`lfg`, `src/ToroSquad.Modules.Lfg`). Sunucudaki oyuncular **herhangi bir oyun veya aktivite** için `/ekip`
ile hızlıca ekip ilanı açar; diğerleri butonlarla katılır, "belki" der veya ayrılır; ilan sahibi veya bir moderatör kapatır,
ilan süresi dolunca kendiliğinden kapanır. İsteğe bağlı olarak ileri bir başlangıç zamanı, başlangıçtan 30 dakika önce ve
başlangıçta katılanları etiketleyen bildirimler ve bir ses kanalı seçilebilir.

**Tek generic sistem.** Bot yalnızca şunu bilir: *bir kullanıcı, adı X olan bir oyun/aktivite için Y kişilik ekip arıyor,
Z detayını yazmış; kim katıldı / kim belki; ne zaman başlıyor; hangi ses kanalı.* Oyun listesi, oyuna özel model,
handler, alan, doğrulama veya buton yoktur (Deadlock, CS2, WoW, Valheim ya da yarın çıkan bir oyun aynı yaşam döngüsünü
kullanır; yeni oyun kod değişikliği gerektirmez). Rank, rating, boss, rol, dungeon, mod… hepsi kullanıcının `detay`
metnidir ve bot bu metni yorumlamaz (`LfgArchitectureTests` oyuna özel tip/kolon adlarını yasaklar).

Kapsam dışı (mimari engel değil): ses kanalı / thread açma, tekrarlayan ilan, lider devri, rol mention'ı, davetle
katılım, geçmiş/itibar, otomatik eşleştirme.

## Kullanım

`/ekip` parametresizdir: yazınca ilan **açılmaz**, bir form açılır. Discord bir modalda en fazla **5** üst düzey bileşene
izin verir (Discord API: modal `components` 1–5; her alan bir `Label` + metin girişi), bu yüzden akış iki adımdır:

1. **Form (modal)** — *Ekip İlanı Oluştur*

   | Alan | Kural |
   |---|---|
   | Oyun / Etkinlik | serbest metin, 2–50 karakter (kontrol/format karakterleri atılır, boşluklar sadeleşir) |
   | Kişi sayısı | toplam ekip, **sahip dahil**; 2 … `Lfg:MaxPlayersPerListing` (varsayılan 20, üst sınır 50); yalnızca rakam |
   | Detay | isteğe bağlı, en fazla 200 karakter |
   | Başlangıç | isteğe bağlı, **tek alan**: boş / `şimdi` = şimdi · göreli `30 dk`, `45 dakika`, `1 saat`, `1,5 saat` / `1.5 saat`, `2 saat (sonra)`, `1 gün` · tarih `05.10.2026 21:30` (GG.AA.YYYY SS:DD) veya ISO `2026-10-05 21:30`. Göreli başlangıç için birim zorunludur (`30` tek başına tarih sayılır ve reddedilir; `d` birim değildir — dakika mı gün mü belirsiz); büyük harf (`ŞİMDİ`, `30 DAKİKA`) de olur; doğal dil ayrıştırıcısı yoktur |
   | Süre (saat) | isteğe bağlı: `1`, `2` veya `3` (`2 saat` da olur); boşsa `Lfg:DefaultExpirationMinutes` (varsayılan 120). Planlı ilanda **başlangıçtan itibaren** sayılır |

2. **Ayarlar (yalnızca formu gönderene görünür mesaj)** — formun özeti ve Discord'un yerel bileşenleri:
   bildirim seçimi (çoklu seçim: *⏰ 30 dk önce hatırlat*, *🚀 Başladığında etiketle*; yalnızca ileri bir başlangıçta
   gösterilir), ses kanalı seçici (yalnızca ses kanalları, isteğe bağlı) ve **[İlanı Oluştur] [✏️ Formu Düzenle] [İptal]**.
   *Formu Düzenle* aynı formu yazılanlarla yeniden açar.

Form gönderilince hiçbir şey kaydedilmez: yazılanlar oluşturma kurallarının aynısıyla (`LfgService.CheckCreateAsync`)
denetlenir; hata varsa Türkçe neden ve *Formu Düzenle* düğmesi gösterilir. **İlanı Oluştur** mevcut
`LfgService.CreateAsync` akışını çalıştırır (kanal kısıtı, kişi başı aktif ilan sınırı, sahip ilk Katılan, `BEGIN
IMMEDIATE`, özel tarih/saat dilimi, bildirimler, ses doğrulaması) ve kart kanala herkese açık bir takip mesajı olarak
**ping'siz** gönderilir; mesaj kimliği kaydedilir (bot kartı sonra kendisi düzenler). İlan yalnızca Discord kartı
kesin olarak reddettiyse (4xx) silinir (kimse görmedi) ve ayarlar mesajı yeniden denenebilir kalır. Başka her hatada
(zaman aşımı, 5xx) Discord kartı yine de oluşturmuş olabilir: kanalın son 20 mesajında bu ilanın kartı (Katıl düğmesi
kimliği) aranır ve bulunursa kaydedilir; bulunamazsa ilan **tutulur** (boş geçmiş kanıt değildir — Read Message History
yoksa Discord boş liste döner, mesaj bir an sonra da görünebilir): ikinci kart açılmaz, kartın ilk tıklaması mesaj
kimliğini kaydeder; kart gerçekten yoksa ilan en geç süresi dolunca kapanır — planlı ilanda bu, başlangıç + süre kadar
(en fazla ~1 yıl) sürebilir ve o süre sahibin aktif ilan haklarından birini tutar; bu nadir durum kabul edilen risktir
(yanlış mesaja bağlama ya da ikinci kart yerine). Böyle bir ilanın bildirimi yalnızca sahibini (tek Katılan) etiketler. Kanal kısıtı ve aktif ilan sınırı formu açmadan önce de
denetlenir; kimse boşuna form doldurmaz.

**Taslak.** Adımlar arasındaki form, veritabanına değil **bellekte** kısa ömürlü bir taslakta durur (`LfgFormDrafts`):
128 bit rastgele kimlik, yalnızca açan kullanıcı + sunucu için geçerli (kopyalanan/tahmin edilen kimlik başkası için
yoktur), son kullanımdan 30 dk sonra düşer, kullanıcı başına en fazla 3 ve toplamda en fazla 2000; restart'ta kaybolur
(kullanıcı formu yeniden açar). Kaydederken taslak alınır: çift tıklama tek kayıt yapar; reddedilirse taslak geri konur.
Özel kimliklerde (`tsq:lfg:form:<taslak>`, `tsq:lfg:draft:save|back|cancel|notify|voice:<taslak>`) yalnızca taslak
kimliği bulunur; ne yapılabileceğine her adımda sunucu karar verir.

### İlanı düzenle

Aktif (Açık **veya Dolu**) her kartta ikinci satırda **✏️ Düzenle** vardır (`tsq:lfg:edit:<id>`). Düğme herkese görünür,
ama yetki sunucu tarafında: **yalnızca ilan sahibi** (`actor == OwnerUserId`). Moderatör/yönetici ilanı kapatabilir,
içeriğini değiştiremez → `Bu ilanı yalnızca ilan sahibi düzenleyebilir.` Başka sunucunun ilanı "yok" sayılır.
Kapalı / süresi dolmuş / Orphaned ilan → `Bu ilan artık düzenlenemez.`

Sahip aynı formu **mevcut değerlerle dolu** açar (başlangıç sunucunun saat diliminde `GG.AA.YYYY SS:DD`; "şimdi"
ilanlarında boş; süre tam saatse `2`, değilse `90 dk`), ardından aynı ayarlar adımı mevcut bildirim tercihleri ve ses
kanalıyla gelir → **Kaydet**. Değişebilenler: oyun, detay, kişi sayısı, başlangıç, süre, iki bildirim tercihi, ses kanalı.
Sahip, katılımcılar ve durum düzenlenemez. Başarılıysa sahibe `✅ İlan güncellendi.` ve **aynı kart** bot tarafından
yeniden çizilir (yeni mesaj yok, kanala "güncellendi" duyurusu yok, ping yok, katılımcı listesi korunur).

Kurallar (hepsi kayıt anında, yazma kilidi içinde **veritabanındaki güncel duruma** göre; formun açıldığı andaki görüntü
karar vermez; hepsi-ya-hiç — reddedilen düzenleme hiçbir alanı değiştirmez):

| Konu | Kural |
|---|---|
| Kişi sayısı | yeni değer ≥ **Katılan** sayısı (Belki sayılmaz). Eşitse ilan **Dolu**, fazlaysa **Açık**. Az ise `Kişi sayısı, katılmış oyuncu sayısından (N) az olamaz.` Eşzamanlı katılımlar aynı kilitle sıralanır: aşırı rezervasyon olmaz |
| Başlangıç | yalnızca etkinlik **henüz başlamadıysa** değişir (göreli, tarih veya boş = şimdi başlat). Başladıysa (`EventAt ≤ şimdi`, "şimdi" ilanları oluşturulduğu an başlamış sayılır, ya da başlangıç bildirimi işlendiyse) → `Etkinlik başladıktan sonra başlangıç zamanı değiştirilemez.`; diğer alanlar yine düzenlenir. Yeni başlangıç geçmişe alınamaz (tarih en erken şimdi + 1 dk) |
| Süre / bitiş | `ExpiresAt = (EventAt ?? CreatedAt) + süre`: "şimdi" ilanında süre **oluşturulma anından** sayılır, her düzenleme ilanı baştan başlatmaz (90. dakikada süreyi 2 saate çekmek bitişi `şimdi + 2 saat` yapmaz). Bitiş geçmişte kalırsa reddedilir. Dokunulmayan süre (ör. 1, 2, 3 dışındaki yapılandırılmış varsayılan) aynen kalır |
| Bildirimler | bkz. [Etkinlik bildirimleri](#etkinlik-bildirimleri) → *Düzenleme* |
| Ses kanalı | eklenebilir, değiştirilebilir, kaldırılabilir; her kayıtta (değişmese de) bu sunucunun gerçek bir ses kanalı mı diye yeniden denetlenir. İlan kapanmaz, katılımcılar etkilenmez, kart yeniden çizilir; sonraki hatırlatma/başlangıç bildirimi yeni kanalı kullanır; gönderilmiş bildirim düzenlenmez |

Aynı formu ikinci kez kaydetmek `Değişiklik yok; ilan aynı kaldı.` der (sürüm ve kart değişmez). **Her alan** (oyun,
detay, kişi, başlangıç, süre, iki bildirim tercihi, ses kanalı) formun **açıldığı andaki** haliyle karşılaştırılır;
dokunulmamış alan kayıtta veritabanındaki **güncel** değerini korur (aynı tarih başka yazımla — `5.10.2026 21:30` — ya da
başlangıcı boş formda `şimdi` da dokunulmamış sayılır; metinlerde büyük/küçük harf değişikliği düzenlemedir): iki açık
düzenleme formundan eskisi, yenisinin değiştirdiği hiçbir alanı geri almaz (kayıp güncelleme yok). Değiştirilen alan
güncel duruma göre doğrulanır. Dokunulmamış kişi sayısı, yapılandırılan üst sınır sonradan düşürülmüş olsa da geçerli kalır.
Form gönderildiğindeki denetim yazma kilidi almaz ve hiçbir şey kaydetmez; karar kayıtta kilit altında yeniden verilir.
Silinmiş bir ses kanalı düzenleme ayarlarında yeniden önerilmez. Saat dilimi çözülemezse (bozuk ayar) yazılan tarih
oluşturmadaki gibi reddedilir.

### Başlangıç ve süre

`EventAt` etkinliğin başlayacağı an, `ExpiresAt` ilanın artık kullanılamayacağı an — ikisi ayrı alanlardır. Başlangıç tek
bir kaynaktan gelir (`LfgStart`: şimdi · göreli · mutlak); nereden geldiği sonrasında önemsizdir — süre dolumu,
bildirimler, kart ve ses aynı `EventAt` hattını kullanır.

| Başlangıç alanı | Sonuç |
|---|---|
| boş / `şimdi` | Şimdi: `EventAt = null`, `ExpiresAt = CreatedAt + süre` (V1 davranışı) |
| göreli (`30 dk`, `2 saat`, …) | `EventAt = şimdi + gecikme` (1 dk … 365 gün; kaydetme anına göre) |
| tarih (`05.10.2026 21:30`) | `EventAt` = girilen tarih/saat, sunucunun saat diliminde |

Domain, göreli başlangıç ile tarihin aynı anda verilmesini yine reddeder (`LfgRules.ResolveStart`); form tek alan olduğu
için kullanıcı bunu yapamaz.

Planlıysa `ExpiresAt = EventAt + süre`: ör. Başlangıç `05.10.2026 21:30`, Süre `2` → ilan 05.10.2026 23:30'da
kapanır; başlangıçtan önce asla expire olmaz. 30 dk hatırlatma `EventAt − 30 dk`'da (21:00), başlangıç bildirimi `EventAt`'te.

### Özel tarih/saat ve saat dilimi

- **Özel tarih/saat, sunucunun saat diliminde yorumlanır. Discord kartında tarih her kullanıcının kendi yerel saatinde
  gösterilir** (`<t:…:F> • <t:…:R>`; bot kartta saat dilimi dönüştürmez).
- Saat dilimi, TSQ Bot'un mevcut sunucu ayarıdır (`/setup` → saat dilimi; `GuildSettings.TimeZoneId`, varsayılan
  **Europe/Istanbul**). LFG ikinci bir saat dilimi ayarı tutmaz, yeni kolon/migration yoktur. Kayıt yalnızca `/setup`
  üzerinden, doğrulanmış IANA kimlikleriyle olur (`GuildTime.TryResolve`; Railway/Linux ve Windows'ta aynı ID'ler);
  `/lfg-admin status` kullanılan saat dilimini gösterir.
- Ayrıştırma açık ve kültürden bağımsızdır (`TryParseExact`, `d.M.yyyy H:mm` ve `yyyy-M-d H:mm`; makine yereli yok).
  `31.02.2026 21:00`, `05/10/2026`, eksik saat vb. → `Tarih/saat anlaşılamadı. Biçim: GG.AA.YYYY SS:DD`.
- Sınırlar: en erken **şimdi + 1 dakika** (geçmiş, şimdi veya 30 sn sonrası → `Başlangıç tarihi gelecekte olmalı.`), en
  geç **şimdi + 365 gün** (`Başlangıç tarihi en fazla 1 yıl sonrası olabilir.`). Sabit domain sınırları (`LfgEventDate`).
- Yaz saati: ileri alınırken hiç var olmayan saat → `Bu tarih/saat seçilen saat diliminde geçerli değil.`; geri alınırken
  iki kez yaşanan saat tahmin edilmez, reddedilir → `Bu saat, saat değişimi nedeniyle iki farklı zamana denk geliyor.`
  (Europe/Istanbul'da yaz saati yok; kural tüm saat dilimleri için aynı.)

## Kart

Kart, formun **İlanı Oluştur** tıklamasına verilen herkese açık takip (follow-up) mesajıdır (formun kendisi yalnızca
açana görünür; `/ekip` önceden komutun kendi yanıtıydı). Aynı renderer her oyun için:

```
🎮 Deadlock
@Toro ekip arıyor
👥 3 / 6
📝 Ranked gireceğiz, mikrofon gerekli.

Katılanlar
@Toro · @Oykeli · @Hasom
🤔 Belki
@Arif · @Shotgun

🗓️ Başlangıç: 5 Ekim 2026 Pazartesi 21:30 • 8 gün içinde
🔊 Ses Odası: #Deadlock
⏰ 4 saat içinde kapanır
[Katıl] [Belki] [Ayrıl] [🔊 Ses Odası]
[✏️ Düzenle] [İlanı Kapat]
```

- Kapasite yalnızca **Katılanlar** sayısıdır (`3 / 6`); Belki listesi ayrı gösterilir ve sayılmaz. Belki listesinin ilk 20
  kişisi gösterilir (`(+N)`).
- Dolu: `✅ Ekip tamamlandı`, `[Katıl]` devre dışı; `[Belki]`, `[Ayrıl]`, `[🔊 Ses Odası]`, `[✏️ Düzenle]`, `[İlanı Kapat]` açık.
- İki satır: oyuncuların düğmeleri (`Katıl · Belki · Ayrıl`, ses kanalı varsa `· 🔊 Ses Odası`) ve ilanın düğmeleri
  (`✏️ Düzenle · İlanı Kapat`). Satır ayrımı Core'daki `MessageButton.NewRow` ile yapılır (varsayılan kapalı ve kayıtlı
  yüklerden dışarıda bırakılır: diğer modüllerin buton yerleşimi ve yük hash'leri değişmez). Dağıtımdan önce açılmış
  kartlar ilk yeniden çiziminde yeni düzene geçer.
- Oyuncular Discord kullanıcı kimliğiyle tutulur; kartta `<@id>` mention'ı olarak (her izleyici güncel görünen adı görür)
  **embed içinde** gösterilir. Kartın her gönderimi ve düzenlemesi (oluşturma, katıl, belki, ayrıl, kapat, süre dolumu)
  `allowed_mentions` boş gider: **kart asla ping atmaz**. Görünen ad kalıcı veri olarak saklanmaz.
- Oyun adı ve detay güvenilmez metindir: mention, markdown ve link etkisizleştirilir (`DiscordText.Untrusted*`).
- Zamanlar Discord'un yerel zaman damgalarıdır (`<t:…:f>`, `<t:…:R>`): her kullanıcı kendi saat diliminde görür; bot saat
  dilimi dönüştürmez ve geri sayım için kartı düzenlemez.
- Durumlar: açık (yeşil) · dolu · **🔒 İlan kapatıldı** · **⏰ Bu ekip ilanının süresi doldu.** Bitmiş ilanda mesaj
  silinmez; listeler kalır, tüm butonlar devre dışıdır.

## RSVP: Katıl / Belki / Ayrıl

| Durum → işlem | Sonuç |
|---|---|
| yok → Katıl | boş slot varsa `Joined` (son slot `Full` yapar); yoksa `Bu ekip dolu.` |
| yok → Belki | `Maybe` (kapasite denetimi yok; dolu ilanda da mümkün) |
| Belki → Katıl | slot varsa `Joined`; yoksa **Belki kalır**: `Bu ekip şu anda dolu. "Belki" olarak kaldın.` |
| Katıldı → Belki | izinli; slot boşalır, ilan `Full` idiyse `Open` olur ve Katıl yeniden aktifleşir |
| Katıldı / Belki → Ayrıl | kayıt tamamen silinir; yalnızca Katılan ayrılınca slot boşalır |
| Sahip | her zaman `Joined`; Belki'ye geçemez, ayrılamaz (`İlan sahibi … seçemez/ayrılamaz`), ilanı kapatabilir |

`Belki` olan kişi kapasiteye sayılmaz, bildirimlerde etiketlenmez, ses butonunu kullanamaz. Durum `lfg_participant.Response`
alanındadır; aynı kullanıcı bir ilanda tek satırdır (fikir değiştirmek satırı günceller).

## Yaşam döngüsü

| Olay | Davranış |
|---|---|
| Oluştur | Girdi + kanal kısıtı + ses kanalı doğrulaması + kişi başı aktif ilan sınırı (`Lfg:MaxActiveListingsPerUser`, varsayılan 2, guild başına) denetlenir; ilan `Open`, sahip ilk oyuncu (`1 / N`). Kart yanıt olarak gönderilir, mesaj kimliği kaydedilir. Yanıt hiç gönderilemediyse ilan silinir (sahibin hakkını yemez) |
| Katıl / Belki / Ayrıl | Etkileşim hemen onaylanır (deferred update); ilan veritabanından yeniden okunur: guild, durum, süre, üyelik, boş slot. **Aynı kart** düzenlenir, tıklayana ephemeral sonuç. Hatalar ephemeral: `Zaten bu ekiptesin.` · `Bu ekip dolu.` · `Bu ilan kapatılmış.` · `Bu ilanın süresi dolmuş.` |
| Kapat | Yalnızca sahip veya moderatör (Discord **Manage Messages** ya da Administrator; mevcut `Authorize.Require`). Önce ephemeral onay (`Evet, kapat` / `Vazgeç`), sonra `Closed`; kart kapalı olarak düzenlenir; outbox'ta bekleyen bildirim iptal edilir. Tekrar kapatmak idempotenttir |
| Süre dolumu | Tek arka plan döngüsü (`LfgExpiryWorker`, ~60 sn; ilan başına zamanlayıcı yok) `ExpiresAt <= now` olan aktif ilanları toplu `Expired` yapar ve kartları düzenler. Bir butona süre dolduktan sonra basılırsa ilan o anda da expire edilir |
| Restart | Durum yalnızca veritabanındadır. Buton custom id'leri yalnızca ilan kimliğini taşır (`tsq:lfg:join|maybe|leave|voice|close:<id>`), restart sonrası da çalışır. Açılışta ilk tur (~20 sn sonra) kapalıyken süresi dolan ilanları expire eder, vadesi gelen bildirimleri kurallara göre işler ve kartları düzenler |
| Mesaj silindi | Bot yalnızca **Guilds** intent'i kullanır; mesaj silme olayı (`MESSAGE_DELETE`) ayrıcalıksız ama ayrı bir intent (`GuildMessages`) ister ve botun gördüğü her kanaldaki her mesajın olaylarını getirirdi — belgelenmiş "yalnızca Guilds" politikasına ve gizlilik metnine aykırı olduğu için eklenmedi. Onun yerine: (1) worker **en fazla 5 dakikada bir** aktif kartları sırayla, tur başına en çok 50 tek okumayla (`GET /channels/{kanal}/messages/{mesaj}`) doğrular — bir imleçle devam ederek her aktif karta sırası gelir; (2) kullanıcı aktif ilan sınırına takılınca yalnızca **onun** aktif kartları o anda (1,5 sn sınırla) kontrol edilir; (3) bir kart düzenlemesi `Unknown Message/Channel` dönerse. Üç yolda da ilan hemen `Orphaned` olur (terminal; aktif sayılmaz, bir daha düzenlenmez, yeniden açılamaz, bildirim üretmez; tekrar kontrol hiçbir şey değiştirmez). "Belirlenemedi" (Read Message History yok, 5xx, gateway hazır değil) asla silinmiş sayılmaz. `Closed`'dan ayrı tutulur çünkü kimse kapatmadı — mesaj ortadan kalktı; geçmiş ve tanı doğru kalır |

Modül bir sunucuda kapatılırsa (`/modules disable lfg`) butonlar "modül kapalı" cevabı verir, yeni bildirim üretilmez;
süre dolumu yine işler (yalnızca kartı "süresi doldu" yapar, veri silinmez).

## Etkinlik bildirimleri (30 dk önce / başlangıçta)

Açıkça istenirse (varsayılan kapalı) iki **yeni** mesaj — kart düzenlenmez, çünkü amaç gerçekten bildirim göndermek:

```
⏰ Deadlock 30 dakika içinde başlıyor!        🚀 Deadlock şimdi başlıyor!
@Toro @Oykeli @Hasom                          @Toro @Oykeli @Hasom @Arif
👥 3 / 6 · 🕘 21:00                            🔊 Ses Odası: #Deadlock
🔊 Ses Odası: #Deadlock                        [🔊 Ses Odasına Katıl]
[🔊 Ses Odasına Katıl]
```

- **Kim etiketlenir**: mesaj planlandığı andaki **Joined** oyuncular (sahip dahil), veritabanından yeniden okunur. Belki
  olanlar ve ayrılmış kullanıcılar etiketlenmez; 30 dk mesajından sonra katılan başlangıç mesajında vardır. En fazla 50.
- **Ne zaman**: 30 dk hatırlatma `EventAt − 30 dk ≤ now < EventAt` penceresinde; başlangıç mesajı `EventAt ≤ now <
  EventAt + 5 dk` (kısa tolerans). Pencere geçtiyse (bot kapalıydı) geç mesaj **gönderilmez**: bildirim `Skipped` olarak
  tüketilir, restart'ta tekrar denenmez. Çözünürlük ~1 dk (worker döngüsü).
- **Kapalı / süresi dolmuş / Orphaned** ilanlar bildirim üretmez; kapatma, Orphaned ve `/privacy delete` ile silinen ilan
  outbox'ta bekleyen bildirimi iptal eder. O an gönderilmekte olan ya da teslimi belirsiz (uzlaştırılan) bildirimin süresi
  geçmişe çekilir: Discord'a ulaşmışsa `Sent` kaydedilir, ulaşmamışsa `Expired` olur — **yeniden denenmez, yeniden
  gönderilmez**. Geri çağrılamayan tek şey Discord'un o an almakta olduğu istektir: pencere tek bir Discord isteğinin
  süresidir (genelde < 1 sn) ve en fazla o ilanın o anki Joined oyuncularına tek bir bildirim olur — tekrar veya toplu ping
  değildir.
- **Modül kapalı** (veya guild izin listesinde değil) iken vadesi gelen bildirim `Skipped` olur; modül saatler sonra açılsa
  bile geçmiş bildirimler toplu gönderilmez.
- **Düzenleme**: bir bildirim **bir kez** işlenir. Henüz işlenmemiş (`Pending`) hatırlatma/başlangıç bildirimi yeni
  `EventAt`'e göre planlanır. İşlenmiş olan (`Queued` = gönderildi/kuyrukta, `Skipped` = atlandı) yeni başlangıç, kapatıp
  açma veya tekrar kaydetme ile **sıfırlanmaz**: aynı ilan için ikinci 30 dk hatırlatması olmaz. Kuyruktaki bir bildirimi
  kapatmak onu outbox'ta iptal eder (kapatma ile aynı anlam: bekleyen iptal, gönderilmekte/uzlaştırılmakta olan yeniden
  denenmez). Anı geçmiş bir bildirimi sonradan açmak onu `Skipped` olarak tüketir; geç gönderilmez. Başlangıç zamanı etkinlik
  başladıktan (veya başlangıç bildirimi işlendikten) sonra değişmez, bu yüzden başlangıç bildirimi de tekrar etmez.
  Başlangıç değişirken kuyrukta bekleyen (henüz teslim edilmemiş) hatırlatma eski saati yazdığı için outbox'ta iptal edilir
  ve yeniden planlanmaz (bir eksik hatırlatma kabul, yanlış saatli ya da ikinci hatırlatma değil).
- **Dayanıklılık / tekrar yok**: `LfgNoticePlanner` tek bir yazma transaction'ında ilanı yeniden okur, Joined oyuncuları
  alıcı yapar, **mevcut outbox'a** satır ekler ve bildirimi `Queued` işaretler (`ReminderState` / `StartNoticeState` +
  zaman). "Gönder, sonra işaretle" penceresi yoktur; teslimi outbox yapar (modül kapısı, izin listesi, `InFlight` claim,
  belirsiz teslimde uzlaştırma). Benzersiz outbox anahtarı + ilan işareti, restart'larda aynı bildirimin ikinci kez
  oluşmasını engeller; belirsiz bir teslimden sonraki tek yeniden gönderim **ping taşımaz** (bir eksik ping kabul, ikinci
  ping değil). Kişisel liste ilan satırında tutulmaz; bildirim satırı (etiketlenen kimlikler) teslimden 24 saat sonra silinir.

### Kontrollü kullanıcı pingi

Core'daki `MentionPolicy` varsayılan olarak kullanıcı ping'ine izin vermez. Tek, açık bir ek: `MentionPolicy.ExplicitUsers(...)`
→ kablo tarafında yalnızca listelenen kimlikler `allowed_mentions.users` olur; `parse` boş (metinden kullanıcı/@everyone/
@here ayrıştırılmaz), rol yok. Bunu kullanabilen **tek** yer `LfgNoticeRenderer`'dır ve yalnızca Joined oyuncuların
kimliklerini verir (`LfgArchitectureTests.Explicit_user_pings_exist_only_in_the_lfg_notice_renderer`). `Users`
kurucu parametresi değildir ve public setter'ı yoktur; başka bir yol derlenmez (`with { Users = … }` dahil). Oyun adı/detaydaki
`@everyone`, `<@id>` gibi metinler etkisizleştirilir. TSQ Live'ın `@everyone` davranışı değişmedi.

## Ses kanalı

Discord API'sinin gerçek kabiliyeti (2026-09-27, resmî belgeler ve Discord.Net 3.20 ile doğrulandı):

- **Taşıma**: `Modify Guild Member` `channel_id` bir üyeyi **yalnızca zaten bir ses kanalına bağlıysa** başka bir ses
  kanalına taşır; bot için **Move Members** + hedefte **Connect** gerekir. Bağlı değilse Discord `40032 Target user is not
  connected to voice` döner. **Botun, seste olmayan birini sese bağlamasının hiçbir yolu yoktur.**
- Bot yalnızca Guilds intent'iyle kullanıcının seste olup olmadığını bilemez (GuildVoiceStates eklenmedi); bu yüzden izin
  varsa taşımayı dener ve 40032'yi "bağlı değil" olarak yorumlar.

`[🔊 Ses Odası]` (kart) ve `[🔊 Ses Odasına Katıl]` (bildirimler) aynı işleyicidir (`tsq:lfg:voice:<id>`). Buton hiçbir
kanal/izin bilgisi taşımaz; her tıklamada: doğru guild, ilan aktif, tıklayan **Joined** (Belki/üye olmayan →
`Önce ekibe katılmalısın.`), ilanın ses kanalı var ve hâlâ bu guild'in bir ses kanalı.

| Durum | Sonuç |
|---|---|
| Tıklayan zaten başka bir ses kanalında, bot Move Members + Connect'e sahip, tıklayanın kendisi o kanala bağlanabilir | **Tek tıkla taşınır**: `🔊 #kanal kanalına taşındın.` |
| Tıklayan seste değil | Taşınmaz; ephemeral: kanal + "bot yalnızca zaten seste olanları taşıyabilir" + **Ses kanalını aç** link butonu |
| Bot Move Members'a sahip değil | Taşıma **denenmez**; ephemeral: kanal + **Ses kanalını aç** link butonu |
| Tıklayan kanala kendisi bağlanamıyor (View/Connect yok) | Taşınmaz (botun izni kullanıcının erişimini aşamaz): `… bağlanma iznin yok.` |
| Kanalın kullanıcı sınırı var (ve tıklayanın kendi Move Members izni yok) | Taşınmaz, kanal + **Ses kanalını aç** link butonu: botun Move Members izni dolu bir kanalın sınırını aşabilirdi ve kanal doluluğu (ses durumu olayları olmadan) bilinemez; kullanıcı kendisi katılınca sınırı Discord uygular |
| Ses kanalı silinmiş / artık ses kanalı değil | `Seçilen ses kanalı artık mevcut değil.` İlan kapanmaz ve Orphaned olmaz; yalnızca ses özelliği kaldırılır, kart sonraki çizimde ses satırı/butonu olmadan gösterilir |

**Ses kanalını aç** butonu `https://discord.com/channels/{guild}/{kanal}` adresli bir link butonudur: Discord'da kanalı
**açar**, kimseyi kendiliğinden sese **bağlamaz**; kullanıcı Discord'un kendi **Sese Katıl** düğmesine basar. (Davet
linki oluşturmak ek bir izin ve kalıcı davet kayıtları gerektirirdi; kullanılmadı.) Bu davranış istemci tarafıdır ve canlı
olarak ayrıca gözlemlenmelidir.

## Tutarlılık ve eşzamanlılık

- Her durum değişikliği bir yazma transaction'ında çalışır; SQLite'ta `BeginTransaction` = **`BEGIN IMMEDIATE`**: yazma
  kilidi okumadan *önce* alınır, böylece "boş slot var mı / zaten üye mi / aktif ilan sınırı" kontrolü ile yazma tüm
  bağlantılar arasında sıralanır (diğer yazıcı meşgul zaman aşımı kadar bekler). Bildirim planlaması da aynı kilidi alır:
  alıcı listesi ile işaret arasına katıl/ayrıl giremez.
- `lfg_participant` birincil anahtarı `(ListingId, UserId)`: aynı kullanıcı bir ilanda veritabanı seviyesinde iki kez
  olamaz. `lfg_listing.Version` iyimser eşzamanlılık belirteci ek güvencedir (çakışmada işlem yeniden denenir).
- Testler: 5/6 ilana aynı anda iki katılım → biri katılır, diğeri "dolu", sonuç 6/6; 12 eşzamanlı katılım 3 boş slota
  → tam 3; aynı kullanıcının 4 eşzamanlı tıklaması → tek kayıt; aynı kullanıcının 5 eşzamanlı ilanı (sınır 2) → tam 2;
  karışık eşzamanlı Katıl/Belki → kişi başı tek satır, Joined ≤ kapasite, Full yalnızca Joined sayısına göre.
- İki eşzamanlı tıklamada Discord eski görüntünün düzenlemesini sonra uygulayabilir: her tıklama kartı düzenledikten sonra
  sürümü yeniden okur, değiştiyse güncel hali yeniden çizer; oturmazsa kart `CardStale` işaretlenir ve worker düzeltir.

## Arka plan düzenlemesi (kullanıcı etkileşimi gerekmez)

Süre dolumu, onaylı kapatma, ses kanalının kaldırılması ve restart sonrası telafi kartı etkileşim olmadan günceller.
Kart mesajının kanal ve mesaj kimliği takip mesajı gönderilince saklanır (belirsiz bir hatada son 20 mesajdan tam kimlik
eşleşmesiyle; bir buton tıklaması yalnızca kimlik boşsa, yalnızca aynı guild'de ve yalnızca botun kendi kart mesajı için
tamamlar; bildirim mesajları asla kart sayılmaz). Düzenleme **botun kendi REST kimliğiyle** `PATCH /channels/{kanal}/messages/{mesaj}` üzerinden yapılır;
etkileşim/webhook token'ı kullanılmaz (`DiscordEditRouteContractTests`). `Unknown Message/Channel` → `Orphaned`;
yetki/erişim kaybı, 429, 5xx, zaman aşımı → uyarı, worker aralığıyla en fazla 8 deneme. Başarısız bir düzenleme ilanın
durumunu asla değiştirmez.

## Kart outbox'sız, bildirimler outbox'lu

LFG kartı kullanıcının kendi etkileşimine verilen **takip mesajıdır**: anında görünür, kanal izni gerektirmez,
tekilleştirilecek bir "gönderim" yoktur; bot sonrasında yalnızca **düzenler** (`LfgCardSync`, yalnızca `EditAsync`
ve tek okumalık `GetPresenceAsync`). Etkinlik bildirimleri ise planlanmış, yeni, ping atan mesajlardır; bu yüzden mevcut
outbox'tan gider (yalnızca `LfgNoticePlanner` outbox'a yazar — mimari test). İkinci bir kuyruk yoktur.

## Kanal ve izinler

İsteğe bağlı: `/lfg-admin channel kanal:#ekip-bul` → `/ekip` yalnızca o kanalda çalışır. `/lfg-admin channel` (boş)
kısıtı kaldırır. İlan kanalında gerekli: `ViewChannel`, `SendMessages` (bildirimler), `EmbedLinks`. İsteğe bağlı:
`ReadMessageHistory` (silinen kartın erken fark edilmesi), ses kanalında **Move Members** + **Connect** (seste olanı tek
tıkla taşıma). İsteğe bağlılar olmadan özellikler zarifçe geri çekilir; ek gateway intent'i gerekmez.

## Yapılandırma (`Lfg`)

| Ayar | Varsayılan | Aralık |
|---|---|---|
| `DefaultExpirationMinutes` | `120` | 15–720 |
| `MaxActiveListingsPerUser` | `2` | 1–10 |
| `MaxPlayersPerListing` | `20` | 2–50 |

Hatırlatma öncesi süre (30 dk) ve başlangıç toleransı (5 dk) sabittir (`LfgRules`), yapılandırma değildir. Modül **her
sunucuda varsayılan kapalıdır** (`EnabledByDefault=false`) ve depodaki standart mekanizmayla açılır: `/modules enable lfg`.

## Veri ve gizlilik

Tablolar (additive migration'lar `LfgModule`, `LfgScheduledEvents`): `lfg_listing` (ilan; oyun adı + detay kullanıcının
kendi metni, `EventAt`, `VoiceChannelId`, bildirim tercihleri ve işaretleri), `lfg_participant` (ListingId + UserId +
`Response` Joined/Maybe + cevap zamanı), `lfg_guild_config` (isteğe bağlı kanal). İndeksler: `(Status, ExpiresAt)` süre
dolumu, `(GuildId, OwnerUserId, Status)` aktif ilan sınırı, `CardStale = 1` kısmi, `(Status, EventAt)` kısmi
(`EventAt IS NOT NULL`, vadesi gelen bildirimler), `lfg_participant(UserId)` gizlilik; FK `ListingId` → `lfg_listing`
(cascade). `LfgScheduledEvents` yalnızca kolon/indeks ekler: mevcut katılımcılar `Joined`, mevcut ilanlar `EventAt = null`,
bildirim bayrakları kapalı, ses kanalı yok — V1 anlamı korunur (test).

`/privacy export` açtığın ilanları (oyun, detay, durum, zamanlar, bildirim tercihleri), katıldığın/belki dediğin ilanları
(cevabınla) ve seni etiketleyen, henüz silinmemiş bildirim sayısını içerir. `/privacy delete` katıldığın veya belki dediğin
ilanlardan seni çıkarır (yalnızca Katılan silinince dolu ilan yeniden açılır; kart senin olmadan yeniden çizilir), açtığın
ilanları oyuncularıyla siler (gönderilmemiş bildirimleri de durdurulur), moderatör olarak kapattığın ilanlardaki "kapatan" kaydını temizler ve seni etiketleyen
bildirim satırlarını kaldırır (o an gönderilmekte/uzlaştırılmakta olan bir satır bittikten 24 saat sonra silinir). Teslimi
uzlaştırılamayıp bırakılan bildirim satırları da 24 saat sonra budanır. Sunucudan ayrılma sonrası saklama süresi
dolunca guild'in tüm LFG verisi silinir.

Kartı Discord tarafından kesin reddedilip silinen ilanın arada planlanmış bildirimi de iptal edilir.

Form taslakları (adımlar arasındaki yazılanlar ve seçimler) **hiçbir zaman veritabanına yazılmaz**: yalnızca bot
sürecinin belleğinde, son kullanımdan en fazla 30 dakika tutulur ve restart'ta kaybolur (`LfgFormDrafts`).

## Günlükler

`LFG listing {id} created|is full|closed by its owner/moderator|expired`, `{Kind} queued for N joined player(s)`,
`{Kind} skipped (reason)`, `voice channel no longer exists` (Information), `card message is gone … orphaned`,
`card update failed, will retry`, `giving up …` (Warning), `expiry/card recovery pass failed` (Error). Normal buton
tıklamaları ve beklenen retler günlüğe yazılmaz ve takip kodu üretmez; kullanıcı metni günlüğe yazılmaz.
