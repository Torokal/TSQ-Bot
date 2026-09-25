using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Modules.Formula1.Providers.OpenF1;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// Review findings: (1) a classification is complete only when it covers the session's participant roster (never a fixed
/// grid size or a minimum count); (3) the real OpenF1 MQTT client bounds connect and disconnect.
/// </summary>
public sealed class F1CompletenessAndLiveBoundsTests
{
    private static F1Session Session(F1SessionType type) => new(2030, 8, type, new DateTimeOffset(2030, 6, 9, 7, 0, 0, TimeSpan.Zero));

    private static string Raw(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "f1", name));

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>OpenF1-shaped result rows for <paramref name="numbers"/>; <paramref name="status"/> marks dnf/dns/dsq rows (no position).</summary>
    private static JsonElement ResultRows(IEnumerable<int> numbers, Dictionary<int, string>? status = null)
    {
        var rows = new List<string>();
        var position = 1;
        foreach (var n in numbers)
        {
            var flag = status?.GetValueOrDefault(n);
            var pos = flag is null ? Num(position++) : "null";
            rows.Add("{\"position\":" + pos + ",\"driver_number\":" + Num(n) + ",\"number_of_laps\":50," +
                     "\"dnf\":" + (flag == "dnf" ? "true" : "false") + ",\"dns\":" + (flag == "dns" ? "true" : "false") + ",\"dsq\":" + (flag == "dsq" ? "true" : "false") +
                     ",\"duration\":" + (flag is null ? "5400.5" : "null") + ",\"gap_to_leader\":0,\"meeting_key\":1,\"session_key\":7}");
        }

        return JsonDocument.Parse("[" + string.Join(",", rows) + "]").RootElement;
    }

    private static JsonElement Roster(IEnumerable<int> numbers) => JsonDocument.Parse("[" + string.Join(",", numbers.Select(n =>
        "{\"driver_number\":" + Num(n) + ",\"first_name\":\"Test\",\"last_name\":\"Driver " + Num(n) + "\",\"name_acronym\":\"T" + Num(n) +
        "\",\"team_name\":\"Team\",\"session_key\":7}")) + "]").RootElement;

    private static string? Problem(JsonElement results, JsonElement? roster, F1SessionType type = F1SessionType.Race) =>
        F1ResultValidator.Problem(OpenF1Parser.ParseSessionResult(results, roster, Session(type)), minEntries: 10);

    [Theory]
    [InlineData(20)]
    [InlineData(22)]
    public void Ten_contiguous_rows_are_not_complete_while_the_session_roster_is_larger(int grid)
    {
        var roster = Enumerable.Range(1, grid).ToList();
        Problem(ResultRows(roster.Take(10)), Roster(roster)).Should().Contain($"{grid - 10} of {grid} session drivers have no result row",
            "positions 1..10 are contiguous and exceed MinResultEntries, yet the grid is not complete");
        Problem(ResultRows(roster.Take(10)), Roster(roster), F1SessionType.Practice1).Should().NotBeNull();
    }

    [Fact]
    public void All_session_drivers_present_is_publishable_and_dnf_dns_dsq_rows_count_as_present()
    {
        var roster = Enumerable.Range(1, 20).ToList();
        Problem(ResultRows(roster), Roster(roster)).Should().BeNull();
        Problem(ResultRows(roster, new() { [18] = "dnf", [19] = "dns", [20] = "dsq" }), Roster(roster)).Should().BeNull();

        using var results = JsonDocument.Parse(Raw("openf1-session-result-race.json"));
        using var drivers = JsonDocument.Parse(Raw("openf1-drivers.json"));
        var fixture = OpenF1Parser.ParseSessionResult(results.RootElement, drivers.RootElement, Session(F1SessionType.Race));
        fixture.ExpectedDriverNumbers.Should().HaveCount(12);
        F1ResultValidator.Problem(fixture, 10).Should().BeNull("the fixture's DNF, DNS and DSQ drivers all have rows");
    }

    [Fact]
    public void Missing_duplicate_or_foreign_driver_numbers_are_not_publishable_and_a_missing_roster_fails_closed()
    {
        var roster = Enumerable.Range(1, 20).ToList();
        Problem(ResultRows(roster.Where(n => n != 7)), Roster(roster)).Should().Contain("1 of 20");
        Problem(ResultRows(roster.Append(7)), Roster(roster)).Should().Contain("duplicate driver numbers");
        Problem(ResultRows(roster.Where(n => n != 7).Append(99)), Roster(roster)).Should().NotBeNull();
        Problem(ResultRows(roster.Append(99)), Roster(roster)).Should().Contain("outside the session roster");
        Problem(ResultRows(roster), Roster(roster.Append(5))).Should().Contain("duplicate driver numbers in the participant roster");
        Problem(ResultRows(roster), null).Should().Contain("empty participant roster", "no driver list (OpenF1 404) proves nothing");
        Problem(ResultRows(roster), Roster([])).Should().Contain("empty participant roster");
    }

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    [InlineData(26)]
    public void Grid_size_changes_need_no_code_or_configuration_change(int grid)
    {
        var roster = Enumerable.Range(1, grid).ToList();
        Problem(ResultRows(roster), Roster(roster)).Should().BeNull();
        Problem(ResultRows(roster.Take(grid - 1)), Roster(roster)).Should().NotBeNull();
    }

    [Fact]
    public async Task OpenF1_results_provider_treats_a_missing_driver_list_as_not_publishable()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var stub = new StubHttpHandler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/drivers", StringComparison.Ordinal)
            ? StubHttpHandler.Json(Raw("openf1-not-found.json"), HttpStatusCode.NotFound)
            : StubHttpHandler.Json(Raw("openf1-session-result-race.json"))));
        var options = Options.Create(new OpenF1Options { MaxRetries = 0 });
        var tokens = new OpenF1TokenProvider(new SingleClientFactory(new HttpClient(stub)), options, clock, NullLogger<OpenF1TokenProvider>.Instance);
        var client = new OpenF1Client(new HttpClient(stub) { BaseAddress = new Uri("https://api.openf1.org/") }, options, tokens, new F1RequestBudget(clock), clock, NullLogger<OpenF1Client>.Instance);
        var provider = new OpenF1ResultsProvider(client, tokens, options, new F1DataMode(F1ProviderMode.Live));
        var result = await provider.GetResultAsync(Session(F1SessionType.Race), "8805", TestContext.Current.CancellationToken);
        F1ResultValidator.Problem(result.Value!, 10).Should().Contain("empty participant roster");
    }

    // ---------------------------------------------------------------- real MQTT client bounds

    [Fact]
    public async Task Mqtt_connect_to_a_stalled_endpoint_is_bounded_and_not_an_auth_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            // Accept and never answer: the TLS handshake / CONNACK stalls forever.
            try
            {
                while (true)
                    accepted.Add(await listener.AcceptTcpClientAsync());
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // listener stopped
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
            var stub = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json("{\"expires_in\":\"3600\",\"access_token\":\"tok-abcdefgh\"}")));
            var options = Options.Create(new OpenF1Options { Username = "u", Password = "p", MqttHost = "127.0.0.1", MqttPort = port, TimeoutSeconds = 1 });
            var tokens = new OpenF1TokenProvider(new SingleClientFactory(new HttpClient(stub)), options, clock, NullLogger<OpenF1TokenProvider>.Instance);
            var client = new OpenF1LiveClient(tokens, options, clock, NullLogger<OpenF1LiveClient>.Instance);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var act = () => client.RunConnectionAsync((_, _) => Task.CompletedTask, () => { }, TestContext.Current.CancellationToken);
            var thrown = await act.Should().ThrowAsync<Exception>();
            thrown.Which.Should().NotBeOfType<F1LiveAuthenticationException>("a stalled network is transient, not a credential problem");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "connect has a hard upper bound");
        }
        finally
        {
            listener.Stop();
            await acceptLoop;
            foreach (var c in accepted)
                c.Dispose();
        }
    }

    [Fact]
    public async Task Mqtt_disconnect_is_bounded_even_when_the_operation_ignores_cancellation()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var never = new TaskCompletionSource();
        (await OpenF1LiveClient.DisconnectBoundedAsync(_ => never.Task, TimeSpan.FromMilliseconds(200))).Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (await OpenF1LiveClient.DisconnectBoundedAsync(t => Task.Delay(Timeout.Infinite, t), TimeSpan.FromMilliseconds(200))).Should().BeFalse();
        (await OpenF1LiveClient.DisconnectBoundedAsync(_ => Task.CompletedTask, TimeSpan.FromSeconds(1))).Should().BeTrue();
        (await OpenF1LiveClient.DisconnectBoundedAsync(_ => throw new IOException("reset"), TimeSpan.FromSeconds(1))).Should().BeFalse("never throws");
        OpenF1LiveClient.DisconnectTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
