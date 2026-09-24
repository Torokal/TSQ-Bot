# Mimari

ToroSquad Bot bir **modüler monolittir**: tek çalıştırılabilir (`ToroSquad.Bot`), tek süreç, tek instance, SQLite.
Mikroservis, message broker, Redis, ayrı web paneli veya çalışma anında DLL yükleyen plugin sistemi **yoktur**
(bkz. [ADR-0001](adr/0001-modular-monolith.md)).

## Projeler ve bağımlılık yönü

```
                ┌───────────────────────────── ToroSquad.Bot (composition root, CLI, EF migrations)
                │                 │                    │                      │
                ▼                 ▼                    ▼                      ▼
ToroSquad.Modules.Esports   ToroSquad.Modules.Example  (her modül yalnızca platform katmanlarına bağlıdır)
     │        │       │            │        │
     │        │       ▼            │        ▼
     │        │   ToroSquad.Discord ◄───────┘      Discord.Net (yalnızca bu katman ve modüllerin Commands ad alanı)
     │        ▼       │
     │  ToroSquad.Infrastructure (EF Core + SQLite, outbox, config, log, yedekleme)
     │        │       │
     ▼        ▼       ▼
            ToroSquad.Core  (modül sözleşmeleri, guild bağlamı, yetki, localization, mesaj modeli, rol politikası)
```

| Proje | Sorumluluk | Bağımlı olamaz |
|---|---|---|
| `ToroSquad.Core` | `IToroModule`, `ModuleRegistry`, `IModuleGate`, `ActorContext`/`Authorize`, `OutgoingMessage`+`MentionPolicy`, `INotificationOutbox`, `IDeliveryPolicy`, gizlilik sözleşmesi, self-service rol politikası, localization | Discord.Net, EF Core, diğer tüm ToroSquad projeleri |
| `ToroSquad.Infrastructure` | `ToroDbContext` (modüllerin model katkılarıyla), depolar, outbox + dispatcher, tek-instance kilidi, secret redaction, yedek/geri yükleme, saklama süresi | Discord, modüller |
| `ToroSquad.Discord` | Interaction altyapısı, çekirdek komutlar, komut manifesti/doğrulama/senkron, gerçek ve sahte transport, guild geçidi | Infrastructure, modüller, EF |
| `ToroSquad.Modules.Esports` | CS2 alan modeli, sağlayıcılar, planlayıcı, servisler, komutlar | Example modülü; `Domain/Providers/Application/Persistence` ad alanları Discord SDK kullanamaz |
| `ToroSquad.Modules.Example` | Referans modül (üretimde kayıtlı değil) | Esports, Infrastructure |
| `ToroSquad.Bot` | Modül listesi, DI, yapılandırma doğrulama, CLI fiilleri, EF migration'ları | — |

Bu yönler `tests/ToroSquad.Tests/Architecture/ArchitectureTests.cs` ile (assembly referansları + ArchUnitNET tip
bağımlılıkları) denetlenir.

## Modül sözleşmesi

`IToroModule`: sabit `ModuleId`, sürüm, localization anahtarları, `IsCore`, `EnabledByDefault`, gerekli/opsiyonel bot
izinleri, **yönetici komut adları**, interaction sınıfları, `ConfigureServices`, `ValidateConfiguration`.
Sağlık: `IModuleHealthCheck`. Yaşam döngüsü: `IModuleLifecycleHandler`. Gönderim: `IDeliveryPolicy`.
Gizlilik: `IUserDataContributor`. Kurulum: `IModuleSetupFlow`. Veri: `IModelContributor`.

Modül kapısı (`IModuleGate`) tek karar noktasıdır ve şu yolların **hepsinde** uygulanır:

| Yol | Nerede |
|---|---|
| Slash komut, buton, select, modal | `[ToroModule("…")]` precondition (sunucu bağlamını da sunucu tarafında doğrular) |
| Autocomplete | `EsportsAutocompleteBase` (kapalıysa boş liste) |
| Arka plan planlama | `NotificationPlanner` (kapalı guild atlanır) |
| Gönderim | `OutboxProcessor` gönderimden hemen önce kapı + modül politikası (pause, kanal değişti, kanal sorunu) |

Kapatmak veri silmez; kuyrukta bekleyenler `Cancelled` olur ve yeniden açıldığında topluca gönderilmez (watermark).

## Katmanlarda ana akış

```
EsportsPoller (tek ortak fetch)  →  EsportsCache (komutlar/autocomplete buradan okur)
        │
        └─► NotificationPlanner ── tek SQLite transaction ──► match snapshot + outbox satırları
                                                                  │
OutboxProcessor ◄─────────────────────────────────────────────────┘
   kapı + politika → InFlight (commit) → IMessageTransport → Sent / Pending(retry) / Failed / DeliveryUnknown
```

Ayrıntılı kurallar: [NOTIFICATIONS.md](NOTIFICATIONS.md). Kararlar: [adr/](adr/).
