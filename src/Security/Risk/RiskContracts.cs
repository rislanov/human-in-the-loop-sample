namespace HumanLoopBooking.Services;

// Risk scoring is intentionally exposed as a contract. The sample booking flow
// can use this implementation in-process, while a production system could move
// it behind a service boundary or replace the weights with calibrated models.
public interface IRiskEngine
{
    int ScoreForm(FormTelemetry? telemetry);

    RiskAssessment ScoreChallenge(
        BookingIntent intent,
        ChallengeSession challenge,
        VerifyChallengeRequest request,
        bool acceptedSolution,
        ChallengeProtocolAssessment protocol);
}

// Trajectory analysis is its own contract because it is the most likely area to
// be tuned, A/B tested, or split into desktop/mobile implementations.
public interface ITrajectoryAnalyzer
{
    TrajectoryAssessment Analyze(
        ChallengeTelemetry? telemetry,
        ChallengeSolution? solution,
        ChallengeSession challenge);
}
