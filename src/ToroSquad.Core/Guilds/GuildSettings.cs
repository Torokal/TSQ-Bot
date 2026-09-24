using ToroSquad.Core.Localization;
using ToroSquad.Core.Security;

namespace ToroSquad.Core.Guilds;

/// <summary>
/// Guild-wide core preferences. The time zone is the *server's* display preference (default Europe/Istanbul),
/// not an assumption about any individual member's location. All instants are stored in UTC.
/// </summary>
public sealed record GuildSettings(GuildId GuildId, string Language, string TimeZoneId, bool SetupCompleted)
{
    public const string DefaultTimeZoneId = "Europe/Istanbul";

    public static GuildSettings Default(GuildId guild) => new(guild, Languages.Default, DefaultTimeZoneId, false);
}

public interface IGuildSettingsStore
{
    Task<GuildSettings> GetAsync(GuildId guild, CancellationToken cancellationToken);
    Task SaveAsync(GuildSettings settings, UserId changedBy, CancellationToken cancellationToken);
}

public static class GuildTime
{
    /// <summary>
    /// Resolves an IANA id (e.g. "Europe/Istanbul") on every OS. On Windows .NET uses ICU to map IANA ids to
    /// Windows zones; we also accept Windows ids. Returns false for unknown ids — never falls back to a fixed offset.
    /// </summary>
    public static bool TryResolve(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 64)
            return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static DateTimeOffset ToGuildLocal(DateTimeOffset utcInstant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utcInstant, zone);
}

public sealed class GuildSettingsService(IGuildSettingsStore store)
{
    public Task<GuildSettings> GetAsync(GuildId guild, CancellationToken cancellationToken) => store.GetAsync(guild, cancellationToken);

    public async Task<OperationResult> UpdateAsync(ActorContext actor, string? language, string? timeZoneId, CancellationToken cancellationToken)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var current = await store.GetAsync(actor.GuildId, cancellationToken);
        if (language is not null && !Languages.IsSupported(language))
            return OperationResult.Fail(OperationError.InvalidInput, "setup.invalid_language", language);
        if (timeZoneId is not null && !GuildTime.TryResolve(timeZoneId, out _))
            return OperationResult.Fail(OperationError.InvalidInput, "setup.invalid_timezone", timeZoneId);

        var updated = current with
        {
            Language = language ?? current.Language,
            TimeZoneId = timeZoneId ?? current.TimeZoneId,
        };
        await store.SaveAsync(updated, actor.UserId, cancellationToken);
        return OperationResult.Ok("setup.saved");
    }

    public async Task MarkSetupCompletedAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(actor.GuildId, cancellationToken);
        await store.SaveAsync(current with { SetupCompleted = true }, actor.UserId, cancellationToken);
    }
}
