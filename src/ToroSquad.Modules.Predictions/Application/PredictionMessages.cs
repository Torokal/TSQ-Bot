using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// Every message of TSQ Öngörü except the card (<see cref="PredictionCards"/>): the private form preview and errors, the
/// entry preview and receipt, the settle/cancel/end confirmations, the wallet, the entry list, and the public leaderboard,
/// tournament status and closing announcement. All go out with <see cref="MentionPolicy.None"/>: members are named by
/// embed mentions (which Discord shows with the member's current name and never pings) — nobody is ever called a "former
/// member" because a cache missed them. Custom ids carry only a random token, an id or a page number; what a click may do
/// is decided server-side.
/// </summary>
public sealed class PredictionMessages(ILocalizer localizer, IOptions<PredictionsOptions> options)
{
    public const string FormModalPrefix = "tsq:pred:form:";
    public const string PublishPrefix = "tsq:pred:publish:";
    public const string EditPrefix = "tsq:pred:edit:";
    public const string DiscardPrefix = "tsq:pred:discard:";
    public const string StakeModalPrefix = "tsq:pred:stake:";
    public const string EntryConfirmPrefix = "tsq:pred:entry-ok:";
    public const string SettlePickPrefix = "tsq:pred:settle-pick:";
    public const string SettleConfirmPrefix = "tsq:pred:settle-ok:";
    public const string CancelConfirmPrefix = "tsq:pred:cancel-ok:";
    public const string EndConfirmPrefix = "tsq:pred:end-ok:";
    public const string DismissPrefix = "tsq:pred:dismiss:";
    public const string MinePagePrefix = "tsq:pred:mine:";

    public const uint BrandColor = 0x9B59B6;
    public const uint SuccessColor = 0x57F287;
    public const uint DangerColor = 0xED4245;
    public const uint WarningColor = 0xF59F00;

    /// <summary>At most this many form problems are listed (the rest are counted), keeping the message under Discord's 2000 characters.</summary>
    public const int MaxListedErrors = 12;

    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    // ---- create ----

    public OutgoingMessage FormErrors(IReadOnlyList<FormError> errors, string draftId, string language)
    {
        var lines = new List<string> { L(language, "predictions.form.errors_title") };
        lines.AddRange(errors.Take(MaxListedErrors).Select(e => "• " + L(language, e.Key, [.. e.Args])));
        if (errors.Count > MaxListedErrors)
            lines.Add(L(language, "predictions.form.errors_more", errors.Count - MaxListedErrors));
        lines.Add("");
        lines.Add(L(language, "predictions.form.errors_hint"));
        return new OutgoingMessage(Fit(string.Join("\n", lines)), null, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.form.edit"), EditPrefix + draftId, null, Style: MessageButtonStyle.Primary),
            new MessageButton(L(language, "predictions.form.discard"), DiscardPrefix + draftId, null),
        ]);
    }

    public OutgoingMessage FormPreview(OutgoingMessage card, PredictionInput input, string draftId, string language)
    {
        var lines = new List<string> { L(language, "predictions.form.preview_title"), L(language, "predictions.form.preview_fixed") };
        if (input.UsesDefaultOdds)
            lines.Add(L(language, "predictions.form.preview_default_odds", Odds.Format(options.Value.DefaultOddsX100)));
        return new OutgoingMessage(string.Join("\n", lines), card.Embed, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.form.publish"), PublishPrefix + draftId, null, Style: MessageButtonStyle.Success),
            new MessageButton(L(language, "predictions.form.edit"), EditPrefix + draftId, null),
            new MessageButton(L(language, "predictions.form.discard"), DiscardPrefix + draftId, null),
        ]);
    }

    // ---- enter ----

    public OutgoingMessage EntryPreview(string title, string outcome, int oddsX100, long stake, long payout, long balanceAfter, string token, string language)
    {
        var embed = new MessageEmbed(L(language, "predictions.entry.preview_title"), Question(title), null,
        [
            new(L(language, "predictions.entry.choice"), DiscordText.Untrusted(outcome, 400), true),
            new(L(language, "predictions.entry.odds"), Odds.Format(oddsX100), true),
            new(L(language, "predictions.entry.stake"), Coin(stake, language), true),
            new(L(language, "predictions.entry.return"), Coin(payout, language), true),
            new(L(language, "predictions.entry.gain"), Coin(Coins.Subtract(payout, stake), language), true),
            new(L(language, "predictions.entry.balance_after"), Coin(balanceAfter, language), true),
            new(L(language, "predictions.entry.final_title"), L(language, "predictions.entry.final")),
        ], null, null, BrandColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.entry.confirm"), EntryConfirmPrefix + token, null, Style: MessageButtonStyle.Success),
            new MessageButton(L(language, "predictions.dismiss"), DismissPrefix + token, null),
        ]);
    }

    public OutgoingMessage EntryReceipt(string title, string outcome, int oddsX100, long stake, long payout, long balance, string language)
    {
        var embed = new MessageEmbed(L(language, "predictions.entry.receipt_title"), Question(title), null,
        [
            new(L(language, "predictions.entry.choice"), DiscordText.Untrusted(outcome, 400), true),
            new(L(language, "predictions.entry.odds"), Odds.Format(oddsX100), true),
            new(L(language, "predictions.entry.stake"), Coin(stake, language), true),
            new(L(language, "predictions.entry.return"), Coin(payout, language), true),
            new(L(language, "predictions.entry.balance_now"), Coin(balance, language), true),
        ], L(language, "predictions.entry.receipt_footer"), null, SuccessColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

    // ---- settle / cancel ----

    public OutgoingMessage SettlePicker(PredictionView view, string token, long? selected, string language) =>
        new(L(language, "predictions.settle.pick_title", Question(view.Title)), null, MentionPolicy.None,
            [new MessageButton(L(language, "predictions.dismiss"), DismissPrefix + token, null)], OutcomeSelect(view, token, selected, language));

    public OutgoingMessage SettlePreview(PredictionView view, long outcomeId, int winners, int losers, long payout, string token, string language)
    {
        var outcome = view.Outcomes.First(o => o.Id == outcomeId);
        var embed = new MessageEmbed(L(language, "predictions.settle.preview_title"), Question(view.Title), null,
        [
            new(L(language, "predictions.settle.winner"), DiscordText.Untrusted(outcome.Label, 400) + " — **" + Odds.Format(outcome.OddsX100) + "**"),
            new(L(language, "predictions.settle.winners"), Number(winners), true),
            new(L(language, "predictions.settle.losers"), Number(losers), true),
            new(L(language, "predictions.settle.payout"), Coin(payout, language), true),
            new(L(language, "predictions.settle.note_title"), L(language, winners == 0 ? "predictions.settle.note_nobody" : "predictions.settle.note")),
        ], L(language, "predictions.card.footer_short", Number(view.Id)), null, WarningColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.settle.confirm"), SettleConfirmPrefix + token, null, Style: MessageButtonStyle.Success),
            new MessageButton(L(language, "predictions.dismiss"), DismissPrefix + token, null),
        ], OutcomeSelect(view, token, outcomeId, language));
    }

    public OutgoingMessage CancelPreview(PredictionView view, string reason, int entries, long total, string token, string language)
    {
        var embed = new MessageEmbed(L(language, "predictions.cancel.preview_title"), Question(view.Title), null,
        [
            new(L(language, "predictions.cancel.reason"), DiscordText.Untrusted(reason, 800)),
            new(L(language, "predictions.cancel.entries"), Number(entries), true),
            new(L(language, "predictions.cancel.refund"), Coin(total, language), true),
            new(L(language, "predictions.settle.note_title"), L(language, "predictions.cancel.note")),
        ], L(language, "predictions.card.footer_short", Number(view.Id)), null, DangerColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.cancel.confirm"), CancelConfirmPrefix + token, null, Style: MessageButtonStyle.Danger),
            new MessageButton(L(language, "predictions.dismiss"), DismissPrefix + token, null),
        ]);
    }

    private MessageSelectMenu OutcomeSelect(PredictionView view, string token, long? selected, string language) =>
        new(SettlePickPrefix + token, L(language, "predictions.settle.pick_placeholder"), view.Outcomes.Select(o => new MessageSelectOption(
            Cut(Number(o.Position) + ". " + DiscordText.UntrustedPlain(o.Label, 400), MessageSelectMenu.MaxLabelLength),
            o.Id.ToString(CultureInfo.InvariantCulture),
            (o.Id == selected ? "✓ " : "") + L(language, "predictions.card.option_odds", Odds.Format(o.OddsX100)))).ToList());

    // ---- member views ----

    public OutgoingMessage Wallet(WalletView view, string language)
    {
        var total = Coins.Add(view.AvailableMinor, view.PendingMinor);
        var fields = new List<EmbedField>
        {
            new(L(language, "predictions.wallet.available"), Coin(view.AvailableMinor, language), true),
            new(L(language, "predictions.wallet.pending"), Coin(view.PendingMinor, language), true),
            new(L(language, "predictions.wallet.total"), Coin(total, language), true),
            new(L(language, "predictions.wallet.record"), view.SettledCount == 0
                ? L(language, "predictions.wallet.record_none", Number(view.PendingEntries))
                : L(language, "predictions.wallet.record_value", Number(view.CorrectCount), Number(view.SettledCount), Percent(view.CorrectCount, view.SettledCount), Number(view.PendingEntries))),
            new(L(language, "predictions.wallet.daily"), view.DailyClaimedMinorCoins is { } coins
                ? L(language, "predictions.wallet.daily_claimed", Number(coins), DiscordText.Timestamp(view.NextDaily, 'R'))
                : L(language, "predictions.wallet.daily_ready")),
        };
        var description = view.Exists ? L(language, "predictions.wallet.total_note") : L(language, "predictions.wallet.new_note");
        return new OutgoingMessage(null, new MessageEmbed(L(language, "predictions.wallet.title", view.TournamentNumber), description, null, fields, null, null, BrandColor),
            MentionPolicy.None);
    }

    public OutgoingMessage MyEntries(int tournament, IReadOnlyList<MyEntryRow> rows, int page, int pages, string language)
    {
        var title = L(language, "predictions.mine.title", tournament);
        if (rows.Count == 0)
            return new OutgoingMessage(null, new MessageEmbed(title, L(language, "predictions.mine.empty"), null, [], null, null, BrandColor), MentionPolicy.None);
        var lines = rows.Select(r =>
            "**#" + Number(r.PredictionId) + "** " + DiscordText.Untrusted(Short(r.Title, 60), 200) + "\n" +
            "↳ " + DiscordText.Untrusted(r.OutcomeLabel, 200) + " · " + Odds.Format(r.OddsX100) + " · " + Coin(r.StakeMinor, language) + " → " + EntryState(r, language));
        var footer = pages > 1 ? L(language, "predictions.mine.page", page + 1, pages) : null;
        IReadOnlyList<MessageButton>? buttons = pages > 1
            ?
            [
                new MessageButton("◀", MinePagePrefix + Number(page - 1), null, Disabled: page == 0),
                new MessageButton("▶", MinePagePrefix + Number(page + 1), null, Disabled: page >= pages - 1),
            ]
            : null;
        return new OutgoingMessage(null, new MessageEmbed(title, Fit(string.Join("\n\n", lines), DiscordLimits.EmbedDescriptionMax), null, [], footer, null, BrandColor),
            MentionPolicy.None, buttons);
    }

    private string EntryState(MyEntryRow row, string language) => row.Status switch
    {
        PredictionEntryStatus.Won => L(language, "predictions.mine.won", Coin(row.PayoutMinor ?? 0, language)),
        PredictionEntryStatus.Lost => L(language, "predictions.mine.lost"),
        PredictionEntryStatus.Refunded => L(language, "predictions.mine.refunded"),
        _ => L(language, row.PredictionStatus == PredictionStatus.Locked ? "predictions.mine.pending_locked" : "predictions.mine.pending", Coin(row.PotentialPayoutMinor, language)),
    };

    public OutgoingMessage Leaderboard(int tournament, IReadOnlyList<StandingRow> coins, IReadOnlyList<StandingRow> correct, string language)
    {
        var coinText = coins.Count == 0
            ? L(language, "predictions.leaderboard.coins_empty")
            : string.Join("\n", coins.Select(r => Place(r.Rank) + " " + Mention(r.User) + " — **" + Coin(r.TotalMinor, language) + "**"));
        var correctText = correct.Count == 0
            ? L(language, "predictions.leaderboard.correct_empty")
            : string.Join("\n", correct.Select(r => Place(r.Rank) + " " + Mention(r.User) + " — " +
                                                     L(language, "predictions.leaderboard.correct_value", Number(r.CorrectCount), Number(r.SettledCount), Percent(r.CorrectCount, r.SettledCount))));
        var embed = new MessageEmbed(L(language, "predictions.leaderboard.title", tournament), L(language, "predictions.leaderboard.note"), null,
        [
            new(L(language, "predictions.leaderboard.coins"), coinText),
            new(L(language, "predictions.leaderboard.correct"), correctText),
        ], L(language, "predictions.leaderboard.footer"), null, BrandColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

    public OutgoingMessage TournamentStatus(int number, DateTimeOffset startedAt, int participants, int predictions, int unresolved, string language) =>
        new(null, new MessageEmbed(L(language, "predictions.tournament.title", number), null, null,
        [
            new(L(language, "predictions.tournament.started"), DiscordText.Timestamp(startedAt, 'f') + "\n" + DiscordText.Timestamp(startedAt, 'R'), true),
            new(L(language, "predictions.tournament.participants"), Number(participants), true),
            new(L(language, "predictions.tournament.predictions"), Number(predictions), true),
            new(L(language, "predictions.tournament.unresolved_count"), Number(unresolved), true),
        ], L(language, "predictions.tournament.footer", Coins.Format(options.Value.InitialBalanceMinor, language)), null, BrandColor), MentionPolicy.None);

    public OutgoingMessage TournamentNotStarted(string language) =>
        new(null, new MessageEmbed(L(language, "predictions.tournament.title", 1), L(language, "predictions.tournament.not_started"), null, [], null, null, BrandColor),
            MentionPolicy.None);

    public OutgoingMessage Unresolved(IReadOnlyList<PredictionEconomy.UnresolvedPrediction> open, string language)
    {
        var lines = new List<string> { L(language, "predictions.tournament.unresolved") };
        foreach (var p in open.Take(15))
        {
            var link = p.Message is { } m ? " — " + string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{p.Guild.Value}/{p.Channel.Value}/{m.Value}") : "";
            lines.Add("• **#" + Number(p.Id) + "** " + DiscordText.Untrusted(Short(p.Title, 60), 200) + " · " + StatusName(p.Status, language) + link);
        }

        if (open.Count > 15)
            lines.Add(L(language, "predictions.form.errors_more", open.Count - 15));
        return new OutgoingMessage(Fit(string.Join("\n", lines)), null, MentionPolicy.None);
    }

    public OutgoingMessage TournamentEndPreview(int number, IReadOnlyList<StandingRow> podium, int participants, string token, string language)
    {
        var embed = new MessageEmbed(L(language, "predictions.tournament.end_title", number), L(language, "predictions.tournament.end_warning",
                Coins.Format(options.Value.InitialBalanceMinor, language)), null,
        [
            new(L(language, "predictions.tournament.podium"), PodiumText(podium, language)),
            new(L(language, "predictions.tournament.reset_count"), Number(participants), true),
        ], null, null, DangerColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None,
        [
            new MessageButton(L(language, "predictions.tournament.end_confirm"), EndConfirmPrefix + token, null, Style: MessageButtonStyle.Danger),
            new MessageButton(L(language, "predictions.dismiss"), DismissPrefix + token, null),
        ]);
    }

    /// <summary>The public closing announcement, rendered once from the frozen podium (stored in the outbox; retries resend exactly this).</summary>
    public OutgoingMessage TournamentAnnouncement(TournamentClosing closing, string language)
    {
        var embed = new MessageEmbed(L(language, "predictions.announce.title", closing.Number), L(language, "predictions.announce.intro", Number(closing.Participants),
                Number(closing.Predictions)), null,
        [
            new(L(language, "predictions.tournament.podium"), PodiumText(closing.Podium, language)),
            new(L(language, "predictions.announce.next_title", closing.NextNumber), L(language, "predictions.announce.next", Coins.Format(options.Value.InitialBalanceMinor, language))),
        ], L(language, "predictions.announce.footer"), null, SuccessColor);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

    private string PodiumText(IReadOnlyList<StandingRow> podium, string language) => podium.Count == 0
        ? L(language, "predictions.leaderboard.coins_empty")
        : string.Join("\n", podium.Select(r => Place(r.Rank) + " " + Mention(r.User) + " — **" + Coin(r.TotalMinor, language) + "** · " +
                                               L(language, "predictions.tournament.podium_correct", Number(r.CorrectCount))));

    public string StatusName(PredictionStatus status, string language) => L(language, "predictions.status." + status.ToString().ToLowerInvariant());

    // ---- helpers ----

    private string Coin(long minor, string language) => L(language, "predictions.coin", Coins.Format(minor, language));

    private static string Question(string title) => "### " + DiscordText.Untrusted(title, 1000);

    private static string Place(int rank) => rank <= Medals.Length ? Medals[rank - 1] : "`" + Number(rank) + ".`";

    private static string Mention(UserId user) => string.Create(CultureInfo.InvariantCulture, $"<@{user.Value}>");

    /// <summary>Whole percent, rounded down ("%66"); only called with at least one settled prediction.</summary>
    private static string Percent(int correct, int settled) => Number(settled == 0 ? 0 : correct * 100L / settled);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Fit(string text, int max = DiscordLimits.ContentMax) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private string L(string language, string key, params object?[] args) =>
        args.Length == 0 ? localizer.Get(language, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(language, key), args);
}
