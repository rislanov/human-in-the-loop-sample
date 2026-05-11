using System.Security.Cryptography;

namespace HumanLoopBooking.Services;

// Generates server-owned challenge sessions. The caller persists the returned
// object and treats it as authoritative state; the browser receives only the
// render-safe subset via RenderUiConfig and asset URLs.
public sealed class InteractiveChallengeSessionFactory : IChallengeSessionFactory
{
    public ChallengeSession Create(
        Guid intentId,
        string verificationSessionId,
        DateTimeOffset now,
        TimeSpan ttl,
        int currentRiskScore)
    {
        var targetX = RandomNumberGenerator.GetInt32(126, 288);
        var variant = PickVariant(currentRiskScore);

        return new ChallengeSession
        {
            Id = SecurityHelpers.NewToken("ch"),
            IntentId = intentId,
            VerificationSessionId = verificationSessionId,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
            Variant = variant,
            TargetX = targetX,
            PreviewTargetX = PickPreviewTarget(targetX),
            PieceY = RandomNumberGenerator.GetInt32(48, 102),

            // These values randomize the presentation layer without changing the
            // server-side answer. They make brittle coordinate-based automation
            // less reusable while keeping the task simple for a real user.
            Tolerance = RandomNumberGenerator.GetInt32(6, 10),
            TrackWidth = RandomNumberGenerator.GetInt32(332, 392),
            HandleSize = RandomNumberGenerator.GetInt32(50, 59),
            StripeOffset = RandomNumberGenerator.GetInt32(0, 28),

            // The visual target is intentionally revealed only after pointerdown.
            // This breaks the cheapest "solve screenshot, then drag once" strategy.
            ActivationDelayMs = RandomNumberGenerator.GetInt32(220, 421),
            HoldRequirementMs = variant == ChallengeVariants.HoldAndRelease
                ? RandomNumberGenerator.GetInt32(420, 721)
                : 0
        };
    }

    private static string PickVariant(int currentRiskScore)
    {
        // Variant selection is intentionally weighted but not deterministic by risk.
        // Normal users still see all variants, and suspicious sessions see more
        // variants that require post-start adjustment.
        var roll = RandomNumberGenerator.GetInt32(0, 100);
        if (currentRiskScore >= 35)
        {
            return roll switch
            {
                < 45 => ChallengeVariants.ShiftAfterStart,
                < 75 => ChallengeVariants.HoldAndRelease,
                _ => ChallengeVariants.RevealTarget
            };
        }

        return roll switch
        {
            < 45 => ChallengeVariants.RevealTarget,
            < 78 => ChallengeVariants.ShiftAfterStart,
            _ => ChallengeVariants.HoldAndRelease
        };
    }

    private static int PickPreviewTarget(int targetX)
    {
        var shift = RandomNumberGenerator.GetInt32(34, 66);
        var canShiftLeft = targetX - shift >= 88;
        var canShiftRight = targetX + shift <= 306;

        if (canShiftLeft && canShiftRight)
        {
            return targetX + (RandomNumberGenerator.GetInt32(0, 2) == 0 ? -shift : shift);
        }

        return canShiftLeft ? targetX - shift : targetX + shift;
    }
}
