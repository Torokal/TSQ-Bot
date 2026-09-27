# TSQ Quote — Alıntı Kartı

`/quote`, bu sunucudaki bir mesajı siyah-beyaz bir alıntı görseline (`quote.png`) çevirip komutun çalıştırıldığı kanala
gönderir: solda yazarın profil fotoğrafı siyah-beyaz ve siyaha doğru kararan bir geçişle, sağda mesaj büyük beyaz
harflerle, altında "— Görünen Ad" ve gri "@kullanıcıadı". Logo, çerçeve, renk yok.

Durumsuz bir yardımcı modüldür: tablo, migration, arka plan işi, sağlayıcı ve yapılandırma yoktur. Her sunucuda varsayılan
kapalıdır: `/modules enable quote`.

## Komut

```
/quote message:<mesaj ID'si veya mesaj bağlantısı> [channel:<kanal>]
```

| Girdi | Mesajın arandığı kanal |
|---|---|
| Ham ID, `channel` yok | Komutun çalıştırıldığı kanal |
| Ham ID + `channel` | Seçilen kanal (metin, duyuru, ses/sahne sohbeti, herkese açık thread) |
| Bağlantı `https://discord.com/channels/<sunucu>/<kanal>/<mesaj>` | Bağlantıdaki kanal (`channel` gerekmez, verilirse yok sayılır) |

Kabul edilen bağlantı hostları: `discord.com`, `ptb.`/`canary.`/`www.` varyantları ve eski `discordapp.com`; yalnızca
https, varsayılan port, kullanıcı bilgisi yok, yol tam olarak `channels/<sunucu>/<kanal>/<mesaj>`. ID'ler 17–20 haneli ASCII
snowflake. Mesaj bulunmak için sunucudaki kanallar **taranmaz**: tek kanal, tek mesaj, tek okuma.

## Güvenlik (sunucu tarafında, istemciye güvenmeden)

Sırasıyla; her adım geçmeden hiçbir mesaj okunmaz:

1. Bağlantı başka bir sunucuya veya DM'e aitse → "Yalnızca bu sunucudaki mesajlar alıntılanabilir."
2. Kanal bu sunucunun önbellekte bilinen bir **mesaj kanalı** olmalı (forum kanalının kendisi değil; özel thread'ler
   desteklenmez — thread üyeliği denetlenmediği için hiç alıntılanmaz).
3. Komutu çalıştıran üyenin o kanalda (thread'de üst kanalda) **View Channel + Read Message History** izni olmalı —
   rolleri ve kanal izin üzerine yazmalarıyla, Discord.Net'in izin çözümlemesiyle (sahip ve Administrator dahil).
4. Botun da aynı kanalda aynı iki izni olmalı (`IGuildGateway.GetBotChannelAccessAsync`).
5. Yaş sınırlı (NSFW) bir kanaldaki mesaj, yaş sınırı olmayan bir kanala alıntılanmaz.
6. Mesaj tek bir REST okumasıyla alınır.

2–4 ve 6'daki her olumsuz sonuç (yok, silinmiş, erişim yok, bot okuyamıyor, Discord hatası) kullanıcıya **aynı** ephemeral
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

**Message Content intent**: Discord, bu ayrıcalıklı intent'i (Developer Portal) açık olmayan uygulamalara başka
kullanıcıların mesaj metnini boş verir (REST dahil). TSQ Bot bugün yalnızca `Guilds` intent'iyle çalışır; intent kapalıyken
`/quote` yalnızca botun kendi mesajlarında ve botu etiketleyen mesajlarda metin görür, diğerlerinde "Discord bu mesajın
metnini bota göstermiyor…" der ve loga bir uyarı yazar. Intent'i açmak işletmeci kararıdır; gateway intent'leri değişmez.

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
gönderilir ve gizli not silinir. Tüm cevaplar `allowed_mentions` boş (hiçbir şey ping atmaz). Beklenmeyen hatalar mevcut
TSQ hata yolundan (`error.internal` + takip kodu) geçer.

## Kapsam dışı (V1)

Alıntı geçmişi/veritabanı, liderlik tablosu, tepkiler, rastgele/zamanlanmış alıntı, web paneli, tema/şablon sistemi,
bağlam menüsü komutu, animasyonlu GIF, sunucu genelinde mesaj arama, eklerin karta eklenmesi.
