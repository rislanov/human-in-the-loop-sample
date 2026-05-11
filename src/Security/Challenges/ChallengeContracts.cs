namespace HumanLoopBooking.Services;

// Public contract for the reusable interactive challenge module.
// The booking flow owns persistence and state transitions; this module owns
// challenge generation, rendering, and protocol validation.
public interface IChallengeSessionFactory
{
    ChallengeSession Create(
        Guid intentId,
        string verificationSessionId,
        DateTimeOffset now,
        TimeSpan ttl,
        int currentRiskScore);
}

// Rendering is hidden behind an interface so a production application can swap
// this demo renderer for a licensed/open-source CAPTCHA widget or a CDN-backed
// image renderer without changing the booking state machine.
public interface IChallengeAssetRenderer
{
    byte[] RenderBackground(ChallengeSession challenge, string? phase);

    string RenderPiece(ChallengeSession challenge);
}

// Protocol validation answers a narrow question: did the browser complete the
// one-time interactive challenge lifecycle in a way that is acceptable for this
// challenge variant? It does not decide whether the user is trusted overall.
public interface IChallengeProtocolValidator
{
    ChallengeProtocolAssessment Assess(
        ChallengeSession challenge,
        ChallengeSolution? solution,
        ChallengeTelemetry? telemetry);
}

public sealed record ChallengeProtocolAssessment(
    bool IsSatisfied,
    int RiskScore,
    IReadOnlyList<string> Signals);
