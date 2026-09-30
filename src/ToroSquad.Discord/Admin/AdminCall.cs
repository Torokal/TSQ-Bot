using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Discord;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;

namespace ToroSquad.Discord.Admin;

/// <summary>
/// How an admin operation answers. The slash command, its buttons, selects and modals implement it on the live interaction;
/// tests use a recording fake. Admin answers are private unless an operation explicitly posts publicly (the esports panel).
/// </summary>
public interface IAdminResponder
{
    /// <summary>Acknowledge a slash command within Discord's 3 seconds (no-op for a click or a modal: they answer at once).</summary>
    Task DeferAsync();

    /// <summary>The localized result text plus, for a failure, its trace line (logged under the same code).</summary>
    Task<string> DescribeAsync(OperationResult result);

    /// <summary>A new message: private by default.</summary>
    Task SendAsync(string? text, MessageEmbed? embed = null, MessageComponent? components = null, bool ephemeral = true);

    /// <summary>Replace the form message this click came from (or answer privately when there is none).</summary>
    Task UpdateAsync(string? text, MessageComponent? components, MessageEmbed? embed = null);

    /// <summary>Open a modal as the first answer to this interaction.</summary>
    Task ModalAsync(Modal modal);
}

/// <summary>A member picked in the <c>uye</c> option or a user select, as Discord resolved it.</summary>
/// <param name="IsHumanMemberHere">A human member of THIS guild (never a bot, never someone who left).</param>
public sealed record AdminMember(ulong Id, bool IsHumanMemberHere)
{
    public static AdminMember From(IUser user, GuildId guild) =>
        new(user.Id, user is IGuildUser { IsBot: false } member && member.GuildId == guild.Value);
}

/// <summary>The shared slash options, already checked (the channel is a text/announcement channel of this guild).</summary>
public sealed record AdminArgs(ulong? ChannelId, AdminMember? Member, ulong? RoleId, string? Date)
{
    public static readonly AdminArgs None = new(null, null, null, null);
}

/// <summary>What a form interaction carried: select values, or a modal's fields by custom id, plus Discord's resolved entities.</summary>
public sealed record AdminInput(
    IReadOnlyList<string> Values,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    IReadOnlyCollection<IGuildUser> Members,
    IReadOnlyCollection<IChannel> Channels)
{
    public static readonly AdminInput None = new([], new Dictionary<string, IReadOnlyList<string>>(), [], []);

    /// <summary>A button or select click: its values and Discord's resolved members and channels.</summary>
    public static AdminInput FromComponent(IComponentInteractionData data) =>
        new(data.Values?.ToList() ?? [], new Dictionary<string, IReadOnlyList<string>>(), data.Members?.ToList() ?? [], data.Channels?.Cast<IChannel>().ToList() ?? []);

    /// <summary>
    /// A submitted modal: every field by custom id — a text input's value, or a select's / checkbox group's values (an empty
    /// list when nothing is ticked). A field Discord did not send is absent, not empty.
    /// </summary>
    public static AdminInput FromModal(IModalInteractionData data) =>
        new([], data.Components.Where(c => !string.IsNullOrEmpty(c.CustomId)).GroupBy(c => c.CustomId).ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)(g.First().Value is { } text ? [text] : g.First().Values?.ToList() ?? [])),
            data.Members?.ToList() ?? [], data.Channels?.Cast<IChannel>().ToList() ?? []);

    public string? Text(string field) => Fields.TryGetValue(field, out var v) && v.Count > 0 ? v[0] : null;

    public IReadOnlyList<string> Selected(string field) => Fields.TryGetValue(field, out var v) ? v : [];

    public ulong? Id(string field) =>
        ulong.TryParse(Text(field), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The (first) value picked in a select.</summary>
    public string? FirstValue => Values.Count > 0 ? Values[0] : null;

    public ulong? FirstValueId => ulong.TryParse(Values.Count > 0 ? Values[0] : null, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>Everything an admin operation needs: the verified caller, the answer channel, the shared options, the form state.</summary>
public sealed class AdminCall(
    ActorContext actor,
    string language,
    ILocalizer localizer,
    IAdminResponder respond,
    AdminDrafts drafts,
    AdminModule module,
    AdminOperation operation,
    AdminArgs args,
    AdminDraft? draft = null,
    AdminInput? input = null)
{
    public const string CustomIdPrefix = "tsq:adm:";

    public ActorContext Actor => actor;
    public string Language => language;
    public ILocalizer Localizer => localizer;
    public IAdminResponder Respond => respond;
    public AdminModule Module => module;
    public AdminOperation Operation => operation;
    public AdminArgs Args => args;

    /// <summary>The form this interaction belongs to (null for the slash command itself).</summary>
    public AdminDraft? Draft => draft;

    public AdminInput Input => input ?? AdminInput.None;

    public string T(string key, params object?[] values) => localizer.Get(language, key, values);

    public Task DeferAsync() => respond.DeferAsync();

    public async Task ReplyResultAsync(OperationResult result) => await respond.SendAsync(await respond.DescribeAsync(result));

    public Task ReplyTextAsync(string key, params object?[] values) => respond.SendAsync(T(key, values));

    public Task ReplyEmbedAsync(MessageEmbed embed) => respond.SendAsync(null, embed);

    /// <summary>End a form: its message shows the result and loses its buttons (answers privately when there is no form message).</summary>
    public async Task FinishAsync(OperationResult result) => await respond.UpdateAsync(await respond.DescribeAsync(result), EmptyComponents);

    public Task FinishTextAsync(string text) => respond.UpdateAsync(text, EmptyComponents);

    /// <summary>Start a private form bound to this caller, guild, module and operation.</summary>
    public AdminDraft OpenDraft(object state) => drafts.Open(actor, module.Id, operation.Id, state);

    /// <summary>Keep the form open with new state (a later click sees it).</summary>
    public void UpdateDraft(object state)
    {
        if (draft is not null)
            drafts.Update(draft.Id, state);
    }

    /// <summary>Claim the form for its final action: only one of two fast clicks gets it; the other gets null.</summary>
    public AdminDraft? TakeDraft() => draft is null ? null : drafts.Take(draft.Id);

    public static string CustomId(AdminDraft draft, string action) => CustomIdPrefix + draft.Id + ":" + action;

    public static readonly MessageComponent EmptyComponents = new ComponentBuilder().Build();
}

/// <summary>A private form's server-side state: bound to one caller in one guild, one module and one operation.</summary>
public sealed record AdminDraft(string Id, GuildId Guild, UserId User, string Module, string Operation, object State, DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory form state (like the LFG form drafts): short-lived, bounded, lost on restart (the form then says so). No tokens,
/// no service scopes and no personal data beyond ids are kept; the custom id carries only the random draft id.
/// </summary>
public sealed class AdminDrafts(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int MaxPerUser = 5;
    public const int MaxTotal = 500;

    private readonly ConcurrentDictionary<string, AdminDraft> _drafts = new(StringComparer.Ordinal);

    public AdminDraft Open(ActorContext actor, string module, string operation, object state)
    {
        Sweep();
        foreach (var old in _drafts.Values.Where(d => d.User == actor.UserId && d.Guild == actor.GuildId).OrderByDescending(d => d.ExpiresAt).Skip(MaxPerUser - 1))
            _drafts.TryRemove(old.Id, out _);
        if (_drafts.Count >= MaxTotal)
        {
            foreach (var oldest in _drafts.Values.OrderBy(d => d.ExpiresAt).Take(_drafts.Count - MaxTotal + 1))
                _drafts.TryRemove(oldest.Id, out _);
        }

        var draft = new AdminDraft(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), actor.GuildId, actor.UserId, module, operation, state,
            clock.GetUtcNow() + Lifetime);
        _drafts[draft.Id] = draft;
        return draft;
    }

    /// <summary>The draft, or null when it is unknown or expired (expired drafts are dropped).</summary>
    public AdminDraft? Get(string id)
    {
        if (!_drafts.TryGetValue(id, out var draft))
            return null;
        if (draft.ExpiresAt > clock.GetUtcNow())
            return draft;
        _drafts.TryRemove(id, out _);
        return null;
    }

    public void Update(string id, object state)
    {
        if (_drafts.TryGetValue(id, out var draft))
            _drafts.TryUpdate(id, draft with { State = state }, draft);
    }

    /// <summary>Atomically removes and returns a live draft (a second caller gets null).</summary>
    public AdminDraft? Take(string id) =>
        _drafts.TryRemove(id, out var draft) && draft.ExpiresAt > clock.GetUtcNow() ? draft : null;

    public void Remove(string id) => _drafts.TryRemove(id, out _);

    public int Count => _drafts.Count;

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var expired in _drafts.Values.Where(d => d.ExpiresAt <= now))
            _drafts.TryRemove(expired.Id, out _);
    }
}
