# TSQ Çekiliş

Tepkiyle katılımlı çekilişler. Her sunucuda varsayılan kapalıdır: `/modules enable giveaway` (listede **TSQ Çekiliş**).
Komutlar yalnızca **Sunucuyu Yönet** (Manage Server) yetkisi olanlara görünür ve her adımda sunucu tarafında yeniden
denetlenir (`Authorize.ServerSettings`). Katılmak için yetki veya rol gerekmez.

## Akış

```
/giveaway create → form (Ödül, Süre, Kazanan Sayısı, Açıklama)
  → bot kartı bu kanala gönderir ve altına 🎉 ekler
  → üyeler 🎉 tepkisiyle katılır
  → süre dolunca bot 🎉 tepkilerini okur, kazananları çeker
  → aynı kart sonuç kartına dönüşür + yalnızca kazananları etiketleyen kısa bir mesaj
```

| Komut | Ne yapar |
|---|---|
| `/giveaway create` | Formu açar. Süre: `30m`, `2h`, `1d`, `1d 12h` (Türkçe: `30dk`, `2s`/`2 saat`, `1g`); en az 1 dakika, en fazla 30 gün. Kazanan: 1–10 (boş: 1). Ödül en fazla 100, açıklama en fazla 500 karakter |
| `/giveaway end giveaway:<hedef>` | Aktif çekilişi hemen sonuçlandırır (otomatik çekilişle aynı yol) |
| `/giveaway cancel giveaway:<hedef>` | Kazanan çekmeden iptal eder; kart "Bu çekiliş iptal edildi." olur |
| `/giveaway reroll giveaway:<hedef>` | Sonuçlanmış çekiliş için yeni kazanan çeker; daha önce kazanan herkes dışarıda kalır |

Hedef: kartın altbilgisindeki numara (`#12`, otomatik tamamlama önerir), mesaj bağlantısı veya mesaj ID'si. Başka bir
sunucunun çekilişi "bulunamadı" görünür. Tüm yanıtlar yalnızca komutu kullanana görünür; sunucuda aynı anda en fazla 20
aktif çekiliş olabilir.

## Kart düzeni

Her durumda aynı iskelet: başlık durumu söyler (`🎉 ÇEKİLİŞ` / `🎉 ÇEKİLİŞ SONUÇLANDI` / `⚠️ ÇEKİLİŞ İPTAL EDİLDİ`),
hemen altında `🎁 ÖDÜL` ve ödül **başlık boyutunda** (kartın en büyük metni). Sonra alanlar — aktif: Kazanan Sayısı ve
Bitiş yan yana (önce kalan süre, altında tam tarih), Katılım; sonuçlandı: Kazanan(lar) (🥇 🥈 🥉, sonra 4., 5. …), Katılımcı
ve Bitti yan yana; iptal: Durum. Açıklama varsa her zaman en sonda `📝 Açıklama` alanıdır. Altbilgi: `Başlatan: … · Çekiliş #12`
(yeniden çekildiyse sonuna eklenir).

## Kurallar

- **Katılım** yalnızca karttaki 🎉 tepkisidir (Discord'un kendi tepkisi; ayrı `/katıl` yok). Çekiliş anında Discord'daki
  gerçek durum okunur: tepkisini kaldıran dahil değildir, botlar (botun kendi 🎉'si dahil) sayılmaz, herkes bir kez sayılır.
  Discord istek başına en fazla 100 kullanıcı döndürür; liste `after` ile sayfa sayfa sonuna kadar okunur. Herhangi bir
  sayfa okunamazsa çekiliş yapılmaz (yarım listeyle asla).
- **Seçim**: Fisher–Yates (`RandomNumberGenerator`, sapmasız). Seçilen kişi sunucudan ayrılmışsa atlanır ve kalanlar
  arasından yeniden eşit olasılıkla seçilir. Kazanandan az katılımcı varsa hepsi kazanır; geçerli katılımcı yoksa kart
  "Çekiliş sona erdi ancak geçerli katılımcı bulunamadı." olur ve kimse etiketlenmez.
- **Sonuç**: kart düzenlenir (🥇 🥈 🥉, sonra 4., 5. …; katılımcı sayısı; bitiş). Kart düzenlemeleri kimseyi etiketlemez;
  kazananları yalnızca ayrı kısa duyuru etiketler (outbox, en fazla bir kez, 1 saatten geç gönderilmez).
- **Reroll**: güncel 🎉 tepkilerinden, önceki tüm kazananlar hariç çekilir; yeterli başka katılımcı yoksa kalanların hepsi,
  hiç yoksa kazananlar değişmez. Her tur `giveaway_winner` tablosunda saklanır.

## Dayanıklılık

- Çekiliş bir veritabanı satırıdır; süreç içi zamanlayıcı yoktur. `GiveawayWorker` ~30 saniyede bir `Active` ve süresi
  dolmuş çekilişleri sonuçlandırır — restart/deploy sırasında dolanlar ilk turda.
- Sonuç, tek bir yazma işleminde (SQLite `BEGIN IMMEDIATE`) çekilişin **hâlâ aktif** olduğu yeniden denetlenerek kaydedilir;
  kazanan duyurusu aynı işlemde outbox'a yazılır. Aynı anda gelen worker turları, `/giveaway end` ve `/giveaway cancel`
  arasında yalnızca ilk kaydeden geçerlidir; iptal edilmiş veya sonuçlanmış çekiliş tekrar çekilmez.
- Discord okunamazsa (erişim, 429, 5xx, üye sorgusu cevapsız) çekiliş aktif kalır ve artan beklemeyle (1, 2, 4 … en fazla
  30 dk) yeniden denenir. Kart veya kanal silinmişse çekiliş çekilmeden `Orphaned` olur.
- Kart düzenlemesi başarısız olursa sonuç kaybolmaz; worker kartı en fazla 8 kez yeniden düzenlemeyi dener.
- `/modules disable giveaway` yalnızca yeni çekilişleri ve komutları kapatır; aktif çekilişleri iptal etmez. Başlamış bir
  çekiliş süresi dolunca normal sonuçlanır: tepkiler okunur, kazanan seçilir, kart güncellenir ve kazanan duyurusu gönderilir
  (`GiveawayDeliveryPolicy.DeliversWhileModuleDisabled`: outbox'ın modül kapısı yalnızca bu tür için açılır; tekil teslim,
  geç göndermeme ve izin listesi aynen geçerli).

## İzinler ve veri

- Kanalda bot için: View Channel, Send Messages, Embed Links, **Add Reactions**, Read Message History — hepsi zorunlu; form
  açılmadan önce ve gönderimde yeniden denetlenir. Add Reactions yoksa çekiliş oluşturulmaz, kayıt yazılmaz, kart gönderilmez
  (`❌ Botun bu kanalda Tepki Ekle (Add Reactions) iznine ihtiyacı var.`). İzin denetimden sonra kaybolur ve Discord botun
  🎉'sini reddederse kart gönderilmiş olsa bile çekiliş hemen iptal edilir. 🎉 Unicode olduğundan Use External Emojis gerekmez.
- Saklanan: başlatanın ID'si ve o anki görünen adı, ödül, açıklama, süre, kazanan ID'leri, katılımcı **sayısı**. Katılımcı
  listesi saklanmaz ve loglanmaz. Loglar: `giveaway_created`, `giveaway_finished`, `giveaway_cancelled`,
  `giveaway_rerolled` (yalnızca ID'ler ve sayılar). `/privacy delete`: kazanma kayıtları silinir, başlatılan / bitirilen / iptal edilen
  çekilişlerden ad ve ID kaldırılır.
