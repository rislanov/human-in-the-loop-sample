namespace HumanLoopBooking.Services;

// Shared variant names for the custom interactive challenge. They are strings
// because the value crosses the server/client boundary in JSON.
public static class ChallengeVariants
{
    public const string RevealTarget = "reveal_target";
    public const string ShiftAfterStart = "shift_after_start";
    public const string HoldAndRelease = "hold_and_release";
    public const string FollowUpShift = "follow_up_shift";
}

// Server-owned challenge state. The sensitive fields (TargetX, tolerance,
// PhaseNonce, timestamps) must stay server-side; only RenderUiConfig and asset
// URLs are sent to the browser.
public sealed record class ChallengeSession
{
    public required string Id { get; init; }
    public required Guid IntentId { get; init; }
    public required string VerificationSessionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Variant { get; init; }
    public required int TargetX { get; init; }
    public required int PreviewTargetX { get; init; }
    public required int PieceY { get; init; }
    public int Width { get; init; } = 360;
    public int Height { get; init; } = 180;
    public int PieceSize { get; init; } = 48;
    public int Tolerance { get; init; } = 9;
    public int TrackWidth { get; init; } = 360;
    public int HandleSize { get; init; } = 54;
    public int StripeOffset { get; init; }
    public int ActivationDelayMs { get; init; } = 280;
    public int HoldRequirementMs { get; init; }
    public int FollowUpTargetX { get; init; }
    public int FollowUpDelayMs { get; init; }
    public int RequiredPostFollowUpAdjustmentPx { get; init; }
    public string? PhaseNonce { get; set; }
    public string? FollowUpNonce { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? FollowUpActivatedAt { get; set; }
    public bool Consumed { get; set; }
}

public sealed class InitChallengeRequest
{
    public BrowserSignals? Browser { get; init; }
}

public sealed class StartChallengeRequest
{
    public string? ChallengeId { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class FollowUpChallengeRequest
{
    public string? ChallengeId { get; init; }
    public string? PhaseNonce { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed record class VerifyChallengeRequest
{
    public string? ChallengeId { get; init; }
    public ChallengeSolution? Solution { get; init; }
    public ChallengeTelemetry? Telemetry { get; init; }
    public BrowserSignals? Browser { get; init; }
}

// Client-submitted solution metadata. These fields are not trusted as truth; the
// protocol validator and risk engine use them as consistency signals.
public sealed record class ChallengeSolution
{
    public int X { get; init; }
    public int TimeSpentMs { get; init; }
    public string? PhaseNonce { get; init; }
    public string? FollowUpNonce { get; init; }
    public string? InteractionPhase { get; init; }
    public int ActiveElapsedMs { get; init; }
    public int FollowUpElapsedMs { get; init; }
    public int HoldMs { get; init; }
    public int LastAdjustmentMs { get; init; }
    public int LastFollowUpAdjustmentMs { get; init; }
}

public sealed class ChallengeTelemetry
{
    public string? Modality { get; init; }
    public List<TelemetryPoint> Points { get; init; } = [];
    public ChallengeEvents Events { get; init; } = new();
}

// Coordinates are reported in challenge-image space, not viewport space. This
// keeps scoring stable across responsive layouts and random slider widths.
public sealed class TelemetryPoint
{
    public int X { get; init; }
    public int Y { get; init; }
    public int T { get; init; }
    public string? Phase { get; init; }
    public string? State { get; init; }
}

public sealed class ChallengeEvents
{
    public bool FocusLost { get; init; }
    public bool VisibilityChanged { get; init; }
    public bool PointerCancelled { get; init; }
}

public sealed record RenderPayload(
    string BackgroundImageUrl,
    string PieceImageUrl,
    RenderUiConfig UiConfig);

// Render-safe configuration. It intentionally omits TargetX, tolerance, phase
// nonce, and any value that would allow offline verification.
public sealed record RenderUiConfig(
    int Width,
    int Height,
    int PieceSize,
    int PieceY,
    int TrackWidth,
    int HandleSize,
    int StripeOffset,
    int ActivationDelayMs,
    string Variant,
    int HoldRequirementMs,
    int FollowUpDelayMs);
