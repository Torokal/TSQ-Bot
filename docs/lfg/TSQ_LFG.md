# TSQ LFG — Oyuncu Bul

Ayrı modül (`lfg`, `src/ToroSquad.Modules.Lfg`). Sunucudaki oyuncular **herhangi bir oyun veya aktivite** için `/ekip`
ile hızlıca ekip ilanı açar; diğerleri butonlarla katılır/ayrılır, ilan sahibi veya bir moderatör kapatır, ilan süresi
dolunca kendiliğinden kapanır.

**Tek generic sistem.** Bot yalnızca şunu bilir: *bir kullanıcı, adı X olan bir oyun/aktivite için Y kişilik ekip arıyor
ve Z detayını yazmış.* Oyun listesi, oyuna özel model, handler, alan, doğrulama veya buton yoktur (Deadlock, CS2, WoW,
Valheim ya da yarın çıkan bir oyun aynı yaşam döngüsünü kullanır; yeni oyun kod değişikliği gerektirmez). Rank, rating,
boss, rol, dungeon, mod… hepsi kullanıcının `detay` metnidir ve bot bu metni yorumlamaz (`LfgArchitectureTests` oyuna
özel tip/kolon adlarını yasaklar).

Kapsam dışı (V1, mimari engel değil): ses kanalı / thread açma, zamanlanmış veya tekrarlayan ilan, lider devri, rol
mention'ı, davetle katılım, geçmiş/itibar, otomatik eşleştirme.

## Kullanım

```
/ekip oyun:Deadlock kisi:6 detay:Casual oynayacağız, rank fark etmez. [sure:1 saat|2 saat|3 saat]
```

| Seçenek | Kural |
|---|---|
| `oyun` | serbest metin, 2–50 karakter (kontrol/format karakterleri atılır, boşluklar sadeleşir) |
| `kisi` | toplam ekip büyüklüğü, **sahip dahil**; 2 … `Lfg:MaxPlayersPerListing` (varsayılan 20, üst sınır 50) |
| `detay` | isteğe bağlı, tek satır, en fazla 200 karakter |
| `sure` | isteğe bağlı: 1 / 2 / 3 saat; boşsa `Lfg:DefaultExpirationMinutes` (varsayılan 120). Serbest dakika girişi yok |

Neden modal değil: depoda modal kalıbı yok ve slash seçenekleri Discord'un kendi doğrulamasını (sayı aralığı, uzunluk,
süre seçimi) istemci tarafında verir; `/ekip` + 3 alan ≈ 10 saniye. Sınırlar sunucu tarafında yeniden denetlenir.

## Kart

Kart, `/ekip` komutunun **herkese açık etkileşim yanıtıdır** (ayrı kanal mesajı yok). Örnekler (aynı renderer):

```
🎮 Deadlock                                   🎮 Counter-Strike 2
@Toro ekip arıyor                             @Toro ekip arıyor
👥 2 / 6                                      👥 3 / 5
📝 Casual oynayacağız. Rank fark etmez.       📝 Premier • 18.500 rating civarı • mikrofon gerekli

Oyuncular                                     Oyuncular
@Toro · @Oykeli                               @Toro · @Oykeli · @Hasom

⏰ 2 saat içinde kapanır                      ⏰ 1 saat içinde kapanır
[Katıl] [Ayrıl] [İlanı Kapat]                 [Katıl] [Ayrıl] [İlanı Kapat]
```

- Oyuncular Discord kullanıcı kimliğiyle tutulur; kartta `<@id>` mention'ı olarak (her izleyici güncel görünen adı görür)
  **embed içinde** gösterilir. Tüm yanıt ve düzenlemeler `allowed_mentions` boş gönderilir: ilk gönderim de, her
  düzenleme de kimseyi pinglemez. Görünen ad kalıcı veri olarak saklanmaz.
- Oyun adı ve detay güvenilmez metindir: mention, markdown ve link etkisizleştirilir (`DiscordText.Untrusted*`).
- Geri sayım Discord'un yerel göreli zaman damgasıdır (`<t:…:R>`); bot saat ilerlesin diye kartı düzenlemez.
- Durumlar: açık (yeşil) · **✅ Ekip tamamlandı** (dolu; Katıl devre dışı) · **🔒 İlan kapatıldı** · **⏰ Bu ekip ilanının
  süresi doldu.** Bitmiş ilanda mesaj silinmez; oyuncu listesi kalır, tüm butonlar devre dışıdır.

## Yaşam döngüsü

| Olay | Davranış |
|---|---|
| Oluştur | Girdi + kanal kısıtı + kişi başı aktif ilan sınırı (`Lfg:MaxActiveListingsPerUser`, varsayılan 2, guild başına) denetlenir; ilan `Open`, sahip ilk oyuncu (`1 / N`). Kart yanıt olarak gönderilir, mesaj kimliği kaydedilir. Yanıt hiç gönderilemediyse ilan silinir (sahibin hakkını yemez) |
| Katıl | Etkileşim hemen onaylanır (deferred update); ilan veritabanından yeniden okunur: guild, durum, süre, üyelik, boş slot. Oyuncu eklenir, son slot `Full` yapar, **aynı kart** düzenlenir, tıklayana ephemeral `✅ Ekibe katıldın.` Hatalar ephemeral: `Zaten bu ekiptesin.` · `Bu ekip dolu.` · `Bu ilan kapatılmış.` · `Bu ilanın süresi dolmuş.` |
| Ayrıl | Oyuncu çıkarılır; ilan `Full` idiyse ve süresi dolmadıysa yeniden `Open` (Katıl yeniden aktif). Sahip ayrılamaz: `İlan sahibi ekipten ayrılamaz. İstersen ilanı kapatabilirsin.` |
| Kapat | Yalnızca sahip veya moderatör (Discord **Manage Messages** ya da Administrator; mevcut `Authorize.Require`). Önce ephemeral onay (`Evet, kapat` / `Vazgeç`), sonra `Closed`; kart kapalı olarak düzenlenir. Tekrar kapatmak idempotenttir |
| Süre dolumu | Tek arka plan döngüsü (`LfgExpiryWorker`, ~60 sn; ilan başına zamanlayıcı yok) `ExpiresAt <= now` olan aktif ilanları toplu `Expired` yapar ve kartları düzenler. Bir butona süre dolduktan sonra basılırsa ilan o anda da expire edilir |
| Restart | Durum yalnızca veritabanındadır. Buton custom id'leri yalnızca ilan kimliğini taşır (`tsq:lfg:join:<id>`, `…:leave:`, `…:close:`), restart sonrası da çalışır. Açılışta ilk tur (~20 sn sonra) kapalıyken süresi dolan ilanları expire eder ve kartlarını düzenler |
| Mesaj silindi | Kart düzenlemesi `Unknown Message/Channel` dönerse ilan `Orphaned` olur (terminal; tekrar denenmez). `Closed`'dan ayrı tutulur çünkü kimse kapatmadı — mesaj ortadan kalktı; geçmiş ve tanı doğru kalır. Diğer düzenleme hataları sınırlı sayıda (8) yeniden denenir |

Modül bir sunucuda kapatılırsa (`/modules disable lfg`) butonlar "modül kapalı" cevabı verir; süre dolumu yine işler
(yalnızca kartı "süresi doldu" yapar, veri silinmez).

## Tutarlılık ve eşzamanlılık

- Her durum değişikliği bir yazma transaction'ında çalışır; SQLite'ta `BeginTransaction` = **`BEGIN IMMEDIATE`**: yazma
  kilidi okumadan *önce* alınır, böylece "boş slot var mı / zaten üye mi / aktif ilan sınırı" kontrolü ile yazma tüm
  bağlantılar arasında sıralanır (diğer yazıcı meşgul zaman aşımı kadar bekler).
- `lfg_participant` birincil anahtarı `(ListingId, UserId)`: aynı kullanıcı bir ilanda veritabanı seviyesinde iki kez
  olamaz. `lfg_listing.Version` iyimser eşzamanlılık belirteci ek güvencedir (çakışmada işlem yeniden denenir).
- Testler: 5/6 ilana aynı anda iki katılım → biri katılır, diğeri "dolu", sonuç 6/6; 12 eşzamanlı katılım 3 boş slota
  → tam 3; aynı kullanıcının 4 eşzamanlı tıklaması → tek kayıt; aynı kullanıcının 5 eşzamanlı ilanı (sınır 2) → tam 2.
- İki eşzamanlı tıklamada Discord eski görüntünün düzenlemesini sonra uygulayabilir: her tıklama kartı düzenledikten sonra
  sürümü yeniden okur, değiştiyse güncel hali yeniden çizer; oturmazsa kart `CardStale` işaretlenir ve worker düzeltir.

## Neden outbox değil

Diğer modüllerin otomatik bildirimleri `INotificationOutbox` üzerinden gider (tekilleştirme, belirsiz gönderim
uzlaştırması). LFG kartı ise kullanıcının kendi komutuna verilen **etkileşim yanıtıdır**: anında görünür, kanal izni
gerektirmez, tekilleştirilecek bir "gönderim" yoktur. Katıl/Ayrıl düzenlemeleri de etkileşimin kendisiyle yapılır. Botun
kendi başına yaptığı tek şey mevcut kartı **düzenlemektir** (süre dolumu, onaylı kapatma, başarısız bir etkileşim
düzenlemesinin telafisi): bu yalnızca `LfgCardSync` içinde `IMessageTransport.EditAsync` ile yapılır — `SendAsync` hiç
çağrılmaz, düzenlemeler asla ping atmaz (mimari test).

## Kanal

İsteğe bağlı: `/lfg-admin channel kanal:#ekip-bul` → `/ekip` yalnızca o kanalda çalışır (başka kanalda ephemeral
`Ekip ilanları bu sunucuda yalnızca #ekip-bul kanalında açılabilir.`). `/lfg-admin channel` (boş) kısıtı kaldırır. Ayar
yoksa `/ekip` kullanıldığı kanalda çalışır. Bot kartları süre dolunca düzenleyebilmek için kanalı görebilmelidir
(`ViewChannel`, `SendMessages`, `EmbedLinks`); eksikse ayar kaydedilir ama uyarı verilir.

## Yapılandırma (`Lfg`)

| Ayar | Varsayılan | Aralık |
|---|---|---|
| `DefaultExpirationMinutes` | `120` | 15–720 |
| `MaxActiveListingsPerUser` | `2` | 1–10 |
| `MaxPlayersPerListing` | `20` | 2–50 |

Modül **her sunucuda varsayılan kapalıdır** (`EnabledByDefault=false`) ve depodaki standart mekanizmayla açılır:
`/modules enable lfg`. Ayrı bir `Lfg:Enabled` bayrağı yoktur (harici bağımlılık olmadığından ikinci bir anahtar yalnızca
sürtünme eklerdi). Ortam değişkeni örneği: `TOROSQUAD_Lfg__MaxActiveListingsPerUser=3`.

## Veri ve gizlilik

Tablolar (additive migration `LfgModule`): `lfg_listing` (ilan; oyun adı + detay kullanıcının kendi metni),
`lfg_participant` (ListingId + UserId + katılma zamanı), `lfg_guild_config` (isteğe bağlı kanal). İndeksler:
`(Status, ExpiresAt)` süre dolumu taraması, `(GuildId, OwnerUserId, Status)` aktif ilan sınırı, `CardStale = 1` kısmi
indeks (bekleyen kart düzenlemeleri), `lfg_participant(UserId)` gizlilik sorguları; FK `ListingId` → `lfg_listing`
(cascade).

`/privacy export` açtığın ilanları (oyun, detay, durum, zamanlar) ve katıldığın ilanları içerir. `/privacy delete`
katıldığın ilanlardan seni çıkarır (dolu ilan yeniden açılır, kart senin olmadan yeniden çizilir) ve açtığın ilanları
oyuncularıyla siler; o ilanların kartındaki butonlar "Bu ilan artık mevcut değil." der ve butonlarını kaldırır. Sunucudan
ayrılma sonrası saklama süresi dolunca guild'in tüm LFG verisi silinir.

## Günlükler

`LFG listing {id} created|is full|closed by its owner/moderator|expired` (Information), `card message is gone … orphaned`,
`card update failed, will retry`, `giving up …` (Warning), `expiry/card recovery pass failed` (Error). Normal buton
tıklamaları ve beklenen retler (dolu, zaten üye…) günlüğe yazılmaz ve takip kodu üretmez; kullanıcı metni günlüğe
yazılmaz.
