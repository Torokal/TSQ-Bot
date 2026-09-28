# TSQ Randomizer

Günlük küçük rastgele işler için dört komut: `/zarat`, `/randomsayi`, `/sec`, `/yazitura`. Sonuç kartları **herkese açıktır**
(ephemeral değil) ve kimseyi etiketlemez. Her sunucuda varsayılan kapalıdır: `/modules enable randomizer` (listede
**TSQ Randomizer**). Yetki gerekmez; modül açıkken herkes kullanabilir. Komutlar yalnızca sunucuda çalışır (guild-only).

Tamamen durumsuzdur: kendi tablosu ve migration'ı yoktur, arka plan işi, HTTP isteği, API anahtarı, yapay zekâ, önbellek
veya yapılandırma yoktur. Sonuçlar hiçbir yerde saklanmaz ve loglanmaz.

## Komutlar

| Komut | Ne yapar |
|---|---|
| `/zarat zar:<girdi>` | Belirtilen zarları atar |
| `/randomsayi maksimum:<sayı> [minimum:<sayı>]` | Belirtilen aralıktan rastgele sayı seçer (iki uç dahil; `minimum` varsayılan 1) |
| `/sec seçenekler:<metin>` | Verilen seçeneklerden rastgele birini seçer |
| `/yazitura` | Yazı tura atar |

Kart düzeni dördünde aynıdır (TSQ turuncu kart rengi, emoji'li başlık, kısa açıklama, altbilgide komutu kullanan;
`/randomsayi` ve `/sec` altbilgisi "… için seçildi"). Örnek:

```
🎲 2d6 atıldı
Zarlar: `4` `6`
Toplam: 10
Toro tarafından atıldı
```

Altbilgideki ad, üyenin bu sunucudaki görünen adıdır (takma ad → global ad → kullanıcı adı); düz metindir, mention değildir.

## `/zarat`

- Girdi: `<adet>-<yüz>` veya zar gösterimi `<adet>d<yüz>` (`d`/`D`). Örnekler: `1-20`, `2-6`, `3-100`, `1d20`, `2d6`, `2D6`.
  Karttaki gösterim her zaman `2d6` biçimindedir.
- Baştaki/sondaki boşluk kabul edilir; bunun dışında kesin: yalnızca ASCII rakamlar (işaret, ondalık, Unicode rakam yok),
  tek ayırıcı, önünde/arkasında başka bir şey yok, içeride boşluk yok (`2 d 6`, `2x6`, `2d6+1`, `d6`, `abc` → geçersiz).
- Sınırlar: **1–20 zar**, **2–10.000 yüz**. Taşma yaratabilecek çok uzun sayılar da (ör. `99999999999999999999-6`) aralık
  hatasıyla reddedilir; hiçbir zaman taşma olmaz.
- Tek zar: "Sonuç: 17". Birden çok zar: her zar ayrı ayrı ("Zarlar: 4 6") ve "Toplam". 20 zarın tamamı gösterilir.

## `/randomsayi`

- `maksimum` zorunlu, `minimum` isteğe bağlı (varsayılan **1**). İki uç da **dahildir**: `maksimum:100` → 1…100.
- `minimum == maksimum` → o sayının kendisi (`maksimum:10 minimum:10` → 10).
- Negatif aralıklar desteklenir (`minimum:-100 maksimum:100`).
- Desteklenen aralık **-1.000.000.000 … 1.000.000.000**: Discord'un tamsayı seçeneği sınırının (±2^53) içinde ve `int`
  sınırından yeterince uzak; böylece rastgele çekimin dışlayıcı üst sınırı (`maksimum + 1`) asla taşmaz. Aynı sınırlar
  slash seçeneklerinde `min_value`/`max_value` olarak tanımlıdır ve sunucu tarafında tekrar denetlenir.
- `minimum > maksimum` → "Minimum değer maksimum değerden büyük olamaz." (ör. yalnızca `maksimum:-5` verildiğinde varsayılan
  minimum 1 olduğu için).

## `/sec`

- Ayırıcı: **virgül** (`CS2, Valheim, WoW`). Girdide `|` varsa **yalnızca `|`** ayırıcıdır ve virgüller seçeneğin
  parçasıdır (`Pizza, kola | Burger` → 2 seçenek). Tek, deterministik kural; tahmin yok.
- Her seçenek kırpılır, içteki boşluklar (satır sonları dahil) tek boşluğa indirilir — bir seçenek birden çok satıra
  yayılamaz. Boş girdiler atlanır (`CS2,,Valheim` → 2 seçenek).
- Yinelenen seçenekler **bir kez** sayılır, büyük/küçük harf duyarsız (`CS2, cs2, Valheim` → `CS2`, `Valheim`); ilk yazım
  gösterilir. Böylece yinelenen seçenek iki kat ağırlık almaz.
- Sınırlar: en az **2 farklı** seçenek, en fazla **25**; bir seçenek en fazla **100** karakter; girdinin tamamı en fazla
  **1000** karakter (slash seçeneğinin `max_length` değeri).
- En fazla 10 seçenek ve kısa bir liste varsa kart tüm seçenekleri gösterir; aksi hâlde yalnızca sayıyı
  (`25 seçenek arasından:` + seçilen) — kart küçük kalır.
- Seçenek metinleri kartta `DiscordText.Untrusted` ile gösterilir: `@everyone`, `@here`, rol/kullanıcı mention'ları mention
  olarak **görüntülenmez** bile, markdown ve bağlantılar etkisizdir. Ayrıca her cevap `allowed_mentions` boş gönderilir.

## `/yazitura`

Parametre yok. Sonuç iki ihtimalden biri: **YAZI** veya **TURA**. Eşleme sabittir ve testle korunur: çekim `0` → Yazı,
`1` → Tura.

## Rastgelelik

Tek kaynak: `IRandomSource` → üretimde `SecureRandomSource` = `System.Security.Cryptography.RandomNumberGenerator.GetInt32`
(kriptografik olarak güvenli, sapmasız, durumsuz, tohumsuz). Dört komut da `RandomizerService` üzerinden bu kaynağı kullanır:

| İşlem | Çağrı |
|---|---|
| Zar | her zar için `GetInt32(1, yüz + 1)` |
| Sayı | `GetInt32(minimum, maksimum + 1)` |
| Seçim | `GetInt32(0, seçenek sayısı)` |
| Yazı tura | `GetInt32(0, 2)` |

Dağılıma dokunulmaz: seri önleme, önceki sonuca bakma veya "daha adil görünsün" diye düzeltme yoktur; her çağrı bağımsızdır.
`System.Random` / `Random.Shared` kullanımı mimari testle yasaktır (`RandomizerArchitectureTests`).

## Hatalar ve görünürlük

- Başarılı sonuçlar **herkese açık**.
- Geçersiz girdi: yalnızca komutu kullananın gördüğü (ephemeral) kısa Türkçe mesaj — TSQ'nun genel kalıbı (reddetmeler
  özel, sonuçlar görünür); kanal yazım hatalarıyla dolmaz. Örnekler: "Zar formatı geçersiz. Örnek: 1-20, 2-6 veya 2d6.",
  "Tek seferde en fazla 20 zar atabilirsin.", "En az 2 farklı seçenek girmelisin.", "En fazla 25 seçenek kullanabilirsin.",
  "Minimum değer maksimum değerden büyük olamaz."
- Beklenmeyen hatalar ortak etkileşim hata yolundan geçer (takip kodu + log, ayrıntı kullanıcıya gösterilmez).
- Loglama: yalnızca `Debug` düzeyinde meta veri (komut, sunucu, kullanıcı ID'si, sayı aralığı veya girdi uzunluğu). Zar
  sonuçları, sayı, seçilen seçenek, yazı/tura ve seçenek metinleri **loglanmaz**. Üretim log düzeyi `Information` olduğundan
  bu satırlar üretimde yazılmaz.

## Keşif

`/help`, açık modüllerin komutlarını manifestten otomatik listeler: modül açıldığında `/zarat`, `/randomsayi`, `/sec`,
`/yazitura` kısa Türkçe açıklamalarıyla görünür. `/modules list` modülü **TSQ Randomizer** adıyla gösterir.

## Kapsam dışı (bilerek)

Ağırlıklı seçim, sonuç geçmişi, liderlik tablosu, bahis/ekonomi, cooldown, yapay zekâ ile seçim, `/karistir`,
`/takimayir`. Yeni bir rastgele komut aynı `RandomizerService` + `RandomizerCards` üzerine eklenebilir.
