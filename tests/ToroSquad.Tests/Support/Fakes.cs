using System.Net;
using System.Reflection;
using System.Text;

namespace ToroSquad.Tests.Support;

/// <summary>Scriptable HTTP handler for provider tests (status codes, bodies, timeouts, call counting).</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHttpHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
        : this((r, n, _) => respond(r, n))
    {
    }

    public List<Uri> Requests { get; } = [];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return await respond(request, Requests.Count, cancellationToken);
    }
}

/// <summary>
/// Minimal interface fake built on DispatchProxy: returns configured property values, defaults otherwise.
/// Used to drive Discord.Net preconditions without a live connection.
/// </summary>
public class InterfaceFake : DispatchProxy
{
    private Dictionary<string, object?> _values = [];

    public static T Create<T>(Dictionary<string, object?> values)
        where T : class
    {
        var proxy = Create<T, InterfaceFake>();
        ((InterfaceFake)(object)proxy)._values = values;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        var name = targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) ? targetMethod.Name[4..] : targetMethod.Name;
        if (_values.TryGetValue(name, out var value))
            return value;
        var type = targetMethod.ReturnType;
        if (type == typeof(void))
            return null;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
