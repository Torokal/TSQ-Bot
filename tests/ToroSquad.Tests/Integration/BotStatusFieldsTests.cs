using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Discord.Transport;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// /bot status with the production wiring (every module and health check): the fields fit Discord's 25 and its embed limits,
/// so no health line — TSQ Özet's last-failure line among them — is cut off.
/// </summary>
public sealed class BotStatusFieldsTests
{
    [Fact]
    public async Task Every_health_line_fits_into_one_status_embed()
    {
        await using var host = await TestHost.CreateAsync();
        var localizer = host.Services.GetRequiredService<ILocalizer>();
        var modules = await host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().ListAsync(new GuildId(777), CancellationToken.None));
        var health = new List<EmbedField>();
        foreach (var check in host.Services.GetServices<IModuleHealthCheck>())
            foreach (var entry in (await check.CheckAsync(CancellationToken.None)).Entries)
                health.Add(BotCommands.HealthField(localizer, "tr", entry));
        var overview = new List<EmbedField> { new("Bağlantı", "Bağlı (42 ms)", true), new("Çalışma başlangıcı", "<t:1:R>", true), new("Dağıtım", "Railway · Tek sunucu", true) };

        var fields = BotCommands.StatusFields(overview, "Modüller", modules.Select(m => (localizer.Get("tr", m.Descriptor.NameKey), "🟢 Açık")).ToList(), health);

        modules.Should().HaveCountGreaterThan(10, "the case that cut the list: one field per module");
        fields.Should().HaveCount(overview.Count + 1 + health.Count).And.HaveCountLessThanOrEqualTo(BotCommands.MaxEmbedFields);
        fields.Select(f => f.Name).Should().Contain(["Özet servisi", "Özet üretimi"]);
        fields.Should().OnlyContain(f => f.Value.Length <= 1024 && f.Name.Length <= 256);
        var act = () => DiscordConversions.ToEmbed(new MessageEmbed("TSQ Bot durumu", null, null, fields, "1.0.0 (abc1234)", null, 0x5865F2));
        act.Should().NotThrow("Discord.Net enforces the 6000-character embed total");
    }
}
