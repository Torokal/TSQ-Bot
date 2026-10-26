using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// /ongoru gunluk as the member sees it (<see cref="PredictionDailyFlow"/> on the real SQLite database, fake Discord): the
/// interaction is acknowledged privately before the claim, a new claim is announced with exactly one public line (name as
/// untrusted text, no pings) and the details stay private; repeats, the wrong channel and a disabled module answer privately
/// and post nothing; parallel claims post once; a failed announcement is reported privately after the coins were committed
/// and never credits again; both ends of 80–200 are paid (scripted draws).
/// </summary>
public sealed class PredictionDailyFlowTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    /// <summary>Records what the command would send through the interaction, in order.</summary>
    private sealed class FakeResponder : IPredictionDailyResponder
    {
        public List<string> Calls { get; } = [];
        public List<OperationResult> Private { get; } = [];
        public Func<Task>? OnDefer { get; init; }

        public async Task DeferPrivateAsync()
        {
            lock (Calls)
                Calls.Add("defer-private");
            if (OnDefer is not null)
                await OnDefer();
        }

        public Task ReplyPrivateAsync(OperationResult result)
        {
            lock (Calls)
            {
                Calls.Add("private:" + result.MessageKey);
                Private.Add(result);
            }

            return Task.CompletedTask;
        }
    }

    private static Task RunAsync(PredictionTestKit kit, ActorContext actor, FakeResponder responder, ChannelId? channel = null, string? name = null) =>
        kit.Host.InScopeAsync(sp => sp.GetRequiredService<PredictionDailyFlow>().RunAsync(actor, channel ?? Commands, name ?? Name(actor), responder, Ct));

    private Task RunAsync(ActorContext actor, FakeResponder responder, ChannelId? channel = null, string? name = null) =>
        RunAsync(_kit, actor, responder, channel, name);

    [Fact]
    public async Task A_new_claim_is_announced_once_in_public_without_pings_and_the_details_stay_private()
    {
        int? claimsAtDefer = null;
        var responder = new FakeResponder { OnDefer = async () => claimsAtDefer = await _kit.CountAsync<PredictionDailyClaimEntity>() };
        _kit.Random.Next.Enqueue(137);

        await RunAsync(Member(1), responder, name: "Toro");

        claimsAtDefer.Should().Be(0, "the private acknowledgement comes before the claim (Discord's 3-second window)");
        responder.Calls.Should().Equal("defer-private", "private:predictions.daily.done");
        responder.Private.Single().Args.Take(2).Should().Equal("137", "1137");
        var post = _kit.Transport.Messages.Should().ContainSingle().Which;
        post.Channel.Should().Be(Commands, "the channel the command was used in");
        post.Message.Content.Should().Be("🪙 **Toro** günlük ödülünü aldı: **+137 TSQ Coin**!");
        post.Message.Embed.Should().BeNull();
        post.Message.Mentions.Should().BeSameAs(MentionPolicy.None);
        post.Pinged.Should().BeFalse();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(113_700);
    }

    [Fact]
    public async Task The_name_is_untrusted_text_and_can_never_mention_or_format()
    {
        await RunAsync(Member(1), new FakeResponder(), name: "@everyone <@&1233057768408350741> **x** [l](https://e.example)");

        var post = _kit.Transport.Messages.Should().ContainSingle().Which;
        post.Message.Content.Should().NotContain("@everyone").And.NotContain("<@&").And.NotContain("**x**").And.NotContain("https://");
        post.Message.Content.Should().StartWith("🪙 **@​everyone").And.EndWith(" günlük ödülünü aldı: **+80 TSQ Coin**!");
        post.Pinged.Should().BeFalse();
    }

    [Fact]
    public async Task A_repeat_the_wrong_channel_and_a_disabled_module_answer_privately_and_post_nothing()
    {
        await RunAsync(Member(1), new FakeResponder());
        _kit.Transport.Messages.Should().ContainSingle();

        var repeat = new FakeResponder();
        await RunAsync(Member(1), repeat);
        repeat.Calls.Should().Equal("defer-private", "private:predictions.daily.already");

        var wrong = new FakeResponder();
        await RunAsync(Member(2), wrong, channel: Elsewhere);
        wrong.Calls.Should().Equal("defer-private", "private:predictions.wrong_channel");
        var predictionsChannel = new FakeResponder();
        await RunAsync(Member(2), predictionsChannel, channel: Predictions);
        predictionsChannel.Calls.Should().Equal("defer-private", "private:predictions.wrong_channel");

        await _kit.SetEnabledAsync(false);
        var off = new FakeResponder();
        await RunAsync(Member(3), off);
        off.Calls.Should().Equal("defer-private", "private:error.module_disabled");

        _kit.Transport.Messages.Should().ContainSingle("only the first claim was announced");
        _kit.Transport.SendCalls.Should().Be(1);
        (await _kit.CountAsync<PredictionDailyClaimEntity>()).Should().Be(1);
    }

    [Fact]
    public async Task Parallel_claims_of_one_member_pay_and_post_once()
    {
        var a = new FakeResponder();
        var b = new FakeResponder();
        await _kit.TogetherAsync(async () => { await RunAsync(Member(1), a); return 0; }, async () => { await RunAsync(Member(1), b); return 0; });

        a.Calls.Concat(b.Calls).Where(c => c.StartsWith("private:", StringComparison.Ordinal)).Should()
            .BeEquivalentTo(["private:predictions.daily.done", "private:predictions.daily.already"]);
        _kit.Transport.Messages.Should().ContainSingle();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(108_000);
        (await _kit.CountAsync<PredictionDailyClaimEntity>()).Should().Be(1);
    }

    public static TheoryData<string> FailedSends => ["permanent", "transient", "rate-limited", "ambiguous-not-posted"];

    [Theory]
    [MemberData(nameof(FailedSends))]
    public async Task A_failed_announcement_keeps_the_coins_says_so_privately_and_never_credits_or_posts_again(string failure)
    {
        _kit.Transport.ScriptSend(() => failure switch
        {
            "permanent" => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"),
            "transient" => new SendOutcome.Transient("503"),
            "rate-limited" => new SendOutcome.RateLimited(TimeSpan.FromSeconds(5)),
            _ => new SendOutcome.Ambiguous("timeout, message not created"),
        });
        _kit.Random.Next.Enqueue(150);
        var responder = new FakeResponder();

        await RunAsync(Member(1), responder);

        responder.Calls.Should().Equal("defer-private", "private:predictions.daily.done_unannounced");
        responder.Private.Single().Args.Take(2).Should().Equal("150", "1150");
        _kit.Transport.Messages.Should().BeEmpty();
        _kit.Transport.SendCalls.Should().Be(1, "the announcement is never retried (no second public post)");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(115_000, "the claim was committed before anything was posted");

        var again = new FakeResponder();
        await RunAsync(Member(1), again);
        again.Calls.Should().Equal("defer-private", "private:predictions.daily.already");
        _kit.Transport.SendCalls.Should().Be(1);
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(115_000);
        (await _kit.LedgerAsync(1)).Count(l => l.Kind == PredictionLedgerKind.Daily).Should().Be(1);
    }

    [Fact]
    public async Task An_announcement_Discord_accepted_but_did_not_confirm_is_found_and_not_sent_twice()
    {
        _kit.Transport.ScriptAcceptedButTimedOut();
        var responder = new FakeResponder();

        await RunAsync(Member(1), responder);

        responder.Calls.Should().Equal("defer-private", "private:predictions.daily.done");
        _kit.Transport.Messages.Should().ContainSingle();
        _kit.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task After_a_restart_the_same_day_is_still_claimed_and_nothing_is_announced_again()
    {
        await RunAsync(Member(1), new FakeResponder());

        await using var second = await PredictionTestKit.CreateAsync(_kit.Host.Clock.GetUtcNow(), directory: _kit.Host.Directory, transport: _kit.Transport);
        var responder = new FakeResponder();
        await RunAsync(second, Member(1), responder);

        responder.Calls.Should().Equal("defer-private", "private:predictions.daily.already");
        _kit.Transport.Messages.Should().ContainSingle();
        (await second.WalletAsync(1))!.BalanceMinor.Should().Be(108_000);
    }

    [Theory]
    [InlineData(80, "1080")]
    [InlineData(200, "1200")]
    public async Task Both_ends_of_the_range_are_paid_and_the_ledger_the_private_and_the_public_message_agree(int coins, string balance)
    {
        _kit.Random.Next.Enqueue(coins);
        var responder = new FakeResponder();

        await RunAsync(Member(1), responder, name: "Toro");

        _kit.Random.Calls.Should().Equal((80, 200));
        responder.Private.Single().Args.Take(2).Should().Equal(coins.ToString(System.Globalization.CultureInfo.InvariantCulture), balance);
        _kit.Transport.Messages.Single().Message.Content.Should().Be($"🪙 **Toro** günlük ödülünü aldı: **+{coins} TSQ Coin**!");
        (await _kit.LedgerAsync(1)).Single(l => l.Kind == PredictionLedgerKind.Daily).AmountMinor.Should().Be(coins * 100L);
        (await _kit.Db(db => db.Set<PredictionDailyClaimEntity>().Select(c => c.AmountMinor).SingleAsync())).Should().Be(coins * 100L);
    }

    [Fact]
    public void The_production_source_includes_the_upper_bound()
    {
        // Deterministic: a one-value range can only return that value, so this pins the "+ 1" of the exclusive upper bound.
        var random = new SecurePredictionRandom();
        random.NextInclusive(200, 200).Should().Be(200);
        random.NextInclusive(80, 80).Should().Be(80);
    }
}
