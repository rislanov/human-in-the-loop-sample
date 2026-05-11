using HumanLoopBooking.Services;

namespace HumanLoopBooking.Tests;

public sealed class ChallengeProtocolAndRiskTests
{
    private readonly ChallengeProtocolValidator _protocol = new();

    [Fact]
    public void verify_without_start_fails()
    {
        var challenge = FollowUpChallenge(started: false, followUpStarted: false);
        var request = TestHelpers.ValidVerifyRequest(challenge);

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("challenge_not_started", result.Signals);
    }

    [Fact]
    public void verify_with_wrong_phase_nonce_fails()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: true);
        var request = TestHelpers.ValidVerifyRequest(challenge);
        request = request.WithSolution(request.Solution! with { PhaseNonce = "wrong" });

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("phase_nonce_mismatch", result.Signals);
    }

    [Fact]
    public void verify_before_active_phase_fails()
    {
        var challenge = RevealChallenge() with
        {
            PhaseNonce = "cp_test",
            StartedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow.AddMilliseconds(300)
        };
        var solution = new ChallengeSolution
        {
            X = challenge.TargetX,
            PhaseNonce = challenge.PhaseNonce,
            InteractionPhase = "pre_active",
            ActiveElapsedMs = 0,
            TimeSpentMs = 300
        };

        var result = _protocol.Assess(challenge, solution, TestHelpers.HumanTelemetry(challenge.TargetX, includeFollowUp: false));

        Assert.False(result.IsSatisfied);
        Assert.Contains("released_before_active_phase", result.Signals);
    }

    [Fact]
    public void verify_hold_and_release_without_hold_fails()
    {
        var challenge = RevealChallenge() with
        {
            Variant = ChallengeVariants.HoldAndRelease,
            HoldRequirementMs = 650,
            PhaseNonce = "cp_test",
            StartedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow
        };
        var solution = new ChallengeSolution
        {
            X = challenge.TargetX,
            PhaseNonce = challenge.PhaseNonce,
            InteractionPhase = "active",
            ActiveElapsedMs = 900,
            TimeSpentMs = 1200,
            HoldMs = 100
        };

        var result = _protocol.Assess(challenge, solution, TestHelpers.HumanTelemetry(challenge.TargetX, includeFollowUp: false));

        Assert.False(result.IsSatisfied);
        Assert.Contains("hold_requirement_missed", result.Signals);
    }

    [Fact]
    public void verify_shift_after_start_without_active_adjustment_fails()
    {
        var challenge = RevealChallenge() with
        {
            Variant = ChallengeVariants.ShiftAfterStart,
            PhaseNonce = "cp_test",
            StartedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow
        };
        var telemetry = new ChallengeTelemetry
        {
            Modality = "mouse",
            Points =
            [
                new() { X = 10, Y = 90, T = 0, Phase = "down", State = "pre_active" },
                new() { X = challenge.TargetX, Y = 90, T = 800, Phase = "up", State = "pre_active" }
            ]
        };
        var solution = new ChallengeSolution
        {
            X = challenge.TargetX,
            PhaseNonce = challenge.PhaseNonce,
            InteractionPhase = "active",
            ActiveElapsedMs = 700,
            TimeSpentMs = 1000
        };

        var result = _protocol.Assess(challenge, solution, telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("missing_post_reveal_adjustment", result.Signals);
    }

    [Fact]
    public void expired_challenge_is_rejected_by_store()
    {
        var harness = TestHelpers.CreateStore(clock: new MutableTimeProvider(DateTimeOffset.UtcNow));
        var flow = TestHelpers.CreateStartedChallenge(harness);
        harness.Clock.Advance(TimeSpan.FromMinutes(3));

        var result = harness.Store.VerifyChallenge(TestHelpers.ValidVerifyRequest(flow.Challenge), flow.SessionId, flow.Context);

        Assert.False(result.Success);
        Assert.Equal("challenge_not_found", result.ErrorCode);
    }

    [Fact]
    public void follow_up_shift_requires_follow_up_endpoint()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: false);
        var request = TestHelpers.ValidVerifyRequest(challenge);

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("follow_up_not_started", result.Signals);
    }

    [Fact]
    public void follow_up_shift_wrong_follow_up_nonce_fails()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: true);
        var request = TestHelpers.ValidVerifyRequest(challenge);
        request = request.WithSolution(request.Solution! with { FollowUpNonce = "fu_wrong" });

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("follow_up_nonce_mismatch", result.Signals);
    }

    [Fact]
    public void follow_up_shift_no_post_follow_up_adjustment_fails()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: true);
        var request = TestHelpers.ValidVerifyRequest(challenge);
        var telemetry = new ChallengeTelemetry
        {
            Modality = "mouse",
            Points =
            [
                new() { X = 10, Y = 90, T = 0, Phase = "down", State = "pre_active" },
                new() { X = challenge.TargetX, Y = 91, T = 500, Phase = "move", State = "active" },
                new() { X = challenge.FollowUpTargetX, Y = 90, T = 1000, Phase = "up", State = "follow_up" }
            ]
        };

        var result = _protocol.Assess(challenge, request.Solution, telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("missing_post_follow_up_adjustment", result.Signals);
    }

    [Fact]
    public void follow_up_shift_release_before_follow_up_fails()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: true);
        var request = TestHelpers.ValidVerifyRequest(challenge);
        request = request.WithSolution(request.Solution! with
        {
            InteractionPhase = "active",
            FollowUpElapsedMs = 0
        });

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.False(result.IsSatisfied);
        Assert.Contains("released_before_follow_up_phase", result.Signals);
    }

    [Fact]
    public void follow_up_shift_valid_lifecycle_passes()
    {
        var challenge = FollowUpChallenge(started: true, followUpStarted: true);
        var request = TestHelpers.ValidVerifyRequest(challenge);

        var result = _protocol.Assess(challenge, request.Solution, request.Telemetry);

        Assert.True(result.IsSatisfied, string.Join(", ", result.Signals));
    }

    [Fact]
    public void follow_up_shift_final_x_uses_follow_up_target()
    {
        var harness = TestHelpers.CreateStore(variant: ChallengeVariants.FollowUpShift);
        var flow = TestHelpers.CreateStartedChallenge(harness);
        var followUp = harness.Store.StartFollowUpChallenge(new FollowUpChallengeRequest
        {
            ChallengeId = flow.Challenge.Id,
            PhaseNonce = flow.Challenge.PhaseNonce,
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);
        Assert.True(followUp.Success, followUp.ErrorCode);

        var staleActiveRequest = TestHelpers.ValidVerifyRequest(followUp.Value!).WithSolution(
            TestHelpers.ValidVerifyRequest(followUp.Value!).Solution! with { X = followUp.Value!.TargetX });

        var result = harness.Store.VerifyChallenge(staleActiveRequest, flow.SessionId, flow.Context);

        Assert.True(result.Success, result.ErrorCode);
        Assert.NotEqual("allow", result.Value!.Decision);
    }

    [Fact]
    public void mobile_sparse_trace_is_not_hard_denied_by_itself()
    {
        var risk = ScoreWithTelemetry(new ChallengeTelemetry
        {
            Modality = "touch",
            Points =
            [
                new() { X = 0, Y = 80, T = 0, Phase = "down", State = "active" },
                new() { X = 176, Y = 86, T = 980, Phase = "up", State = "active" }
            ]
        });

        Assert.NotEqual("hard_denied", risk.Decision);
    }

    [Fact]
    public void desktop_perfect_straight_trace_increases_risk()
    {
        var risk = ScoreWithTelemetry(new ChallengeTelemetry
        {
            Modality = "mouse",
            Points = Enumerable.Range(0, 12)
                .Select(index => new TelemetryPoint
                {
                    X = index * 16,
                    Y = 90,
                    T = index * 80,
                    Phase = index == 0 ? "down" : index == 11 ? "up" : "move",
                    State = "active"
                })
                .ToList()
        });

        Assert.Contains("single_plane_drag", risk.Signals);
        Assert.True(risk.Score > 0);
    }

    [Fact]
    public void missing_telemetry_increases_risk_but_not_hard_denied_by_itself()
    {
        var risk = ScoreWithTelemetry(null);

        Assert.Contains("missing_telemetry", risk.Signals);
        Assert.NotEqual("hard_denied", risk.Decision);
    }

    [Fact]
    public void webdriver_signal_increases_risk_but_not_single_signal_block()
    {
        var challenge = RevealChallenge() with
        {
            PhaseNonce = "cp_test",
            StartedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow
        };
        var request = TestHelpers.ValidVerifyRequest(challenge) with
        {
            Browser = TestHelpers.Browser with { WebDriver = true }
        };
        var engine = new RiskEngine(new TrajectoryAnalyzer());

        var risk = engine.ScoreChallenge(TestIntent(), challenge, request, acceptedSolution: true, new ChallengeProtocolAssessment(true, 0, []));

        Assert.NotEqual("hard_denied", risk.Decision);
        Assert.True(risk.Score >= 16);
    }

    private static RiskAssessment ScoreWithTelemetry(ChallengeTelemetry? telemetry)
    {
        var challenge = RevealChallenge() with
        {
            PhaseNonce = "cp_test",
            StartedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow
        };
        var request = TestHelpers.ValidVerifyRequest(challenge) with
        {
            Telemetry = telemetry
        };
        var engine = new RiskEngine(new TrajectoryAnalyzer());
        return engine.ScoreChallenge(TestIntent(), challenge, request, acceptedSolution: true, new ChallengeProtocolAssessment(true, 0, []));
    }

    private static BookingIntent TestIntent()
    {
        return new BookingIntent
        {
            BookingData = new BookingData { Email = "user@example.test", Name = "Test" },
            EmailHash = SecurityHelpers.Hash("user@example.test"),
            EmailTicket = "et_test",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        };
    }

    private static ChallengeSession RevealChallenge()
    {
        return new ChallengeSession
        {
            Id = "ch_test",
            IntentId = Guid.NewGuid(),
            VerificationSessionId = "vs_test",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            Variant = ChallengeVariants.RevealTarget,
            TargetX = 176,
            PreviewTargetX = 232,
            PieceY = 72,
            Width = 360,
            Height = 180,
            PieceSize = 48,
            Tolerance = 8,
            TrackWidth = 360,
            HandleSize = 54,
            ActivationDelayMs = 260,
            StripeOffset = 3
        };
    }

    private static ChallengeSession FollowUpChallenge(bool started, bool followUpStarted)
    {
        var now = DateTimeOffset.UtcNow;
        return RevealChallenge() with
        {
            Variant = ChallengeVariants.FollowUpShift,
            TargetX = 176,
            FollowUpTargetX = 214,
            RequiredPostFollowUpAdjustmentPx = 12,
            FollowUpDelayMs = 420,
            PhaseNonce = started ? "cp_test" : null,
            StartedAt = started ? now : null,
            ActivatedAt = started ? now.AddMilliseconds(260) : null,
            FollowUpNonce = followUpStarted ? "fu_test" : null,
            FollowUpActivatedAt = followUpStarted ? now.AddMilliseconds(700) : null
        };
    }
}

internal static class VerifyRequestExtensions
{
    public static VerifyChallengeRequest WithSolution(this VerifyChallengeRequest request, ChallengeSolution solution)
    {
        return new VerifyChallengeRequest
        {
            ChallengeId = request.ChallengeId,
            Solution = solution,
            Telemetry = request.Telemetry,
            Browser = request.Browser
        };
    }
}
