using HumanLoopBooking.Services;

namespace HumanLoopBooking.Tests;

public sealed class AtomicAndPolicyTests
{
    [Fact]
    public void shadow_mode_allows_high_risk_but_logs_would_deny()
    {
        var applied = BotDefensePolicy.Apply("hard_denied", EnforcementMode.Shadow);

        Assert.Equal("allow", applied.AppliedDecision);
        Assert.Equal("hard_denied", applied.WouldHaveDecision);
    }

    [Fact]
    public void retry_only_converts_hard_deny_to_retry()
    {
        var applied = BotDefensePolicy.Apply("hard_denied", EnforcementMode.RetryOnly);

        Assert.Equal("retry_challenge", applied.AppliedDecision);
    }

    [Fact]
    public void cooldown_only_converts_hard_deny_to_temporary_deny()
    {
        var applied = BotDefensePolicy.Apply("hard_denied", EnforcementMode.CooldownOnly);

        Assert.Equal("temporarily_denied", applied.AppliedDecision);
    }

    [Fact]
    public void full_mode_applies_hard_deny()
    {
        var applied = BotDefensePolicy.Apply("hard_denied", EnforcementMode.Full);

        Assert.Equal("hard_denied", applied.AppliedDecision);
    }

    [Fact]
    public void parallel_verify_same_challenge_only_one_can_pass()
    {
        var harness = TestHelpers.CreateStore();
        var flow = TestHelpers.CreateStartedChallenge(harness);
        var request = TestHelpers.ValidVerifyRequest(flow.Challenge);

        var results = Enumerable.Range(0, 20)
            .AsParallel()
            .Select(_ => harness.Store.VerifyChallenge(request, flow.SessionId, flow.Context))
            .ToArray();

        Assert.Equal(1, results.Count(result => result.Success && result.Value?.Decision == "allow"));
        Assert.Equal(19, results.Count(result => !result.Success || result.ErrorCode == "challenge_expired_or_consumed"));
    }

    [Fact]
    public void parallel_finalize_same_validation_token_only_one_can_succeed()
    {
        var harness = TestHelpers.CreateStore();
        var flow = TestHelpers.CreateStartedChallenge(harness);
        var verify = harness.Store.VerifyChallenge(TestHelpers.ValidVerifyRequest(flow.Challenge), flow.SessionId, flow.Context);
        Assert.True(verify.Success, verify.ErrorCode);
        Assert.Equal("allow", verify.Value!.Decision);

        var token = verify.Value.ValidationToken!;
        var results = Enumerable.Range(0, 20)
            .AsParallel()
            .Select(_ => harness.Store.FinalizeBooking(new FinalizeBookingRequest
            {
                ValidationToken = token,
                SlotId = "slot_0701_1130",
                Browser = TestHelpers.Browser
            }, flow.SessionId, flow.Context))
            .ToArray();

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(19, results.Count(result => !result.Success));
    }

    [Fact]
    public void parallel_increment_rate_limit_is_consistent()
    {
        var harness = TestHelpers.CreateStore();

        var values = Enumerable.Range(0, 50)
            .AsParallel()
            .Select(_ => harness.State.IncrementWithExpiry("rate:test", TimeSpan.FromMinutes(1)))
            .ToArray();

        Assert.Equal(50, values.Distinct().Count());
        Assert.Contains(1, values);
        Assert.Contains(50, values);
    }

    [Fact]
    public void parallel_start_challenge_does_not_create_multiple_phase_nonces()
    {
        var harness = TestHelpers.CreateStore();
        var intentResult = harness.Store.CreateBookingIntent(TestHelpers.BookingRequest(), "https://localhost", TestHelpers.Context());
        Assert.True(intentResult.Success, intentResult.ErrorCode);
        var confirm = harness.Store.ConfirmEmailTicket(
            new ConfirmEmailRequest { Ticket = intentResult.Value!.Intent.EmailTicket, Browser = TestHelpers.Browser },
            TestHelpers.Context());
        Assert.True(confirm.Success, confirm.ErrorCode);
        var context = TestHelpers.Context(confirm.Value!.SessionNonce);
        var init = harness.Store.InitializeChallenge(new InitChallengeRequest { Browser = TestHelpers.Browser }, confirm.Value.VerificationSessionId, context);
        Assert.True(init.Success, init.ErrorCode);

        var nonces = Enumerable.Range(0, 20)
            .AsParallel()
            .Select(_ => harness.Store.StartChallenge(
                new StartChallengeRequest { ChallengeId = init.Value!.Id, Browser = TestHelpers.Browser },
                confirm.Value.VerificationSessionId,
                context))
            .Where(result => result.Success)
            .Select(result => result.Value!.PhaseNonce)
            .ToArray();

        Assert.NotEmpty(nonces);
        Assert.Single(nonces.Distinct());
    }

    [Fact]
    public void normal_pressure_uses_default_validation_ttl()
    {
        var harness = TestHelpers.CreateStore();
        var token = VerifyAndGetToken(harness, out var flow);
        var grant = harness.State.Get<ValidationGrant>($"validation:{token}")!;

        Assert.Equal(SlotPressureLevel.Normal, grant.PressureLevel);
        Assert.Equal(180, (int)(grant.ExpiresAt - harness.Clock.GetUtcNow()).TotalSeconds);
        Assert.Equal(flow.SessionId, grant.VerificationSessionId);
    }

    [Fact]
    public void high_pressure_shortens_validation_ttl()
    {
        var harness = TestHelpers.CreateStore();
        for (var index = 0; index < 20; index++)
        {
            harness.State.IncrementWithExpiry("pressure:serbian-permit-july-2026:score", TimeSpan.FromMinutes(1));
        }

        harness.State.Set("slot-pressure:serbian-permit-july-2026", new SlotPressureSnapshot
        {
            SlotGroup = "serbian-permit-july-2026",
            Level = SlotPressureLevel.High,
            UpdatedAt = harness.Clock.GetUtcNow(),
            ExpiresAt = harness.Clock.GetUtcNow().AddMinutes(5)
        }, TimeSpan.FromMinutes(5));

        var token = VerifyAndGetToken(harness, out _);
        var grant = harness.State.Get<ValidationGrant>($"validation:{token}")!;

        Assert.Equal(SlotPressureLevel.High, grant.PressureLevel);
        Assert.Equal(90, (int)(grant.ExpiresAt - harness.Clock.GetUtcNow()).TotalSeconds);
        Assert.Equal(2, grant.MaxAvailabilityRequests);
    }

    [Fact]
    public void validation_token_binds_to_first_slot_group()
    {
        var harness = TestHelpers.CreateStore();
        var token = VerifyAndGetToken(harness, out var flow);

        var first = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            ValidationToken = token,
            SlotGroup = "serbian-permit-july-2026",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);
        var second = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            ValidationToken = token,
            SlotGroup = "other-group",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(second.Success);
        Assert.Equal("slot_group_mismatch", second.ErrorCode);
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
