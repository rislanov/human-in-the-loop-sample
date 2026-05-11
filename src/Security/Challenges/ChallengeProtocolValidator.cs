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
        if (!string.Equals(solution.InteractionPhase, "active", StringComparison.OrdinalIgnoreCase))
        {
            satisfied = false;
            AddRisk(14, "released_before_active_phase");
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

        if (solution.LastAdjustmentMs > 0 &&
            solution.ActiveElapsedMs > 0 &&
            solution.LastAdjustmentMs + 350 < solution.ActiveElapsedMs &&
            challenge.Variant != ChallengeVariants.HoldAndRelease)
        {
            AddRisk(4, "stale_final_adjustment");
        }

        return new ChallengeProtocolAssessment(satisfied, risk, signals);
    }

    private static bool HasActiveAdjustment(ChallengeTelemetry? telemetry)
    {
        if (telemetry is null)
        {
            return false;
        }

        var activeMoves = telemetry.Points
            .Where(point =>
                string.Equals(point.State, "active", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(point.Phase, "down", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (activeMoves.Length < 2)
        {
            return false;
        }

        var activeXSpan = activeMoves.Max(point => point.X) - activeMoves.Min(point => point.X);
        return activeXSpan >= 8;
    }
}
