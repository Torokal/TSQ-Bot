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
           AdminCommands: [TsqAdminRoot.Group("polls")]); // "tsq-admin polls": manifest doğrulaması grubu ister

       public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(PollCommands), typeof(PollsTsqAdmin)];

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
   modül kapısı; mimari test her interaction sınıfında arar). Yanıtlar `ReplyResultAsync`/`ReplyEmbedAsync` ile (ephemeral
   ve ping'siz). Uzun işlerde önce `DeferEphemeralAsync()`.
   **Yönetici işlemleri** ayrı bir `/polls-admin` kökü açmaz; ortak `/tsq-admin` köküne bir grup olarak eklenir
   (`/tsq-admin polls <işlem>`). Kökün adı, açıklaması ve `ManageGuild` izni `TsqAdminRoot`'tan miras alınır; grup sınıfı
   kendi modül kapısını taşır, her işlem servis içinde `Authorize.Require(actor, …)` çağırır:
   ```csharp
   public sealed class PollsTsqAdmin : TsqAdminRoot
   {
       [ToroModule("polls", AllowWhenDisabled = true)]   // kurulum modül açılmadan yapılabilsin
       [Group("polls", "Poll settings (admins)")]
       public sealed class PollsAdminCommands(InteractionServices services, PollsConfigService config) : ToroInteractionModule(services)
       {
           [SlashCommand("configure", "Poll channel")]
           public async Task ConfigureAsync(IChannel channel) { /* … Authorize.Require servis içinde … */ }
       }
   }
   ```
   Discord bir komutun altında yalnızca **bir** grup seviyesine izin verir: grubun içinde `[Group]` açmayın. İşlemleri
   ayrı bir sınıfta toplamak isterseniz `[Group]` **olmadan** iç içe sınıf kullanıp adı `<altgrup>-<işlem>` yapın
   (`roles-map` gibi). Manifest oluşturucu kökü tek komutta birleştirir; kök seviyesinde komut, aynı grup adını iki modülün
   kullanması, `AdminCommands`'ta bildirilmemiş grup veya grup içinde grup sync'i engelleyen hatadır.
4. **Metinler** — `Localization/tr.json` ve `en.json` (aynı anahtarlar ve yer tutucular; test denetler). Komut açıklamaları
   `cmd.<yol>` anahtarlarıyla (`cmd.polls`, `cmd.polls.create`, `cmd.polls.create.<seçenek>`; yönetici grubu için
   `cmd.tsq-admin.polls`, `cmd.tsq-admin.polls.configure`, `cmd.tsq-admin.polls.configure.<seçenek>`). csproj'a:
   `<EmbeddedResource Include="Localization\*.json" LogicalName="ToroSquad.Modules.Polls.Localization.%(Filename)%(Extension)" />`
5. **Kayıt** — `src/ToroSquad.Bot/ToroHost.cs` → `Modules()` listesine tek satır. Tablo eklediyseniz
   `DesignTimeDbContextFactory.AllContributors()`'a katkıcınızı ekleyip migration üretin:
   `dotnet ef migrations add Polls --project src/ToroSquad.Bot`.
6. **Arka plan işi** varsa her döngüde `IModuleGate` kontrol edin; bildirimleri doğrudan göndermeyin, `INotificationOutbox`
   ile outbox'a yazın (dedup, pause/kapı kontrolü, retry, uzlaştırma ücretsiz gelir).
7. **Manifesti yenileyin**: `dotnet run --project src/ToroSquad.Bot -- commands export --out docs/commands.manifest.json`
   (üretim ortamında), `.\scripts\Test.ps1`, sonra test sunucusunda `.\scripts\Sync-Commands.ps1 -GuildId … -Apply`.

Çekirdeğin iş kurallarını değiştirmek gerekmez; tek zorunlu çekirdek dışı değişiklik composition root'taki kayıttır.
