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

## Formula 1 (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/f1 next` | Sıradaki (veya süren) Grand Prix: tur, pist, yarış saati, tüm seanslar (Discord zaman damgaları) |
| `/f1 schedule [round]` | Parametresiz tam sezon takvimi; `round:N` ile o hafta sonunun ayrıntılı programı. `next`/`schedule`/`standings`/`now` kanalda herkese açık, `results` ephemeral |
| `/f1 results [session] [spoiler]` | Önbellekteki son sınıflandırma (latest, race, sprint, qualifying, sprint-qualifying, fp1–fp3) |
| `/f1 now` | Canlı sağlayıcıya göre süren seans; bilinmiyorsa "kullanılamıyor" (programdan tahmin yok) |
| `/f1 standings drivers` / `/f1 standings constructors` | Sağlayıcının puan tablosu, tur, güncellik |

Hepsi yalnızca botun önbelleğinden okur (etkileşim yolunda sağlayıcı çağrısı yok; mimari testli) ve ephemeral yanıt verir.

## Formula 1 yönetici (`/f1-admin`, ManageGuild + sunucu tarafı `Authorize.Require`)

| Komut | Ne yapar |
|---|---|
| `/f1-admin configure channel` | Bildirim kanalı (bu sunucudan; eksik izinler uyarılır, başka kanala otomatik geçiş yok) |
| `/f1-admin configure notifications` | Antrenman/sprint/yarış başlangıç ve sonuç, puan durumu; sıralama türleri (vars. kapalı) |
| `/f1-admin configure role` | İsteğe bağlı ping rolü (`ping_role`; asla @everyone), başlangıç/sonuç ping anahtarları, `clear` |
| `/f1-admin configure spoilers` | Spoiler modu |
| `/f1-admin preview` · `status` · `doctor` · `pause` · `resume` | TEST/DEMO pingsiz önizleme · ayarlar · tanı · duraklat/devam |

Ayrıntı: [FORMULA1.md](FORMULA1.md).

## Voleybol — Filenin Sultanları (modül açıkken; yalnızca Türkiye Kadın A Milli Takımı)

| Komut | Ne yapar |
|---|---|
| `/volleyball next` | Sıradaki (veya sağlayıcıya göre süren) maç: rakip, saat, turnuva, salon, güncellik |
| `/volleyball schedule` | Yaklaşan maçlar ve son sonuçlar |
| `/volleyball-admin configure channel \| notifications \| role` | Yönetici (ManageGuild + `Authorize.Require`): kanal; `match_reminder_15m`, `match_started`, `set_finished`, `match_finished`, `match_postponed_cancelled`; isteğe bağlı `ping_role` (asla @everyone) |
| `/volleyball-admin preview` · `status` · `doctor` · `pause` · `resume` | TEST/DEMO pingsiz önizleme · ayarlar · tanı · duraklat/devam |

Başka takım seçtiren komut yoktur. Ayrıntı: [volleyball/VOLLEYBALL.md](volleyball/VOLLEYBALL.md).

## TSQ Live — Twitch + Kick yayın duyuruları

| Komut | Ne yapar |
|---|---|
| `/live-admin doctor` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): `Live:Enabled`, modül kapısı, duyuru kanalı ve izinleri (Mention Everyone dahil), Twitch/Kick yetkilendirme ve son başarılı uzlaştırma, yayıncı durumları ve duyuru mesajları. Sağlayıcıya istek atmaz, secret göstermez |

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
| `/lfg-admin channel [channel]` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): `/ekip`'i tek kanala kısıtlar; boş = her kanal |
| `/lfg-admin status` | Yönetici: modül durumu, kanal, aktif ilan sayıları, sınırlar, bekleyen kart düzenlemeleri |

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

## TSQ Doğum Günü (modül açıkken)

| Komut | Ne yapar |
|---|---|
| `/birthday set <tarih>` · `show` · `remove` | Herkes, yalnızca **kendi** kaydı: gün + ay (`14.03`, `14/03`, `14-03`; yıl yok). Başkasının kaydına erişen komut veya liste yok |
| `/birthday-admin set member:@üye date:14.03` | **Yalnızca Administrator veya sunucu sahibi** (etkin izinlerden, sunucu tarafında; Manage Server yetmez): başka bir üyenin gün + ayını oluşturur/günceller; ephemeral, ping'siz cevap |
| `/birthday-admin show member:@üye` | **Yalnızca Administrator veya sunucu sahibi** (etkin izinlerden, DB okunmadan önce; Manage Server yetmez): tek bir üyenin kayıtlı gün + ayını gösterir; ephemeral, ping'siz; denetim logu (tarih yok). Liste komutu yok |
| `/birthday-admin configure channel:` | Yönetici (ManageGuild + `Authorize.Require`; modül kapalıyken de çalışır): duyuru kanalı |
| `/birthday-admin status` · `doctor` | Yönetici: ayarlar, bugünün durumu, zamanlayıcı; kanal/rol hiyerarşisi/izin/veritabanı kontrolü |

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
| `/esports-admin configure` | `channel` (kanal seçici), `reminders`, `reminder_minutes` 1–120, `results`, `spoilers`. Kanal bu sunucuda ve metin kanalı olmalı; eksik bot izinleri uyarılır |
| `/esports-admin filters show \| team \| tournament \| tier \| vrs \| clear` | Kural: aynı tür VEYA, farklı türler VE; VRS verisi yoksa filtre durdurur |
| `/esports-admin roles list \| map \| unmap \| selfservice` | `map`: mevcut rolü ping hedefi yapar (managed/@everyone reddedilir; bahsedilemez rol için yöneticinin de Herkesten Bahset izni olmalı). `selfservice`: ayrı onay, güvenlik denetimi, onaylayan rolden yukarıda olmalı |
| `/esports-admin panel` | Herkese açık takip düğmeleri (durumsuz custom id → yeniden başlatmada çalışır; modül kapalıysa reddedilir) |
| `/esports-admin preview` | Ping atmayan önizleme; pinglenecek rolleri metin olarak listeler |
| `/esports-admin pause` / `resume` | Resume, watermark'ı ileri alır: kaçanlar topluca gönderilmez |
| `/esports-admin doctor` | Kanal izinleri tek tek, rol ping'i/self-service güvenliği, sağlayıcı modu/durumu, VRS, 24 saatlik gönderim istatistiği |

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
