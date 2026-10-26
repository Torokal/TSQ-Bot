using System.Reflection;
using Discord;

namespace ToroSquad.Tests.Support;

/// <summary>
/// A recording <see cref="IDiscordInteraction"/> (no Discord client): every call is recorded in order with its arguments; an
/// edit's <see cref="MessageProperties"/> are captured by running the edit; <c>HasResponded</c> turns true after the first
/// response or deferral; a method listed in <see cref="Throws"/> throws instead. Getters come from the given values.
/// </summary>
public class InteractionRecorder : DispatchProxy
{
    private Dictionary<string, object?> _values = [];
    private bool _responded;

    public List<(string Method, object?[] Args, MessageProperties? Edit)> Calls { get; } = [];

    public Dictionary<string, Exception> Throws { get; } = [];

    public static IDiscordInteraction Create(out InteractionRecorder recorder, Dictionary<string, object?>? values = null)
    {
        var proxy = Create<IDiscordInteraction, InteractionRecorder>();
        recorder = (InteractionRecorder)(object)proxy;
        recorder._values = values ?? [];
        return proxy;
    }

    /// <summary>The value of a named argument of a recorded call (e.g. "ephemeral", "allowedMentions").</summary>
    public static object? Arg((string Method, object?[] Args, MessageProperties? Edit) call, string name, Type owner)
    {
        var method = owner.GetMethods().First(m => m.Name == call.Method && m.GetParameters().Length == call.Args.Length);
        var index = Array.FindIndex(method.GetParameters(), p => p.Name == name);
        return index < 0 ? null : call.Args[index];
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.Name == "get_HasResponded")
            return _responded;
        if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            return _values.TryGetValue(targetMethod.Name[4..], out var value) ? value : Default(targetMethod.ReturnType);

        MessageProperties? edit = null;
        if (args is { Length: > 0 } && args[0] is Action<MessageProperties> change)
        {
            edit = new MessageProperties();
            change(edit);
        }

        Calls.Add((targetMethod.Name, args ?? [], edit));
        if (Throws.TryGetValue(targetMethod.Name, out var error))
            throw error;
        if (targetMethod.Name is "DeferAsync" or "RespondAsync" or "RespondWithModalAsync")
            _responded = true;
        return Default(targetMethod.ReturnType);
    }

    private static object? Default(Type type)
    {
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [Default(type.GetGenericArguments()[0])]);
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
