# İşletim: dağıtım, yedekleme, geri yükleme, sürüm kontrol listesi

## Çalıştırma modeli
- Tek süreç, **tek instance** (veri dizininde işletim sistemi kilidi; ikinci instance başlamaz). Yatay ölçekleme desteklenmez.
- Veri: `Bot:DataDirectory` (scriptlerde `<repo>\data`) → `torosquad.db` (+ `-wal`, `-shm`), `torosquad.instance.lock`.
- Başlangıç sırası (`run`): yapılandırma doğrulaması → depolama kontrolleri (Railway'de volume zorunlu, klasör yazılabilir)
  → instance kilidi → **bütünlük kontrolü** (`PRAGMA integrity_check`; hata → başlamaz, hiçbir şey silinmez) → migration
  → Discord + işçiler. Elle: `db migrate`, `db check [DOSYA]` (salt okunur bütünlük).
- **Tek sunucu:** `Discord:AllowedGuildIds` = izin verilen sunucu(lar). Her etkileşim (komut, düğme, modal, otomatik
  tamamlama) işlenmeden önce sunucu kontrolü yapılır; liste dışı sunucu kısa bir retle geri çevrilir, hiçbir şey okunmaz/
  yazılmaz. Planlayıcı liste dışı sunucuyu görmez; teslimat da son anda iptal eder (`guild_not_allowed`). Liste varken
  `TestGuildIds`/`CommandSyncGuildIds` listenin dışına çıkamaz ve global komut izni açılamaz (başlangıç doğrulaması).
  **Aynı bot token'ıyla asla iki kopya çalıştırmayın** (ör. barındırılan üretim botu çalışırken yerelde üretim botu).
- **Bekleme modu** `Bot:Standby=true`: doğrular ve bekler; Discord yok, işçi yok, veritabanı açılmaz (dağıtım/bakım).
- **7/24 barındırma: Railway** — kurulum, değişkenler, volume, veritabanı
  taşıma, yedek ve geri dönüş: **[RAILWAY_DEPLOYMENT.md](RAILWAY_DEPLOYMENT.md)**. Depoda `Dockerfile` (SDK 10.0.401 →
  .NET 10 runtime), `.dockerignore`; servis ayarları Railway panelinde (1 replika, Serverless kapalı, Restart Always, volume `/data`) — Railway Config as Code kullanımdan kalktığı için `railway.json` yok.
  **Replika = 1 zorunludur** (SQLite + tek zamanlayıcı/outbox; Railway volume'lü serviste replikaya zaten izin vermez).
  Genel HTTP/domain yok. Resmî kaynaklar (okundu 2026-09-25): https://docs.railway.com/reference/volumes ·
  https://docs.railway.com/guides/volumes · https://docs.railway.com/reference/backups ·
  https://docs.railway.com/deployments/restart-policy · https://docs.railway.com/config-as-code/reference ·
  https://docs.railway.com/reference/variables · https://docs.railway.com/cli/volume ·
  https://docs.railway.com/reference/pricing/plans.

## Yedekleme / geri yükleme (SQLite)

Otomatik: çalışan bot 24 saatte bir `<veri klasörü>/backups/torosquad-<zaman>.db` yedeği alır (bütünlük kontrollü, en
yeni 7 tutulur; `Bot:Backup:Enabled|IntervalHours|Keep|Directory`). Yalnızca bu adlandırmadaki dosyalar temizlenir.
```powershell
dotnet run --project src\ToroSquad.Bot -- db backup                       # çalışırken güvenli (SQLite backup API)
dotnet run --project src\ToroSquad.Bot -- db backup --out D:\yedek
dotnet run --project src\ToroSquad.Bot -- db restore D:\yedek\torosquad-20260924T120000Z.db --yes   # bot DURDURULMUŞ olmalı
```
Restore: yedeğin `PRAGMA integrity_check` sonucunu doğrular, mevcut veritabanını `*.pre-restore` olarak saklar, kilit
tutulurken (bot çalışıyorsa) reddeder. Yedekler kullanıcı verisi içerir: güvenli yerde tutun, saklama süresine uyun
(docs/PRIVACY_AND_DATA.md). Önerilen: günlük yedek, 14 gün saklama.

## Migration kuralları
- Yeni/değişen tablo → `dotnet tool restore; dotnet ef migrations add <Ad> --project src\ToroSquad.Bot`.
- Test `Migrations_cover_the_model…` migration unutulursa kırılır.
- Yıkıcı migration (kolon/tablo silme, veri dönüştürme) **açık onay** gerektirir; önce yedek alın.

## Sağlayıcı ve tarama

- Varsayılan maç sağlayıcısı **PandaScore**: maçlar 5 dakikada bir (≤5 sayfa), etkinlikler 6 saatte bir; yerel bütçe planın
  %50'si (500 istek/saat), yeniden denemeler dahil. Başlangıçta bütçe doğrulanır. 429'da Retry-After'a uyulur.
- Sağlayıcı değiştirmek (`Esports:Provider:Name`) geçmişi duyurmaz (ilk açılış sağlayıcı bazındadır); eski sağlayıcının
  takım anahtarlarıyla yapılmış takipler/rol eşlemeleri yeni sağlayıcının takımlarıyla eşleşmez (yeniden eşlenmeli).
- Canlı bildirim kontrol edilmeden önce: token ile yalnızca okuma doğrulaması, sonra test sunucusunda TEST/DEMO kartları
  (`esports demo-cards`).

## Gözlem
- Loglar konsola, secret maskelemeli özel formatla. Önemli satırlar: `Esports poll: …`, `Outbox recovery`,
  `delivery unknown`, `Command manifest OK/problem`.
- Kullanıcıya hata yerine takip kodu gösterilir (`TS-XXXXXXXX`); loglarda aynı kodla aranır.
- Sunucu yöneticisi: `/esports-admin doctor`. İşletmeci: `.\scripts\Doctor.ps1`.

## Sürüm kontrol listesi
1. `git status` temiz; `.\scripts\Test.ps1 -Repeat 3` yeşil.
2. `commands export --out docs/commands.manifest.json` (Production ortamı) → değişiklik varsa commit.
3. `docs/STATUS.md` güncel; `VersionPrefix` (Directory.Build.props) artırıldı.
4. `Bot:SourceUrl` = https://github.com/Torokal/TSQ-Bot ve dağıtılan commit o depoda (AGPL Corresponding Source).
5. Yedek al → yeni sürümü dağıt → migration log'unu kontrol et → `/bot about` sürüm+commit doğru.
6. Komut şeması değiştiyse: test guild'de `Sync-Commands.ps1` dry-run → `-Apply` → doğrula → (onaylıysa) global.
7. Geri alma: önceki sürüm ikilisi + `db restore` (şema geri alınamaz; yedek şart).

## Herkese açık yayın kontrol listesi

Bot şu an tek sunucuda çalışır (global komut ve herkese açık davet yok). Birden çok sunucuya açılmadan önce:

1. **Secret taraması**: izlenen dosyalarda ve geçmişte token, API anahtarı, webhook, bağlantı dizesi, kimlik bilgisi
   içeren URL yok (GitHub secret scanning + push protection açık).
2. **İzlenen dosya denetimi**: `.env`, `*.db`/`*.sqlite`, `bin/`, `obj/`, `TestResults/`, loglar, yedekler, kullanıcı
   verisi ve IDE yerel dosyaları izlenmiyor.
3. **Kaynak arşivi**: `.\scripts\Export-Source.ps1` derlemeye yeten her şeyi içerir; veritabanı, kimlik bilgisi, kullanıcı
   verisi ve makineye özgü veri içermez.
4. **Lisans/provenance**: `LICENSE` (AGPL-3.0), upstream atıfları ve `THIRD_PARTY_NOTICES.md` güncel; BOT Greg'in resmî
   devamı gibi sunulmuyor.
5. **Kod/Discord/sağlayıcı**: dağıtılacak commit = güncel `main`, tam testler yeşil, izinler asgari, ayrıcalıklı intent
   yok, davet URL'si doğru, global komut geçişi (dry-run → uygulama) gözden geçirildi, sağlayıcı koşulları (atıf,
   kota) teyitli, VRS canlı doğrulandı.
6. Liquipedia ile HLTV bağlantı zenginleştirmesi isteğe bağlıdır; onaylı anahtar yoksa kapalı kalır,
   `Esports:VerifiedMatchLinks` elle yedek olarak çalışır. Ayrıntı: docs/PROVIDERS.md "Access and plans".
