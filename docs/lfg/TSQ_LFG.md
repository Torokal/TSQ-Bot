# TSQ LFG — Oyuncu Bul

Ayrı modül (`lfg`, `src/ToroSquad.Modules.Lfg`). Sunucudaki oyuncular **herhangi bir oyun veya aktivite** için `/ekip`
ile hızlıca ekip ilanı açar; diğerleri butonlarla katılır (ekip doluysa bekleme listesine girer), "belki" der veya
ayrılır; ilan sahibi veya bir moderatör kapatır,
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
izin verir (Discord API: modal `components` 1–5; her alan bir `Label` içindedir), bu yüzden akış iki adımdır:

1. **Ana form (modal)** — *Ekip İlanı Oluştur*; tam olarak 5 bileşen, hepsi Discord'un belgelenmiş modal bileşenleri:

   | Alan | Bileşen | Kural |
   |---|---|---|
   | Oyun / Etkinlik | Text Input | serbest metin, 2–50 karakter (kontrol/format karakterleri atılır, boşluklar sadeleşir) |
   | Kişi Sayısı | String Select (1 seçim, zorunlu) | toplam ekip, **sahip dahil**; seçenekler koddan üretilir: `LfgRules.MinPlayers` (2) … `Lfg:MaxPlayersPerListing` (varsayılan 20). Discord bir seçicide en fazla **25** seçenek gösterir; aralık sığmıyorsa (`MaxPlayersPerListing` > 26) seçenekler kesilmez, metne dönülmez: yapılandırma başlangıçta hata verir. Seçilen değer sunucuda yeniden doğrulanır (sahte `234`, `1` → `Ekip büyüklüğü 2 ile 20 kişi arasında olmalı (sen dahil).`) |
   | Başlangıç Tarihi | Text Input | Discord'un herkese açık API'sinde tarih seçici bileşeni yoktur, bu yüzden metin alanı. Açıklama `Boş = şimdi • Örn: 27.09.2026 21:30`, örnek metin `27.09.2026 21:30`. Kural: | isteğe bağlı. **Boş = şimdi**; yoksa tam tarih ve saat: `27.09.2026 21:30` (GG.AA.YYYY SS:DD) veya kısa yıl `27.09.26 21:30` (GG.AA.YY SS:DD); baştaki sıfırlar gerekmez (`5.10.26 20:00`). Uyumluluk için ISO `2026-09-27 21:30` de kabul edilir. Göreli süreler (`2 saat`, `30 dk`, `1 gün`, `2`), kelimeler (`yarın 21:00`) ve eksik tarih/saat (`27.09.2026`, `21:30`) **kabul edilmez** → `Tarih/saat anlaşılamadı. Örnek: 27.09.2026 21:30 veya 27.09.26 21:30` |
   | Ses Kanalı | Channel Select | isteğe bağlı, Discord'un yerel kanal seçicisi (yalnızca ses kanalları, 0–1 seçim); sunucu tarafında bu guild'in gerçek bir ses kanalı mı diye yeniden denetlenir |
   | Bildirimler | Checkbox Group (0–2, isteğe bağlı) | *⏰ 30 dk önce katılanları etiketle* (`before`), *🚀 Başlangıçta katılanları etiketle* (`start`); oluştururken ikisi de işaretsiz, düzenlemede mevcut tercihler işaretli. Modal kutuları dinamik olarak kapatamaz: başlangıç boşken işaretlenirse seçim **sessizce atılmaz**, form reddedilir → `Bildirim kullanmak için bir başlangıç tarihi seçmelisin.` + *✏️ Formu Düzenle* (taslak korunur) |

2. **Ayarlar (yalnızca formu gönderene görünür mesaj)** — formun özeti (oyun, kişi, başlangıç, süre, ses kanalı,
   bildirimler, `📝 Detay: …` ya da `📝 Detay eklenmedi`) ve: **İlan süresi** seçicisi (1 / 2 / 3 saat; varsayılan
   `Lfg:DefaultExpirationMinutes` seçili — 120 dk — ve seçeneklerden biri değilse "(varsayılan)", düzenlemede 1/2/3 dışı
   mevcut süre "(mevcut)" olarak eklenir; planlı ilanda **başlangıçtan itibaren** sayılır), **[📝 Detay Ekle]** /
   **[📝 Detayı Düzenle]** ve **[İlanı Oluştur]** (düzenlemede **[Kaydet]**) **[✏️ Ana Formu Düzenle] [İptal]**.
   Ses kanalı ve bildirimler artık bu adımda değil, ana formdadır.
3. **Detay (ayrı küçük modal)** — *İlan Detayı*, tek alan *Detay* (isteğe bağlı paragraf, en fazla 200 karakter, aynı
   normalizasyon). Ayarlar mesajındaki düğmeyle açılır (Discord bir modal gönderimine modal ile cevap veremez; düğme
   tıklaması açabilir). Boş gönderim detayı siler. Gönderince yeniden denetlenir ve ayarlar mesajına dönülür.

Ana form → ayarlar → detay → ayarlar → *Ana Formu Düzenle* → ayarlar turunda hiçbir alan kaybolmaz: ana form yazılanları,
seçilen kişi sayısını, ses kanalını ve bildirim kutularını yeniden doldurur; süre ve detay taslakta kalır.

### Doğrulama: Discord'un kendi denetimi ve sunucu

Discord'un herkese açık modal API'si (resmi Component Reference; Discord.Net 3.20.1 ve discord.js builder'ları da aynı
alanları taşır) Text Input için yalnızca `required`, `min_length`, `max_length`, `value` ve `placeholder` tanır;
`min_value`/`max_value`, regex/pattern, özel doğrulayıcı, alana özel hata gösterimi ya da botun Gönder düğmesini
açıp kapatması **yoktur**. Modal açıkken bot yazılanı göremez (yalnızca gönderimde gelir) ve bir modal gönderimine yeni
bir modalla cevap veremez. Bu yüzden:

| Alan | Discord'un gönderimden önce uyguladığı (native) | Gönderimden sonra sunucuda |
|---|---|---|
| Oyun / Etkinlik | `required`, `min_length` 2, `max_length` 50 (düzenlemede 100: emoji'li kayıtlı ad kesilmeden dolsun; kural 50 karakter) | normalizasyon + 2–50 karakter |
| Kişi Sayısı | String Select, `required`, tam 1 seçim; yalnızca 2…üst sınır seçenekleri (`234` yazılamaz) | aralık + Katılan sayısı (düzenleme) |
| Başlangıç Tarihi | isteğe bağlı, `max_length` 40, örnek metin | tarih biçimi, gelecekte, ≤ 1 yıl, saat dilimi/yaz saati, başlamış etkinlik kilidi |
| Ses Kanalı | Channel Select, yalnızca ses kanalı, 0–1, isteğe bağlı | bu sunucunun gerçek ses kanalı |
| Bildirimler | Checkbox Group, 0–2, isteğe bağlı | başlangıç tarihi gerektirir (alanlar arası bağımlılık) |
| Detay (ayrı modal) | isteğe bağlı, `max_length` 200, paragraf | normalizasyon + 200 karakter |
| Süre (ayarlar) | String Select: 1/2/3 saat (+ "(varsayılan)"/"(mevcut)") | izinli seçenek |

Discord istemcisi zorunlu alan boşken, metin `min_length`'ten kısa / `max_length`'ten uzunken veya zorunlu seçici
seçilmemişken formu göndermez. Tarihin anlamı, alanlar arası bağımlılık (bildirim ↔ başlangıç), veritabanındaki güncel
Katılan sayısı ve yetki gönderimden önce denetlenemez: sunucu hepsini gönderimde ve kayıtta yeniden denetler (sahte
yüklere karşı da; istemci güvenlik sınırı değildir).

Form gönderilince hiçbir şey kaydedilmez: yazılanlar oluşturma kurallarının aynısıyla (`LfgService.CheckCreateAsync`)
denetlenir. Hata varsa yalnızca gönderene, **hangi alanın neden** hatalı olduğu gösterilir (genel "Geçersiz giriş" yok):

```
❌ Başlangıç Tarihi
Tarih/saat anlaşılamadı.
Örnek: `27.09.2026 21:30` veya `27.09.26 21:30`.
[✏️ Formu Düzelt] [📝 Detay Ekle] [İptal]
```

*✏️ Formu Düzelt* aynı formu taslaktaki her şeyle (oyun, kişi seçimi, tarih, ses kanalı, bildirimler) yeniden açar;
yalnızca hatalı alan düzeltilir. İstisnalar: reddedilen ses kanalı yeniden önerilmez (başka bir kanal ya da hiçbiri
seçilir) ve alanın kendi sınırı dışındaki bir metin (ör. 1 karakterlik oyun adı) Discord bu değeri kabul etmediği için
önceden doldurulmaz. Kayıtta (kilit altında) reddedilen bir form da alanı ve nedeni gösterir; alanla düzeltilebiliyorsa
aynı düğmeleri verir. İlanın kendisiyle ilgili retler (kanal kısıtı, ilan sınırı, yetki) alan başlığı olmadan `❌ …`
olarak gösterilir. **İlanı Oluştur** mevcut
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
Özel kimliklerde (`tsq:lfg:form:<taslak>`, `tsq:lfg:details:<taslak>`, `tsq:lfg:draft:save|back|cancel|details|duration:<taslak>`)
yalnızca taslak kimliği bulunur; ne yapılabileceğine her adımda sunucu karar verir.

### İlanı düzenle

Aktif (Açık **veya Dolu**) her kartta ikinci satırda **✏️ Düzenle** vardır (`tsq:lfg:edit:<id>`). Düğme herkese görünür,
ama yetki sunucu tarafında: **yalnızca ilan sahibi** (`actor == OwnerUserId`). Moderatör/yönetici ilanı kapatabilir,
içeriğini değiştiremez → `Bu ilanı yalnızca ilan sahibi düzenleyebilir.` Başka sunucunun ilanı "yok" sayılır.
Kapalı / süresi dolmuş / Orphaned ilan → `Bu ilan artık düzenlenemez.`

Sahip aynı formu **mevcut değerlerle dolu** açar (mevcut kişi sayısı seçili; başlangıç sunucunun saat diliminde
`GG.AA.YYYY SS:DD`, "şimdi" ilanlarında boş; mevcut ses kanalı seçili, seçim kaldırılabilir; bildirim kutuları mevcut
tercihlerle işaretli), ardından aynı ayarlar adımı mevcut süre (1/2/3 saat değilse "(mevcut)" olarak) ve detayla gelir
(**📝 Detayı Düzenle**) → **Kaydet**. Yapılandırılan üst sınır sonradan düşürülmüşse ilanın kendi kişi sayısı seçicide
kalır (dokunulmamış değer geçerli kalır). Değişebilenler: oyun, detay, kişi sayısı, başlangıç, süre, iki bildirim tercihi, ses kanalı.
Sahip, katılımcılar ve durum düzenlenemez. Başarılıysa sahibe `✅ İlan güncellendi.` ve **aynı kart** bot tarafından
yeniden çizilir (yeni mesaj yok, kanala "güncellendi" duyurusu yok, ping yok, katılımcı listesi korunur).

Kurallar (hepsi kayıt anında, yazma kilidi içinde **veritabanındaki güncel duruma** göre; formun açıldığı andaki görüntü
karar vermez; hepsi-ya-hiç — reddedilen düzenleme hiçbir alanı değiştirmez):

| Konu | Kural |
|---|---|
| Kişi sayısı | yeni değer ≥ **Katılan** sayısı (Belki sayılmaz). Eşitse ilan **Dolu**, fazlaysa **Açık**. Az ise `Kişi sayısı, katılmış oyuncu sayısından (N) az olamaz.` Eşzamanlı katılımlar aynı kilitle sıralanır: aşırı rezervasyon olmaz |
| Başlangıç | yalnızca etkinlik **henüz başlamadıysa** değişir (tam tarih/saat ya da boş = şimdi başlat). Başladıysa (`EventAt ≤ şimdi`, "şimdi" ilanları oluşturulduğu an başlamış sayılır, ya da başlangıç bildirimi işlendiyse) → `Etkinlik başladıktan sonra başlangıç zamanı değiştirilemez.`; diğer alanlar yine düzenlenir. Yeni başlangıç geçmişe alınamaz (tarih en erken şimdi + 1 dk) |
| Süre / bitiş | `ExpiresAt = (EventAt ?? CreatedAt) + süre`: "şimdi" ilanında süre **oluşturulma anından** sayılır, her düzenleme ilanı baştan başlatmaz (90. dakikada süreyi 2 saate çekmek bitişi `şimdi + 2 saat` yapmaz). Bitiş geçmişte kalırsa reddedilir. Dokunulmayan süre (ör. 1, 2, 3 dışındaki yapılandırılmış varsayılan) aynen kalır |
| Bildirimler | bkz. [Etkinlik bildirimleri](#etkinlik-bildirimleri) → *Düzenleme* |
| Ses kanalı | eklenebilir, değiştirilebilir, kaldırılabilir; her kayıtta (değişmese de) bu sunucunun gerçek bir ses kanalı mı diye yeniden denetlenir. İlan kapanmaz, katılımcılar etkilenmez, kart yeniden çizilir; sonraki hatırlatma/başlangıç bildirimi yeni kanalı kullanır; gönderilmiş bildirim düzenlenmez |

Aynı formu ikinci kez kaydetmek `Değişiklik yok; ilan aynı kaldı.` der (sürüm ve kart değişmez). **Her alan** (oyun,
detay, kişi, başlangıç, süre, iki bildirim tercihi, ses kanalı) formun **açıldığı andaki** haliyle karşılaştırılır;
dokunulmamış alan kayıtta veritabanındaki **güncel** değerini korur (aynı tarih başka yazımla — `5.10.2026 21:30` ya da
`5.10.26 21:30` — da dokunulmamış sayılır; metinlerde büyük/küçük harf değişikliği düzenlemedir): iki açık
düzenleme formundan eskisi, yenisinin değiştirdiği hiçbir alanı geri almaz (kayıp güncelleme yok). Değiştirilen alan
güncel duruma göre doğrulanır. Dokunulmamış kişi sayısı, yapılandırılan üst sınır sonradan düşürülmüş olsa da geçerli kalır.
Form gönderildiğindeki denetim yazma kilidi almaz ve hiçbir şey kaydetmez; karar kayıtta kilit altında yeniden verilir.
Silinmiş bir ses kanalı düzenleme formunda yeniden önerilmez (ön seçili gelmez); ayar "yok" bırakılırsa kayıtta kaldırılır (başka bir form o arada yeni bir kanal seçtiyse o kanal korunur). Dokunulmamış kanal, kayıtta saklanacak kanal olarak yeniden doğrulanır; form açıldıktan sonra silinmişse kayıt reddedilir (form yeniden açılınca "yok" ile kaydedilebilir). Saat dilimi çözülemezse (bozuk ayar) yazılan tarih
oluşturmadaki gibi reddedilir.

### Başlangıç ve süre

`EventAt` etkinliğin başlayacağı an, `ExpiresAt` ilanın artık kullanılamayacağı an — ikisi ayrı alanlardır. Başlangıç ya
**şimdi** ya da **mutlak bir an**dır (`LfgStart`); göreli başlangıç kavramı yoktur. Süre dolumu, bildirimler, kart ve ses
aynı `EventAt` hattını kullanır.

| Başlangıç Tarihi alanı | Sonuç |
|---|---|
| boş | Şimdi: `EventAt = null`, `ExpiresAt = CreatedAt + süre` |
| `27.09.2026 21:30` / `27.09.26 21:30` | `EventAt` = girilen tarih/saat, sunucunun saat diliminde; `ExpiresAt = EventAt + süre` |
| `2 saat`, `30 dk`, `1 gün`, `2`, `yarın 21:00`, `27.09.2026`, `21:30` | reddedilir (tarih biçimi hatası) |

Planlıysa `ExpiresAt = EventAt + süre`: ör. Başlangıç Tarihi `05.10.2026 21:30`, Süre 2 saat → ilan 05.10.2026 23:30'da
kapanır; başlangıçtan önce asla expire olmaz. 30 dk hatırlatma `EventAt − 30 dk`'da (21:00), başlangıç bildirimi `EventAt`'te.

### Özel tarih/saat ve saat dilimi

- **Özel tarih/saat, sunucunun saat diliminde yorumlanır. Discord kartında tarih her kullanıcının kendi yerel saatinde
  gösterilir** (`<t:…:F> • <t:…:R>`; bot kartta saat dilimi dönüştürmez).
- Saat dilimi, TSQ Bot'un mevcut sunucu ayarıdır (`/setup` → saat dilimi; `GuildSettings.TimeZoneId`, varsayılan
  **Europe/Istanbul**). LFG ikinci bir saat dilimi ayarı tutmaz, yeni kolon/migration yoktur. Kayıt yalnızca `/setup`
  üzerinden, doğrulanmış IANA kimlikleriyle olur (`GuildTime.TryResolve`; Railway/Linux ve Windows'ta aynı ID'ler);
  `/tsq-admin lfg status` kullanılan saat dilimini gösterir.
- Ayrıştırma açık ve kültürden bağımsızdır (kendi kurallarıyla: `G.A.YYYY S:DD`, `G.A.YY S:DD`, ISO `YYYY-A-G S:DD`; makine
  yereli ya da `Calendar.TwoDigitYearMax` kullanılmaz). **Kısa yıl**, sunucunun saat dilimindeki bu yıla en yakın yıldır
  (bu yıl − 50 … bu yıl + 49; 2026'da `26` → 2026, `27` → 2027) — 365 gün sınırıyla pratikte yalnızca bu yıl ve gelecek
  yıl geçerlidir. `31.02.2026 21:00`, `05/10/2026`, eksik saat vb. → `Tarih/saat anlaşılamadı. Örnek: 27.09.2026 21:30 veya 27.09.26 21:30`.
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
👥 3 / 3
📝 Ranked gireceğiz, mikrofon gerekli.

Katılanlar
@Toro · @Oykeli · @Hasom
🎟️ Bekleme Listesi (2)
`1.` @Arif · `2.` @Shotgun
🤔 Belki
@Despale

🗓️ **Başlangıç:** 5 Ekim 2026 Pazartesi 21:30 • 8 gün içinde    ("şimdi" ilanında: 🕘 **Başlangıç:** Şimdi)
🔊 Ses Odası: #Deadlock
✅ Ekip dolu
⏰ 4 saat içinde kapanır
[🎟️ Sıraya Gir] [Belki] [Ayrıl] [🔊 Ses Odası]      (açık ilanda ilk düğme: [Katıl])
[✏️ Düzenle] [İlanı Kapat]
```

- Başlangıç satırı **her zaman** gösterilir: planlı ilanda `🗓️ **Başlangıç:** <t:…:F> • <t:…:R>`, başlangıcı boş ilanda
  `🕘 **Başlangıç:** Şimdi` (en: `🕘 **Start:** Now`); veritabanında `EventAt` yine `null` kalır ve kapanış geri sayımı değişmez.
- Kapasite yalnızca **Katılanlar** sayısıdır (`3 / 3`); bekleme listesi (sıra numarasıyla, sıra düzeninde) ve Belki
  listesi ayrı gösterilir ve sayılmaz. İkisinin de ilk 20 kişisi gösterilir (`(+N)`); başlık toplamı verir
  (`Bekleme Listesi (28)`).
- Dolu: `✅ Ekip dolu`; **hiçbir düğme kapanmaz**. İlk düğme `[🎟️ Sıraya Gir]` olur (en: `Join Waitlist`) — aynı
  `tsq:lfg:join:<id>` düğmesidir: slot mu sıra mı kararını sunucu kayıtlı duruma göre verir, etiket hiçbir şeyi
  yetkilendirmez.
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

## RSVP: Katıl / Sıraya Gir / Belki / Ayrıl

Üç cevap: `Joined` (kapasiteye sayılır, Full'u belirler, bildirimlerde etiketlenir, ses düğmesini kullanır), `Maybe`
(sayılmaz, etiketlenmez, ses yok) ve `Waitlisted` (sayılmaz, etiketlenmez, ses yok; sırası vardır ve slot açılınca kendiliğinden
`Joined` olur). Sahip her zaman `Joined`'dır.

| Durum → işlem | Sonuç |
|---|---|
| yok → Katıl | boş slot varsa ve kimse beklemiyorsa `Joined` (son slot `Full` yapar); yoksa `Waitlisted`, sıranın sonu: `🎟️ Ekip dolu. Bekleme listesine eklendin. Sıran: #N` |
| Belki → Katıl | slot varsa `Joined`; yoksa sıranın sonuna `Waitlisted` (Katıl'a basan artık Belki kalmaz) |
| Bekliyor → Katıl | değişiklik yok, ikinci satır ya da ikinci sıra yok: `🎟️ Zaten bekleme listesindesin. Sıran: #N` (güncel sıra) |
| Katıldı → Katıl | `Zaten bu ekiptesin.` |
| yok → Belki | `Maybe` (dolu ilanda da mümkün) |
| Katıldı → Belki | slot boşalır; sıranın ilki **aynı yazmada** `Joined` olur |
| Bekliyor → Belki | sırasını bırakır (`WaitlistOrder` silinir); diğerlerinin sırası korunur |
| Katıldı → Ayrıl | kayıt silinir: `Ekipten ayrıldın.`; sıranın ilki aynı yazmada `Joined` olur |
| Bekliyor → Ayrıl | kayıt silinir: `🎟️ Bekleme listesinden çıktın.`; slot boşalmaz |
| Belki → Ayrıl | kayıt silinir: `İlandan ayrıldın.` |
| Sahip | her zaman `Joined`; Belki'ye geçemez, sıraya giremez, ayrılamaz, ilanı kapatabilir |

**Sıra (FIFO) nasıl tutulur.** `lfg_participant.WaitlistOrder` (nullable tam sayı) yalnızca `Waitlisted` iken doludur.
Sıraya giren, yazma kilidi (SQLite `BEGIN IMMEDIATE`) altında ilanın kuyruğundaki en büyük sıra + 1'i alır (kuyruk tamamen
boşalırsa 1'den başlar). Sıra bu kayıtlı sayıdır — yazmaların gerçekleştiği sıra; zaman damgasından, addan ya da Discord
kimliğinden **tahmin edilmez** (aynı anda gelenler de kimlikleri ne olursa olsun geliş sırasını korur; restart'ta aynıdır).
Gösterilen sıra numarası (`#2`) kayıtlı sayı değil, canlı kuyruktan hesaplanır (önündeki bekleyen sayısı + 1); ortadan
biri çıkınca kimsenin kaydı yeniden yazılmaz.

**Otomatik terfi.** Tek bir kural (`LfgRoster.Rebalance`): aktif ilanda `Joined < MaxPlayers` ve kuyruk doluyken kuyruğun
başı (`WaitlistOrder` artan) `Joined` olur (`WaitlistOrder = null`, cevap zamanı = şimdi); sonra `Joined = MaxPlayers` ise
`Full`, değilse `Open`. Çağıran yollar: **Ayrıl**, **Belki** (Joined'dan ya da sıradan), sahibin **kişi sayısını artırması**
(birden çok slot → sıranın ilk N kişisi; kuyruktan büyük kapasite → herkes, ilan `Open`), **`/privacy delete`** (başka
ilanlardaki katılımın silinince) ve Katıl (yalnızca durumu Open/Full yapmak için; yeni gelen hiçbir zaman kuyruğun önüne
geçemez). Kapalı / süresi dolmuş / Orphaned ilanda **hiç terfi yoktur**. Terfi ayrı mesaj, DM ya da ping üretmez; kart
yalnızca son durumu gösterir (ara `9 / 10 Açık` hali hiç çizilmez). Kişi sayısı yine Katılan sayısının altına inemez;
bekleyenler bu sınıra sayılmaz ve kimse sıradan düşürülmez. Ayrı bir bekleme listesi sınırı yoktur (kişi başı tek satır
zaten sınırdır; kart ilk 20'yi gösterir).

**Değişmez kural.** Her yazmadan sonra aktif ilanda: `Joined ≤ MaxPlayers` ve (`bekleme listesi boş` veya
`Joined = MaxPlayers`) — **bekleyen varken boş slot olamaz**.

Durum `lfg_participant.Response` alanındadır; aynı kullanıcı bir ilanda tek satırdır (fikir değiştirmek satırı günceller).

## Yaşam döngüsü

| Olay | Davranış |
|---|---|
| Oluştur | Girdi + kanal kısıtı + ses kanalı doğrulaması + kişi başı aktif ilan sınırı (`Lfg:MaxActiveListingsPerUser`, varsayılan 2, guild başına) denetlenir; ilan `Open`, sahip ilk oyuncu (`1 / N`). Kart yanıt olarak gönderilir, mesaj kimliği kaydedilir. Yanıt hiç gönderilemediyse ilan silinir (sahibin hakkını yemez) |
| Katıl / Sıraya Gir / Belki / Ayrıl | Etkileşim hemen onaylanır (deferred update); ilan veritabanından yeniden okunur: guild, durum, süre, üyelik, boş slot, kuyruk. **Aynı kart** düzenlenir, tıklayana ephemeral sonuç (sıraya girince sıra numarası). Retler ephemeral: `Zaten bu ekiptesin.` · `Bu ilan kapatılmış.` · `Bu ilanın süresi dolmuş.` |
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
  olanlar, bekleme listesindekiler ve ayrılmış kullanıcılar etiketlenmez; 30 dk mesajından sonra katılan (ya da sıradan
  terfi eden) başlangıç mesajında vardır — ona geç bir hatırlatma gönderilmez; hatırlatmadan önce terfi eden
  hatırlatmada da vardır. En fazla 50.
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
@here ayrıştırılmaz), rol yok. LFG'de bunu kullanabilen **tek** yer `LfgNoticeRenderer`'dır ve yalnızca Joined oyuncuların
kimliklerini verir; depo genelindeki tek diğer üretici TSQ Doğum Günü duyurusudur (yalnızca kutlananlar)
(`LfgArchitectureTests.Explicit_user_pings_exist_only_in_the_lfg_notice_renderer_and_the_birthday_announcement`). `Users`
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
kanal/izin bilgisi taşımaz; her tıklamada: doğru guild, ilan aktif, tıklayan **Joined** (bekleme listesindeki →
`🎟️ Bekleme listesindesin. Ekipte yer açıldığında otomatik olarak katılacaksın.`; Belki/üye olmayan →
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
- Testler: 5/6 ilana aynı anda iki katılım → biri katılır, diğeri sıranın başında, sonuç 6/6; 12 eşzamanlı katılım 3 boş
  slota → tam 3, kalan 9 farklı sırada; dolu ilana 20 eşzamanlı katılım → 1…20 sıraları, her cevap kayıtlı sıraya eşit;
  iki eşzamanlı ayrılma → sıranın ilk ikisi terfi eder; ayrılma ile yeni katılım yarışı → önceden bekleyen hep önde;
  sahibin kapasite artışı ile katılım yarışı → kapasite aşılmaz, sıra korunur; aynı kullanıcının 4 eşzamanlı tıklaması →
  tek kayıt, tek sıra; aynı kullanıcının 5 eşzamanlı ilanı (sınır 2) → tam 2; rastgele 150 adımlık karışık akış → her
  adımda değişmez kural.
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

İsteğe bağlı: `/tsq-admin lfg channel kanal:#ekip-bul` → `/ekip` yalnızca o kanalda çalışır. `/tsq-admin lfg channel` (boş)
kısıtı kaldırır. İlan kanalında gerekli: `ViewChannel`, `SendMessages` (bildirimler), `EmbedLinks`. İsteğe bağlı:
`ReadMessageHistory` (silinen kartın erken fark edilmesi), ses kanalında **Move Members** + **Connect** (seste olanı tek
tıkla taşıma). İsteğe bağlılar olmadan özellikler zarifçe geri çekilir; ek gateway intent'i gerekmez.

## Yapılandırma (`Lfg`)

| Ayar | Varsayılan | Aralık |
|---|---|---|
| `DefaultExpirationMinutes` | `120` | 15–720 |
| `MaxActiveListingsPerUser` | `2` | 1–10 |
| `MaxPlayersPerListing` | `20` | 2–26 (kural üst sınırı 50; ama form her kişi sayısını tek bir seçicide sunar ve Discord en fazla 25 seçenek gösterir → 26 üstü başlangıçta yapılandırma hatası, seçenekler asla kesilmez) |

Hatırlatma öncesi süre (30 dk) ve başlangıç toleransı (5 dk) sabittir (`LfgRules`), yapılandırma değildir. Modül **her
sunucuda varsayılan kapalıdır** (`EnabledByDefault=false`) ve depodaki standart mekanizmayla açılır: `/modules enable lfg`.

## Veri ve gizlilik

Tablolar (additive migration'lar `LfgModule`, `LfgScheduledEvents`, `LfgWaitlist`): `lfg_listing` (ilan; oyun adı + detay
kullanıcının kendi metni, `EventAt`, `VoiceChannelId`, bildirim tercihleri ve işaretleri), `lfg_participant` (ListingId +
UserId + `Response` Joined/Maybe/Waitlisted + cevap zamanı + `WaitlistOrder`), `lfg_guild_config` (isteğe bağlı kanal). İndeksler: `(Status, ExpiresAt)` süre
dolumu, `(GuildId, OwnerUserId, Status)` aktif ilan sınırı, `CardStale = 1` kısmi, `(Status, EventAt)` kısmi
(`EventAt IS NOT NULL`, vadesi gelen bildirimler), `lfg_participant(UserId)` gizlilik; FK `ListingId` → `lfg_listing`
(cascade). `LfgScheduledEvents` yalnızca kolon/indeks ekler: mevcut katılımcılar `Joined`, mevcut ilanlar `EventAt = null`,
bildirim bayrakları kapalı, ses kanalı yok — V1 anlamı korunur (test). `LfgWaitlist` yalnızca nullable
`lfg_participant.WaitlistOrder` ekler (varsayılan `NULL`; mevcut Joined/Maybe satırları ve ilan durumları değişmez, test);
`Waitlisted` enum değeri mevcut int kolonda saklanır. Ek indeks yok: kuyruk sorguları birincil anahtarın öneki
`ListingId` ile ilanın satırlarını okur.

`/privacy export` açtığın ilanları (oyun, detay, durum, zamanlar, bildirim tercihleri), katıldığın/belki dediğin/sırada
beklediğin ilanları (cevabınla: `Joined` / `Maybe` / `Waitlisted`) ve seni etiketleyen, henüz silinmemiş bildirim sayısını
içerir. `/privacy delete` bu ilanlardan seni çıkarır (bir Katılan silinince boşalan slot aynı işlemde sıranın ilkine geçer;
kimse beklemiyorsa dolu ilan yeniden açılır; kart senin olmadan yeniden çizilir), açtığın
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
