# İşletim: dağıtım, yedekleme, geri yükleme, sürüm kontrol listesi

## Çalıştırma modeli
- Tek süreç, **tek instance** (veri dizininde işletim sistemi kilidi; ikinci instance başlamaz). Yatay ölçekleme desteklenmez.
- Veri: `Bot:DataDirectory` (scriptlerde `<repo>\data`) → `torosquad.db` (+ `-wal`, `-shm`), `torosquad.instance.lock`.
- Başlangıçta migration otomatik uygulanır (`run`). Elle: `dotnet run --project src\ToroSquad.Bot -- db migrate`.
- **7/24 barındırma henüz seçilmedi/kurulmadı.** Seçenekler (karar sahibi sizsiniz): Windows'ta hizmet olarak
  (ör. `sc.exe`/NSSM ile `ToroSquad.Bot.exe run`) veya bir VPS. Docker dağıtımı hazırlanmadı ve doğrulanmadı.

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

1. **Tüm git geçmişinde secret taraması** (tüm dallar, tüm commit'ler): Discord token, Liquipedia anahtarı, GitHub
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
   geçiş planı gözden geçirildi, Liquipedia erişim/lisans modeli teyitli, VRS canlı doğrulandı.
7. **Yalnızca açık yayın onayıyla**: `gh repo edit Torokal/TSQ-Bot --visibility public --accept-visibility-change-consequences`
   → anonim erişimi doğrula → ancak ondan sonra herkese açık bot/global komut kaydı.
