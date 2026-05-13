namespace HumanLoopBooking.Services;

// Coarse form lifecycle signals collected before the email confirmation step.
// They are deliberately lightweight and are never used as a single blocking
// condition; they only seed the cumulative risk score.
public sealed class FormTelemetry
{
    public int SubmitElapsedMs { get; init; }
    public int FirstInteractionMs { get; init; }
    public int KeyEventCount { get; init; }
    public int PasteCount { get; init; }
    public int PointerMoveCount { get; init; }
    public int FocusChangeCount { get; init; }
    public BrowserSignals? Browser { get; init; }
}

// Browser hints are cheap to spoof, so they are scored only as weak signals.
// The goal is to catch commodity automation patterns without overfitting to one
// browser fingerprint.
public sealed record class BrowserSignals
{
    public bool WebDriver { get; init; }
    public int PluginsLength { get; init; }
    public int LanguagesLength { get; init; }
    public string? UserAgent { get; init; }
    public string? Platform { get; init; }
    public bool TouchCapable { get; init; }
}

public sealed record RiskAssessment(
    int Score,
    string Bucket,
    string Decision,
    string ReasonCode,
    string? TrajectoryFingerprint,
    IReadOnlyList<string> Signals);
