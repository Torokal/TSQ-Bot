# Windows kurulumu ve canlıya geçiş

WSL, Docker veya ek orkestrasyon gerekmez. Windows PowerShell 5.1 veya PowerShell 7 yeterlidir.

## 1. Ön koşullar

| Araç | Sürüm | Kurulum |
|---|---|---|
| .NET SDK | 10.0.401+ (`global.json`, `latestPatch`) | `winget install Microsoft.DotNet.SDK.10` |
| git | herhangi güncel | `winget install Git.Git` |

`dotnet-ef` global kurulmaz; repo yerel araç manifestindedir (`.config/dotnet-tools.json`): `dotnet tool restore`.

## 2. Yerel geliştirme (secret gerekmez)

```powershell
.\scripts\Doctor.ps1
.\scripts\Test.ps1
.\scripts\Start-Dev.ps1 -Simulate
.\scripts\Start-Dev.ps1
```

Yerel veri `<repo>\data\` altındadır (git'e girmez). Scriptler Development ortamında çalışır (örnek modül açık).

## 3. Secret'lar — sohbete, dosyaya veya git'e YAZMAYIN

Secret'lar yalnızca şu iki yoldan okunur (TSQ Bot `.env` dosyası **okumaz**):

**a) .NET user-secrets (geliştirme makinesi, Development ortamı):**
```powershell
dotnet user-secrets set "Discord:Token" "<BOT_TOKEN>" --project src\ToroSquad.Bot
dotnet user-secrets set "Esports:Liquipedia:ApiKey" "<LPDB_API_KEY>" --project src\ToroSquad.Bot
```
Değerler `%APPDATA%\Microsoft\UserSecrets\torosquad-bot-local-dev\secrets.json` içinde, repo dışında durur.

**b) Ortam değişkenleri (her ortam; `TOROSQUAD_` öneki, `:` yerine `__`):**
```powershell
[Environment]::SetEnvironmentVariable('TOROSQUAD_Discord__Token', '<BOT_TOKEN>', 'User')
```

Doctor yalnızca "set / NOT SET" yazar; loglar ve hata çıktıları bilinen token/anahtar biçimlerini maskeler.

## 4. Discord uygulaması (sizin hesabınızda — bu adımları TSQ Bot sizin yerinize yapmaz)

1. https://discord.com/developers/applications → yeni uygulama → **Bot** → token'ı yukarıdaki yolla kaydedin.
2. Privileged Gateway Intents: **hepsi kapalı** kalsın.
3. Uygulama kimliğini (Application ID) `appsettings` yerine user-secrets/env ile de verebilirsiniz:
   `dotnet user-secrets set "Discord:ApplicationId" "<id>" --project src\ToroSquad.Bot`.
4. Davet: OAuth2 URL Generator → kapsamlar `bot`, `applications.commands`; izin tamsayısı **84992**
   (self-service rolleri kullanacaksanız **268520448**). Administrator vermeyin. Botu önce **test sunucunuza** ekleyin.

## 5. Test sunucusunda canlı deneme (onay kapısı)

Yerel ayar (user-secrets veya env), örnek:
```powershell
dotnet user-secrets set "Discord:Transport" "Gateway" --project src\ToroSquad.Bot
dotnet user-secrets set "Discord:TestGuildIds:0" "<TEST_GUILD_ID>" --project src\ToroSquad.Bot
dotnet user-secrets set "Discord:CommandSyncGuildIds:0" "<TEST_GUILD_ID>" --project src\ToroSquad.Bot
.\scripts\Sync-Commands.ps1 -GuildId <TEST_GUILD_ID>          # önce DRY-RUN planını okuyun
.\scripts\Sync-Commands.ps1 -GuildId <TEST_GUILD_ID> -Apply   # sonra uygulayın
.\scripts\Start-Dev.ps1
```
Sunucuda: `/setup` → esports kanalı → önizleme → etkinleştir. Fixture modunda mesajlar TEST/DEMO etiketlidir (bildirim kartlarında footer'da, komut yanıtlarında başlıkta) ve
yalnızca `TestGuildIds` içindeki sunuculara gider. Gerçek bildirim göndermek için ayrıca `Delivery:Mode=Send` gerekir.

## 6. Canlı esports verisi (ayrı onay kapısı)

**PandaScore (varsayılan sağlayıcı).** https://app.pandascore.co adresinde hesap açıp panodan token alın (ücretsiz plan:
saatte 1.000 istek; ücretli plan otomatik açılmaz, karar sizin). Token'ı **sohbete yazmadan** yerel olarak kaydedin:
```powershell
dotnet user-secrets set "PandaScore:Token" "<TOKEN>" --project src\ToroSquad.Bot
dotnet user-secrets set "Esports:Provider:Mode" "Live" --project src\ToroSquad.Bot
```
Önce yalnızca okuma doğrulaması yapılır (gerçek bildirim göndermeden): `.\scripts\Doctor.ps1` → "PandaScore token: set".
Token yoksa canlı PandaScore **BLOCKED** olur; fixture modu ve testler token gerektirmez.

**Liquipedia (isteğe bağlı: otomatik HLTV maç sayfası bağlantıları).** Bot Liquipedia olmadan normal çalışır; PandaScore
verisi, hatırlatmalar ve sonuçlar Liquipedia'ya bağlı değildir. Onaylı bir LiquipediaDB anahtarı (`Esports:Liquipedia:ApiKey`,
secret) varsa PandaScore maçlarının HLTV bağlantıları Liquipedia'dan eşleştirilir (Doctor: "HLTV match links via Liquipedia").
Güncel durum (2026-09-25): LPDB en fazla **60 istek/saat**; TSQ Bot bağlantı kaynağı için 30 dakikada bir sorgular (en kötü
≈10 istek/saat) ve yanıtı veritabanında önbelleğe alır. Basic/Premium planlar **geçici olarak kullanılamıyor**; ticari
tarafta Enterprise sunuluyor. Ücretsiz erişim açık kaynak eğitim / ticari olmayan kamu / topluluk projeleri için
**başvuruya** bağlı ve çoğu zaman süreli. Depo geliştirme boyunca **private** kalır; ücretsiz erişim başvurusu yayın
aşamasında depo public olduktan **sonra** yapılacak. O zamana kadar bu zenginleştirme **BLOCKED/OPTIONAL**'dır.
User-Agent: Liquipedia koşullarında iletişim bilgili User-Agent şartı açıkça **MediaWiki API** bölümünde yer alır; LPDB
bölümünde ayrıca belirtilmez. TSQ Bot yine de her istekte özel bir User-Agent gönderir (ayarlanmamışsa
`TSQBot (https://github.com/Torokal/TSQ-Bot)`); kendi iletişiminizi eklemeniz **önerilir**, zorunlu değildir:
`Esports:Liquipedia:UserAgent` = `TSQBot/0.1 (<sizin URL'niz>; <iletişim e-postanız>)`.
**Liquipedia'yı maç sağlayıcısı yapmak (eski/isteğe bağlı).** `Esports:Provider:Name=Liquipedia` seçilirse onaylı anahtar
gerekir ve `Esports:MatchPollMinutes` ≥ 10 olmalıdır (60 istek/saat bütçesi).

**Doğrulanmış maç sayfaları (elle, her zaman çalışır).** HLTV kazınmaz; bir maç için doğrulanmış HLTV/resmî bağlantıyı elle
ekleyebilirsiniz (`Esports:VerifiedMatchLinks`, docs/NOTIFICATIONS.md). Liquipedia anahtarı olmadan bu, HLTV bağlantısının
tek yoludur. Doctor ve başlangıç doğrulaması eksikleri söyler.

**TEST/DEMO kartları (Discord görünüm testi).** Bot `Start-Dev.ps1` ile çalışırken, **aynı veri klasörüyle**:
```powershell
$env:TOROSQUAD_Bot__DataDirectory = "$PWD\data"   # Start-Dev.ps1 ile aynı veritabanı
dotnet run --project src\ToroSquad.Bot -- esports demo-cards --guild <TEST_GUILD_ID>          # önizleme
dotnet run --project src\ToroSquad.Bot -- esports demo-cards --guild <TEST_GUILD_ID> --apply  # kuyruğa al
```
Yalnızca `Discord:TestGuildIds` içindeki sunucuya, TEST/DEMO etiketli ve ping'siz gider; tekrar çalıştırmak kopya üretmez.

## 7. Herkese açık kullanım (production)

`Bot:SourceUrl` zorunludur (AGPL-3.0). Varsayılanı kanonik depodur: https://github.com/Torokal/TSQ-Bot (`/bot source`
sürüm ve commit ile birlikte gösterir). Depo geliştirme/test süresince **private**tir; **PRE-RELEASE REQUIREMENT:** bot
herkese açılmadan önce depo public yapılır ve yayımlanan kaynak dağıtılan commit ile eşleşmelidir (bkz. docs/OPERATIONS.md
"Yayın öncesi güvenlik kapısı"). **Değiştirilmiş** bir sürüm işletiyorsanız `Bot:SourceUrl` kendi deponuzu göstermeli ya da
`.\scripts\Export-Source.ps1` arşivini yayımlamalısınız. `Bot:OperatorContact` ayarlayın. Global komut kaydı `Discord:AllowGlobalCommandSync=true`
gerektiren ayrı bir karardır. 7/24 barındırma: Railway — bkz. docs/RAILWAY_DEPLOYMENT.md.
