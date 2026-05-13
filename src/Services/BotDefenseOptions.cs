namespace HumanLoopBooking.Services;

// Controls how risk decisions are applied to user traffic. Production systems
// should start in Shadow, compare "would have blocked" telemetry with real user
// outcomes, then gradually move to stronger modes after tuning thresholds.
public sealed class BotDefenseOptions
{
    public EnforcementMode EnforcementMode { get; init; } = EnforcementMode.Shadow;
}

public enum EnforcementMode
{
    // Risk is scored and audited, but the user-facing decision remains allow.
    Shadow,

    // Uncertain sessions may retry the challenge, but hard blocks are suppressed.
    RetryOnly,

    // Temporary cooldowns are enforced, while hard denies degrade to cooldowns.
    CooldownOnly,

    // The risk engine's decision is applied exactly as produced.
    Full
}

public static class BotDefensePolicy
{
    public static AppliedRiskDecision Apply(string riskDecision, EnforcementMode mode)
    {
        var normalized = string.IsNullOrWhiteSpace(riskDecision)
            ? "retry_challenge"
            : riskDecision;

        var applied = mode switch
        {
            EnforcementMode.Shadow => "allow",
            EnforcementMode.RetryOnly => normalized == "allow"
                ? "allow"
                : "retry_challenge",
            EnforcementMode.CooldownOnly => normalized == "hard_denied"
                ? "temporarily_denied"
                : normalized,
            EnforcementMode.Full => normalized,
            _ => normalized
        };

        return new AppliedRiskDecision(normalized, applied, mode);
    }
}

public sealed record AppliedRiskDecision(
    string WouldHaveDecision,
    string AppliedDecision,
    EnforcementMode EnforcementMode);
