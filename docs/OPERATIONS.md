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
4. `.\scripts\Export-Source.ps1` → arşivi yayımla → `Bot:SourceUrl` bu sürüme işaret ediyor.
5. Yedek al → yeni sürümü dağıt → migration log'unu kontrol et → `/bot about` sürüm+commit doğru.
6. Komut şeması değiştiyse: test guild'de `Sync-Commands.ps1` dry-run → `-Apply` → doğrula → (onaylıysa) global.
7. Geri alma: önceki sürüm ikilisi + `db restore` (şema geri alınamaz; yedek şart).
