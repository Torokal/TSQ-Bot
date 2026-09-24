using System.Globalization;

namespace ToroSquad.Core;

// Discord snowflakes are unsigned 64-bit integers. We keep them as ulong everywhere in the domain
// (never as double/JSON number) so no precision is lost. Persistence maps them bit-for-bit (see Infrastructure).

/// <summary>Discord guild (server) identifier.</summary>
public readonly record struct GuildId(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Discord user identifier.</summary>
public readonly record struct UserId(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Discord channel identifier.</summary>
public readonly record struct ChannelId(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Discord role identifier.</summary>
public readonly record struct RoleId(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Discord message identifier.</summary>
public readonly record struct MessageId(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
