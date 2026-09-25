# İşletim: dağıtım, yedekleme, geri yükleme, sürüm kontrol listesi

## Çalıştırma modeli
- Tek süreç, **tek instance** (veri dizininde işletim sistemi kilidi; ikinci instance başlamaz). Yatay ölçekleme desteklenmez.
- Veri: `Bot:DataDirectory` (scriptlerde `<repo>\data`) → `torosquad.db` (+ `-wal`, `-shm`), `torosquad.instance.lock`.
- Başlangıç sırası (`run`): yapılandırma doğrulaması → depolama kontrolleri (Railway'de volume zorunlu, klasör yazılabilir)
  → instance kilidi → **bütünlük kontrolü** (`PRAGMA integrity_check`; hata → başlamaz, hiçbir şey silinmez) → migration
  → Discord + işçiler. Elle: `db migrate`, `db check [DOSYA]` (salt okunur bütünlük).
- **Bekleme modu** `Bot:Standby=true`: doğrular ve bekler; Discord yok, işçi yok, veritabanı açılmaz (dağıtım/bakım).
- **7/24 barındırma: Railway** (özel test barındırma, sahip kararı 2026-09-25) — kurulum, değişkenler, volume, veritabanı
  taşıma, yedek ve geri dönüş: **[RAILWAY_DEPLOYMENT.md](RAILWAY_DEPLOYMENT.md)**. Depoda `Dockerfile` (SDK 10.0.401 →
  .NET 10 runtime), `.dockerignore`; servis ayarları Railway panelinde (1 replika, Serverless kapalı, Restart Always, volume `/data`) — Railway Config as Code kullanımdan kalktığı için `railway.json` yok.
  **Replika = 1 zorunludur** (SQLite + tek zamanlayıcı/outbox; Railway volume'lü serviste replikaya zaten izin vermez).
  Genel HTTP/domain yok. Resmî kaynaklar (okundu 2026-09-25): https://docs.railway.com/reference/volumes ·
  https://docs.railway.com/guides/volumes · https://docs.railway.com/reference/backups ·
  https://docs.railway.com/deployments/restart-policy · https://docs.railway.com/config-as-code/reference ·
  https://docs.railway.com/reference/variables · https://docs.railway.com/cli/volume ·
  https://docs.railway.com/reference/pricing/plans.

## Yedekleme / geri yükleme (SQLite)
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
3. `docs/PROJECT_STATE.md` güncel; `VersionPrefix` (Directory.Build.props) artırıldı.
4. `Bot:SourceUrl` = https://github.com/Torokal/TSQ-Bot ve dağıtılan commit o depoda. İlk herkese açık yayından önce
   aşağıdaki **yayın öncesi güvenlik kapısı** tamamlanmış ve depo public yapılmış olmalı.
5. Yedek al → yeni sürümü dağıt → migration log'unu kontrol et → `/bot about` sürüm+commit doğru.
6. Komut şeması değiştiyse: test guild'de `Sync-Commands.ps1` dry-run → `-Apply` → doğrula → (onaylıysa) global.
7. Geri alma: önceki sürüm ikilisi + `db restore` (şema geri alınamaz; yedek şart).

## Yayın öncesi güvenlik kapısı (PRE-RELEASE REQUIREMENT)

Depo `Torokal/TSQ-Bot` geliştirme/test süresince **private**tir. Bot herkese açılmadan **hemen önce** ve yalnızca sahip
açıkça "public release" aşamasına geçtiğinde yapılır. Durum: **henüz başlamadı / gerekli değil**.

1. **Tüm git geçmişinde secret taraması** (tüm dallar, tüm commit'ler): Discord token, PandaScore token, Liquipedia anahtarı, GitHub
   kimlik bilgisi, webhook, bağlantı dizesi, parola, kimlik bilgisi içeren URL, yerel ortam değerleri. Bilinen tek
   istisna: `6c4b696`/`5570842` içindeki `OperationsTests.cs:86` **sahte** test token'ı (GitHub'da "used in tests"
   olarak izinli; gerçek değil). Gerçeğe benzeyen herhangi bir şey yayından önce çözülür.
2. **İzlenen dosya denetimi**: `.env`, `*.db`/`*.sqlite`, `bin/`, `obj/`, `TestResults/`, `artifacts/`, loglar, dışa
   aktarımlar, kullanıcı verisi, yedekler, IDE yerel dosyaları, secret yapılandırması izlenmiyor olmalı.
3. **Kaynak arşivi denetimi**: `.\scripts\Export-Source.ps1` → arşiv derlemeye yetecek her şeyi içerir; veritabanı,
   kimlik bilgisi, kullanıcı verisi, build/cache çıktısı ve makineye özgü veri içermez.
4. **Lisans/provenance**: `LICENSE` (AGPL-3.0) mevcut; upstream atıfları doğru; BOT Greg'in resmî devamı gibi
   sunulmuyor; bağımlılık bildirimleri (`THIRD_PARTY_NOTICES.md`) güncel.
5. **README/kamuya dönük metin**: özellikler, dağıtım durumu, komutlar, kurulum, test, lisans, kaynak, sınırlamalar,
   sağlayıcı gereksinimleri, ertelenen özellikler; VERIFIED_LIVE olmayan hiçbir şey canlı test edilmiş gibi sunulmaz.
6. Kod/Discord/sağlayıcı kontrolleri: dağıtılacak commit = güncel `main`, temiz build ve tam testler, sabit test guild
   ID'si veya makine yolu yok, izinler asgari, ayrıcalıklı intent yok (ya da gerekçeli), davet URL'si doğru, global komut
   geçiş planı gözden geçirildi, PandaScore planı/koşulları (atıf, ücretsiz planda sonuç alanları) ve gerekiyorsa Liquipedia erişimi teyitli, VRS canlı doğrulandı.
7. **Yalnızca açık yayın onayıyla**: `gh repo edit Torokal/TSQ-Bot --visibility public --accept-visibility-change-consequences`
   → anonim erişimi doğrula → ancak ondan sonra herkese açık bot/global komut kaydı.
8. **Depo public olduktan sonra (sahip kararı)**: Liquipedia ücretsiz LiquipediaDB API erişimine başvuru (açık kaynak /
   ticari olmayan / topluluk projeleri için başvuruya bağlı, çoğu zaman süreli; Basic/Premium 2026-09-25 itibarıyla geçici
   olarak kullanılamıyor). Depo **yalnızca** Liquipedia erişimi için erkenden public yapılmaz. Onaylanana kadar HLTV
   bağlantı zenginleştirmesi BLOCKED/OPTIONAL kalır; `Esports:VerifiedMatchLinks` elle yedek olarak çalışır; PandaScore
   Liquipedia'ya bağlı değildir. Ayrıntı: docs/PROVIDERS.md "Access and plans".
