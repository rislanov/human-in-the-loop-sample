namespace HumanLoopBooking.Services;

// Validates the interactive protocol that wraps the visual slider. This layer is
// separate from image matching: a solution can have the correct X coordinate and
// still fail if it skips the lifecycle that produces useful behavioral signals.
public sealed class ChallengeProtocolValidator : IChallengeProtocolValidator
{
    public ChallengeProtocolAssessment Assess(
        ChallengeSession challenge,
        ChallengeSolution? solution,
        ChallengeTelemetry? telemetry)
    {
        var signals = new List<string>();
        var risk = 0;
        var satisfied = true;

        void AddRisk(int value, string signal)
        {
            risk += value;
            signals.Add(signal);
        }

        if (solution is null)
        {
            AddRisk(30, "missing_solution");
            return new ChallengeProtocolAssessment(false, risk, signals);
        }

        // /challenge/start is the server-side marker that a real interaction
        // lifecycle began. Without it, a script could post an offline-generated X
        // value without ever rendering or interacting with the challenge.
        if (challenge.StartedAt is null || string.IsNullOrWhiteSpace(challenge.PhaseNonce))
        {
            AddRisk(26, "challenge_not_started");
            return new ChallengeProtocolAssessment(false, risk, signals);
        }

        if (!string.Equals(solution.PhaseNonce, challenge.PhaseNonce, StringComparison.Ordinal))
        {
            satisfied = false;
            AddRisk(22, "phase_nonce_mismatch");
        }

        // The active phase begins after the server-recorded start and a short
        // randomized delay. Releasing before this phase defeats the dynamic visual
        // part of the challenge and is therefore treated as protocol failure.
        var expectedReleasePhase = challenge.Variant == ChallengeVariants.FollowUpShift
            ? "follow_up"
            : "active";
        if (!string.Equals(solution.InteractionPhase, expectedReleasePhase, StringComparison.OrdinalIgnoreCase))
        {
            satisfied = false;
            AddRisk(14, challenge.Variant == ChallengeVariants.FollowUpShift
                ? "released_before_follow_up_phase"
                : "released_before_active_phase");
        }

        if (solution.ActiveElapsedMs <= 0)
        {
            satisfied = false;
            AddRisk(10, "missing_active_elapsed");
        }
        else if (solution.ActiveElapsedMs < 120)
        {
            satisfied = false;
            AddRisk(10, "active_phase_too_short");
        }

        if (challenge.Variant == ChallengeVariants.HoldAndRelease)
        {
            // The hold requirement is deliberately short. It gives the protocol a
            // time-based component without turning the challenge into a memory or
            // dexterity test for normal users.
            if (solution.HoldMs + 80 < challenge.HoldRequirementMs)
            {
                satisfied = false;
                AddRisk(18, "hold_requirement_missed");
            }
            else if (solution.HoldMs > challenge.HoldRequirementMs + 3000)
            {
                AddRisk(4, "unusually_long_hold");
            }
        }

        if (challenge.Variant == ChallengeVariants.ShiftAfterStart &&
            !HasActiveAdjustment(telemetry))
        {
            // This variant is meant to require a correction after the real target is
            // revealed. A trace with no active-phase adjustment looks like a one-shot
            // screenshot solve and should not count as a protocol pass.
            satisfied = false;
            AddRisk(14, "missing_post_reveal_adjustment");
        }

        if (challenge.Variant == ChallengeVariants.FollowUpShift)
        {
            // Follow-up validation makes the challenge interactive after the first
            // target reveal. A correct final X is not enough: the server must also
            // see that the browser requested the follow-up phase and moved after it.
            if (challenge.FollowUpActivatedAt is null || string.IsNullOrWhiteSpace(challenge.FollowUpNonce))
            {
                satisfied = false;
                AddRisk(22, "follow_up_not_started");
            }

            if (!string.Equals(solution.FollowUpNonce, challenge.FollowUpNonce, StringComparison.Ordinal))
            {
                satisfied = false;
                AddRisk(22, "follow_up_nonce_mismatch");
            }

            if (solution.FollowUpElapsedMs <= 0)
            {
                satisfied = false;
                AddRisk(14, "missing_follow_up_elapsed");
            }
            else if (solution.FollowUpElapsedMs < 120)
            {
                satisfied = false;
                AddRisk(10, "follow_up_phase_too_short");
            }

            if (!HasStateAdjustment(telemetry, "follow_up", challenge.RequiredPostFollowUpAdjustmentPx))
            {
                satisfied = false;
                AddRisk(18, "missing_post_follow_up_adjustment");
            }
        }

        if (solution.LastAdjustmentMs > 0 &&
            solution.ActiveElapsedMs > 0 &&
            solution.LastAdjustmentMs + 350 < solution.ActiveElapsedMs &&
            challenge.Variant is not (ChallengeVariants.HoldAndRelease or ChallengeVariants.FollowUpShift))
        {
            AddRisk(4, "stale_final_adjustment");
        }

        if (challenge.Variant == ChallengeVariants.FollowUpShift &&
            solution.LastFollowUpAdjustmentMs > 0 &&
            solution.FollowUpElapsedMs > 0 &&
            solution.LastFollowUpAdjustmentMs + 350 < solution.FollowUpElapsedMs)
        {
            AddRisk(5, "stale_follow_up_adjustment");
        }

        return new ChallengeProtocolAssessment(satisfied, risk, signals);
    }

    private static bool HasActiveAdjustment(ChallengeTelemetry? telemetry)
    {
        return HasStateAdjustment(telemetry, "active", 8);
    }

    private static bool HasStateAdjustment(ChallengeTelemetry? telemetry, string state, int requiredSpan)
    {
        if (telemetry is null)
        {
            return false;
        }

        var activeMoves = telemetry.Points
            .Where(point =>
                string.Equals(point.State, state, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(point.Phase, "down", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (activeMoves.Length < 2)
        {
            return false;
        }

        var activeXSpan = activeMoves.Max(point => point.X) - activeMoves.Min(point => point.X);
        return activeXSpan >= Math.Max(1, requiredSpan);
    }
}
