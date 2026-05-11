using HumanLoopBooking.Services;

namespace HumanLoopBooking.Tests;

public sealed class RateLimitTests
{
    [Fact]
    public void email_rate_limit_per_email_works()
    {
        var harness = TestHelpers.CreateStore();
        var email = $"limited-{Guid.NewGuid():N}@example.test";

        var first = harness.Store.CreateBookingIntent(TestHelpers.BookingRequest(email), "https://localhost", TestHelpers.Context());
        var second = harness.Store.CreateBookingIntent(TestHelpers.BookingRequest(email), "https://localhost", TestHelpers.Context());

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(second.Success);
        Assert.Equal("email_rate_limited", second.ErrorCode);
    }

    [Fact]
    public void challenge_init_limit_per_ticket_works()
    {
        var harness = TestHelpers.CreateStore();
        var intent = harness.Store.CreateBookingIntent(TestHelpers.BookingRequest(), "https://localhost", TestHelpers.Context());
        Assert.True(intent.Success, intent.ErrorCode);
        var confirm = harness.Store.ConfirmEmailTicket(
            new ConfirmEmailRequest { Ticket = intent.Value!.Intent.EmailTicket, Browser = TestHelpers.Browser },
            TestHelpers.Context());
        Assert.True(confirm.Success, confirm.ErrorCode);
        var context = TestHelpers.Context(confirm.Value!.SessionNonce);

        StoreResult<ChallengeSession>? last = null;
        for (var index = 0; index < 6; index++)
        {
            last = harness.Store.InitializeChallenge(
                new InitChallengeRequest { Browser = TestHelpers.Browser },
                confirm.Value.VerificationSessionId,
                context);
        }

        Assert.NotNull(last);
        Assert.False(last!.Success);
        Assert.Equal("too_many_attempts", last.ErrorCode);
    }

    [Fact]
    public void failed_challenge_limit_per_ticket_works()
    {
        var harness = TestHelpers.CreateStore();
        var flow = TestHelpers.CreateStartedChallenge(harness);

        StoreResult<ChallengeVerifyResult>? lastVerify = null;
        for (var index = 0; index < 3; index++)
        {
            var request = TestHelpers.ValidVerifyRequest(flow.Challenge) with
            {
                Solution = TestHelpers.ValidVerifyRequest(flow.Challenge).Solution! with { X = 1 }
            };
            lastVerify = harness.Store.VerifyChallenge(request, flow.SessionId, flow.Context);
            if (index < 2)
            {
                var next = harness.Store.InitializeChallenge(new InitChallengeRequest { Browser = TestHelpers.Browser }, flow.SessionId, flow.Context);
                Assert.True(next.Success, next.ErrorCode);
                flow = flow with { Challenge = harness.Store.StartChallenge(new StartChallengeRequest { ChallengeId = next.Value!.Id, Browser = TestHelpers.Browser }, flow.SessionId, flow.Context).Value! };
            }
        }

        Assert.NotNull(lastVerify);
        Assert.True(lastVerify!.Success, lastVerify.ErrorCode);
        Assert.Contains(lastVerify.Value!.Decision, new[] { "temporarily_denied", "hard_denied" });
    }

    [Fact]
    public void slot_pressure_limit_works()
    {
        var harness = TestHelpers.CreateStore();
        for (var index = 0; index < 10; index++)
        {
            harness.State.IncrementWithExpiry("rate:slot-group:serbian-permit-july-2026", TimeSpan.FromMinutes(1));
        }

        var token = VerifyAndGetToken(harness, out var flow);
        var result = harness.Store.FinalizeBooking(new FinalizeBookingRequest
        {
            ValidationToken = token,
            SlotId = "slot_0701_1130",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.False(result.Success);
        Assert.Equal("slot_pressure_cooldown", result.ErrorCode);
    }

    private static string VerifyAndGetToken(StoreHarness harness, out FlowState flow)
    {
        flow = TestHelpers.CreateStartedChallenge(harness);
        var verify = harness.Store.VerifyChallenge(TestHelpers.ValidVerifyRequest(flow.Challenge), flow.SessionId, flow.Context);
        Assert.True(verify.Success, verify.ErrorCode);
        Assert.Equal("allow", verify.Value!.Decision);
        return verify.Value.ValidationToken!;
    }
}
