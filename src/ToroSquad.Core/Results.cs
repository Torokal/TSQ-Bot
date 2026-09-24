using System.Security.Cryptography;
using ToroSquad.Core.Security;

namespace ToroSquad.Core;

/// <summary>
/// Outcome of an application operation. <see cref="MessageKey"/> is a localization key; the Discord layer renders it.
/// Failures never carry stack traces or secrets to users; <see cref="TraceCode"/> correlates with logs.
/// </summary>
public sealed record OperationResult(
    bool Succeeded,
    string MessageKey,
    IReadOnlyList<object> Args,
    OperationError Error = OperationError.None,
    string? TraceCode = null)
{
    public static OperationResult Ok(string messageKey, params object[] args) => new(true, messageKey, args);

    public static OperationResult Fail(OperationError error, string messageKey, params object[] args) =>
        new(false, messageKey, args, error, TraceCodes.New());

    public static OperationResult Forbidden(AuthorizationResult auth) => auth.Failure switch
    {
        AuthorizationFailure.WrongGuild => Fail(OperationError.Forbidden, "error.wrong_guild"),
        AuthorizationFailure.RoleHierarchy => Fail(OperationError.Forbidden, "error.role_hierarchy"),
        AuthorizationFailure.NotOwnerOfResource => Fail(OperationError.Forbidden, "error.not_owner"),
        _ => Fail(OperationError.Forbidden, "error.missing_permission", auth.Missing.ToString()),
    };
}

public enum OperationError
{
    None = 0,
    Forbidden = 1,
    NotFound = 2,
    InvalidInput = 3,
    ModuleDisabled = 4,
    Conflict = 5,
    ProviderUnavailable = 6,
    NotConfigured = 7,
    Expired = 8,
    Unsafe = 9,
    Internal = 10,
}

/// <summary>Short, non-guessable correlation codes shown to users instead of stack traces.</summary>
public static class TraceCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return "TS-" + new string(chars);
    }
}
