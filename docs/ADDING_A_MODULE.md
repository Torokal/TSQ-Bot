# Yeni modül ekleme

Referans uygulama: [`src/ToroSquad.Modules.Example`](../src/ToroSquad.Modules.Example/ExampleModule.cs) — `/example ping`.
Bu modül **üretimde kayıtlı değildir** (`Modules:Example:Enabled=false`), geliştirmede açıktır ve kayıtlı olsa bile her
sunucuda yönetici `/modules enable example` diyene kadar kapalıdır. Testler
(`CommandManifestTests.New_module_adds_commands_without_changing_esports_or_core_commands`) bu modülün eklenmesinin
çekirdek ve esports komutlarını **bayt bayt değiştirmediğini** doğrular.

## Adımlar

1. **Proje**: `src/ToroSquad.Modules.<Ad>/` — yalnızca `ToroSquad.Core` ve `ToroSquad.Discord`'a (veri gerekiyorsa
   `ToroSquad.Infrastructure`'a) referans verin. Başka modüle referans vermeyin (mimari test yakalar).
2. **Modül tanımı** — `IToroModule`:
   ```csharp
   public sealed class PollsModule : IToroModule
   {
       public ModuleDescriptor Descriptor { get; } = new(
           new ModuleId("polls"), new Version(0, 1, 0), "module.polls.name", "module.polls.description",
           IsCore: false, EnabledByDefault: false,
           RequiredBotChannelPermissions: GuildPermission.SendMessages,
           OptionalBotPermissions: GuildPermission.None,
           AdminCommands: ["polls-admin"]);          // manifest doğrulaması bunları ManageGuild ister

       public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(PollCommands)];

       public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
       {
           services.AddSingleton(new LocalizationSource(typeof(PollsModule).Assembly, "ToroSquad.Modules.Polls.Localization"));
           // services.AddSingleton<IModelContributor, PollsModelContributor>();   // tablo gerekiyorsa
           // services.AddScoped<IDeliveryPolicy, PollsDeliveryPolicy>();          // bildirim gönderiyorsa
           // services.AddScoped<IUserDataContributor, PollsUserData>();           // kullanıcı verisi tutuyorsa (zorunlu)
       }

       public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
   }
   ```
3. **Komutlar** — `ToroInteractionModule`'dan türeyin ve **mutlaka** `[ToroModule("polls")]` ekleyin (guild bağlamı +
   modül kapısı; mimari test her interaction sınıfında arar). Yönetici grupları için
   `[DefaultMemberPermissions(GuildPermission.ManageGuild)]` + servis içinde `Authorize.Require(actor, …)`.
   Yanıtlar `ReplyResultAsync`/`ReplyEmbedAsync` ile (ephemeral ve ping'siz). Uzun işlerde önce `DeferEphemeralAsync()`.
4. **Metinler** — `Localization/tr.json` ve `en.json` (aynı anahtarlar ve yer tutucular; test denetler). Komut açıklamaları
   `cmd.<yol>` anahtarlarıyla (`cmd.polls`, `cmd.polls.create`, `cmd.polls.create.<seçenek>`). csproj'a:
   `<EmbeddedResource Include="Localization\*.json" LogicalName="ToroSquad.Modules.Polls.Localization.%(Filename)%(Extension)" />`
5. **Kayıt** — `src/ToroSquad.Bot/ToroHost.cs` → `Modules()` listesine tek satır. Tablo eklediyseniz
   `DesignTimeDbContextFactory.AllContributors()`'a katkıcınızı ekleyip migration üretin:
   `dotnet ef migrations add Polls --project src/ToroSquad.Bot`.
6. **Arka plan işi** varsa her döngüde `IModuleGate` kontrol edin; bildirimleri doğrudan göndermeyin, `INotificationOutbox`
   ile outbox'a yazın (dedup, pause/kapı kontrolü, retry, uzlaştırma ücretsiz gelir).
7. **Manifesti yenileyin**: `dotnet run --project src/ToroSquad.Bot -- commands export --out docs/commands.manifest.json`
   (üretim ortamında), `.\scripts\Test.ps1`, sonra test sunucusunda `.\scripts\Sync-Commands.ps1 -GuildId … -Apply`.

Çekirdeğin iş kurallarını değiştirmek gerekmez; tek zorunlu çekirdek dışı değişiklik composition root'taki kayıttır.
