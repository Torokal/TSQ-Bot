# TSQ Doğum Günü

Ayrı modül (`birthday`, `src/ToroSquad.Modules.Birthday`). Üyeler kendi doğum gününü **gün ve ay** olarak kaydeder
(`/birthday set 14.03`); yıl sorulmaz ve saklanmaz. Gün `Europe/Istanbul`'da başladığında bot duyuru kanalına tek bir mesaj
atar ve kutlananlara gün boyunca **Doğum Günü Bireyi** rolünü verir; rol ertesi gün 00:00'da (İstanbul) geri alınır.
Her sunucuda varsayılan kapalıdır (`/modules enable birthday`).

## Komutlar

| Komut | Kim | Ne yapar |
|---|---|---|
| `/birthday set <tarih>` | herkes | `14.03`, `14/03` veya `14-03` (gün önce). Var olan kaydı günceller. Olmayan tarihler (`31.02`, `00.05`, `32.01`, `13.13`) ve yıl içeren girdiler reddedilir; `29.02` geçerlidir. Cevaba her zaman "🎂 Lütfen gerçek doğum gününüzü giriniz. Bizim için önemli." eklenir |
| `/birthday show` | herkes | Yalnızca kendi kaydını gösterir |
| `/birthday remove` | herkes | Kendi kaydını siler; o gün botun verdiği rol bir sonraki turda geri alınır |
| `/tsq-admin modul:birthday islem:set uye:@üye tarih:14.03` | **Administrator** veya sunucu sahibi | Başka bir üyenin gün + ayını oluşturur/günceller (aynı `(sunucu, kullanıcı)` kaydı, aynı tarih kuralları). Manage Server yetmez. Hedef bu sunucunun insan üyesi olmalı |
| `/tsq-admin modul:birthday islem:show uye:@üye` | **Administrator** veya sunucu sahibi | Tek bir üyenin kayıtlı gün + ayını gösterir ("🎂 @X kullanıcısının kayıtlı doğum günü: 14 Mart" / "… kayıtlı bir doğum günü yok."). Yetki DB okunmadan önce denetlenir; yetkisiz kişi kayıt olup olmadığını bile öğrenemez. Her bakış denetim logudur (tarih loglanmaz). Hedef bu sunucunun insan üyesi olmalı |
| `/tsq-admin modul:birthday islem:configure kanal:` | Manage Server | Duyuru kanalı (bu sunucuda, botun görebildiği metin kanalı; tahmin edilmez) |
| `/tsq-admin modul:birthday islem:status` | Manage Server | Modül, kanal, rol ID, saat dilimi ve bugünün tarihi, kayıt sayısı, bugünkü kutlama sayısı ve duyuru durumu, aktif/sorunlu rol sayısı, zamanlayıcı |
| `/tsq-admin modul:birthday islem:doctor` | Manage Server | Yapılandırma, modül, veritabanı, kanal (var mı, görme, mesaj gönderme), rol (var mı, Rolleri Yönet, **hiyerarşi**, yönetilen rol, ek izin), zamanlayıcı, gönderim modu |

Tüm cevaplar ephemeral ve ping'siz. Toplu/herkese açık doğum günü listesi **yoktur**. Başkasının kaydını yalnızca
`/tsq-admin modul:birthday islem:show` (tek üye, okuma) ve `/tsq-admin modul:birthday islem:set` (yazma) görür/değiştirir. Discord bir alt komutu ayrı
gizleyemediği için `set` ve `show` Manage Server sahiplerine de görünür; yetki
sunucu tarafında çağıranın gerçek izinlerinden denetlenir (`Authorize.Require(..., Administrator)`; `ActorContext.Has`
sunucu sahibini de kabul eder = `user.Id == guild.OwnerId || Administrator`; rol adına bakılmaz). Yetkisiz çağrı hiçbir şey
yazmaz: "❌ Bu işlem için Yönetici (Administrator) yetkisine sahip olmalısınız." Bugünün tarihine yapılan admin-set normal
kurallara tabidir (duyuru henüz gitmediyse dahil olur, gittiyse yalnızca rol; yılda bir kutlama). Yönetici komutları modül
kapalıyken de çalışır (önce kanal ve doctor, sonra etkinleştirme).

## Duyuru

Bir sunucunun bir günü için **tek** düz mesaj (embed yok):

```
🎂 Bugün @Shotgun doğum gününü kutluyor!
İyi ki doğdun! 🥳

🎂 Bugün @A, @B ve @C doğum günlerini kutluyor!
İyi ki doğdunuz! 🥳
```

Kutlananlar **gerçekten pinglenir**, ama yalnızca onlar: `MentionPolicy.ExplicitUsers(named)` → `allowed_mentions =
{ parse: [], users: [metindeki kutlananlar] }`; @everyone/@here ve roller asla, metinden kimse ayrıştırılmaz. Liste metnin
kurulduğu kimliklerin kendisidir (veritabanından); 60'tan fazla kişide adı yazılmayanlar pinglenmez. Depo genelinde
kullanıcı pingi yalnızca iki üreticide mümkündür: `LfgNoticeRenderer` ve `BirthdayAnnouncementRenderer` (mimari test).
Belirsiz bir teslimden sonraki yeniden gönderim ping taşımaz (outbox). İsimden sonra ek gerektirmeyen cümle, her takma
adla doğru okunur.
Duyuru outbox üzerinden gider (modül kapısı, sunucu izin listesi, `Delivery:Mode=DryRun`, InFlight/uzlaştırma, retry) ve
günün sonunda (`ExpiresAt` = ertesi gün 00:00 İstanbul) teslim edilemediyse **ertesi gün gönderilmez**.

## Yeniden başlatma ve kesinti

00:00'da çalışan bir zamanlayıcı yoktur. `BirthdayReconciler` açılıştan 20 sn sonra, her 60 sn'de bir ve her zaman yerel
gece yarısından hemen sonra şunu yapar: İstanbul'daki bugünün tarihini hesaplar (UTC tarihi değil) → bugün doğan ve hâlâ
üye olanlar için kutlama kaydı (`birthday_celebration`, benzersiz *sunucu + kullanıcı + yıl*) → günün duyurusu yoksa
duyuru satırı (`birthday_announcement`, benzersiz *sunucu + yerel tarih*) + outbox satırı **aynı transaction'da** → bugünün
kutlananlarına rol, önceki günlerden (veya kaydı silinmiş) kalan **botun verdiği** rolleri geri alma → duyurunun outbox
sonucunu izleme. Böylece 08:00'de açılan bot bugünü yakalar, birkaç günlük kesintiden sonra eski roller temizlenir, eski
günler duyurulmaz; restart, redeploy, üst üste binen turlar veya retry duyuruyu ikiletmez (DB unique constraint).

- **Yılda bir kez**: bir üye aynı yıl içinde tarihini değiştirse de ikinci kez kutlanmaz (her gün rol alma yolu yok).
- **Günde tek mesaj**: duyurudan sonra o gün kayıt olan üye rolü alır, ikinci mesaj atılmaz.
- **29 Şubat** yalnızca gerçek 29 Şubat'ta kutlanır; artık yıl olmayan yılda 28 Şubat/1 Mart'a kaydırılmaz.
- **Ayrılan üye**: duyuruya girmez, rol denenmez, kayıt silinmez; günde bir kez Information logu (hata spam'i yok).
- **Discord'a ulaşılamıyorsa** (sunucu gateway'de yok, üye sorgusu başarısız) karar verilmez; duyuru bu üyeler için en
  fazla 10 tur bekler.

## Rol

Rol ID yapılandırmadır: `Birthday:RoleId` (varsayılan `1553890408348520468`; `0` = rol yok, yalnızca duyuru). Saat dilimi
`Birthday:TimeZone` (varsayılan `Europe/Istanbul`), tur aralığı `Birthday:ReconciliationIntervalSeconds` (30–600, 60).

- Rolü üye zaten taşıyorsa (elle verilmiş) kayıt `NotManaged` olur; bot o rolü **hiç** kaldırmaz. Yalnızca `Active`
  (botun verdiği) roller geri alınır.
- Her rol işlemi Discord çağrısından **önce** kaydedilir; hata veren bir ekleme sonrası tekrar denemeden önce üyenin
  rolleri okunur (zaman aşımına rağmen uygulanmışsa tekrar eklenmez, ertesi gün kaldırılır).
- Rolleri Yönet tek başına yetmez: botun en yüksek rolü Doğum Günü rolünün **üstünde** olmalıdır. Değilse (ya da rol
  yönetilen/entegrasyon rolüyse, `@everyone`'da olmayan bir izin veriyorsa, botta Rolleri Yönet yoksa) Discord çağrısı
  yapılmaz, `birthday_role_failed` uyarısı bir kez loglanır, doctor **BAŞARISIZ** gösterir; duyuru yine gider. Yönetici
  düzeltince o gün içinde bir sonraki turda rol verilir. Kanal izin geçersiz kılmaları (ör. doğum günü kanalı) engel değildir.
- Bir üyenin hatası diğerlerini durdurmaz (her üye ayrı iş birimi). Discord hatasında en fazla 5 deneme (kaldırmada 10),
  sonra `Failed` (doctor/status'ta görünür).
- `/birthday remove` veya `/privacy delete` aynı gün botun verdiği rolü geri alır.

## Veri

`birthday_registration` (sunucu, kullanıcı, gün, ay, zamanlar; benzersiz sunucu + kullanıcı), `birthday_celebration`
(sunucu, kullanıcı, yıl, yerel tarih, duyuruldu mu, rol durumu), `birthday_announcement` (sunucu, yerel tarih, kanal,
kişi sayısı, durum), `birthday_guild_config` (kanal, kanal sorunu). Migration yalnızca yeni tablo/indeks ekler. Kutlamalar
geçen yıldan eskiyse, duyurular 30 günden eskiyse, modülün outbox satırları (mention ID'leri içerir) teslimden 2 gün sonra
silinir. Loglarda kullanıcı ID'si bulunur; üyenin kendi kaydında girilen metin ve tarih loglanmaz (`source=self`). Admin-set
denetim izi için `source=admin admin=<id> user=<id> day month` loglar (yıl yok). Admin-show
`birthday_admin_viewed guild admin target found` loglar; gün/ay loglanmaz.

Log olayları: `birthday_registered`, `birthday_updated`, `birthday_removed`, `birthday_detected`,
`birthday_member_missing`, `birthday_announcement_queued|sent|skipped|failed`, `birthday_role_assigned`,
`birthday_role_removed`, `birthday_role_failed`, `birthday_reconciliation_started|completed|failed`.

## Canlıya alma

1. Deploy (migration açılışta uygulanır), `scripts/Sync-Commands.ps1` önce dry-run, sonra onayla.
2. `/tsq-admin modul:birthday islem:configure kanal:#kanal` → `/tsq-admin modul:birthday islem:doctor` (hiyerarşi ✅ olmalı).
3. `/modules enable birthday`. Etkinleştirmeden önce hiçbir şey duyurulmaz ve rol verilmez.
