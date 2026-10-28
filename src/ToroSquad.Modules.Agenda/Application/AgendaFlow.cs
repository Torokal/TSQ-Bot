using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;

namespace ToroSquad.Modules.Agenda.Application;

/// <summary>A card button: what to show again and who may press it. Carries no authority — every click is re-checked.</summary>
public sealed record AgendaButton(char Action, AgendaView View, AgendaSport Sport, AgendaPeriod Period, ulong Owner, int Page);

/// <summary>
/// <c>tsq:agenda:&lt;n|r&gt;:&lt;a|g&gt;:&lt;sport&gt;:&lt;period&gt;:&lt;owner&gt;:&lt;page&gt;</c> — stateless, so a page or refresh
/// still works after a restart; the card's age (its message timestamp) limits how long it can be used.
/// </summary>
public static partial class AgendaCustomId
{
    public const string Prefix = "tsq:agenda:";
    public const char Navigate = 'n';
    public const char Refresh = 'r';

    public static string Format(char action, AgendaRequest request, int page) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{action}:{(request.View == AgendaView.Digest ? 'g' : 'a')}:{(int)request.Sport}:{(int)request.Period}:{request.Invoker.Value}:{page}");

    public static bool TryParse(string? customId, out AgendaButton button)
    {
        button = null!;
        var m = customId is null ? Match.Empty : Pattern().Match(customId);
        if (!m.Success || !ulong.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) ||
            !int.TryParse(m.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            return false;
        button = new AgendaButton(m.Groups[1].Value[0], m.Groups[2].Value == "g" ? AgendaView.Digest : AgendaView.Agenda,
            (AgendaSport)(m.Groups[3].Value[0] - '0'), (AgendaPeriod)(m.Groups[4].Value[0] - '0'), owner, page);
        return true;
    }

    [GeneratedRegex(@"^tsq:agenda:([nr]):([ag]):([0-3]):([0-3]):([1-9][0-9]{0,19}):([0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>A short per-card cooldown for page and refresh clicks. Bounded: old entries are pruned, the map never grows past its cap.</summary>
public sealed class AgendaCooldown(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    public const int Capacity = 2000;

    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _last = new();

    public bool TryEnter(MessageId message)
    {
        var now = clock.GetUtcNow();
        if (_last.Count >= Capacity)
        {
            foreach (var (key, at) in _last)
            {
                if (now - at >= Window)
                    _last.TryRemove(key, out _);
            }

            if (_last.Count >= Capacity)
                _last.Clear();
        }

        var entered = false;
        _last.AddOrUpdate(message.Value, _ => { entered = true; return now; }, (_, at) =>
        {
            if (now - at < Window)
                return at;
            entered = true;
            return now;
        });
        return entered;
    }
}

/// <summary>How the flow answers (the Discord command class; faked in tests).</summary>
public interface IAgendaResponder
{
    /// <summary>Public "thinking…" acknowledgement: the card replaces it (a private acknowledgement could never become public).</summary>
    Task DeferPublicAsync();

    Task ReplyPublicAsync(AgendaCard card);

    /// <summary>Only the member who clicked or typed sees it.</summary>
    Task ReplyPrivateAsync(string text);

    /// <summary>Acknowledges a button click; the same message is edited afterwards.</summary>
    Task DeferUpdateAsync();

    /// <summary>Edits the card the button was on (never a second public message).</summary>
    Task UpdateAsync(AgendaCard card);
}

/// <summary>A button click: where it happened and on which card.</summary>
public sealed record AgendaClick(GuildId Guild, ChannelId Channel, UserId Clicker, MessageId Message, DateTimeOffset MessageCreatedAt, string? CustomId);

/// <summary>
/// /ajanda, /gundem and their buttons. A command answers with ONE public card (pages edit that same message). A button is
/// checked before anything is read: well-formed, pressed by the member who opened the card, card not older than
/// <see cref="CardLifetime"/>, not clicked again within the cooldown — each refusal is private and changes nothing. A click
/// that passes recomputes everything (time, days, sources, access) from scratch.
/// </summary>
public sealed class AgendaFlow(AgendaService service, AgendaRenderer renderer, AgendaCooldown cooldown, IGuildSettingsStore settings, ILocalizer localizer, TimeProvider clock)
{
    public static readonly TimeSpan CardLifetime = TimeSpan.FromMinutes(30);

    public async Task CommandAsync(AgendaRequest request, IAgendaResponder responder, CancellationToken ct)
    {
        await responder.DeferPublicAsync();
        var snapshot = await service.BuildAsync(request, ct);
        await responder.ReplyPublicAsync(renderer.Render(snapshot, 0));
    }

    public async Task ButtonAsync(AgendaClick click, IAgendaResponder responder, CancellationToken ct)
    {
        var lang = (await settings.GetAsync(click.Guild, ct)).Language;
        if (!AgendaCustomId.TryParse(click.CustomId, out var button))
        {
            await responder.ReplyPrivateAsync(localizer.Get(lang, "agenda.button.invalid"));
            return;
        }

        if (button.Owner != click.Clicker.Value)
        {
            await responder.ReplyPrivateAsync(localizer.Get(lang, button.View == AgendaView.Digest ? "agenda.button.not_yours_digest" : "agenda.button.not_yours_agenda"));
            return;
        }

        if (clock.GetUtcNow() - click.MessageCreatedAt > CardLifetime)
        {
            await responder.ReplyPrivateAsync(localizer.Get(lang, "agenda.button.expired"));
            return;
        }

        if (!cooldown.TryEnter(click.Message))
        {
            await responder.ReplyPrivateAsync(localizer.Get(lang, "agenda.button.cooldown"));
            return;
        }

        await responder.DeferUpdateAsync();
        var request = new AgendaRequest(click.Guild, click.Channel, click.Clicker, button.View, button.Sport, button.Period);
        var snapshot = await service.BuildAsync(request, ct);
        await responder.UpdateAsync(renderer.Render(snapshot, button.Page));
    }
}
