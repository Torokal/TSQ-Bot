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

## Modül yönetimi: tek düz komut `/tsq-admin` (`default_member_permissions = ManageGuild (32)` + sunucu tarafı yetki kontrolü)

Modüllerin yönetici işlemleri **alt komutu ve alt komut grubu olmayan** tek bir slash komutundadır; menüde tek satır görünür:

`/tsq-admin modul:<modül> islem:<işlem> [kanal] [uye] [rol] [tarih]`

| Seçenek | Tür | Ne işe yarar |
|---|---|---|
| `modul` | metin, zorunlu, autocomplete | `birthday`, `esports`, `f1`, `lfg`, `live`, `news`, `updates`, `volleyball` ("Haberler — news" gibi Türkçe etiketle önerilir) |
| `islem` | metin, zorunlu, autocomplete | Yalnızca seçili modülün, çağıranın yetkisinin yettiği işlemleri önerilir; modül seçilmeden öneri yoktur |
| `kanal` | kanal seçici (metin/duyuru), isteğe bağlı | Kanal isteyen işlemler (`configure`, `channel`, `configure-channel`) |
| `uye` | kullanıcı seçici, isteğe bağlı | `birthday set/show` |
| `rol` | rol seçici, isteğe bağlı | `esports roles-map`, `f1/volleyball configure-role` (formu önceden doldurur) |
| `tarih` | metin, isteğe bağlı | `birthday set` (`14.03`, `14/03`, `14-03`) |

Örnekler: `/tsq-admin modul:news islem:status` · `/tsq-admin modul:news islem:configure kanal:#haberler` ·
`/tsq-admin modul:birthday islem:set uye:@Toro tarih:14.03` · `/tsq-admin modul:lfg islem:channel kanal:#oyuncu-bul` ·
`/tsq-admin modul:esports islem:filters-show` · `/tsq-admin modul:f1 islem:configure-notifications`.

- **Doğrudan / form:** Gerekli girdiler verilmişse işlem hemen çalışır (ek onay yok). Eksik girdi veya çok alanlı ayar
  yalnızca komutu çalıştırana görünen küçük bir arayüz açar: kanal/üye seçici, seçim listesi, düğmeler veya modal. Çok
  alanlı formlar (esports ayarları, F1/voleybol bildirimleri ve rolü, rol eşleme) mevcut değerleri gösterir ve **yalnızca
  değiştirilen alanları** yazar; başka bir yöneticinin o sırada değiştirdiği alan ezilmez. İptal hiçbir şey değiştirmez.
- **Doğrulama:** Bilinmeyen modül, başka modülün işlemi (ör. `modul:news islem:roles-map`) veya işlemin kullanmadığı bir
  seçenek (ör. `news status` + `uye`) çalıştırılmadan reddedilir; başka bir işleme yönlendirilmez. Kanal bu sunucunun metin
  veya duyuru kanalı olmalıdır.
- **Yetki:** Kök yalnızca Manage Server ile görünür; her işlem ayrıca kendi iznini ister (çoğu Manage Server; esports rol
  işlemleri Manage Server + Manage Roles; `birthday set/show` Administrator veya sunucu sahibi) ve modül servisi
  `Authorize.Require` ile yeniden denetler. Öneri listesi yalnızca kolaylıktır, yetki garantisi değildir.
- **Formlar:** Rastgele taslak kimliğiyle (`tsq:adm:<taslak>:<eylem>`) çalışır; taslak komutu açan kullanıcıya, sunucuya,
  modüle ve işleme bağlıdır, 15 dakika geçerlidir, bellektedir (yeniden başlatmada kaybolur ve bunu söyler). Her tıklama ve
  gönderimde sahip, sunucu, süre ve yetki yeniden denetlenir; çift tıklama değişikliği bir kez yapar.
- **Modül kapısı:** Yönetim işlemleri modül kapalıyken de çalışır (eskisi gibi); esports `panel` modül kapalıysa reddedilir.
  Bir modülü kapatmak diğer modüllerin işlemlerini etkilemez.
- **Arayüz farkı:** Discord'un Entegrasyonlar ekranında izin tek komut (`/tsq-admin`) üzerinden verilir; işlem başına
  Discord izni yoktur.
- `/help` yöneticilere `/tsq-admin` kullanımını ve kullanabilecekleri modülleri gösterir.

### Eski → yeni (47 işlem)

| Eski | Yeni (`/tsq-admin …`) | Ek girdi |
|---|---|---|
| `/birthday-admin set member date` | `modul:birthday islem:set uye: tarih:` | eksik üye → üye seçici; eksik tarih → tarih formu |
| `/birthday-admin show member` | `modul:birthday islem:show uye:` | eksik üye → üye seçici |
| `/birthday-admin configure channel` | `modul:birthday islem:configure kanal:` | eksik kanal → kanal seçici |
| `/birthday-admin status \| doctor` | `modul:birthday islem:status \| doctor` | — |
| `/esports-admin configure …` | `modul:esports islem:configure [kanal:]` | kanal ile yalnızca kanal; kanalsız → form (kanal, hatırlatma/sonuç/spoiler, dakika) |
| `/esports-admin panel \| preview \| pause \| resume \| doctor` | `modul:esports islem:panel \| preview \| pause \| resume \| doctor` | — (panel herkese açık) |
| `/esports-admin filters show \| clear` | `modul:esports islem:filters-show \| filters-clear` | — |
| `/esports-admin filters team \| tournament action …` | `modul:esports islem:filters-team \| filters-tournament` | Ekle (ara → seç) / Kaldır (mevcutlardan seç) |
| `/esports-admin filters tier action tier` | `modul:esports islem:filters-tier` | eklenecek / kaldırılacak tier seçimi |
| `/esports-admin filters vrs top` | `modul:esports islem:filters-vrs` | sayı formu (mevcut değer dolu; 0 = kapalı) |
| `/esports-admin roles list` | `modul:esports islem:roles-list` | — |
| `/esports-admin roles map role …` | `modul:esports islem:roles-map [rol:]` | form (rol, takım araması, ping'ler); belirsiz takım → seçim |
| `/esports-admin roles unmap mapping` | `modul:esports islem:roles-unmap` | eşleme seçimi (sayfalı) |
| `/esports-admin roles selfservice mapping enabled` | `modul:esports islem:roles-selfservice` | eşleme seçimi (sayfalı) → Aç / Kapat |
| `/f1-admin preview [card]` | `modul:f1 islem:preview` | kart seçici |
| `/f1-admin status \| doctor \| pause \| resume` | `modul:f1 islem:status \| doctor \| pause \| resume` | — |
| `/f1-admin configure channel channel` | `modul:f1 islem:configure-channel kanal:` | eksik kanal → kanal seçici |
| `/f1-admin configure notifications …` | `modul:f1 islem:configure-notifications` | form: 16 anahtar |
| `/f1-admin configure role …` | `modul:f1 islem:configure-role [rol:]` | form: rol, ping'ler, "rolü kaldır" |
| `/f1-admin configure spoilers enabled` | `modul:f1 islem:configure-spoilers` | Aç / Kapat |
| `/lfg-admin channel [channel]` | `modul:lfg islem:channel [kanal:]` | kanalsız → kanal seçici veya açık "kısıtlamayı kaldır" |
| `/lfg-admin status` | `modul:lfg islem:status` | — |
| `/live-admin doctor` | `modul:live islem:doctor` | — |
| `/news-admin configure channel` | `modul:news islem:configure kanal:` | eksik kanal → kanal seçici |
| `/news-admin pause \| resume \| preview \| status \| doctor` | `modul:news islem:pause \| resume \| preview \| status \| doctor` | — |
| `/volleyball-admin preview [card]` | `modul:volleyball islem:preview` | kart seçici |
| `/volleyball-admin status \| doctor \| pause \| resume` | `modul:volleyball islem:status \| doctor \| pause \| resume` | — |
| `/volleyball-admin configure channel channel` | `modul:volleyball islem:configure-channel kanal:` | eksik kanal → kanal seçici |
| `/volleyball-admin configure notifications …` | `modul:volleyball islem:configure-notifications` | form: 5 anahtar |
| `/volleyball-admin configure role …` | `modul:volleyball islem:configure-role [rol:]` | form: rol, ping'ler, "rolü kaldır" |

Eski `/…-admin` adları ve 2026-09-30'daki gruplu `/tsq-admin <modül> <işlem>` yolları kaydedilmez. Adı `-admin` ile bitmeyen
komutlar (`/giveaway`, `/ozetle`, `/setup`, `/modules`, `/help`, `/bot`, `/privacy`, `/esports`, `/f1`, `/volleyball`,
`/birthday`, `/quote`, Apps → Quote, `/ekip`, döviz, saat, randomizer, `/ongoru`) değişmedi. Tablonun tamamı
`TsqAdminCommandTests.Moves` içinde test edilir.

## Formula 1 (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/f1 next` | Sıradaki (veya süren) Grand Prix: tur, pist, yarış saati, tüm seanslar (Discord zaman damgaları) |
| `/f1 schedule [round]` | Parametresiz tam sezon takvimi; `round:N` ile o hafta sonunun ayrıntılı programı. `next`/`schedule`/`standings`/`now` kanalda herkese açık, `results` ephemeral |
| `/f1 results [session] [spoiler]` | Önbellekteki son sınıflandırma (latest, race, sprint, qualifying, sprint-qualifying, fp1–fp3) |
| `/f1 now` | Canlı sağlayıcıya göre süren seans; bilinmiyorsa "kullanılamıyor" (programdan tahmin yok) |
| `/f1 standings drivers` / `/f1 standings constructors` | Sağlayıcının puan tablosu, tur, güncellik |

Hepsi yalnızca botun önbelleğinden okur (etkileşim yolunda sağlayıcı çağrısı yok; mimari testli) ve ephemeral yanıt verir.

## Formula 1 yönetici (`/tsq-admin modul:f1`, ManageGuild + sunucu tarafı `Authorize.Require`)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:f1 islem:configure-channel` | Bildirim kanalı (bu sunucudan; eksik izinler uyarılır, başka kanala otomatik geçiş yok) |
| `/tsq-admin modul:f1 islem:configure-notifications` | Antrenman/sprint/yarış başlangıç ve sonuç, puan durumu; sıralama türleri (vars. kapalı) |
| `/tsq-admin modul:f1 islem:configure-role` | İsteğe bağlı ping rolü (`ping_role`; asla @everyone), başlangıç/sonuç ping anahtarları, `clear` |
| `/tsq-admin modul:f1 islem:configure-spoilers` | Spoiler modu |
| `/tsq-admin modul:f1 islem:preview` · `status` · `doctor` · `pause` · `resume` | TEST/DEMO pingsiz önizleme · ayarlar · tanı · duraklat/devam |

Ayrıntı: [FORMULA1.md](FORMULA1.md).

## Voleybol — Filenin Sultanları (modül açıkken; yalnızca Türkiye Kadın A Milli Takımı)

| Komut | Ne yapar |
|---|---|
| `/volleyball next` | Sıradaki (veya sağlayıcıya göre süren) maç: rakip, saat, turnuva, salon, güncellik |
| `/volleyball schedule` | Yaklaşan maçlar ve son sonuçlar |
| `/tsq-admin modul:volleyball islem:configure-channel \| configure-notifications \| configure-role` | Yönetici (ManageGuild + `Authorize.Require`): kanal; `match_reminder_15m`, `match_started`, `set_finished`, `match_finished`, `match_postponed_cancelled`; isteğe bağlı `ping_role` (asla @everyone) |
| `/tsq-admin modul:volleyball islem:preview` · `status` · `doctor` · `pause` · `resume` | TEST/DEMO pingsiz önizleme · ayarlar · tanı · duraklat/devam |

Başka takım seçtiren komut yoktur. Ayrıntı: [volleyball/VOLLEYBALL.md](volleyball/VOLLEYBALL.md).

## TSQ Live — Twitch + Kick yayın duyuruları

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:live islem:doctor` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): `Live:Enabled`, modül kapısı, duyuru kanalı ve izinleri (Mention Everyone dahil), Twitch/Kick yetkilendirme ve son başarılı uzlaştırma, yayıncı durumları ve duyuru mesajları. Sağlayıcıya istek atmaz, secret göstermez |

Duyuru gönderen, yayıncı ekleyen veya ping atan komut yoktur (yayıncılar ve kanal yapılandırmadır). Ayrıntı:
[live/TSQ_LIVE.md](live/TSQ_LIVE.md).

## TSQ LFG — Oyuncu Bul (modül açıkken)

| Komut / etkileşim | Ne yapar |
|---|---|
| `/ekip` (parametresiz) | Herkes: ekip ilanı **formunu** açar — modal (5 bileşen): oyun (metin), kişi sayısı (seçici, 2…üst sınır), başlangıç tarihi (metin; boş = şimdi · `27.09.2026 21:30` · `27.09.26 21:30`, sunucunun `/setup` saat diliminde; 1 dk – 1 yıl ileri; göreli süre yok), ses kanalı (yerel kanal seçici, yalnızca bu sunucunun ses kanalı), bildirimler (iki onay kutusu, varsayılan kapalı; başlangıç tarihi yoksa ret); ardından yalnızca gönderene görünen ayarlar: ilan süresi (1/2/3 saat), **📝 Detay Ekle** (ayrı küçük modal) → **İlanı Oluştur**. Kart kanala ping'siz gönderilir. Kişi başı aktif ilan sınırı ve isteğe bağlı kanal kısıtı form açılmadan ve kayıtta denetlenir |
| Buton `✏️ Düzenle` (`tsq:lfg:edit:<id>`) → aynı form, dolu | **Yalnızca ilan sahibi** (moderatör/yönetici de değil); aktif (Açık/Dolu) ilanlar; kişi sayısı Katılan sayısının altına inemez, başlangıç yalnızca etkinlik başlamadan değişir, süre bitişi baştan başlatmaz; aynı kart ping'siz güncellenir |
| Form adımları (`tsq:lfg:form:<taslak>`, `tsq:lfg:draft:notify|duration|save|back|cancel:<taslak>`) | Yalnızca formu açan kullanıcı (bellekteki taslak kullanıcı + sunucuya bağlı, 30 dk); her kayıt sunucu tarafında yeniden denetlenir |
| Buton `Katıl` (doluyken `🎟️ Sıraya Gir`, aynı düğme) · `Belki` · `Ayrıl` (`tsq:lfg:join|maybe|leave:<id>`) | Herkes; her tıklamada sunucu tarafında guild, durum, süre, üyelik, boş slot ve bekleme sırası yeniden denetlenir; Belki ve bekleme listesi kapasiteye sayılmaz; boşalan slot sıranın ilkine geçer; sonuç ephemeral |
| Buton `İlanı Kapat` (`tsq:lfg:close:<id>`) → `Evet, kapat` / `Vazgeç` | Yalnızca ilan sahibi veya **Manage Messages** (ya da Administrator) yetkili moderatör (`Authorize.Require`); ephemeral onay |
| Buton `🔊 Ses Odası` / `🔊 Ses Odasına Katıl` (`tsq:lfg:voice:<id>`, kart ve bildirimlerde) | Yalnızca Joined oyuncu; zaten seste olanı bot Move Members + Connect ile taşır, aksi hâlde kanal + "Ses kanalını aç" link butonu (sese otomatik bağlama yok — Discord API'si izin vermez) |
| Etkinlik bildirimleri (isteğe bağlı) | Başlangıçtan 30 dk önce / başlangıçta **yalnızca Joined oyuncuları** etiketleyen yeni mesajlar (outbox; tekrarsız, geç gönderilmez) |
| `/tsq-admin modul:lfg islem:channel [channel]` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): `/ekip`'i tek kanala kısıtlar; boş = her kanal |
| `/tsq-admin modul:lfg islem:status` | Yönetici: modül durumu, kanal, aktif ilan sayıları, sınırlar, bekleyen kart düzenlemeleri |

Bot izinleri (ilan kanalı): `ViewChannel`, `SendMessages`, `EmbedLinks` (süre dolumu/kapatma düzenlemesi); isteğe bağlı
`ReadMessageHistory` (silinen kartın erken fark edilmesi); ses kanalında `MoveMembers` + `Connect` (seste olanı tek tıkla
taşıma — davet izinlerinde yok, isteğe bağlı olarak yalnızca ilgili ses kanallarında verilebilir). Ek gateway intent'i gerekmez.

Ayrıntı: [lfg/TSQ_LFG.md](lfg/TSQ_LFG.md).

## TSQ Quote (modül açıkken)

| Komut | Ne yapar |
|---|---|
| Mesaja sağ tık (mobilde uzun bas) → **Uygulamalar → Quote** (MESSAGE komutu `Quote`) | Herkes: tıklanan mesajı aynı kartla **bu kanala** gönderir. Mesaj etkileşimle gelir (Discord'dan okunmaz; Message Content gerekmez). Sunucu tarafında: mesaj bu kanalın olmalı; botun burada `ViewChannel` + `AttachFiles` izni; üyenin burada `ViewChannel` + `ReadMessageHistory` izni. Kart mesajın kanalında kaldığı için özel thread ve yaş sınırlı kanalda da çalışır |
| `/quote message:<mesaj-id> [channel]` | Herkes: mesajı siyah-beyaz alıntı görseline (`quote.png`) çevirip **bu kanala** gönderir. Mesaj kimliği bu kanalda, `channel` verilirse o kanalda aranır (kanallar taranmaz); mesaj bağlantısı da kabul edilir (kanal bağlantıdan). Sunucu tarafında: botun **bu kanalda** `ViewChannel` + `AttachFiles` izni (yoksa hiçbir şey okunmaz); mesaj **bu sunucuya** ait olmalı; üyenin ve botun kaynak kanalda `ViewChannel` + `ReadMessageHistory` izni olmalı (thread'de üst kanal; özel thread desteklenmez); yaş sınırlı kanaldan yaş sınırı olmayan kanala alıntı yapılmaz. Tüm "yok/erişim yok" durumları tek, ephemeral cevaptır; kart ping'sizdir |

Mesaj kimliği: Discord → Ayarlar → Gelişmiş → **Geliştirici Modu**; mesaja sağ tık → **Mesaj Kimliğini Kopyala**; aynı
kanalda `/quote message:<mesaj-id>`, başka kanalda `/quote message:<mesaj-id> channel:<kanal>`.

Bot izinleri: kaynak kanalda `ViewChannel` + `ReadMessageHistory`; komutun çalıştığı kanalda `ViewChannel` +
`AttachFiles` (kart etkileşim takip mesajıdır, `SendMessages` gerekmez). Başka üyelerin mesaj metni için uygulamanın
**Message Content** erişimi (Developer Portal → Bot → Privileged Gateway Intents) `/quote` için açık olmalıdır (Apps → Quote için değil); gateway intent'i
değişmez (aşağıya bakın).
Ayrıntı: [quote/TSQ_QUOTE.md](quote/TSQ_QUOTE.md).

## TSQ Döviz & Altın (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/dolar` · `/euro` · `/altın` | Herkes, argümansız, **yalnızca döviz kanalında** (`Currency:ChannelId` = `1242464361855848459`): güncel USD/TRY, EUR/TRY veya gram altın alış/satış fiyatını **herkese açık** bir kartla gösterir (kaynak + sağlayıcının güncelleme zamanı). Yedek kaynak (TCMB gösterge kuru / Trunçgil) ve eski veri kartta açıkça yazılır; veri yoksa takip kodlu kısa bir mesaj. Başka kanalda yalnızca kullanana görünen "Bu komutu yalnızca <#…> kanalında kullanabilirsiniz." — fiyat sorgusu yapılmaz. Ping yok |
| `/çevir miktar kaynak hedef` | Herkes, **yalnızca döviz kanalında**: TL ↔ USD, TL ↔ EUR, TL ↔ gram altın dönüşümü (varlık → TL: alış kuru; TL → varlık: satış kuru); sonuç herkese açık kart. Yanlış kanal, geçersiz miktar, aynı/çapraz dönüşüm yalnızca kullanana görünen uyarıyla reddedilir, fiyat sorgusu yapılmaz. Ping yok |

Her gün 09:00'da (Türkiye) döviz kanalına tek bir günlük kart gönderilir (outbox; modül kapalıysa gönderilmez). Bu yüzden bot
döviz kanalında `ViewChannel` + `SendMessages` + `EmbedLinks` ister; komut cevapları için ek izin gerekmez. `/altın` Türkçe `ı` ile kayıtlıdır: Discord komut adlarında her dilden küçük
harfe izin verir; manifest doğrulayıcısı Discord'un kuralını uygular (1–32 küçük harf/rakam, `-`, `_`).
Ayrıntı: [currency/TSQ_CURRENCY.md](currency/TSQ_CURRENCY.md).

## TSQ Randomizer (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/zarat zar:<girdi>` | Herkes: `1-20`, `2-6`, `2d6` (1–20 zar, 2–10.000 yüz) — zarları ve toplamı **herkese açık** kartla gösterir |
| `/randomsayi maksimum:<sayı> [minimum:<sayı>]` | Herkes: iki ucu dahil rastgele sayı (`minimum` varsayılan 1; negatif olabilir; ±1.000.000.000) |
| `/sec seçenekler:<metin>` | Herkes: virgül ve/veya `\|` ile (karışık da olur) ayrılmış 2–25 farklı seçenekten birini seçer; seçenek metni mention/markdown olarak görüntülenmez |
| `/yazitura` | Herkes: YAZI veya TURA |

Sonuçlar herkese açık, geçersiz girdi uyarıları yalnızca kullanana görünür (ephemeral); hiçbir cevap ping atmaz. Bot izni
gerekmez (yalnızca etkileşim cevabı). Ayrıntı: [randomizer/TSQ_RANDOMIZER.md](randomizer/TSQ_RANDOMIZER.md).

## TSQ Saat Dönüştürücü (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/saat time:<saat> [timezone:<bölge>]` | Herkes: `21:00`, `9:00`, `09:00` veya `21.00` — saati kaynak bölgenin **bugünkü tarihiyle** okur (varsayılan Türkiye / Europe/Istanbul; `timezone`: `tr`, `uk`/`gmt`/`bst`, `ny`/`est`/`edt`, `chicago`/`cst`/`cdt`, `la`/`pst`/`pdt`, `utc` vb., autocomplete ile); Türkiye, Birleşik Krallık, New York, Chicago ve Los Angeles karşılıklarını (gün değişiyorsa "Önceki gün"/"Sonraki gün") ve tek bir Discord zaman damgasını (`<t:…:t>` · `<t:…:R>`, izleyenin kendi saatinde) **herkese açık** kartla gösterir |
| `/saat time:<saat\|now> [timezone:…] [date:<GG.AA[.YYYY]>] [to:<bölge>]` | `date`: saati o tarihte okur (yıl yoksa kaynak bölgedeki bu yıl; DST o tarihe göre). `to`: yalnızca tek hedef satırı (aynı alias kataloğu; `tokyo`/`jst` dahil). `time:now`: şu anki an (`date` ile birlikte reddedilir). Kartta ayrıca kopyalanabilir ham `<t:…:t>` / `<t:…:R>` kodu |

Dönüşüm işletim sisteminin IANA saat dilimi verisiyle yapılır (DST otomatik, sabit UTC offset yok; `pdt`/`pst` gibi
kısaltmalar yalnızca bölgeyi seçer, o tarihteki offset'i `TimeZoneInfo` belirler). Kaynak bölgede yaz saati geçişi nedeniyle
hiç yaşanmayan veya iki kez yaşanan saatler tahmin edilmez, kısa bir uyarıyla reddedilir. Geçersiz saat uyarısı
yalnızca kullanana görünür (ephemeral); cevap ping atmaz. Bot izni gerekmez (yalnızca etkileşim cevabı). Veri saklanmaz.

## TSQ Çekiliş (modül açıkken; `/giveaway`, ManageGuild + sunucu tarafı `Authorize.Require`)

| Komut | Ne yapar |
|---|---|
| `/giveaway create` | Formu açar (Ödül, Süre, Kazanan Sayısı, Açıklama); bot kartı bu kanala gönderir ve 🎉 ekler. Üyeler 🎉 tepkisiyle katılır; süre dolunca otomatik çekiliş |
| `/giveaway end giveaway:<hedef>` | Aktif çekilişi hemen sonuçlandırır |
| `/giveaway cancel giveaway:<hedef>` | Kazanan çekmeden iptal eder |
| `/giveaway reroll giveaway:<hedef>` | Sonuçlanmış çekilişte önceki kazananlar hariç yeniden çeker |

Hedef: `#12` (autocomplete), mesaj bağlantısı veya mesaj ID'si. Yanıtlar ephemeral; kart ve düzenlemeleri ping atmaz,
yalnızca kazanan duyurusu o çekilişin kazananlarını etiketler. Kanalda bot izni (hepsi zorunlu): View Channel, Send Messages,
Embed Links, **Add Reactions**, Read Message History (form açılmadan ve gönderimde denetlenir). `/modules disable giveaway`
yalnızca yeni çekilişleri kapatır; başlamış çekilişler duyurusuyla birlikte normal sonuçlanır. Ayrıntı: [giveaway/TSQ_GIVEAWAY.md](giveaway/TSQ_GIVEAWAY.md).

## TSQ Özet (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/ozetle` | `Summary:AllowedRoleIds` rollerinden **en az birine** sahip üyeler: bu kanalın (thread'de yalnızca thread'in) son 100 üye mesajını o anda okur, tek AI isteğiyle kısa bir Türkçe özet çıkarır ve **herkese açık** normal mesaj olarak gönderir (ping yok) |

Komut herkese görünür; rol kontrolü çalışma anında sunucu tarafında yapılır (Discord komut izinleriyle gizlenmez). Rolü
olmayan üyeye, gerekli rollerin adlarıyla birlikte yalnızca kendisinin göreceği bir mesaj gider. Aynı kanal veya thread'de
önceki başarılı özetten sonra en az 100 yeni üye mesajı gerekir ve başarılı özetler arasında 2 dakika bekleme vardır. Diğer
retler ve hatalar (yapılandırma yok, desteklenmeyen kanal, üyenin veya botun View Channel + Read Message History izni yok,
30 sn üye cooldown'u, aynı kanalda süren özet, bot genelinde en fazla 2 eşzamanlı özet, ilk özette 5'ten az mesaj, önceki
özet kontrol edilemedi, AI hatası, zaman aşımı) de yalnızca kullanana görünür. Retry ve yedek model yoktur. Kanalda bot izni:
View Channel, Read Message History. Ayrıntı: [summary/TSQ_SUMMARY.md](summary/TSQ_SUMMARY.md).

## TSQ Öngörü (modül açıkken; `/ongoru`, grup izni yok — her alt komut kendi yetkisini denetler)

| Komut | Kanal | Kim | Görünürlük |
|---|---|---|---|
| `/ongoru yarat` | öngörü kanalı | yaratıcı rolü (Administrator tek başına yetmez) | form + özel önizleme; kart herkese açık |
| `/ongoru cuzdan`, `/ongoru gunluk`, `/ongoru tahminlerim` | komut kanalı | herkes | özel |
| `/ongoru liderlik`, `/ongoru turnuva durum` | komut kanalı | herkes | herkese açık, ping'siz |
| `/ongoru turnuva bitir` | komut kanalı | Administrator veya sunucu sahibi (yaratıcı rolü yetmez) | özel önizleme + [🏁 Turnuvayı Bitir]; kapanış duyurusu herkese açık, ping'siz |
| Kart: 🎯 Tahmin Yap → form (sonuç + tutar) → Submit = tahmin | öngörü kanalı | herkes (botlar hariç) | özel makbuz (ikinci onay yok) |
| ✏️ Tahminimi Değiştir / ↩️ Tahminimi Geri Çek (özel mesajda; öngörü açıkken) | öngörü kanalı | yalnızca kendi aktif tahmini | özel |
| Kart: 🔒 Kilitle | öngörü kanalı | öngörünün yaratıcısı (rolü hâlâ varken), Administrator veya sunucu sahibi | özel onay |
| Kart: ✅ Sonuçlandır | öngörü kanalı | aynı | özel sonuç seçimi + önizleme + onay |
| Kart: ↩️ İptal / İade | öngörü kanalı | aynı | gerekçe formu + özel önizleme + onay |

Yönetim ayrı slash komutuyla değil yalnızca kart butonlarıyla yapılır (`/ongoru kilitle|sonuclandir|iptal` ve
`/ongoru-admin` yoktur). Yetkisiz tıklama yalnızca tıklayana "Bu öngörüyü yönetme yetkiniz yok." gösterir ve hiçbir şey
değiştirmez. Kullanıcının TSQ Coin'ini sıfırlayan bir komut yoktur; 1000 TSQ Coin yalnızca yeni turnuvada verilir.

Kanallar ve rol yapılandırmadan gelir (`Predictions:ChannelId`, `Predictions:CommandsChannelId`, `Predictions:CreatorRoleId`)
ve ID birebir eşleşmelidir (thread'ler, DM ve diğer kanallar reddedilir; yöneticiler dahil). Yetki her komutta, form
gönderiminde, önizleme onayında ve her bileşen tıklamasında sunucu tarafında yeniden denetlenir. Kanalda bot izni: öngörü
kanalı View Channel, Send Messages, Embed Links, Read Message History; komut kanalı View Channel, Send Messages, Embed Links.
Add Reactions ve Administrator gerekmez. Ayrıntı: [predictions/TSQ_PREDICTIONS.md](predictions/TSQ_PREDICTIONS.md).

## TSQ Haber — Aurora · HLTV (`/tsq-admin modul:news`, ManageGuild + sunucu tarafı `Authorize.Require`)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:news islem:configure kanal:` | Haber kanalı (View Channel + Send Messages + Embed Links; yalnızca bundan sonra yayımlanan haberler) |
| `/tsq-admin modul:news islem:pause` / `resume` | Gönderimi duraklatır / sürdürür (duraklatma dönemi telafi edilmez) |
| `/tsq-admin modul:news islem:preview` | Son eşleşen gerçek haberin veya açıkça sentetik örneğin kartını yalnızca yöneticiye gösterir |
| `/tsq-admin modul:news islem:status` · `doctor` | Mod, modül, kanal/izinler, akış sonucu ve sonraki kontrol, baseline, kadro güncelliği, kapsam |

Tüm cevaplar ephemeral. Komutlar modül kapalıyken de çalışır. Elle haber/bağlantı komutu yoktur. Ayrıntı:
[news/TSQ_NEWS.md](news/TSQ_NEWS.md).

## TSQ Bot Updates — oyun güncellemeleri (`/tsq-admin modul:updates`, ManageGuild + sunucu tarafı `Authorize.Require`)

| Komut | Ne yapar |
|---|---|
| `/tsq-admin modul:updates islem:configure kanal:` | Güncelleme kanalı (View Channel + Send Messages + Embed Links; yalnızca bundan sonra yayımlanan güncellemeler) |
| `/tsq-admin modul:updates islem:games` | Kayıtlı oyunlar ve bu sunucudaki açık/kapalı durumu |
| `/tsq-admin modul:updates islem:game-enable` / `game-disable` | Bir oyunu açar / kapatır (tek kayıtlı oyun varken doğrudan; birden fazlaysa yalnızca kayıtlı oyunları içeren özel seçim) |
| `/tsq-admin modul:updates islem:pause` / `resume` | Gönderimi duraklatır / sürdürür (duraklatma dönemi telafi edilmez) |
| `/tsq-admin modul:updates islem:preview` | Kayıtlı son gerçek güncellemenin veya açıkça sentetik örneğin kartını yalnızca yöneticiye gösterir |
| `/tsq-admin modul:updates islem:status` · `doctor` | Mod, modül, kanal/izinler, açık oyunlar, kaynak sonucu ve sonraki kontrol, baseline, son bulunan güncelleme, son kart |

Tüm cevaplar ephemeral. Komutlar modül kapalıyken de çalışır. Genel kullanıcı komutu ve elle güncelleme gönderme komutu
yoktur; yeni slash komutu eklenmez (`/tsq-admin` şeması aynıdır). Ayrıntı: [updates/TSQ_UPDATES.md](updates/TSQ_UPDATES.md).

## TSQ Doğum Günü (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/birthday set <tarih>` · `show` · `remove` | Herkes, yalnızca **kendi** kaydı: gün + ay (`14.03`, `14/03`, `14-03`; yıl yok). Başkasının kaydına erişen komut veya liste yok |
| `/tsq-admin modul:birthday islem:set uye:@üye tarih:14.03` | **Yalnızca Administrator veya sunucu sahibi** (etkin izinlerden, sunucu tarafında; Manage Server yetmez): başka bir üyenin gün + ayını oluşturur/günceller; ephemeral, ping'siz cevap |
| `/tsq-admin modul:birthday islem:show uye:@üye` | **Yalnızca Administrator veya sunucu sahibi** (etkin izinlerden, DB okunmadan önce; Manage Server yetmez): tek bir üyenin kayıtlı gün + ayını gösterir; ephemeral, ping'siz; denetim logu (tarih yok). Liste komutu yok |
| `/tsq-admin modul:birthday islem:configure kanal:` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): duyuru kanalı |
| `/tsq-admin modul:birthday islem:status` · `doctor` | Yönetici: ayarlar, bugünün durumu, zamanlayıcı; kanal/rol hiyerarşisi/izin/veritabanı kontrolü |

Bot izinleri: duyuru kanalında `ViewChannel` + `SendMessages` (düz mesaj; yalnızca o günün kutlananlarını pingler, @everyone/rol asla); Doğum Günü rolü için sunucuda
`Manage Roles` ve botun en yüksek rolü bu rolün **üstünde**. Ek gateway intent'i gerekmez (üye rolleri REST ile okunur).
Ayrıntı: [birthday/TSQ_BIRTHDAY.md](birthday/TSQ_BIRTHDAY.md).

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
| `/tsq-admin modul:esports islem:configure [kanal:]` | `kanal:` ile yalnızca kanal; kanalsız form: kanal seçici, hatırlatma/sonuç/spoiler anahtarları, hatırlatma dakikası 1–120 (mevcut değerlerle dolu). Kanal bu sunucuda ve metin kanalı olmalı; eksik bot izinleri uyarılır |
| `/tsq-admin modul:esports islem:filters-show \| filters-team \| filters-tournament \| filters-tier \| filters-vrs \| filters-clear` | Kural: aynı tür VEYA, farklı türler VE; VRS verisi yoksa filtre durdurur |
| `/tsq-admin modul:esports islem:roles-list \| roles-map \| roles-unmap \| roles-selfservice` | `roles-map`: mevcut rolü ping hedefi yapar (managed/@everyone reddedilir; bahsedilemez rol için yöneticinin de Herkesten Bahset izni olmalı). `roles-selfservice`: ayrı onay, güvenlik denetimi, onaylayan rolden yukarıda olmalı |
| `/tsq-admin modul:esports islem:panel` | Herkese açık takip düğmeleri (durumsuz custom id → yeniden başlatmada çalışır; modül kapalıysa reddedilir) |
| `/tsq-admin modul:esports islem:preview` | Ping atmayan önizleme; pinglenecek rolleri metin olarak listeler |
| `/tsq-admin modul:esports islem:pause` / `resume` | Resume, watermark'ı ileri alır: kaçanlar topluca gönderilmez |
| `/tsq-admin modul:esports islem:doctor` | Kanal izinleri tek tek, rol ping'i/self-service güvenliği, sağlayıcı modu/durumu, VRS, 24 saatlik gönderim istatistiği |

Yönetici esports yapılandırması modül kapalıyken de yapılabilir (etkinleştirmeden önce kurulum için); kullanıcı komutları,
panel, autocomplete ve bildirimler modül kapalıyken çalışmaz.

## Bot hesabı, intent'ler, davet

- Resmî bot hesabı + bot token (self-bot/kullanıcı token'ı yok). **Administrator istenmez.**
- Gateway intent (Identify): yalnızca **Guilds** (ayrıcalıklı değil). Presence / Guild Members kapalı, gerekmez (etkileşim
  yükü üyenin rollerini içerir, rol ekleme/çıkarma REST ile yapılır). Mesaj olayları (`GuildMessages`) dinlenmez.
- **Message Content** (ayrıcalıklı erişim, yalnızca TSQ Quote ve TSQ Özet için): Developer Portal → Bot → Privileged Gateway
  Intents → MESSAGE CONTENT INTENT **açık**. Discord'a göre bu erişim bir gateway olayına bağlı değildir ve REST
  cevaplarındaki içerik alanlarını açar; bu yüzden Identify'a eklenmez ve bot hiçbir mesaj olayı almaz. Yalnızca `/quote`'ta
  verilen tek mesajı ve `/ozetle` çalıştırıldığında o kanalın son mesajlarını okur.
- OAuth2 kapsamları: `bot` ve `applications.commands`.
- İzinler (asgari, işleve göre):

| İzin | Bit | Neden | Gerekli mi |
|---|---|---|---|
| View Channel | 1024 | bildirim kanalını görmek | evet |
| Send Messages | 2048 | bildirim göndermek | evet |
| Embed Links | 16384 | embed'ler | evet |
| Add Reactions | 64 | TSQ Çekiliş kartına botun 🎉 tepkisini eklemek (katılım bu tepkiyle olur; Unicode emoji, External Emojis gerekmez) | evet (TSQ Çekiliş için zorunlu; yoksa çekiliş oluşturulmaz) |
| Read Message History | 65536 | belirsiz teslimat uzlaştırması (yalnızca kendi mesajlarını arar); TSQ Quote kaynak kanalı | önerilir; TSQ Quote için kaynak kanalda gerekli |
| Attach Files | 32768 | TSQ Quote kartı (`quote.png`), komutun çalıştığı kanalda | TSQ Quote kullanılıyorsa |
| Manage Roles | 268435456 | self-service bildirim rolleri; TSQ Doğum Günü geçici rolü | self-service veya Doğum Günü rolü kullanılırsa |
| Mention Everyone | 131072 | bahsedilemez rolleri pinglemek; TSQ Live duyurusunun `@everyone` bildirimi | rollerde **önerilmez** (rolü "bahsedilebilir" yapın); TSQ Live kullanılıyorsa **yalnızca duyuru kanalında** kanal izniyle verin |

  Asgari izin tamsayısı (tüm modüller, TSQ Quote ve TSQ Çekiliş dahil): **117824** (View Channel + Send Messages + Embed
  Links + Attach Files + Read Message History + Add Reactions); self-service rollerle: **268553280**. TSQ Quote kullanılmıyorsa
  **85056** yeterlidir (84992 + Add Reactions 64).
  Bot zaten sunucudaysa yeniden davet gerekmez: bot rolüne (ya da yalnızca ilgili kanallarda) **Attach Files** verilmesi
  yeterlidir. Bot zaten sunucudaysa TSQ Çekiliş için bot rolüne (ya da çekiliş kanallarına) **Add Reactions** verilmesi yeterlidir.
  Bot rolü, dağıtacağı self-service rollerin **üstünde** olmalıdır.
- Rate limit: Discord.Net yerleşik yönetimi (`RetryRatelimit`, Retry-After'a uyar). Ek agresif retry katmanı yok;
  outbox kendi sınırlı geri çekilmesini uygular.
