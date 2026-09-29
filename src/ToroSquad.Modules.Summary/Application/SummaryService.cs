using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>A channel as the bot's cache sees it.</summary>
/// <param name="IsSupported">Holds member messages the bot can read (text, announcement, voice/stage chat, thread).</param>
/// <param name="PermissionChannel">Where access is decided: a thread's parent, otherwise the channel itself.</param>
public sealed record SummaryChannel(ChannelId Id, bool IsSupported, ChannelId PermissionChannel);

public enum SummaryFetchStatus
{
    Ok = 0,
    NoAccess = 1,
    Failed = 2,
}

/// <summary>The latest messages of one channel (any order; bots and system events included — the transcript filters).</summary>
public sealed record SummaryFetch(SummaryFetchStatus Status, IReadOnlyList<SummarySourceMessage> Messages, SummaryMentionNames Names)
{
    public static SummaryFetch NoAccess { get; } = new(SummaryFetchStatus.NoAccess, [], SummaryMentionNames.Empty);
    public static SummaryFetch Failed { get; } = new(SummaryFetchStatus.Failed, [], SummaryMentionNames.Empty);
}

/// <summary>The Discord reads /ozetle needs beyond <see cref="IGuildGateway"/>. Implemented over Discord.Net in Commands; faked in tests.</summary>
public interface ISummaryDiscord
{
    SummaryChannel? GetChannel(GuildId guild, ChannelId channel);

    /// <summary>The invoking member's effective permissions in <paramref name="channel"/>; null when unknown.</summary>
    GuildPermission? InvokerPermissions(ChannelId channel);

    /// <summary>
    /// Reads the latest messages of <paramref name="channel"/> only (a thread without its parent), newest first, until
    /// <paramref name="memberMessages"/> member messages are collected or a small page limit is reached. Never throws for
    /// Discord problems.
    /// </summary>
    Task<SummaryFetch> FetchRecentAsync(GuildId guild, ChannelId channel, int memberMessages, CancellationToken cancellationToken);

    /// <summary>The application's Message Content flag (Developer Portal); null when it could not be read.</summary>
    Task<bool?> HasMessageContentAccessAsync();
}

/// <summary>How /ozetle answers (implemented by the Discord command class; faked in tests). Nothing sent here pings.</summary>
public interface ISummaryResponder
{
    /// <summary>Only the member who ran the command sees it (before or after <see cref="DeferPrivateAsync"/>).</summary>
    Task ReplyPrivateAsync(string text);

    /// <summary>Acknowledges within Discord's 3 seconds; only the member sees the "thinking" state.</summary>
    Task DeferPrivateAsync();

    /// <summary>Posts the summary as normal public message(s) in the channel; false when Discord refused.</summary>
    Task<bool> PostPublicAsync(IReadOnlyList<string> parts);
}

/// <summary>Who asked where, in which language, with which time zone (for &lt;t:…&gt; in messages).</summary>
public sealed record SummaryRequest(GuildId Guild, ChannelId Channel, UserId Member, string Language, TimeZoneInfo Zone);

public enum SummaryOutcome
{
    Posted = 0,
    NotConfigured = 1,
    UnsupportedChannel = 2,
    MemberNoAccess = 3,
    BotNoAccess = 4,
    Throttled = 5,
    FetchFailed = 6,
    NotEnoughMessages = 7,
    ContentUnavailable = 8,
    AiFailed = 9,
    PostFailed = 10,
}

/// <summary>The last run, for /bot status (no ids, no text).</summary>
public sealed record SummaryLastRun(SummaryOutcome Outcome, SummaryAiFailure AiFailure, DateTimeOffset At);

/// <summary>
/// /ozetle from start to end, with every check on the server and at most ONE AI request: configuration, channel type, the
/// member's and the bot's access (View Channel + Read Message History), admission (channel busy, cooldowns, bot-wide limit)
/// — all answered privately and at once, before anything is read. Then a private acknowledgement, one bounded read of the
/// channel, the transcript (too few member messages → no AI request), the AI request (a failure is never retried and no other
/// model is tried), light deterministic clean-up, and the summary as public message(s) without pings. Logs carry ids, counts,
/// token usage, latency and outcome — never message text, names, the prompt or the answer.
/// </summary>
public sealed partial class SummaryService(
    ISummaryAiClient ai,
    SummaryThrottle throttle,
    IGuildGateway guilds,
    ILocalizer localizer,
    IOptions<SummaryOptions> options,
    TimeProvider clock,
    ILogger<SummaryService> logger)
{
    /// <summary>What the member and the bot both need in the channel (a thread: in its parent).</summary>
    public const GuildPermission RequiredToRead = GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory;

    /// <summary>The time allowed for reading the channel from Discord.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private SummaryLastRun? _last;

    public SummaryLastRun? LastRun => Volatile.Read(ref _last);

    public bool IsConfigured => ai.IsConfigured;

    public async Task<SummaryOutcome> RunAsync(SummaryRequest request, ISummaryDiscord discord, ISummaryResponder responder)
    {
        var trace = TraceCodes.New();
        string T(string key, params object?[] args) => localizer.Get(request.Language, key, args);

        if (!ai.IsConfigured)
        {
            LogNotConfigured(logger, trace, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(T("summary.not_configured"));
            return Remember(SummaryOutcome.NotConfigured);
        }

        if (discord.GetChannel(request.Guild, request.Channel) is not { IsSupported: true } channel)
            return await RefuseAsync(SummaryOutcome.UnsupportedChannel, "summary.unsupported_channel");

        if (discord.InvokerPermissions(channel.PermissionChannel) is not { } member || !member.Grants(RequiredToRead))
            return await RefuseAsync(SummaryOutcome.MemberNoAccess, "summary.member_no_access");

        var bot = await guilds.GetBotChannelAccessAsync(request.Guild, channel.PermissionChannel, CancellationToken.None);
        if (!bot.Exists || !bot.Permissions.Grants(RequiredToRead))
            return await RefuseAsync(SummaryOutcome.BotNoAccess, "summary.bot_no_access");

        var (admission, ticket, retryAt) = throttle.TryEnter(request.Member.Value, request.Channel.Value);
        if (ticket is null)
        {
            LogThrottled(logger, trace, admission, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(admission switch
            {
                SummaryAdmission.ChannelBusy => T("summary.channel_busy"),
                SummaryAdmission.UserCooldown => T("summary.user_cooldown", Seconds(retryAt)),
                SummaryAdmission.ChannelCooldown => T("summary.channel_cooldown", Seconds(retryAt)),
                _ => T("summary.at_capacity"),
            });
            return SummaryOutcome.Throttled; // not remembered: says nothing about the service
        }

        using (ticket)
        {
            await responder.DeferPrivateAsync();
            return await SummarizeAsync(request, discord, responder, ticket, trace);
        }

        async Task<SummaryOutcome> RefuseAsync(SummaryOutcome outcome, string key)
        {
            LogRefused(logger, trace, outcome, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(T(key));
            return outcome;
        }
    }

    private async Task<SummaryOutcome> SummarizeAsync(
        SummaryRequest request, ISummaryDiscord discord, ISummaryResponder responder, SummaryThrottle.Ticket ticket, string trace)
    {
        string T(string key, params object?[] args) => localizer.Get(request.Language, key, args);
        var settings = options.Value;
        var ids = (Guild: request.Guild.Value, Channel: request.Channel.Value, Member: request.Member.Value);

        SummaryFetch fetch;
        using (var deadline = new CancellationTokenSource(FetchTimeout, clock))
            fetch = await discord.FetchRecentAsync(request.Guild, request.Channel, settings.MaxMessages, deadline.Token);
        if (fetch.Status != SummaryFetchStatus.Ok)
        {
            LogFetchFailed(logger, trace, fetch.Status, ids.Guild, ids.Channel, ids.Member);
            await responder.ReplyPrivateAsync(T(fetch.Status == SummaryFetchStatus.NoAccess ? "summary.bot_no_access" : "summary.fetch_failed") + TraceLine(request, trace));
            return Remember(SummaryOutcome.FetchFailed);
        }

        var transcript = SummaryTranscript.Build(fetch.Messages, fetch.Names, request.Zone, settings.MaxMessages);
        if (transcript.MessageCount < settings.MinMessages)
        {
            // Normal member messages that came back completely empty: Discord withholds content without Message Content access.
            var withheld = transcript.EmptyMessageCount > 0 && await discord.HasMessageContentAccessAsync() == false;
            LogTooFew(logger, trace, transcript.MessageCount, transcript.EmptyMessageCount, withheld, ids.Guild, ids.Channel, ids.Member);
            await responder.ReplyPrivateAsync(withheld ? T("summary.content_unavailable") : T("summary.not_enough", settings.MinMessages));
            return Remember(withheld ? SummaryOutcome.ContentUnavailable : SummaryOutcome.NotEnoughMessages);
        }

        var result = await ai.SummarizeAsync(SummaryPrompt.Build(transcript.Text), CancellationToken.None);
        var summary = result.Succeeded ? SummaryOutput.Normalize(result.Text, cutOff: result.FinishReason == "length") : null;
        var failure = result.Succeeded && summary is null ? SummaryAiFailure.EmptyOutput : result.Failure;
        LogInference(logger, trace, failure == SummaryAiFailure.None ? "ok" : "failed", failure, result.HttpStatus, result.ProviderError, ai.Model,
            transcript.MessageCount, transcript.TruncatedMessageCount, transcript.DroppedForSizeCount, result.Usage.InputTokens,
            result.Usage.OutputTokens, result.Usage.ReasoningTokens, result.FinishReason, (long)result.Latency.TotalMilliseconds,
            ids.Guild, ids.Channel, ids.Member);

        if (summary is null)
        {
            ticket.End(SummaryRunEnd.InferenceFailed);
            await responder.ReplyPrivateAsync(T(failure switch
            {
                SummaryAiFailure.Timeout => "summary.timeout",
                SummaryAiFailure.RateLimited => "summary.provider_busy",
                SummaryAiFailure.EmptyOutput or SummaryAiFailure.InvalidResponse => "summary.empty_output",
                _ => "summary.provider_unavailable",
            }) + TraceLine(request, trace));
            return Remember(SummaryOutcome.AiFailed, failure);
        }

        ticket.End(SummaryRunEnd.Completed); // the inference was spent: the cooldowns apply even if Discord refuses the post
        var parts = SummaryOutput.Split(summary);
        if (!await responder.PostPublicAsync(parts))
        {
            LogPostFailed(logger, trace, ids.Guild, ids.Channel);
            return Remember(SummaryOutcome.PostFailed);
        }

        LogPosted(logger, trace, parts.Count, ids.Guild, ids.Channel);
        return Remember(SummaryOutcome.Posted);
    }

    private SummaryOutcome Remember(SummaryOutcome outcome, SummaryAiFailure failure = SummaryAiFailure.None)
    {
        Volatile.Write(ref _last, new SummaryLastRun(outcome, failure, clock.GetUtcNow()));
        return outcome;
    }

    private string TraceLine(SummaryRequest request, string trace) => "\n" + localizer.Get(request.Language, "error.trace_code", trace);

    private string Seconds(DateTimeOffset? until)
    {
        var seconds = until is { } at ? Math.Max(1, (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds)) : 1;
        return seconds.ToString(CultureInfo.InvariantCulture);
    }

    // Logs carry ids, counts, usage, latency and outcomes only — never message text, names, the prompt or the answer.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] not configured: " + SummaryOptions.ApiKeyVariable + " is not set guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogNotConfigured(ILogger logger, string trace, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] refused: {Outcome} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogRefused(ILogger logger, string trace, SummaryOutcome outcome, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] throttled: {Admission} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogThrottled(ILogger logger, string trace, SummaryAdmission admission, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] channel read failed: {Status} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogFetchFailed(ILogger logger, string trace, SummaryFetchStatus status, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] no AI request: message_count={Count} empty_message_count={Empty} " +
        "content_withheld={Withheld} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogTooFew(ILogger logger, string trace, int count, int empty, bool withheld, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] inference {Result} failure={Failure} status={Status} provider_error={ProviderError} " +
        "model={Model} message_count={Count} truncated_message_count={Truncated} dropped_message_count={Dropped} input_tokens={Input} " +
        "output_tokens={Output} reasoning_tokens={Reasoning} finish_reason={Finish} latency_ms={Latency} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogInference(ILogger logger, string trace, string result, SummaryAiFailure failure, int? status, string? providerError,
        string model, int count, int truncated, int dropped, int? input, int? output, int? reasoning, string? finish, long latency,
        ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] Discord refused the public post guild={Guild} channel={Channel}")]
    private static partial void LogPostFailed(ILogger logger, string trace, ulong guild, ulong channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] posted parts={PartCount} guild={Guild} channel={Channel}")]
    private static partial void LogPosted(ILogger logger, string trace, int partCount, ulong guild, ulong channel);
}
