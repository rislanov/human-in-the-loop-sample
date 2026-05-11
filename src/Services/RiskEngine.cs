namespace HumanLoopBooking.Services;

public sealed class RiskEngine
{
    public int ScoreForm(FormTelemetry? telemetry)
    {
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
        bool accurate)
    {
        var telemetry = request.Telemetry;
        var solution = request.Solution;
        var risk = Math.Min(intent.RiskScore, 50);

        risk += intent.ChallengeFailCount * 10;
        risk += Math.Max(0, intent.ChallengeInitCount - 1) * 4;
        risk += ScoreBrowserSignals(request.Browser);

        if (!accurate)
        {
            risk += 40;
        }

        if (solution is null)
        {
            risk += 30;
        }
        else
        {
            if (solution.TimeSpentMs is > 0 and < 450)
            {
                risk += 35;
            }
            else if (solution.TimeSpentMs is > 0 and < 850)
            {
                risk += 15;
            }

            var exactError = Math.Abs(solution.X - challenge.TargetX);
            if (exactError == 0 && solution.TimeSpentMs < 1000)
            {
                risk += 8;
            }
        }

        if (telemetry is null)
        {
            risk += 20;
        }
        else
        {
            var pointCount = telemetry.Points.Count;
            var modality = telemetry.Modality?.Trim().ToLowerInvariant();

            if (modality == "mouse" && pointCount < 8)
            {
                risk += 14;
            }
            else if (modality == "touch" && pointCount < 3)
            {
                risk += 8;
            }
            else if (pointCount == 0)
            {
                risk += 18;
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

            if (LooksSynthetic(telemetry.Points))
            {
                risk += 14;
            }
        }

        risk = Math.Clamp(risk, 0, 100);
        var decision = Decide(risk, accurate, intent.ChallengeFailCount);
        return new RiskAssessment(risk, Bucket(risk), decision.Decision, decision.ReasonCode);
    }

    private static int ScoreBrowserSignals(BrowserSignals? browser)
    {
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

    private static bool LooksSynthetic(IReadOnlyList<TelemetryPoint> points)
    {
        if (points.Count < 6)
        {
            return false;
        }

        var identicalY = points.Select(point => point.Y).Distinct().Count() == 1;
        var intervals = points.Zip(points.Skip(1), (left, right) => Math.Max(0, right.T - left.T)).ToArray();
        var repeatedIntervals = intervals.Length > 4 && intervals.Distinct().Count() <= 2;
        var repeatedStep = points.Zip(points.Skip(1), (left, right) => right.X - left.X)
            .Where(delta => delta > 0)
            .Distinct()
            .Count() <= 2;

        return identicalY && repeatedIntervals && repeatedStep;
    }

    private static (string Decision, string ReasonCode) Decide(int risk, bool accurate, int previousFailures)
    {
        if (risk >= 90)
        {
            return ("hard_denied", "critical_risk");
        }

        if (!accurate)
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
