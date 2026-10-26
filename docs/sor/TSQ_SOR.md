# TSQ Sor — yapay zekâ soru-cevap, sohbet hafızası ve kişi tanıma

Modül kimliği `sor` (`ToroSquad.Modules.Sor`). Varsayılan **kapalı**: `/modules enable sor`. Açıldıktan sonra normal
üyeler kullanabilir; yönetici kanal/rol sınırı koyabilir (`/tsq-admin modul:sor`).

## Komutlar

| Komut | Ne yapar |
|---|---|
| `/sor soru:<metin> [gizli:evet/hayır] [yeni:evet/hayır] [kisi:<üye>]` | Her konuda soru. `gizli:evet` cevabı yalnız sorana gösterir (varsayılan: kanalda). `yeni:evet` yeni sohbet başlatır, kalıcı notları silmez. `kisi` soru bir üyeyle ilgiliyse doğru kimliği seçer; o üyenin notlarına erişim yetkisi **vermez**. |
| `/hafizam` | Çağıranın bu sunucudaki kendi hafızası (özel ekran). |
| `/tsq-admin modul:sor islem:<işlem>` | `status`, `limits`, `context`, `allow-channel`, `remove-channel`, `allow-role`, `remove-role`, `usage`, `doctor` (Sunucuyu Yönet; servis tarafında `Authorize.Require`). |

Örnekler:

```
/sor soru:Strateji oyunlarını seviyorum, bana kısa cevap ver. Ne önerirsin?
/sor soru:Peki ikincisi?                      → aynı sohbet devam eder
/sor soru:Fotosentez nedir? yeni:evet         → yeni konu, tercihler korunur
/sor soru:Bunu gizli sorayım gizli:evet
/sor soru:Mert ne oynamayı sever? kisi:@Mert  → yalnız Mert'in paylaştığı notlar
/sor soru:Bunu unut: satranç notu
```

`/sor hafiza` gibi bir alt komut **yoktur**: Discord'da alt komut kullanan bir komutun kendisi çağrılamaz ("Using
subcommands or subcommand groups will make your base command unusable", discord-api-docs → Application Commands). Bu
yüzden `/sor soru:…` düz kalır ve hafıza ekranı ayrı `/hafizam` komutudur (adı mevcut komutlarla çakışmıyor).

Cevap kartı: küçük "TSQ Sor" başlığı, cevap ve sorunun kısa bir alıntısı (uzun soru tekrar basılmaz; model adı, token,
durum kodu, hafıza JSON'u gösterilmez). Altında üç düğme vardır:

- **Devam et**: takip sorusu için form. Yalnız konuşmanın sahibi açabilir; başkasına "kendi /sor sorunu başlat" denir.
- **Yeni sohbet**: yeni soru formu. Eski konu ancak geçerli bir soru gönderildiğinde kapanır; formu kapatmak hiçbir şeyi
  değiştirmez. Görünürlük konuşmanınkiyle aynı kalır.
- **Hafızam**: her zaman **tıklayanın** kendi hafızasını yeni bir özel mesajda açar; mesaj sahibininkini asla açmaz.

Form (modal) düğmeye verilen **ilk** cevap olarak açılır: Discord'da MODAL bir etkileşimin ilk cevabı olmalıdır ve
MODAL_SUBMIT'e modal açılamaz (discord-api-docs → Receiving and Responding). Gönderilen form yeni bir etkileşim olarak
baştan doğrulanır (sahip, sunucu, kanal, görünürlük, modül kapısı).

## Akış (tek üretim isteği)

1. AI isteğinden **önce** ve yalnız kullanana görünür retler: boş/çok uzun soru, anahtar yok, kanal/rol sınırı, bekleme
   süresi, üye/sunucu günlük sınırı, aynı sohbette süren üretim, eşzamanlılık dolu, sağlayıcının istediği bekleme (429).
2. Discord'un 3 saniyesi içinde **son görünürlükle** defer (gizli → ephemeral). Görünürlük sonradan değişmez; gizli cevabı
   kanala çeviren bir yol yoktur. Etkileşim token'ı 15 dakika geçerlidir; Sor'un sohbet süreleri bundan bağımsızdır.
3. Kabul + kota rezervasyonu tek `BEGIN IMMEDIATE` işleminde: interaction ID benzersizdir (aynı olay ikinci üretim, çift
   kota veya çift kayıt üretmez), son günlük hakka gelen iki istekten yalnız biri geçer.
4. Bağlam seçimi (sahiplik ve kapsam **sorguda** uygulanır; yetkisiz bilgi modele hiç verilmez) ve sürümlerin kaydı.
5. **Tek** `chat/completions` isteği. Hafıza çıkarma, sohbet özeti, reviewer, JSON tamiri, otomatik yeniden deneme veya
   yedek model yoktur. 429/zaman aşımı/5xx yeni istek veya model değişikliği başlatmaz.
6. Cevap ayrıştırılır, bekleyen sonuç olarak saklanır.
7. Teslimden önce sürüm kontrolü: soranın ve sorulan üyenin hafıza damgası/anahtarı, kullanılan her not, konuşma, modül
   kapısı ve kanal/rol erişimi. Arada silme, kapatma, paylaşımı geri alma olduysa cevap **gösterilmez** ("bayat").
8. Teslim: kart + düğmeler (gerekirse en fazla 2 devam mesajı, aynı görünürlükle). Discord reddederse cevap yeniden
   üretilmez, sohbete eklenmez, not kaydedilmez. Onaylanamayan teslimde bir kez orijinal cevaba bakılır.
9. Teslim doğrulandıktan sonra tek işlemde (idempotent, `Generated → Completed`): sürüm kontrolü tekrar, izinli not
   değişiklikleri, tur, eski turların budanması, not kullanım kayıtları, sayaçlar. **"🧠 1 not kaydedildi"** satırı yalnız
   bu yazım başarılıysa uygulama tarafından eklenir; model "kaydettim" diyemez. Teslimden sonra DB hatası olursa kayıt
   adımı (AI çağrısı olmadan) en fazla iki kez denenir; Discord ile veritabanı arasında atomik işlem veya uçtan uca
   exactly-once garantisi **yoktur**.

## Cevap sözleşmesi

Sağlayıcının JSON/şema modu bu ağ geçidi için doğrulanmadığından `response_format` gönderilmez. Model önce yalnız
cevabı düz metin yazar; üye son mesajında kendisi hakkında kalıcı bir şey söylediyse ardından `<<<HAFIZA>>>` ve tek
satırlık `{"memory":[{"op":"add|update|forget","kind":"…","text":"…","evidence":"…","target":"…"}]}` gelir. Çıktı bütçesi
(1.200 token) cevap ve önerileri birlikte kapsar. Gramer (`SorAnswerFormat`):

| Durum | Sonuç |
|---|---|
| Ayıraç kod bloğu dışında ve ardından yalnız boşluk, `{` veya ``` geliyor | **Protokol ayıracı**: cevap orada biter, sonrası hafıza bloğudur ve hiç gösterilmez |
| Ayıraç metnin ortasında, alıntıda veya kod örneğinde | Cevabın parçası; `‹‹‹HAFIZA›››` olarak etkisizleştirilip gösterilir (cevap kesilmez) |
| Blok: tek JSON nesnesi (isteğe bağlı ``` içinde), `memory` dizisi | Bilinmeyen alanlar yok sayılır; ilk 3 geçerli öğe alınır |
| Blokta ikinci ayıraç, JSON sonrası metin, bozuk/kesik JSON, 4.000 karakterden uzun blok | Öneri yok; cevap gösterilir |
| `finish_reason=length` + protokol ayıracı var | Cevap ayıraçtan önce bittiği için gösterilir; blok atılır |
| `finish_reason=length` + ayıraç yok | **Eksik** (`Truncated`): gösterilmez, kotadan düşer |
| Ayıraçtan önce metin yok / yalnız hafıza bölümü / boş içerik | **Boş**: gösterilmez |
| Sağlayıcı boş içerik veya geçersiz gövde döndü | `EmptyOutput` / `InvalidResponse`: gösterilmez |
| Üye sorusunda ayıraç | Prompta girmeden etkisizleştirilir (`SorText.Inert`), model bunu protokol sanmaz |

Hiçbir durumda ikinci istek, JSON tamiri veya gizli yeniden deneme yapılmaz; ayıraç ve hafıza JSON'u hata yolundan da
Discord'a gitmez.

## Kimlik

- Kalıcı kimlik **(GuildId, UserId)**: ad değişince hafıza devam eder, aynı adlı iki kişi birleşmez, aynı hesabın başka
  sunucudaki hafızası ayrı kalır. Kimlik ve roller interaction'dan gelir; model çıktısından hiçbir kimlik/kapsam alınmaz.
- Konuşma: sahip + kanal/thread + görünürlük + durum. Başka üyenin sorusu bu geçmişe eklenmez; sahte bir konuşma ID'si
  (düğme) sahibi olmayan kişiye başkasının geçmişini açmaz.
- Görünen ad interaction'dan; `kisi` seçeneği veya sorudaki ilk ham `<@id>` için **tek** üye çözümlemesi (REST). Üye
  listesi indirilmez, presence izlenmez, yeni intent yok (gateway: yalnız Guilds). Modele Discord ID'si gitmez; adlar ve
  bahsedilen üye "B" olarak, veri bloğunda verilir. Yalnız adıyla anılan belirsiz biri için model tahmin etmez,
  `kisi`/etiket ister.

## Hafıza

### Kısa sohbet

Yalnız `/sor` soru-cevapları; kullanıcı ve asistan rolleri ayrı mesajlar olarak gönderilir (asistanın iddiası üyenin
beyanı sayılmaz). Bağlamda en fazla **8** tamamlanmış çift + yaklaşık **6.000** token girdi bütçesi (kesin tokenizer yok:
3 karakter/token muhafazakâr tahmin; önce en eski turlar, sonra en az ilgili notlar çıkarılır). **60 dakika**
hareketsizlikte yeni sohbet; sohbetler son etkinlikten **24 saat** sonra silinir. Yeni tur yazılırken eski turlar silinir
(veritabanında 8'den fazla birikmez). Uzun sohbet ayrıca özetlenmez.

### Kalıcı notlar

Üyenin **kendisi hakkında** doğrudan söylediği kısa bilgiler: hitap, kısa/ayrıntılı cevap tercihi, oyun, hobi, ilgi alanı,
devam eden hedef, kalıcı tercih. En fazla **30** not, not başına **240** karakter; istek başına en fazla **6** not.
Seçim deterministiktir (`SorPrompt.Rank`) ve **kapsam süzgecinden sonra** (veritabanı sorgusu) yapılır: önce hitap ve
cevap tarzı, sonra soruyla ortak içerik kelimesi (Türkçe ekler için ilk dört harf) daha çok olan, sonra en son
güncellenen, sonra büyük ID. Rastgele seçim veya embedding yok; ilgili eski bir tercih, ilgisiz yeni notların önüne geçer.
Sorulan başka üyenin paylaşılmış notları bu altı notun en fazla üçünü alır. Bir istek en fazla **bir** başka üyeyi
(`kisi` veya sorudaki ilk etiket) kapsar.

Uygulamanın kuralları (model yalnız önerir):

- Dayanak (`evidence`) üyenin **o anki** mesajından birebir alıntı olmalı; eski turlardan yeniden çıkarım yapılmaz.
- Birebir alıntı tek başına öz-beyan değildir. Yüzeysel ve **muhafazakâr** ek denetimler (`SorMemoryRules`) şüpheli
  öneriyi kaydetmez:

  | Örnek | Ret |
  |---|---|
  | "Mert, ‘Ben strateji oyunlarını seviyorum’ dedi." | `Quoted` (dayanağın cümlesinde tırnak/‘…’ veya "dedi/diyor/söyledi…") |
  | "Farz et C# biliyorum." | `Hypothetical` ("farz et, varsay, diyelim, rol yap, sanki…" mesajın herhangi bir yerinde) |
  | "Şakaydı; beni Galatasaraylı diye kaydetme." | `NotToBeSaved` ("şaka, dalga geçiyorum, kaydetme, hatırlama…") |
  | Önceki iddiaya tek başına "aynen" | `Unsupported` (dayanak ile not arasında ortak içerik kelimesi yok) |
  | "kısa cevap istemiyorum" → "Kısa cevap istiyor." | `Negated` (olumsuz dayanak, olumlu not) |
  | "Strateji oyunları güzel bir tür." → oyun notu | `NotSelfStated` (oyun/hobi/ilgi/hedef/tercih için birinci tekil biçim yok) |

  "Artık kısa cevap istemiyorum; ayrıntılı anlat." mesajında `ayrıntılı anlat` dayanağıyla cevap tarzı güncellemesi
  geçer (hitap ve cevap tarzı bota yönelik istek olabileceği için birinci tekil biçim aranmaz). Bunlar Türkçe için birkaç
  kalıptır; anlamın tamamını çözmez, yanlış bir adayı her zaman yakalayamaz ve bazı doğru adayları da reddedebilir.
  Belirsiz aday kaydedilmez; ikinci bir model çağrısı yapılmaz. Gerçek modelin hatırlama kalitesi çevrimdışı
  fixture'larla **doğrulanamaz**.
- Parola, token, anahtar, kimlik/kart/hesap numarası, e-posta, telefon asla kaydedilmez (elle eklenen notlarda da).
- Sağlık, din, siyaset, cinsellik gibi hassas ayrıntılar otomatik kaydedilmez.
- Sorunun ilgili olduğu üyenin adını içeren öneri reddedilir; A'nın "B şunu sever" demesi B'nin hafızasını asla
  değiştirmez (sahip her zaman soran kişidir).
- Hitap ve cevap tarzı tek değerlidir: net yeni beyan eskisini günceller. Diğer türlerde aynı not tekrar yazılmaz; hafıza
  doluysa yeni not reddedilir (eski not rastgele silinmez) ve üyeye özel bilgi verilir.
- "Bunu hatırla" aynı kurallarla kaydedilir. "Bunu unut" tek nota kesin eşleşirse siler; birden fazla nota uyarsa hiçbir
  şey silinmez ve Hafızam'dan seçmesi için özel mesaj gider.

### Kapsamlar

| Kapsam | Nasıl oluşur | Kullanıldığı yer |
|---|---|---|
| 🔒 Yalnız bana özel | Gizli `/sor` ile öğrenilen notlar, Hafızam'dan elle eklenenler | Yalnız sahibinin gizli cevapları |
| #️⃣ Bu kanalda benim cevaplarımda | Kanal `/sor` ile öğrenilen notlar | Sahibinin **aynı kanal/thread**'deki kanal cevapları ve sahibinin gizli cevapları |
| 🌐 Sunucuda başkalarına da söylenebilir | Yalnız Hafızam'da, not bazında, önizleme + onayla | Sahibinin cevapları ve diğer üyelerin `kisi:`/etiketle sahibi hakkındaki soruları |

Herkese açık kanalda söylenmiş olmak sunucu geneli profil izni değildir; paylaşım her zaman ayrı ve açık bir eylemdir.
Başka biri hakkında soruda yalnız Discord'dan çözülen kimlik ve o kişinin paylaşılmış notları (hafızası açıksa)
kullanılır; özel notları ve konuşmaları yüklenmez, paylaşılmış bilgi yoksa kişilik/geçmiş uydurulmaz.

### Hafızam ekranı

Notlar ve kapsamları; not seçme → düzelt (form), sil, paylaş (önizleme) / paylaşımı geri al; not ekle; hafızayı aç/kapat;
"Sohbetlerimi ve hafızamı sil". Her işlemde not ID'si ve sürümü (rastgele damga) sunucuda sahip/sunucu/sürüm ile tekrar
denetlenir; 15 dakikadan eski ekran yeniden açılmalıdır. Tek ve açıkça seçilmiş bir notun silinmesi ikinci onay istemez.

- **Yeni sohbet**: yalnız konu geçmişini sıfırlar, notlar kalır.
- **Hafızayı kapat**: tüm notların okunması (paylaşılanlar dahil) ve yeni not yazılması durur; notlar silinmez, tekrar
  açılınca önceki paylaşım tercihleri geçerli olur; kısa sohbet devam eder. Eski turlara yansımış notlar dolaylı
  kullanılmasın diye üyenin mevcut sohbetleri sıfırlanır.
- **Sohbetlerimi ve hafızamı sil**: konuşmalar, turlar, notlar ve bekleyen içerikler silinir; tek kullanımlık, 5 dakikalık,
  üye + sunucu + işleme bağlı onay (`IConfirmationStore`). Günlük kullanım hakkını yenilemez (istek kayıtları yalnız sayı).
- **Bir not silinince** onu kullanan veya öğreten bütün konuşmalar (sahibininki dahil) silinir: not eski turlardan geri
  öğrenilemez. **Paylaşılan not değişince / paylaşım geri alınınca / silinince** onu kullanmış diğer üyelerin
  konuşmaları bütünüyle geçersizleştirilir (yalnız ilk tur değil). Düzeltilen paylaşılan not önizlenmediği için
  paylaşımdan çıkar.

## Yarış koruması

Üye ayarlarında, her notta ve her konuşmada rastgele bir **damga** vardır; her değişiklikte yenilenir. Silinip yeniden
oluşturulan bir kayıt yeni damga alır: eski bir istek bu yolla yeniden geçerli olamaz. İstek, başladığı damgaları
(soranın, sorulan üyenin, konuşmanın, kullanılan her notun) kaydeder; teslimden önce ve kayıt anında karşılaştırılır.
Model çağrısı hiçbir veritabanı işleminin içinde beklemez.

## Varsayılan ayarlar (ürün başlangıç ayarı; sağlayıcı kotası değildir)

| Ayar | Değer | Nerede değişir |
|---|---:|---|
| Üye bekleme süresi | 10 sn | `Sor:UserCooldownSeconds`, sunucu bazında `/tsq-admin … islem:limits` |
| Üye başına günlük üretim | 30 | `Sor:UserDailyLimit`, `limits` (1–500) |
| Sunucu başına günlük üretim | 300 | `Sor:GuildDailyLimit`, `limits` (1–5000) |
| Eşzamanlı üretim (Sor çağrılarının toplamı; Özet ayrı) | 1 | `Sor:MaxConcurrentRequests` |
| Soru uzunluğu | 4.000 karakter | `Sor:MaxQuestionLength` |
| Girdi bütçesi | ~6.000 token | `Sor:MaxInputTokens` |
| Çıktı bütçesi | 1.200 token | `Sor:MaxOutputTokens` |
| İstek başına not | 6 | `Sor:MaxMemoryItemsPerRequest` |
| Bağlamdaki tur | 8 | `Sor:MaxContextTurns`, `context` (0–8) |
| Yeni sohbet için hareketsizlik | 60 dk | `Sor:ConversationIdleMinutes`, `context` |
| Sohbet saklama | 24 saat | `Sor:HistoryRetentionHours`, `context` (yalnız kısaltılabilir) |
| Üye başına not / not uzunluğu | 30 / 240 | `Sor:MaxMemoryItems`, `Sor:MaxMemoryItemLength` |

Gün sınırı sunucunun `/setup` saat dilimine göredir (varsayılan Europe/Istanbul). Kota ve bekleme kalıcıdır (restart'ta
sıfırlanmaz) ve yarışsız rezerve edilir. Kabul edilen her istek kotadan düşer (sağlayıcı hatası, zaman aşımı ve
kullanılamayan cevap dahil); yalnız AI isteğinden önce veritabanı hatasıyla biten istek geri bırakılır.

## Sağlayıcı ve model

- Özet ile **aynı** OpenCode Go hesabı, anahtar (`OPENCODE_GO_API_KEY`) ve uç nokta (`https://opencode.ai/zen/go/v1/` +
  `chat/completions`). Yeni hesap, ikinci anahtar veya başka ücretli servis yoktur.
- Paylaşılan katman `ToroSquad.Ai` (TSQ'ya, EF'ye, Discord'a bağımlılığı yok): istek/cevap sözleşmesi, tek HTTP gönderimi,
  ayrıştırma, hata sınıflandırması. Özet'in promptları, kaynak/spoiler kuralları, rol/mesaj eşikleri, cooldown'ları ve
  legacy/grounded davranışı Özet modülünde kalır; Sor Özet'e referans vermez.
- Sor'un açık profili Özet'in tek çağrılı legacy profiliyle aynı başlar: `Sor:Model = deepseek-v4.1-flash`,
  `thinking: {"type":"disabled"}` ve `reasoning_effort` yok, temperature 0.3, top_p 0.9, `max_tokens` 1200, 25 sn. Değerler
  Sor'a kopyalanmıştır (Özet'ten okunmaz): bir modülün yapılandırması diğerinin modelini sessizce değiştiremez. Üyeler ve
  yöneticiler modeli değiştiremez. Grounded generator/reviewer zinciri Sor'a taşınmamıştır.
- `x-opencode-session`: Go belgesi konuşma başına sabit bir oturum kimliği ister. Sor bunu konuşma satırının ID'si ve
  rastgele damgasından türetilen bir GUID olarak gönderir (`SorSession`): aynı konuşmanın her turunda ve restart
  sonrasında aynı; yeni sohbette, başka üyede, başka sunucuda ve diğer görünürlükte farklı; kimlik, isim veya metin
  içermez. Bu yalnız yönlendirme/önbellek bilgisidir, sunucu tarafı hafıza sayılmaz: geçmişi uygulama gönderir. Özet
  her istekte yeni bir GUID göndermeye devam eder (davranışı değişmedi).
- Ortak anahtar limiti: paylaşılan katmanda (`ToroSquad.Ai`) sınırlayıcı **yoktur**. `SorThrottle` yalnız Sor
  çağrılarının toplamını 1 ile sınırlar; Özet kendi `SummaryThrottle` sınırlarıyla (en fazla 2) bundan bağımsız çalışır —
  bekleyen bir Sor çağrısı Özet'i durdurmaz (test edildi). 429'dan sonra
  sağlayıcının Retry-After süresi (yoksa 30 sn, en fazla 1 saat) dolana kadar yeni istek gönderilmez. Bot, hesabın bütün
  dış tüketimini göremez; `usage` ekranı yalnız Sor'un kendi sayılarını gösterir. Dolar maliyeti hesaplanmaz.
- Web araması, link indirme veya araç yoktur; model canlı erişimi varmış gibi konuşmaz, kaynak uydurmaz. Kullanıcı
  mesajından komut, SQL, dosya işlemi, yönetici eylemi veya dış servis çağrısı çalıştırılmaz.

## Veri modeli ve saklama

| Tablo | İçerik | Saklama |
|---|---|---|
| `sor_guild_config` | izinli kanal/rol, sınır değerleri | sunucu purge'ü |
| `sor_user` | (sunucu, üye), hafıza açık/kapalı, ilk kullanım notu, damga | `/privacy delete`, purge |
| `sor_conversation` | sahip, kanal/thread, görünürlük, durum, damga, zamanlar | son etkinlikten 24 saat |
| `sor_turn` | teslim edilmiş soru-cevap (soru ≤ 4.000, cevap ≤ 12.000 karakter) | konuşmayla birlikte; en fazla 8 tur |
| `sor_memory_use` | konuşma ↔ kullanılan not | konuşmayla birlikte |
| `sor_memory` | sahip, tür, kısa metin, kapsam, kaynak kanal/konuşma/interaction ID'si, köken, sürüm, damga, zamanlar | üye silene, düzeltene, `/privacy delete` veya purge'e kadar |
| `sor_request` | interaction (benzersiz), gün, durum, hata sınıfı, izleme kodu, token sayıları; bekleyen cevap yalnız üretim ile kayıt arasında | bekleyen içerik en fazla 15 dk; kayıt 48 saat |
| `sor_guild_usage` | sunucu + gün toplamları (üye ID'si yok) | 35 gün |

Kaynak tur silindiğinde not CASCADE ile silinmez (yalnız asgari kaynak kimlikleri tutulur; ham kanıt metni veya ikinci
transcript arşivi yoktur). Temizlik (`SorCleanupWorker`, 10 dakikada bir) modül kapalıyken de çalışır: süresi dolan
konuşmalar, sonuç bekleyen eski istekler (restart'ta "üretiliyor" kalan istek **Unknown** olur — yeniden üretilmez, kotadan
düşmez), eski istek kayıtları ve eski günlük toplamlar. Özet'in transcript/prompt/yanıt saklamama sözleşmesi aynen
devam eder; Özet çıktıları, kanal geçmişleri ve diğer modüllerin kayıtları hafıza kaynağı değildir.

## `/privacy`

`IUserDataContributor` (`SorUserData`): export (ayarlar, notlar, konuşmalar + turlar, istek meta verisi), silme önizlemesi,
silme ve sunucu purge'ü — yalnız çağıran üye ve mevcut sunucu; modül kapalıyken de çalışır. Silme üyenin ayarlarını,
notlarını, konuşmalarını, turlarını ve istek kayıtlarını siler (bu yüzden kişisel günlük sayısı da gider); sunucu
toplamları üye ID'si taşımadığı için kalır ve sunucu sınırı silme yoluyla yenilenmez. Diğer üyelerin, bu üyenin paylaşılmış
notunu kullanmış konuşmaları geçersizleştirilir. Veritabanından silmek, daha önce Discord'a gönderilmiş cevapları,
sağlayıcıdaki veya yedeklerdeki kopyaları silmez.

## Discord teslimi

- Her gönderim, düzenleme ve follow-up `MentionPolicy.None` (allowed mentions boş); modelin yazdığı @everyone/@here ve
  kullanıcı/rol etiketleri bildirim üretmez.
- Tüm Discord çağrıları `SorInteractionResponder` üzerinden yapılır (`IDiscordInteraction` arayüzü; çağrı sırası ve
  payload'ı çevrimdışı testle gözlenir): ret ilk yanıt olarak ve gizli; defer son görünürlükle; cevap orijinal yanıtı
  düzenler; devam parçaları ve notlar aynı veya gizli görünürlükte follow-up'tır.
- Uzun cevap en fazla 3 parçaya (≤ 3.800 karakter, embed açıklaması 4.096) paragraf/satır sınırından bölünür; kod bloğu
  bölünürse kapatılıp aynı dille yeniden açılır, spoiler (`||`) bölünmez; sığmayan kısım "…" ile kesilir.
- Kanalda defer edildikten sonraki hata: orijinal cevap kısa, kişisel içerik taşımayan bir satırla kapanır, ayrıntı özel
  follow-up olarak gider. Gizli istekte ayrıntı doğrudan orijinal cevaptadır. Yükleniyor durumu açık bırakılmaz.

## Hata teşhisi

Üyeye kısa Türkçe açıklama ve `TS-XXXXXXXX` izleme kodu. Log satırı (metin yok, yalnız meta veri):

```
Sor [TS-…] <Outcome> guild=… private=… failure=<AiFailure> http=<status> provider=<kısa kod> finish=… latencyMs=… in=… out=… reasoning=… cached=…
```

| Outcome / failure | Aşama | Anlamı |
|---|---|---|
| `NotConfigured` | yapılandırma | `OPENCODE_GO_API_KEY` yok (istek gönderilmez) |
| `Provider` + `Unauthorized`/`Forbidden`/`Rejected` | yapılandırma/hesap | 401/403/diğer 4xx: anahtar, model, bölge veya plan |
| `Provider` + `RateLimited` | sağlayıcı | 429 (Retry-After kadar bekleme) |
| `Provider` + `ServerError`/`Network` | sağlayıcı | 5xx veya bağlantı |
| `Timeout` | zaman aşımı | kullanım **bilinmiyor** olarak sayılır |
| `Parse:Empty`/`Parse:Truncated`, `InvalidResponse`/`EmptyOutput` | ayrıştırma | kullanılamaz/kesik cevap |
| `Stale` | sürüm | hafıza/erişim istek sırasında değişti |
| `Delivery:Failed`/`Delivery:Unknown` | teslim | Discord reddetti / doğrulanamadı |
| "database error …" | veritabanı | rezervasyon/bağlam/kayıt adımı |

`/bot status` son sonucu (aşama, sınıf, HTTP, izleme kodu) gösterir; `/tsq-admin modul:sor islem:doctor` anahtarın varlığını
(değerini değil), uç noktayı, model profilini, ayarları ve tabloları **ücretli istek yapmadan** denetler; `usage` son 7
günün toplamlarını ve hata sınıflarını gösterir. Yönetim ekranları kimsenin sohbetini veya notunu göstermez.

## Teslim ve kayıt aralıkları

| Aralık | Davranış |
|---|---|
| AI döndü, Discord reddetti | `Failed`; tur ve not yok; özel bilgi; yeniden üretim yok |
| AI döndü, teslim doğrulanamadı | Orijinale bir kez bakılır; bulunamazsa `Unknown`; tur ve not yok |
| AI çalışırken / teslimden önce silme, kapatma, paylaşım daraltma | Damga değişir → `Discarded`; cevap gösterilmez |
| Teslim ile kayıt arasında silme | Cevap Discord'da kalır (geri alınamaz); kayıtta damga kontrolü → tur ve not yazılmaz (`stale-after-delivery`) |
| Teslim sonrası DB hatası | Kayıt adımı en fazla 2 kez, modelsiz; olmazsa istek `Generated` kalır, 15 dk sonra temizlikte `Unknown` olur ve bekleyen içerik silinir; kotadan düşmüş kalır |
| "Kaydedildi" satırı düzenlemesi başarısız | Kayıt ve üretim tekrarlanmaz |
| Üretim sırasında restart | `Generating` istek zaman aşımı + 2 dk sonra `Unknown`; yeni üretim yok, kota iade edilmez |
| Kullanıcı kaydı silinip yeniden oluşturuldu | Yeni rastgele damga: eski istek geçerli olmaz |

Discord'a gönderilmiş bir istek geri alınamaz; Discord ile SQLite arasında atomiklik veya uçtan uca exactly-once
garantisi yoktur. Aynı interaction ikinci kez işlenmez (benzersiz anahtar), bir tur/not/kota iki kez kesinleşmez.

## Sağlayıcı kullanım koşulları (açık dış koşul)

Kaynaklar 2026-10-26'da okundu; kod bu konuyla değişmedi, başka bir istemci gibi görünülmez (User-Agent `SorModule`).

- **OpenCode Go belgesi**: Go "OpenCode ve benzer istekler üreten diğer kodlama ajanları" için tasarlanmıştır; trafik
  kötüye kullanım için izlenir; konuşma başına sabit `x-opencode-session` istenir; doğrudan çağıran uygulamalar için AI
  SDK paketleri ve API uç noktaları belgelenir. Yani harici API kullanımı belgelenmiş, ama ürün odağı kodlama ajanlarıdır.
- **API Overview**: anahtarlar "scripts and integrations" için; entegrasyonlar için service account anahtarı önerilir.
- **Terms of Use**: hizmet yalnız kullanıcının "own internal use" için ve "not on behalf of or for the benefit of any
  third party" kullanılabilir. Bir Discord topluluğunun üyelerine cevap veren bot (Sor ve aynı şekilde Özet) bu
  hükümle **çelişebilir**.

Bu nedenle canlı kullanım yalnız "sahip karar verir" diyerek onaylanmış sayılmaz: sağlayıcıdan yazılı açıklama veya
topluluk kullanımına izin veren bir plan/koşul bekleyen **dış koşul** olarak açıktır. `docs/STATUS.md`'deki Özet
`401 authentication_error` kaydı ayrı bir erişim konusudur ve güncel erişimin durumunu tek başına kanıtlamaz.

## Doğrulama durumu

Çevrimdışı testler: sahte sağlayıcı, gerçek SQLite, kontrol edilebilir saat, kayıt tutan `IDiscordInteraction`
(`tests/ToroSquad.Tests/**/Sor*`). Gerçek komut gövdesi (`SocketInteractionContext`) çevrimdışı çalıştırılamaz; testler
yönlendirici + modül kapısını ve adaptörün çağrılarını kapsar. Gerçek sağlayıcıyla cevap kalitesi, kişilik, hatırlama
kalitesi ve gecikme **denenmedi**; Discord'da canlı akış (defer, modal, düğmeler, ephemeral paneller, görünüm)
**gözlenmedi**. Önerilen sonraki adım: kullanım koşulu netleştikten sonra test sunucusunda veya tek kanal + tek rol
pilotuyla (`allow-channel`, `allow-role`) sınırlı canlı doğrulama.
