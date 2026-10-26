using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>
/// The <c>x-opencode-session</c> value of a Sor conversation: a GUID derived (SHA-256) from the conversation's row id and its
/// random stamp. Stable for every turn of that conversation, also after a restart (both are stored); different for a new
/// conversation, another member, another guild or the other visibility (each is another conversation row with its own
/// random stamp); carries no guild, channel, member, name or text, and cannot be guessed without the stamp. Routing and
/// prompt-caching information for the provider only: the history is still sent by the application.
/// </summary>
public static class SorSession
{
    public static string For(long conversationId, string conversationStamp)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("tsq-sor-session:" + conversationId.ToString(CultureInfo.InvariantCulture) + ":" + conversationStamp));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
