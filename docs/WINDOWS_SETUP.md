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
Sunucuda: `/setup` → esports kanalı → önizleme → etkinleştir. Fixture modunda mesajlar `[TEST/DEMO]` etiketlidir ve
yalnızca `TestGuildIds` içindeki sunuculara gider. Gerçek bildirim göndermek için ayrıca `Delivery:Mode=Send` gerekir.

## 6. Canlı esports verisi (ayrı onay kapısı)

Liquipedia API erişimi başvuru ile ve çoğu planda ücretlidir (docs/PROVIDERS.md). Onaylı anahtar geldiğinde:
`Esports:Provider:Mode=Live`, `Esports:Liquipedia:ApiKey` (secret), `Esports:Liquipedia:UserAgent`
= `TSQBot/0.1 (<sizin URL'niz>; <iletişim e-postanız>)`. Doctor ve başlangıç doğrulaması eksikleri söyler.

## 7. Herkese açık kullanım (production)

`Bot:SourceUrl` zorunludur (AGPL-3.0). Varsayılanı kanonik açık depodur: https://github.com/Torokal/TSQ-Bot
(`/bot source` sürüm ve commit ile birlikte gösterir). Çalışan commit o depoda yayımlanmış olmalıdır; **değiştirilmiş** bir
sürüm çalıştırıyorsanız `Bot:SourceUrl` kendi deponuzu göstermeli ya da `.\scripts\Export-Source.ps1` arşivini yayımlamalısınız. `Bot:OperatorContact` ayarlayın. Global komut kaydı `Discord:AllowGlobalCommandSync=true`
gerektiren ayrı bir karardır. 7/24 barındırma seçilmemiştir — bkz. docs/OPERATIONS.md.
