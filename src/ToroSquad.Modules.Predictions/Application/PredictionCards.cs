using System.Globalization;
using System.Text;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

public sealed record OutcomeView(long Id, int Position, string Label, int OddsX100);

/// <summary>A prediction as stored, with its outcomes (position order) and its tournament's number.</summary>
public sealed record PredictionView(
    long Id,
    GuildId Guild,
    long TournamentId,
    int TournamentNumber,
    ChannelId Channel,
    MessageId? Message,
    UserId Creator,
    string CreatorName,
    string Title,
    string? Rules,
    DateTimeOffset? LockAt,
    PredictionStatus Status,
    PredictionLockReason? LockReason,
    DateTimeOffset? LockedAt,
    IReadOnlyList<OutcomeView> Outcomes,
    int EntryCount,
    long StakeTotalMinor,
    long? WinningOutcomeId,
    int? WinnerCount,
    long? PayoutTotalMinor,
    string? CancelReason,
    long? RefundTotalMinor,
    DateTimeOffset? SettledAt,
    DateTimeOffset? CancelledAt,
    bool CardMissing,
    long Version)
{
    public OutcomeView? Winner => Outcomes.FirstOrDefault(o => o.Id == WinningOutcomeId);
}

/// <summary>
/// The one public card of a prediction, edited in place through its life. The QUESTION is the largest text (a "## " heading
/// in the description, defused first so only the bot's own heading exists); the small title line says the state, and the
/// "TSQ Öngörü" label lives in the footer with the tournament and prediction numbers and the creator's display name. Below
/// the question: the numbered outcomes (1️⃣ …) each with its fixed odds; then participants and staked coins, the lock time
/// (Discord timestamps — no per-second edits) or "locked manually", the result (settled) or the reason and refund
/// (cancelled), and the creator's rules.
/// <para>
/// Buttons carry only the prediction number; every click is authorized server-side on the stored prediction. Open:
/// 🎯 Tahmin Yap, then 🔒 Kilitle · ✅ Sonuçlandır · ↩️ İptal / İade. Locked: entries shown closed (disabled), Sonuçlandır ·
/// İptal / İade. Settled / cancelled: no components (nothing more can happen). Nothing on the card keeps per-member
/// state, so pressing Tahmin Yap again always starts afresh. Sent and edited with allowed_mentions = none.
/// </para>
/// <para>
/// No silent cuts: the layout falls back from markdown lines to a code block to fields when the text is long, and the form
/// refuses a prediction whose card would not fit Discord's limits in any state (<see cref="Fits"/>).
/// </para>
/// </summary>
public sealed class PredictionCards(ILocalizer localizer)
{
    public const string EnterPrefix = "tsq:pred:enter:";
    public const string LockPrefix = "tsq:pred:lock:";
    public const string SettlePrefix = "tsq:pred:settle:";
    public const string CancelPrefix = "tsq:pred:cancel:";

    public const uint OpenColor = 0x9B59B6;
    public const uint LockedColor = 0xF59F00;
    public const uint SettledColor = 0x57F287;
    public const uint EndedColor = 0x747F8D;

    public const int CreatorNameMax = 64;
    private const int UntrustedMax = 8000; // never reached: the stored text is bounded; defusing only escapes

    private static readonly string[] Keycaps = ["1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟"];

    public OutgoingMessage Render(PredictionView view, string language, bool preview = false)
    {
        string L(string key, params object?[] args) => Format(language, key, args);

        var heading = "## " + DiscordText.Untrusted(view.Title, UntrustedMax);
        var fields = new List<EmbedField>();
        string title;
        uint color;
        switch (view.Status)
        {
            case PredictionStatus.Settled:
                title = L("predictions.card.title_settled");
                color = SettledColor;
                fields.Add(new(L("predictions.card.result"), ResultText(view, language)));
                break;

            case PredictionStatus.Cancelled or PredictionStatus.Abandoned:
                title = L("predictions.card.title_cancelled");
                color = EndedColor;
                fields.Add(new(L("predictions.card.participation"), Participation(view, language), true));
                fields.Add(new(L("predictions.card.status"), view.Status == PredictionStatus.Abandoned
                    ? L("predictions.card.abandoned")
                    : L("predictions.card.cancelled", DiscordText.Untrusted(view.CancelReason ?? "", UntrustedMax),
                        Coins.Format(view.RefundTotalMinor ?? 0, language))));
                break;

            case PredictionStatus.Locked:
                title = L("predictions.card.title_locked");
                color = LockedColor;
                fields.Add(new(L("predictions.card.participation"), Participation(view, language), true));
                fields.Add(new(L("predictions.card.lock"), view.LockReason == PredictionLockReason.CardMissing
                    ? L("predictions.card.locked_missing")
                    : L("predictions.card.locked_at", DiscordText.Timestamp(view.LockedAt ?? view.LockAt ?? DateTimeOffset.UnixEpoch, 'f')), true));
                break;

            default:
                title = preview ? L("predictions.card.title_preview") : L("predictions.card.title_open");
                color = OpenColor;
                fields.Add(new(L("predictions.card.participation"), Participation(view, language), true));
                fields.Add(new(L("predictions.card.lock"), view.LockAt is { } at
                    ? DiscordText.Timestamp(at, 'R') + "\n" + DiscordText.Timestamp(at, 'F')
                    : L("predictions.card.lock_manual"), true));
                break;
        }

        if (!string.IsNullOrEmpty(view.Rules))
        {
            var chunks = Chunks(DiscordText.Untrusted(view.Rules, UntrustedMax), DiscordLimits.EmbedFieldValueMax);
            for (var i = 0; i < chunks.Count; i++)
                fields.Add(new(i == 0 ? L("predictions.card.rules") : L("predictions.card.rules_more"), chunks[i]));
        }

        var creator = string.IsNullOrWhiteSpace(view.CreatorName) ? L("predictions.card.creator_unknown") : DiscordText.UntrustedPlain(view.CreatorName, CreatorNameMax);
        var footer = L("predictions.card.footer", Number(view.Id), view.TournamentNumber, creator);
        var buttons = Buttons(view, language, preview);

        // Markdown lines → code block → fields: the first layout Discord accepts. Nothing is cut.
        foreach (var layout in new[] { Layout.Lines, Layout.CodeBlock, Layout.Fields })
        {
            var (description, outcomeFields) = Outcomes(view, heading, layout, language);
            var embed = new MessageEmbed(title, description, null, [.. outcomeFields, .. fields], footer, null, color);
            var message = new OutgoingMessage(null, embed, MentionPolicy.None, buttons);
            if (DiscordLimits.Validate(message).Count == 0)
                return message;
        }

        // Only reachable for a card the form would have refused (see Fits); the plainest layout, as is.
        var (d, f) = Outcomes(view, heading, Layout.Fields, language);
        return new OutgoingMessage(null, new MessageEmbed(title, d, null, [.. f, .. fields], footer, null, color), MentionPolicy.None, buttons);
    }

    /// <summary>
    /// Whether the card fits Discord's limits in every state it can reach (open, locked, settled with any winner, cancelled
    /// with the longest reason). The form refuses a prediction that does not, instead of cutting text later.
    /// </summary>
    public bool Fits(PredictionView view)
    {
        var reason = new string('_', PredictionRules.CancelReasonMaxLength); // escapes to twice its length: the worst case
        var states = new List<PredictionView>
        {
            view with { Status = PredictionStatus.Open },
            view with { Status = PredictionStatus.Locked, LockReason = PredictionLockReason.Manual },
            view with { Status = PredictionStatus.Cancelled, CancelReason = reason, RefundTotalMinor = long.MaxValue / 2 },
        };
        states.AddRange(view.Outcomes.Select(o => view with
        {
            Status = PredictionStatus.Settled,
            WinningOutcomeId = o.Id,
            WinnerCount = int.MaxValue / 2,
            PayoutTotalMinor = long.MaxValue / 2,
        }));
        foreach (var language in Languages.Supported)
        {
            foreach (var state in states)
            {
                var message = Render(state, language);
                if (DiscordLimits.Validate(message).Count > 0 || message.Embed!.Fields.Count > DiscordLimits.EmbedFieldsMax)
                    return false;
            }
        }

        return true;
    }

    private enum Layout
    {
        Lines,
        CodeBlock,
        Fields,
    }

    /// <summary>"1️⃣" … "🔟", then "**11.**"; the winning outcome of a settled prediction is "🏆".</summary>
    private static string Marker(OutcomeView outcome, long? winner) =>
        outcome.Id == winner ? "🏆" : outcome.Position <= Keycaps.Length ? Keycaps[outcome.Position - 1] : "**" + Number(outcome.Position) + ".**";

    private (string Description, List<EmbedField> Fields) Outcomes(PredictionView view, string heading, Layout layout, string language)
    {
        var winner = view.Status == PredictionStatus.Settled ? view.WinningOutcomeId : null;
        var oddsLabel = Format(language, "predictions.card.odds_label", []);

        switch (layout)
        {
            case Layout.Lines:
                {
                    var blocks = view.Outcomes.Select(o =>
                        Marker(o, winner) + " " + (o.Id == winner ? "**" + DiscordText.Untrusted(o.Label, UntrustedMax) + "**" : DiscordText.Untrusted(o.Label, UntrustedMax)) +
                        "\n" + oddsLabel + " **" + Odds.Format(o.OddsX100) + "**");
                    return (heading + "\n\n" + string.Join("\n\n", blocks), []);
                }

            case Layout.CodeBlock:
                {
                    var width = view.Outcomes.Count >= 10 ? 2 : 1;
                    var lines = view.Outcomes.Select(o =>
                        (winner is null ? "" : o.Id == winner ? "✓ " : "  ") + Number(o.Position).PadLeft(width) + ". " + CodeSafe(o.Label) + "  " + Odds.Format(o.OddsX100));
                    return (heading + "\n```\n" + string.Join("\n", lines) + "\n```", []);
                }

            default:
                {
                    var lines = view.Outcomes.Select(o =>
                        Marker(o, winner) + " " + DiscordText.Untrusted(o.Label, UntrustedMax) + " — " + Odds.Format(o.OddsX100)).ToList();
                    var fields = new List<EmbedField>();
                    var current = new StringBuilder();
                    foreach (var line in lines)
                    {
                        if (current.Length > 0 && current.Length + 1 + line.Length > DiscordLimits.EmbedFieldValueMax)
                        {
                            fields.Add(new(fields.Count == 0 ? Format(language, "predictions.card.outcomes", []) : "​", current.ToString()));
                            current.Clear();
                        }

                        if (current.Length > 0)
                            current.Append('\n');
                        current.Append(line);
                    }

                    if (current.Length > 0)
                        fields.Add(new(fields.Count == 0 ? Format(language, "predictions.card.outcomes", []) : "​", current.ToString()));
                    return (heading, fields);
                }
        }
    }

    /// <summary>The card's buttons for its state; custom ids carry only the prediction number.</summary>
    private IReadOnlyList<MessageButton>? Buttons(PredictionView view, string language, bool preview)
    {
        if (preview || view.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
            return null;
        string L(string key) => Format(language, key, []);
        var id = Number(view.Id);
        var open = view.Status == PredictionStatus.Open;
        var buttons = new List<MessageButton>
        {
            open
                ? new MessageButton(L("predictions.card.enter"), EnterPrefix + id, null, Style: MessageButtonStyle.Primary)
                : new MessageButton(L("predictions.card.entries_closed"), EnterPrefix + id, null, Disabled: true),
        };
        if (open)
            buttons.Add(new MessageButton(L("predictions.card.lock_button"), LockPrefix + id, null, NewRow: true));
        buttons.Add(new MessageButton(L("predictions.card.settle_button"), SettlePrefix + id, null, Style: MessageButtonStyle.Success, NewRow: !open));
        buttons.Add(new MessageButton(L("predictions.card.cancel_button"), CancelPrefix + id, null, Style: MessageButtonStyle.Danger));
        return buttons;
    }

    private string Participation(PredictionView view, string language) =>
        Format(language, "predictions.card.participation_value", [Number(view.EntryCount), Coins.Format(view.StakeTotalMinor, language)]);

    private string ResultText(PredictionView view, string language)
    {
        var participation = Format(language, "predictions.card.result_entries", [Number(view.EntryCount), Coins.Format(view.StakeTotalMinor, language)]);
        if (view.Winner is not { } winner)
            return Format(language, "predictions.card.result_unknown", []) + "\n" + participation;
        var line = Format(language, "predictions.card.result_winner", [DiscordText.Untrusted(winner.Label, UntrustedMax), Odds.Format(winner.OddsX100)]);
        var summary = (view.WinnerCount ?? 0) == 0
            ? Format(language, "predictions.card.result_nobody", [])
            : Format(language, "predictions.card.result_paid", [Number(view.WinnerCount ?? 0), Coins.Format(view.PayoutTotalMinor ?? 0, language)]);
        return line + "\n" + summary + "\n" + participation;
    }

    /// <summary>Inside a code block only a backtick could break out; it is replaced by a look-alike.</summary>
    private static string CodeSafe(string text) => new(text.Where(c => !char.IsControl(c)).Select(c => c == '`' ? 'ˋ' : c).ToArray());

    /// <summary>Splits text into pieces of at most <paramref name="max"/> characters, at a line break or space when possible.</summary>
    public static List<string> Chunks(string text, int max)
    {
        var chunks = new List<string>();
        var rest = text;
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf('\n', max - 1);
            if (cut < max / 2)
                cut = rest.LastIndexOf(' ', max - 1);
            if (cut < max / 2)
                cut = max;
            if (char.IsHighSurrogate(rest[cut - 1]))
                cut--;
            chunks.Add(rest[..cut]);
            rest = rest[cut..].TrimStart('\n', ' ');
        }

        if (rest.Length > 0)
            chunks.Add(rest);
        return chunks;
    }

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the template here rather than through <see cref="ILocalizer"/>'s arguments: the localizer would replace a
    /// string argument that happens to be a catalog key (a title typed as "help.title") with that key's text.
    /// </summary>
    private string Format(string language, string key, object?[] args) =>
        args.Length == 0 ? localizer.Get(language, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(language, key), args);
}
