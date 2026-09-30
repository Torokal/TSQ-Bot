using Discord;
using Discord.Interactions;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Birthday.Application;

namespace ToroSquad.Modules.Birthday.Commands;

/// <summary>
/// /birthday set|show|remove — only ever the caller's own birthday in this guild (user and guild come from the interaction).
/// Every answer is private and pings nobody. There is deliberately no command here that shows or changes another member's
/// birthday (Administrators: /tsq-admin modul:birthday islem:set), and no list.
/// </summary>
[ToroModule(BirthdayModule.ModuleIdValue)]
[Group("birthday", "Your birthday (day and month only)")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class BirthdayCommands(InteractionServices services, BirthdayService birthdays) : ToroInteractionModule(services)
{
    [SlashCommand("set", "Save or change your birthday (day.month, e.g. 14.03)")]
    public async Task SetAsync([Summary("date", "Day and month: 14.03, 14/03 or 14-03 — please enter your real birthday")] string date)
    {
        await DeferEphemeralAsync();
        var result = await birthdays.SetAsync(Actor, date, CancellationToken.None);
        var text = result.Succeeded
            ? await T(result.MessageKey, await DateTextAsync((int)result.Args[0], (int)result.Args[1]))
            : await T(result.MessageKey);
        await SendEphemeralAsync(text + "\n" + await T("birthday.set.reminder"), null, null);
    }

    [SlashCommand("show", "Show your saved birthday (only you can see it)")]
    public async Task ShowAsync()
    {
        await DeferEphemeralAsync();
        var date = await birthdays.GetAsync(Actor, CancellationToken.None);
        if (date is not { } d)
            await ReplyTextAsync("birthday.show.none");
        else
            await ReplyTextAsync("birthday.show.value", await DateTextAsync(d.Day, d.Month));
    }

    [SlashCommand("remove", "Delete your saved birthday")]
    public async Task RemoveAsync()
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await birthdays.RemoveAsync(Actor, CancellationToken.None));
    }

    public static readonly string[] MonthKeys =
    [
        "birthday.month.1", "birthday.month.2", "birthday.month.3", "birthday.month.4", "birthday.month.5", "birthday.month.6",
        "birthday.month.7", "birthday.month.8", "birthday.month.9", "birthday.month.10", "birthday.month.11", "birthday.month.12",
    ];

    /// <summary>"14 Mart" / "March 14".</summary>
    private async Task<string> DateTextAsync(int day, int month) => await T("birthday.date", day, await T(MonthKeys[month - 1]));
}
