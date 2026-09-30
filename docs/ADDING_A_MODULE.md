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
           AdminCommands: [AdminCatalog.Entry("polls")]); // "tsq-admin polls": kayıtlı yönetim işlemleri

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
   modül kapısı; mimari test her interaction sınıfında arar). Yanıtlar `ReplyResultAsync`/`ReplyEmbedAsync` ile (ephemeral
   ve ping'siz). Uzun işlerde önce `DeferEphemeralAsync()`.
   **Yönetici işlemleri** ayrı bir slash komutu açmaz; tek düz `/tsq-admin modul:polls islem:<işlem>` komutuna kayıtlı
   işlemler olarak eklenir (alt komut yoktur). Bir işlem kaydı öneri listesini, doğrulamayı, yetki ön kontrolünü, yönlendirmeyi,
   `/help`'i ve eksiksizlik testlerini besler. İş mantığı modülün kendi işleyicisinde kalır; servis yine
   `Authorize.Require(actor, …)` çağırır:
   ```csharp
   public sealed class PollsAdminOperations(PollsConfigService config) : IAdminFormHandler
   {
       public static readonly AdminModule Definition = AdminModule.For<PollsAdminOperations>("polls", PollsModule.ModuleIdTyped)
           .Op("configure", (h, c) => h.ConfigureAsync(c), AdminFields.Channel)          // kanal: seçeneğini kullanır
           .Op("status", (h, c) => h.StatusAsync(c))                                      // izin varsayılanı: Manage Server
           .Build();

       public async Task ConfigureAsync(AdminCall call)
       {
           if (call.Args.ChannelId is not { } channel)
           {
               await AdminForms.PickChannelAsync(call, "admin.polls.configure.pick");   // eksik girdi → özel seçici
               return;
           }

           await call.DeferAsync();
           await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
       }

       public async Task OnFormAsync(AdminCall call, string action) { /* seçiciden gelen değeri doğrula, ClaimAsync, FinishAsync */ }
   }
   ```
   `ConfigureServices` içinde `services.AddAdminOperations(PollsAdminOperations.Definition);`. Ortak seçenekler yalnızca
   `kanal`, `uye`, `rol`, `tarih`; işlem kullanmadığı bir seçenekle çağrılırsa router reddeder. Başka girdiler işlemin kendi
   formundan gelir (`AdminForms`: kanal/üye seçici, seçim listesi, düğmeler, modal; çok alanlı ayarlarda yalnızca değişenler
   yazılır). Formlar kullanıcı + sunucu + modül + işleme bağlı, 15 dakikalık bellek içi taslaklardır; router her tıklamada
   sahibi, sunucuyu, süreyi ve izni yeniden denetler. Bildirilmiş ama kaydedilmemiş (veya tersi) yönetim modülü manifest
   hatasıdır ve sync'i engeller.
4. **Metinler** — `Localization/tr.json` ve `en.json` (aynı anahtarlar ve yer tutucular; test denetler). Komut açıklamaları
   `cmd.<yol>` anahtarlarıyla (`cmd.polls`, `cmd.polls.create`, `cmd.polls.create.<seçenek>`; yönetim işlemleri için
   etiketler `admin.polls` ("Anketler") ve `admin.polls.<işlem>`; öneride "Etiket — id" olarak görünür). csproj'a:
   `<EmbeddedResource Include="Localization\*.json" LogicalName="ToroSquad.Modules.Polls.Localization.%(Filename)%(Extension)" />`
5. **Kayıt** — `src/ToroSquad.Bot/ToroHost.cs` → `Modules()` listesine tek satır. Tablo eklediyseniz
   `DesignTimeDbContextFactory.AllContributors()`'a katkıcınızı ekleyip migration üretin:
   `dotnet ef migrations add Polls --project src/ToroSquad.Bot`.
6. **Arka plan işi** varsa her döngüde `IModuleGate` kontrol edin; bildirimleri doğrudan göndermeyin, `INotificationOutbox`
   ile outbox'a yazın (dedup, pause/kapı kontrolü, retry, uzlaştırma ücretsiz gelir).
7. **Manifesti yenileyin**: `dotnet run --project src/ToroSquad.Bot -- commands export --out docs/commands.manifest.json`
   (üretim ortamında), `.\scripts\Test.ps1`, sonra test sunucusunda `.\scripts\Sync-Commands.ps1 -GuildId … -Apply`.

Çekirdeğin iş kurallarını değiştirmek gerekmez; tek zorunlu çekirdek dışı değişiklik composition root'taki kayıttır.
