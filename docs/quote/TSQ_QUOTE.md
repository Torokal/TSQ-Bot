# TSQ Quote — Alıntı Kartı

TSQ Quote, bu sunucudaki bir mesajı siyah-beyaz bir alıntı görseline (`quote.png`) çevirip komutun çalıştırıldığı kanala
gönderir: solda yazarın profil fotoğrafı siyah-beyaz ve siyaha doğru kararan bir geçişle, sağda mesaj büyük beyaz
harflerle, altında "— Görünen Ad" ve gri "@kullanıcıadı". Logo, çerçeve, renk yok.

Durumsuz bir yardımcı modüldür: tablo, migration, arka plan işi, sağlayıcı ve yapılandırma yoktur. Her sunucuda varsayılan
kapalıdır: `/modules enable quote`.

## Kullanım

**En hızlı:** mesaja sağ tık → **Uygulamalar (Apps) → Quote**. Mobilde: mesaja uzun bas → Uygulamalar → Quote. Kart,
mesajın bulunduğu kanala gider.

**Alternatif (başka kanaldaki mesaj için de):**

1. Discord → Ayarlar → Gelişmiş → **Geliştirici Modu**'nu aç.
2. Mesaja sağ tık → **Mesaj Kimliğini Kopyala**.
3. Aynı kanalda: `/quote message:<mesaj-id>`
   Başka kanaldaki mesaj: `/quote message:<mesaj-id> channel:<kanal>`

İkisi aynı kartı, aynı metin kurallarını ve aynı çizimi kullanır; `/quote` kaldırılmadı.

### /quote

| Girdi | Mesajın arandığı kanal |
|---|---|
| Mesaj kimliği (ana kullanım), `channel` yok | Komutun çalıştırıldığı kanal |
| Mesaj kimliği + `channel` | Seçilen kanal (metin, duyuru, ses/sahne sohbeti, herkese açık thread) |
| Bağlantı `https://discord.com/channels/<sunucu>/<kanal>/<mesaj>` (ayrıca desteklenir) | Bağlantıdaki kanal (`channel` gerekmez, verilirse yok sayılır) |

Mesaj kimliği kanalı içermez; bu yüzden kimlik başka bir kanaldaysa `channel` verilmelidir — mesajı bulmak için sunucudaki
kanallar **asla taranmaz**: tek kanal, tek mesaj, tek REST okuması (`GET /channels/{kanal}/messages/{mesaj}`). Kimlikler
17–20 haneli ASCII snowflake. Kabul edilen bağlantı hostları: `discord.com`, `ptb.`/`canary.`/`www.` varyantları ve eski
`discordapp.com`; yalnızca https, varsayılan port, kullanıcı bilgisi yok, yol tam olarak `channels/<sunucu>/<kanal>/<mesaj>`.

### Apps → Quote (mesaj komutu)

Discord'un MESSAGE (type 3) uygulama komutu. Tıklanan mesaj etkileşimin kendisiyle (`data.resolved.messages`) gelir;
bot mesajı Discord'dan **okumaz** (REST mesaj okuması sıfır; testle kilitli). Yalnızca yazarın sunucu takma adı ve sunucu
avatarı için, üye önbellekte değilse tek bir üye okuması (`GET /guilds/{sunucu}/members/{kullanıcı}`) yapılabilir.
`CHAT_INPUT quote` ile `MESSAGE Quote` aynı uygulamada birlikte bulunur: Discord komut adlarını uygulama, **tip** ve kapsam
başına benzersiz tutar (discord-api-docs, application-commands).

## Güvenlik (sunucu tarafında, istemciye güvenmeden)

Her iki yol da `quote` modül kapısının arkasındadır (kapalıyken mevcut "modül kapalı" cevabı), yalnızca sunucuda ve yalnızca
izin verilen ana sunucuda çalışır (etkileşim, komuta ulaşmadan reddedilir).

**Apps → Quote:** mesaj, komutun kullanıldığı kanaldadır ve kart da oraya gider (kaynak = hedef). Denetimler:
etkileşimdeki mesaj bu kanala ait olmalı; kanal bu sunucunun bir mesaj kanalı olmalı; botun burada **View Channel +
Attach Files** izni olmalı (yoksa hiçbir şey çizilmez); üyenin burada **View Channel + Read Message History** izni sunucu
tarafında yeniden doğrulanır (tıklamış olması yetmez). Kanallar arası kurallar burada korunacak bir şey bulmaz: özel
thread'de ve yaş sınırlı kanalda da çalışır, çünkü kart mesajın bulunduğu yerde, aynı kişilere görünür.

**/quote:** sırasıyla; her adım geçmeden hiçbir mesaj okunmaz:

1. Bağlantı başka bir sunucuya veya DM'e aitse → "Yalnızca bu sunucudaki mesajlar alıntılanabilir."
2. Komutun çalıştırıldığı kanalda botun **View Channel + Attach Files** izni olmalı (thread'de üst kanal;
   `IGuildGateway.GetBotChannelAccessAsync`). Yoksa kaynak kanala bakılmadan, mesaj okunmadan ve kart çizilmeden →
   "TSQ Bot'un bu kanalda dosya gönderme izni yok." Kart etkileşim webhook'uyla (takip mesajı) gittiği için Send
   Messages gerekmez.
3. Kaynak kanal bu sunucunun önbellekte bilinen bir **mesaj kanalı** olmalı (forum kanalının kendisi değil; özel thread'ler
   desteklenmez — thread üyeliği denetlenmediği için hiç alıntılanmaz).
4. Komutu çalıştıran üyenin o kanalda (thread'de üst kanalda) **View Channel + Read Message History** izni olmalı —
   rolleri ve kanal izin üzerine yazmalarıyla, Discord.Net'in izin çözümlemesiyle (sahip ve Administrator dahil).
5. Botun da kaynak kanalda aynı iki izni olmalı (`IGuildGateway.GetBotChannelAccessAsync`).
6. Yaş sınırlı (NSFW) bir kanaldaki mesaj, yaş sınırı olmayan bir kanala alıntılanmaz.
7. Mesaj tek bir REST okumasıyla alınır.

3–5 ve 7'deki her olumsuz sonuç (yok, silinmiş, erişim yok, bot okuyamıyor, Discord hatası) kullanıcıya **aynı** ephemeral
cevaptır: "Mesaj bulunamadı veya bu mesaja erişim iznin yok." — gizli bir kanalın veya mesajın varlığı sızmaz. Teknik
ayrıntı yalnızca loga yazılır (kimlikler; mesaj metni asla).

Not: erişimi olan bir üye, özel bir kanaldaki mesajı daha açık bir kanala alıntılayabilir (elle kopyalamakla aynı).
Kaynağın hedef kanal kadar görünür olmasını şart koşmak V2 kararıdır.

## Mesaj metni

Kartta mesajın okunabilir metni kullanılır: satır sonları korunur, baştaki/sondaki boşluk kırpılır; `<@id>` → @Ad
(sunucu takma adı varsa o), `<#id>` → #kanal (yalnızca komutu çalıştıranın görebildiği kanalların adı), `<@&id>` → @rol,
özel emoji → `:ad:`, `</komut:id>` → /komut, `<t:…>` → sunucunun saat diliminde sabit tarih; `**x**`, `__x__`, `*x*`,
`~~x~~`, `||x||`, başlık/alıntı işaretleri ve maskeli bağlantılar kalkar, sözcükler kalır; kaçışlı karakterler (`\*`)
ve kod içeriği aynen kalır; `2*3*4` ve `snake_case` bozulmaz. Ekler/görseller V1'de karta eklenmez. Metin boşsa: "Bu
mesajda alıntılanabilecek bir metin yok."

## Message Content erişimi

Karar (2026-09-27): TSQ Quote için Message Content ayrıcalıklı erişimi kullanılır. İşletmeci Developer Portal'da
**Bot → Privileged Gateway Intents → MESSAGE CONTENT INTENT**'i açar (10.000 kullanıcının altındaki uygulamalarda bu bir
toggle'dır; üstünde Discord incelemesi gerekir). Kabul şartı: normal bir üyenin yazdığı, botu etiketlemeyen bir mesajın
`/quote message:<id>` ile okunabilmesi.

Discord'a göre (discord-api-docs, `gateway.mdx` → "Message Content Intent") bu intent hiçbir gateway olayına bağlı
değildir; erişim, uygulamanın mesaj içeriğini **API'lerin genelinde** (REST dahil) almasını sağlar ve `content`,
`embeds`, `attachments`, `components`, `poll` alanlarını kapsar. Bu yüzden **gateway'de hiçbir şey değişmez**:
Identify yine yalnızca `Guilds`; `GuildMessages`/`MessageContent` bitleri, `MessageReceived` işleyicisi, mesaj önbelleği
veya dinleyici eklenmedi. Bot hiçbir mesaj olayı almaz; yalnızca kullanıcının verdiği kimlikteki tek mesajı REST ile okur.

**Apps → Quote bu erişime bağlı değildir:** Discord, bir mesaj bağlam menüsü komutunun kullanıldığı mesajın içeriğini
Message Content olmadan da gönderir (aynı doküman, istisnalar listesi). Portal ayarı `/quote message:<id>` yolu için açık
kalır.

Metin boş gelirse cevap, Discord'un döndürdüğü mesajın biçimine göre ayrılır (`QuoteMessageShape`). Apps → Quote'ta
boş metin her zaman "metin yok"tur, hiçbir zaman "iletilmedi" değildir:

| Mesaj | Cevap |
|---|---|
| Ek/embed/anket var (bunlar da içerik alanı: geldiyse erişim çalışıyor), iletilmiş mesaj, sistem mesajı, botun kendi mesajı veya botu etiketleyen mesaj; erişim açıkken yalnız çıkartma | "Bu mesajda alıntılanabilecek bir metin yok." |
| Normal bir üye mesajı ama hiçbir alanı gelmedi (Discord böyle bir mesajı saklamaz) | "Bu mesajın içeriği Discord tarafından bota iletilmedi. Message Content erişiminin açık olduğundan emin olun." + loga uygulamanın Message Content bayrağıyla birlikte bir uyarı (yalnızca kimlikler) |

İkinci satır Discord verisinden kesin olarak kanıtlanamaz; bu yüzden metin bir neden iddia etmez, kontrol edilmesi
gereken ayarı söyler.

## Kimlik ve avatar

Görünen ad: sunucu takma adı → Discord görünen adı → kullanıcı adı; alt satır `@kullanıcıadı`. Avatar sırası Discord.Net'in
"display avatar"ı: sunucu avatarı → hesap avatarı → Discord'un varsayılan avatarı (PNG; hareketli avatarın ilk karesi).
Yazar sunucudan ayrıldıysa veya mesaj bir webhook'unsa genel kimlik kullanılır.

İndirme `IHttpClientFactory` (`quote-avatar`) ile: yalnızca `https://cdn.discordapp.com` / `media.discordapp.net`
(Discord'un verdiği URL; kullanıcı girdisi asla), yönlendirme izlenmez, çerez yok, 5 sn zaman aşımı, yalnızca 200, en çok
4 MB (Content-Length'e güvenilmeden sayılır). Herhangi bir hata → kart, yazarın baş harfiyle koyu nötr panelle çizilir;
komut başarısız olmaz. 4096 px'ten büyük veya çözülemeyen görsel çözülmez.

## Görsel

1600×800 PNG, 8-bit gri tonlama (kart tamamen gri tonlarından oluştuğu için kayıpsız, RGB'nin üçte biri boyutunda).
Fotoğraf sol 860 px'i doldurur (en-boy korunarak kırpılır), gri tonlama + hafif kontrast + hafif karartma, x=400–860
arasında siyaha geçiş. Metin sütunu x=900–1540; blok dikeyde ortalanır, satırlar sola hizalıdır, kısa alıntıda blok
sütunun ortasına oturur. Yazı 84 px'ten başlar, sığmazsa adım adım 32 px'e kadar küçülür; en küçük boyutta da sığmazsa
son görünen satır bir sözcük sonunda "…" ile biter (grafem güvenli; emoji veya aksanlı harf bölünmez). Uzun ad/kullanıcı
adı tek satırda "…" ile kesilir.

Kütüphane: SixLabors ImageSharp 3.1.12 + ImageSharp.Drawing 2.1.7 + Fonts 2.1.3 — tamamen managed (GDI+/System.Drawing
yok, native kütüphane yok), Linux konteynerde aynı çıktı. Six Labors Split License → açık kaynak yazılımda Apache-2.0.
Bu kütüphanelerin yeni ana sürümleri (ImageSharp 4 / Drawing 3 / Fonts 3) derleme sırasında Six Labors lisans anahtarı
istediği için bilerek bir önceki ana serinin son sürümleri kullanılır.

Fontlar derlemeye **gömülüdür** (host fontlarına bakılmaz; runtime imajında font yoktur): Noto Sans Regular/Italic
(Latin — Türkçe ğ ü ş ı ö ç İ —, Yunan, Kiril) ve tek renkli Noto Emoji (yedek font). SIL Open Font License 1.1
(`src/ToroSquad.Modules.Quote/Assets/Fonts/OFL-*.txt`). Arapça, CJK gibi yazılar bu fontlarda yoktur; çizim hata
vermez ama bu karakterler boş kutu olarak görünür.

## Discord etkileşimi

Komut önce **ephemeral** olarak ertelenir (avatar indirme + çizim 3 saniyeyi aşabilir). Her ret bu gizli cevapta kalır.
Başarıda gizli cevap önce "Alıntı kartı gönderiliyor…" olarak düzenlenir (ertelenmiş cevabın ilk takip mesajı onun
yerini — ve gizliliğini — almasın diye), sonra kart **herkese açık, metinsiz bir takip mesajı** olarak `quote.png` ekiyle
gönderilir ve gizli not silinir. Tüm cevaplar `allowed_mentions` boş (hiçbir şey ping atmaz). Çizim hatası
(`QuoteCardBuilder`) kullanıcıya `error.internal` + takip kodu olarak döner; diğer beklenmeyen hatalar mevcut TSQ hata
yolundan geçer.

## Gizlilik

İçerik yalnızca istek sırasında işlenir: **al (Apps → Quote: etkileşimden; /quote: tek REST okuması) → düz metne çevir → çiz → at**. Mesaj metni, görünen
ad, kullanıcı adı, avatar URL'si ve avatar baytları yalnızca o isteğin belleğinde yaşar; veritabanına, önbelleğe, dosyaya
veya loga yazılmaz; alıntı geçmişi yoktur. Loglar yalnızca sunucu/kanal/mesaj kimliklerini ve sonucu (çözüldü, ret
nedeni, metin yok, içerik iletilmedi, takip kodu + istisna türü) içerir — istisna mesajı bile yazılmaz, çünkü metni
tekrarlayabilir (`QuoteFlowTests` her sonuç için bunu doğrular). Gönderilen kart normal bir kanal mesajıdır.

## Canlı kabul testi (işletmeci)

Ön koşullar: Developer Portal'da MESSAGE CONTENT INTENT açık; branch deploy edildi; `/quote` Sync-Commands ile senkronlandı
(önce dry-run); `/modules enable quote`; bot rolünde (veya test kanalında) Attach Files.

1. Botu **etiketlemeyen** normal bir üye (bot değil) kanala bir mesaj yazar — tercihen uzun, Türkçe karakterli.
2. Mesaja sağ tık → Mesaj Kimliğini Kopyala.
3. Aynı kanalda `/quote message:<id>`.

Beklenen: gizli "Alıntı kartı gönderiliyor…" kısa süre görünür ve silinir; kanala **metinsiz, herkese açık** bir
`quote.png` gelir; kartta mesajın metni boş değildir ve doğru satırlara bölünmüştür; avatar yazarınkidir ve siyah-beyazdır;
"— sunucu takma adı / görünen ad" ve "@kullanıcıadı" doğrudur; kimse etiketlenmez. Loglarda `Quote resolved guild=… channel=…
message=…` satırı vardır, mesaj metni yoktur. Bu adım geçerse Message Content erişimi **VERIFIED_LIVE** sayılır.

Ek kontroller: `channel:` ile başka kanaldaki mesaj; erişemediğin bir kanalın kimliği → "Mesaj bulunamadı veya bu mesaja
erişim iznin yok."; Attach Files'ı olmayan bir kanalda → "TSQ Bot'un bu kanalda dosya gönderme izni yok."; yalnız görsel
eki olan bir mesaj → "Bu mesajda alıntılanabilecek bir metin yok.". Geri alma: `/modules disable quote`.

Apps → Quote için: normal bir üyenin mesajına sağ tık (mobilde uzun bas) → Uygulamalar → Quote → aynı kart gelir; logda
`Quote resolved … via=apps` satırı bulunur (metin, isim, avatar URL'si yok).

## Kapsam dışı (V1)

Alıntı geçmişi/veritabanı, liderlik tablosu, tepkiler, rastgele/zamanlanmış alıntı, web paneli, tema/şablon sistemi,
kullanıcı (sağ tık → kullanıcı) komutu, tepkiyle alıntı, yanıt düğmesi, animasyonlu GIF, sunucu
genelinde mesaj arama, eklerin karta eklenmesi.
