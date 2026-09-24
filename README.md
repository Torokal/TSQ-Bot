# ToroSquad Bot

Modüler bir Discord botu. İlk özellik modülü **Counter-Strike 2 esports takibi ve bildirimleri** (BOT Greg'den
esinlenmiştir; resmî devamı değildir). Çekirdek; ileride moderasyon, karşılama, yayın bildirimi gibi modüllerin
eklenebileceği bir **modül sözleşmesi** üzerine kuruludur. Tüm kullanıcı arayüzü **gerçek Discord slash komutlarıdır**
(`/` seçicisinde görünür); mesaj içeriği okunmaz.

> Durum (2026-09-24): yerel geliştirme, testler ve çevrimdışı uçtan uca simülasyon **çalışıyor**. Gerçek Discord
> bağlantısı ve canlı Liquipedia verisi **henüz doğrulanmadı** (token / onaylı API anahtarı yok → BLOCKED).
> Ayrıntı: [docs/PROJECT_STATE.md](docs/PROJECT_STATE.md).

## Hızlı başlangıç (Windows, PowerShell)

```powershell
.\scripts\Doctor.ps1          # ortam + yapılandırma tanısı (secret değerleri yazdırılmaz)
.\scripts\Test.ps1            # build (analyzer=hata) + format + manifest + tüm testler
.\scripts\Start-Dev.ps1       # botu yerel güvenli modda çalıştırır (Discord bağlantısı YOK)
.\scripts\Start-Dev.ps1 -Simulate   # fixture veri → planlayıcı → outbox → sahte Discord, uçtan uca
.\scripts\Sync-Commands.ps1 -GuildId <id>          # slash komut kaydı: varsayılan DRY-RUN
.\scripts\Export-Source.ps1   # çalışan sürümün kaynak arşivi (AGPL-3.0 Corresponding Source)
```

Gereksinim: .NET SDK 10.0.401+ (`global.json` sabitler). Kurulum ve canlıya geçiş adımları:
[docs/WINDOWS_SETUP.md](docs/WINDOWS_SETUP.md).

## Güvenli varsayılanlar

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `Discord:Transport` | `Fake` | Discord'a bağlanılmaz; mesajlar süreç içinde tutulur |
| `Delivery:Mode` | `DryRun` | Bildirimler planlanır, kaydedilir, yalnızca loglanır |
| `Esports:Provider:Mode` | `Fixture` | Sentetik veri, gerçek istemci/parser kodundan geçer; mesajlar **TEST/DEMO** etiketli |
| `Discord:AllowGlobalCommandSync` | `false` | Global komut kaydı ayrı onay kapısıdır |

Canlı modda hata olursa fixture veriye **sessizce dönülmez**. Fixture veri gerçek bir sunucuya yalnızca
`Discord:TestGuildIds` listesindeki yetkili test sunucularında gösterilir.

## Komutlar (özet)

| Grup | Komutlar | Kimler |
|---|---|---|
| Genel | `/help`, `/bot status\|about\|source`, `/privacy export\|delete` | herkes |
| Yönetici | `/setup`, `/modules list\|enable\|disable` | Sunucuyu Yönet |
| Esports | `/esports matches\|results\|events\|rankings\|team\|follow\|unfollow\|subscriptions` | herkes (modül açıksa) |
| Esports yönetici | `/esports-admin configure\|filters …\|roles …\|panel\|preview\|pause\|resume\|doctor` | Sunucuyu Yönet (+ roller için Rolleri Yönet) |

Tam liste, izinler, intent'ler ve davet kapsamları: [docs/COMMANDS_AND_PERMISSIONS.md](docs/COMMANDS_AND_PERMISSIONS.md).
Üretilen komut manifesti: [docs/commands.manifest.json](docs/commands.manifest.json).

## Belgeler

| Konu | Belge |
|---|---|
| Güncel durum, kararlar, testler, blocker'lar, NEXT ACTION | [docs/PROJECT_STATE.md](docs/PROJECT_STATE.md) |
| Mimari ve ADR'ler | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/adr/](docs/adr/) |
| Yeni modül ekleme | [docs/ADDING_A_MODULE.md](docs/ADDING_A_MODULE.md) |
| Upstream kaynak, yeniden kullanım, lisans | [docs/PROVENANCE.md](docs/PROVENANCE.md) |
| Veri sağlayıcıları, koşullar, kotalar | [docs/PROVIDERS.md](docs/PROVIDERS.md) |
| Bildirimler, filtreler, roller, spoiler | [docs/NOTIFICATIONS.md](docs/NOTIFICATIONS.md) |
| Windows kurulumu, secret'lar, canlıya geçiş | [docs/WINDOWS_SETUP.md](docs/WINDOWS_SETUP.md) |
| Test kanıtları | [docs/TESTING.md](docs/TESTING.md) |
| Dağıtım, yedekleme/geri yükleme, sürüm kontrol listesi | [docs/OPERATIONS.md](docs/OPERATIONS.md) |
| Gizlilik ve veri saklama | [docs/PRIVACY_AND_DATA.md](docs/PRIVACY_AND_DATA.md), [docs/policies/](docs/policies/) |

## Lisans

**AGPL-3.0-only** ([LICENSE](LICENSE)). Veri erişim kodunun bir kısmı
[BOT-Greg-v2_API](https://github.com/julius-gmeinder/BOT-Greg-v2_API) (AGPL-3.0) kaynağından uyarlanmıştır; ayrıntı ve
atıflar [docs/PROVENANCE.md](docs/PROVENANCE.md) ve [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) içinde. Botu
başkalarına sunan işletmeci, çalışan sürümün kaynağını `Bot:SourceUrl` ile erişilebilir kılmakla yükümlüdür
(`/bot source`).
