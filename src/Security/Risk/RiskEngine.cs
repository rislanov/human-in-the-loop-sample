namespace HumanLoopBooking.Services;

// Combines independent weak signals into a single operational decision.
// This class intentionally avoids "one signal equals block" rules: browser
// markers, timing, protocol quality, and pointer telemetry can all be spoofed or
// noisy, but the combination is useful for making automation less reliable.
public sealed class RiskEngine : IRiskEngine
{
    private readonly ITrajectoryAnalyzer _trajectoryAnalyzer;

    public RiskEngine(ITrajectoryAnalyzer trajectoryAnalyzer)
    {
        _trajectoryAnalyzer = trajectoryAnalyzer;
    }

    public int ScoreForm(FormTelemetry? telemetry)
    {
        // Missing pre-submit telemetry is not a fatal error. Real browsers can lose
        // events because of extensions, privacy settings, or script loading races.
        // We start with a small risk bump instead of blocking early in the flow.
        if (telemetry is null)
        {
            return 10;
        }

        var risk = 0;

        if (telemetry.SubmitElapsedMs is > 0 and < 1200)
        {
            risk += 8;
        }

        if (telemetry.FirstInteractionMs is > 0 and < 120)
        {
            risk += 6;
        }

        if (telemetry.KeyEventCount == 0)
        {
            risk += 7;
        }

        if (telemetry.PasteCount > 3)
        {
            risk += 7;
        }

        if (telemetry.PointerMoveCount == 0)
        {
            risk += 4;
        }

        risk += ScoreBrowserSignals(telemetry.Browser);
        return Math.Min(risk, 45);
    }

    public RiskAssessment ScoreChallenge(
        BookingIntent intent,
        ChallengeSession challenge,
        VerifyChallengeRequest request,
        bool acceptedSolution,
        ChallengeProtocolAssessment protocol)
    {
        var telemetry = request.Telemetry;
        var solution = request.Solution;
        var riskSignals = new List<string>();
        var risk = Math.Min(intent.RiskScore, 50);

        // Carry previous context into the challenge decision, but cap it. A user
        // should be able to recover from one awkward drag; repeated failures and
        // repeated challenge inits are what raise confidence.
        risk += intent.ChallengeFailCount * 10;
        risk += Math.Max(0, intent.ChallengeInitCount - 1) * 4;
        risk += ScoreBrowserSignals(request.Browser);

        // A failed visual/protocol solution is strong evidence, but still one
        // signal in a larger decision. This keeps the policy risk-based rather
        // than making the slider a single binary truth source.
        if (!acceptedSolution)
        {
            risk += 40;
        }

        risk += protocol.RiskScore;
        riskSignals.AddRange(protocol.Signals);

        if (solution is null)
        {
            risk += 30;
        }
        else
        {
            // Extremely fast solves are suspicious because the active phase has a
            // randomized delay and the user must visually align the piece. The values
            // are still heuristic and should be calibrated from production telemetry.
            if (solution.TimeSpentMs is > 0 and < 450)
            {
                risk += 35;
            }
            else if (solution.TimeSpentMs is > 0 and < 850)
            {
                risk += 15;
            }

            var exactError = Math.Abs(solution.X - EffectiveTargetX(challenge));
            if (exactError == 0 && solution.TimeSpentMs < 1000)
            {
                risk += 8;
            }
        }

        if (telemetry is not null)
        {
            var modality = telemetry.Modality?.Trim().ToLowerInvariant();

            // Modality inconsistencies are weak signals. They catch simple scripts
            // without requiring fragile properties such as touch force/radius.
            if (modality == "touch" && request.Browser is { TouchCapable: false })
            {
                risk += 8;
                riskSignals.Add("touch_modality_on_non_touch_browser");
            }

            if (telemetry.Events.FocusLost)
            {
                risk += 5;
            }

            if (telemetry.Events.VisibilityChanged)
            {
                risk += 10;
            }

            if (telemetry.Events.PointerCancelled)
            {
                risk += 6;
            }
        }

        var trajectory = _trajectoryAnalyzer.Analyze(telemetry, solution, challenge);
        risk += trajectory.RiskScore;
        riskSignals.AddRange(trajectory.Signals);

        // Replayed movement traces are a strong automation hint. This comparison is
        // intentionally coarse and ticket-local, so it does not store raw telemetry.
        if (!string.IsNullOrWhiteSpace(trajectory.Fingerprint) &&
            string.Equals(intent.LastTrajectoryFingerprint, trajectory.Fingerprint, StringComparison.Ordinal))
        {
            risk += Math.Min(20, 12 + (intent.RepeatedTrajectoryCount * 4));
            riskSignals.Add("repeated_trajectory_fingerprint");
        }

        risk = Math.Clamp(risk, 0, 100);
        var decision = Decide(risk, acceptedSolution, intent.ChallengeFailCount);
        return new RiskAssessment(
            risk,
            Bucket(risk),
            decision.Decision,
            decision.ReasonCode,
            trajectory.Fingerprint,
            riskSignals);
    }

    private static int ScoreBrowserSignals(BrowserSignals? browser)
    {
        // Browser signals are deliberately low-weight. navigator.webdriver and
        // Headless user agents catch commodity automation, but targeted attackers
        // can patch them; they should never be treated as a security boundary.
        if (browser is null)
        {
            return 0;
        }

        var risk = 0;

        if (browser.WebDriver)
        {
            risk += 16;
        }

        if (browser.PluginsLength == 0 && !browser.TouchCapable)
        {
            risk += 5;
        }

        if (browser.LanguagesLength == 0)
        {
            risk += 8;
        }

        var userAgent = browser.UserAgent ?? "";
        if (userAgent.Contains("Headless", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("PhantomJS", StringComparison.OrdinalIgnoreCase))
        {
            risk += 25;
        }

        return risk;
    }

    private static int EffectiveTargetX(ChallengeSession challenge)
    {
        return challenge.Variant == ChallengeVariants.FollowUpShift
            ? challenge.FollowUpTargetX
            : challenge.TargetX;
    }

    private static (string Decision, string ReasonCode) Decide(int risk, bool acceptedSolution, int previousFailures)
    {
        // These thresholds are sample defaults. The design document recommends
        // shadow-mode calibration before production enforcement.
        if (risk >= 90)
        {
            return ("hard_denied", "critical_risk");
        }

        if (!acceptedSolution)
        {
            return previousFailures >= 2
                ? ("temporarily_denied", "too_many_attempts")
                : ("retry_challenge", "verification_uncertain");
        }

        if (risk < 55)
        {
            return ("allow", "risk_acceptable");
        }

        if (risk < 75)
        {
            return ("retry_challenge", "verification_uncertain");
        }

        return ("temporarily_denied", "too_many_attempts");
    }

    private static string Bucket(int risk) => risk switch
    {
        < 30 => "low",
        < 55 => "elevated",
        < 75 => "high",
        _ => "critical"
    };
}
