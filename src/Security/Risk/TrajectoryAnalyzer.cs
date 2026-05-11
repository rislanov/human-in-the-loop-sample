namespace HumanLoopBooking.Services;

// Evaluates slider movement as risk signals. The analyzer never decides by
// itself; it produces additive evidence for the broader risk engine.
public sealed class TrajectoryAnalyzer : ITrajectoryAnalyzer
{
    private const int MaxTrustedPoints = 80;

    public TrajectoryAssessment Analyze(
        ChallengeTelemetry? telemetry,
        ChallengeSolution? solution,
        ChallengeSession challenge)
    {
        var signals = new List<string>();
        var risk = 0;

        void AddRisk(int value, string signal)
        {
            risk += value;
            signals.Add(signal);
        }

        if (telemetry is null)
        {
            AddRisk(20, "missing_telemetry");
            return new TrajectoryAssessment(
                Math.Min(risk, 45),
                signals,
                TrajectoryMetrics.Empty,
                null);
        }

        var modality = NormalizeModality(telemetry.Modality);
        var isTouch = modality == "touch";
        var points = telemetry.Points
            .Take(MaxTrustedPoints)
            .Select(point => new TrajectorySample(point.X, point.Y, point.T, NormalizePhase(point.Phase), NormalizeState(point.State)))
            .ToArray();

        if (points.Length == 0)
        {
            AddRisk(24, "empty_trajectory");
            return new TrajectoryAssessment(
                Math.Min(risk, 45),
                signals,
                TrajectoryMetrics.Empty,
                null);
        }

        // The frontend records the drag lifecycle as down/move/up. Missing phases are
        // not fatal because old clients may omit them, but they make the trace weaker.
        var hasAnyPhase = points.Any(point => point.Phase is not null);
        if (hasAnyPhase)
        {
            if (points.All(point => point.Phase != "down"))
            {
                AddRisk(5, "missing_drag_start");
            }

            if (points.All(point => point.Phase != "up"))
            {
                AddRisk(5, "missing_drag_release");
            }
        }

        var hasAnyState = points.Any(point => point.State is not null);
        if (hasAnyState && points.All(point => point.State != "active"))
        {
            AddRisk(8, "missing_active_visual_state");
        }

        if (modality == "unknown")
        {
            AddRisk(4, "unknown_pointer_modality");
        }

        if (isTouch)
        {
            if (points.Length < 3)
            {
                AddRisk(8, "sparse_touch_trace");
            }
        }
        else if (points.Length < 8)
        {
            AddRisk(14, "sparse_pointer_trace");
        }

        // Convert raw points into movement segments so the rules can reason about
        // timing, distance, speed, straightness, and direction changes.
        var segments = BuildSegments(points);
        var movingSegments = segments.Where(segment => segment.Dt > 0).ToArray();
        var durationMs = Math.Max(0, points[^1].T - points[0].T);
        var netDistanceX = points[^1].X - points[0].X;
        var xSpan = points.Max(point => point.X) - points.Min(point => point.X);
        var yRange = points.Max(point => point.Y) - points.Min(point => point.Y);
        var pathLength = segments.Sum(segment => segment.Distance);
        var correctionCount = CountDirectionChanges(segments);
        var pauseCount = movingSegments.Count(segment => segment.Dt >= 180);
        var intervalCv = CoefficientOfVariation(movingSegments.Select(segment => (double)segment.Dt));
        var stepCv = CoefficientOfVariation(movingSegments.Select(segment => (double)Math.Abs(segment.Dx)).Where(value => value > 0));
        var speedCv = CoefficientOfVariation(movingSegments.Select(segment => segment.Speed).Where(value => value > 0));
        var accelerationSignChanges = CountAccelerationSignChanges(movingSegments);
        var verticalDirectionChanges = CountVerticalDirectionChanges(segments);
        var distinctYBuckets = DistinctBucketCount(points.Select(point => (double)point.Y), 3);
        var maxJumpX = segments.Count == 0 ? 0 : segments.Max(segment => Math.Abs(segment.Dx));
        var maxSpeed = movingSegments.Length == 0 ? 0 : movingSegments.Max(segment => segment.Speed);
        var straightness = Math.Abs(netDistanceX) > 0 ? pathLength / Math.Abs(netDistanceX) : double.PositiveInfinity;

        // Timestamps are supplied by the browser and are easy to forge. We only use
        // impossible or overly regular timing as risk signals, never as proof.
        var nonMonotonicTimestamps = segments.Count(segment => segment.Dt < 0);
        if (nonMonotonicTimestamps > 0)
        {
            AddRisk(12, "non_monotonic_timestamps");
        }

        var nearZeroIntervals = segments.Count(segment => segment.Dt is >= 0 and <= 1);
        if (segments.Count >= 8 && nearZeroIntervals > segments.Count / 2)
        {
            AddRisk(7, "compressed_event_timing");
        }

        if (movingSegments.Length >= 8 && DistinctBucketCount(movingSegments.Select(segment => (double)segment.Dt), 5) <= 2)
        {
            AddRisk(6, "repeated_event_intervals");
        }

        if (solution is not null)
        {
            var finalError = Math.Abs(points[^1].X - solution.X);
            if (finalError > Math.Max(20, challenge.Tolerance * 2))
            {
                AddRisk(10, "solution_does_not_match_trace_end");
            }

            if (durationMs > 0 && solution.TimeSpentMs > 0)
            {
                var durationDelta = Math.Abs(solution.TimeSpentMs - durationMs);
                if (durationDelta > 400 && durationDelta > solution.TimeSpentMs * 0.35)
                {
                    AddRisk(8, "solution_duration_mismatch");
                }
            }

            var exactTargetError = Math.Abs(solution.X - challenge.TargetX);
            if (!isTouch &&
                exactTargetError <= 1 &&
                correctionCount == 0 &&
                yRange <= 1 &&
                durationMs is > 0 and < 1500 &&
                points.Length >= 8)
            {
                AddRisk(6, "perfect_low_noise_finish");
            }
        }

        if (netDistanceX <= 0)
        {
            AddRisk(18, "no_forward_progress");
        }

        // Desktop pointer traces that are perfectly flat and straight are cheap to
        // synthesize. Touch input is intentionally scored more gently.
        if (!isTouch && points.Length >= 8)
        {
            if (yRange == 0)
            {
                AddRisk(8, "flat_y_axis");
            }
            else if (yRange <= 1)
            {
                AddRisk(5, "near_flat_y_axis");
            }

            if (straightness < 1.01)
            {
                AddRisk(6, "nearly_perfect_line");
            }

            // The handle now has a small vertical lane. If a mouse trace stays on a
            // single horizontal plane, it is more suspicious than a normal straight drag.
            if (yRange <= 1 && straightness < 1.01)
            {
                AddRisk(18, "single_plane_drag");
            }

            if (points.Length >= 12 && yRange <= 2 && verticalDirectionChanges == 0)
            {
                AddRisk(10, "missing_second_plane_variation");
            }
        }

        if (solution is not null &&
            !isTouch &&
            points.Length >= 8 &&
            yRange <= 1 &&
            straightness < 1.01 &&
            correctionCount == 0 &&
            Math.Abs(solution.X - challenge.TargetX) <= 1)
        {
            // This is the classic naive browser-agent shape: find the right X, then
            // move there in one clean horizontal line with no second-plane behavior.
            AddRisk(20, "single_plane_precise_finish");
        }

        if (movingSegments.Length >= 8 &&
            stepCv is >= 0 and < 0.10 &&
            DistinctBucketCount(movingSegments.Select(segment => (double)Math.Abs(segment.Dx)), 2) <= 2)
        {
            AddRisk(8, "repeated_step_size");
        }

        if (movingSegments.Length >= 8 &&
            intervalCv is >= 0 and < 0.12 &&
            speedCv is >= 0 and < 0.14)
        {
            AddRisk(8, "constant_speed_profile");
        }

        if (!isTouch &&
            movingSegments.Length >= 10 &&
            accelerationSignChanges < 2 &&
            speedCv is >= 0 and < 0.20)
        {
            AddRisk(5, "weak_acceleration_profile");
        }

        // Very large coordinate jumps are suspicious for mouse/trackpad input. Touch
        // can be bursty, so the touch threshold is intentionally more forgiving.
        var jumpThreshold = isTouch ? challenge.Width * 0.75 : challenge.Width * 0.55;
        if (maxJumpX > jumpThreshold)
        {
            AddRisk(isTouch ? 5 : 10, "large_coordinate_jump");
        }

        var speedThreshold = isTouch ? 8.0 : 4.5;
        if (maxSpeed > speedThreshold)
        {
            AddRisk(isTouch ? 5 : 10, "impossible_pointer_speed");
        }

        var metrics = new TrajectoryMetrics(
            points.Length,
            durationMs,
            netDistanceX,
            xSpan,
            yRange,
            correctionCount,
            pauseCount,
            intervalCv,
            stepCv,
            speedCv,
            maxJumpX,
            maxSpeed,
            accelerationSignChanges,
            verticalDirectionChanges,
            distinctYBuckets);

        return new TrajectoryAssessment(
            Math.Clamp(risk, 0, 60),
            signals,
            metrics,
            BuildFingerprint(points, modality));
    }

    private static IReadOnlyList<TrajectorySegment> BuildSegments(IReadOnlyList<TrajectorySample> points)
    {
        return points
            .Zip(points.Skip(1), (left, right) =>
            {
                var dx = right.X - left.X;
                var dy = right.Y - left.Y;
                var dt = right.T - left.T;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));

                return new TrajectorySegment(dx, dy, dt, distance);
            })
            .ToArray();
    }

    private static int CountDirectionChanges(IReadOnlyList<TrajectorySegment> segments)
    {
        var previousDirection = 0;
        var changes = 0;

        foreach (var segment in segments)
        {
            var direction = Math.Sign(segment.Dx);
            if (direction == 0)
            {
                continue;
            }

            if (previousDirection != 0 && direction != previousDirection)
            {
                changes++;
            }

            previousDirection = direction;
        }

        return changes;
    }

    private static int CountVerticalDirectionChanges(IReadOnlyList<TrajectorySegment> segments)
    {
        var previousDirection = 0;
        var changes = 0;

        foreach (var segment in segments)
        {
            var direction = Math.Sign(segment.Dy);
            if (direction == 0)
            {
                continue;
            }

            if (previousDirection != 0 && direction != previousDirection)
            {
                changes++;
            }

            previousDirection = direction;
        }

        return changes;
    }

    private static int CountAccelerationSignChanges(IReadOnlyList<TrajectorySegment> segments)
    {
        var speeds = segments
            .Where(segment => segment.Dt > 0)
            .Select(segment => segment.Speed)
            .ToArray();

        if (speeds.Length < 3)
        {
            return 0;
        }

        var previousDirection = 0;
        var changes = 0;
        for (var index = 1; index < speeds.Length; index++)
        {
            var delta = speeds[index] - speeds[index - 1];
            var direction = Math.Sign(delta);
            if (direction == 0)
            {
                continue;
            }

            if (previousDirection != 0 && direction != previousDirection)
            {
                changes++;
            }

            previousDirection = direction;
        }

        return changes;
    }

    private static double CoefficientOfVariation(IEnumerable<double> values)
    {
        var sample = values.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (sample.Length < 2)
        {
            return -1;
        }

        var mean = sample.Average();
        if (Math.Abs(mean) < 0.001)
        {
            return -1;
        }

        var variance = sample.Sum(value => Math.Pow(value - mean, 2)) / sample.Length;
        return Math.Sqrt(variance) / Math.Abs(mean);
    }

    private static int DistinctBucketCount(IEnumerable<double> values, int bucketSize)
    {
        return values
            .Select(value => (int)Math.Round(value / bucketSize, MidpointRounding.AwayFromZero))
            .Distinct()
            .Count();
    }

    private static string BuildFingerprint(IReadOnlyList<TrajectorySample> points, string modality)
    {
        var duration = Math.Max(1, points[^1].T - points[0].T);
        var minX = points.Min(point => point.X);
        var xRange = Math.Max(1, points.Max(point => point.X) - minX);
        var minY = points.Min(point => point.Y);
        var step = Math.Max(1, points.Count / 12);

        // The fingerprint stores shape, not raw coordinates. It helps detect replayed
        // synthetic traces without keeping high-resolution behavioral telemetry.
        var shape = points
            .Where((_, index) => index % step == 0)
            .Select(point =>
            {
                var normalizedX = (int)Math.Round(((point.X - minX) / (double)xRange) * 20);
                var normalizedY = (int)Math.Round((point.Y - minY) / 4.0);
                var normalizedT = (int)Math.Round(((point.T - points[0].T) / (double)duration) * 20);
                var state = point.State == "active" ? "a" : "p";
                return $"{normalizedX}:{normalizedY}:{normalizedT}:{state}";
            });

        return SecurityHelpers.Hash(string.Join('|', modality, points.Count / 4, shape));
    }

    private static string NormalizeModality(string? modality)
    {
        return modality?.Trim().ToLowerInvariant() switch
        {
            "mouse" => "mouse",
            "touch" => "touch",
            "pen" => "pen",
            _ => "unknown"
        };
    }

    private static string? NormalizePhase(string? phase)
    {
        return phase?.Trim().ToLowerInvariant() switch
        {
            "down" => "down",
            "move" => "move",
            "up" => "up",
            _ => null
        };
    }

    private static string? NormalizeState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "pre_active" => "pre_active",
            "active" => "active",
            _ => null
        };
    }

    private sealed record TrajectorySample(int X, int Y, int T, string? Phase, string? State);

    private sealed record TrajectorySegment(int Dx, int Dy, int Dt, double Distance)
    {
        public double Speed => Dt > 0 ? Distance / Dt : 0;
    }
}

public sealed record TrajectoryAssessment(
    int RiskScore,
    IReadOnlyList<string> Signals,
    TrajectoryMetrics Metrics,
    string? Fingerprint);

public sealed record TrajectoryMetrics(
    int PointCount,
    int DurationMs,
    int NetDistanceX,
    int XSpan,
    int YRange,
    int CorrectionCount,
    int PauseCount,
    double IntervalCoefficientOfVariation,
    double StepCoefficientOfVariation,
    double SpeedCoefficientOfVariation,
    int MaxJumpX,
    double MaxSpeed,
    int AccelerationSignChanges,
    int VerticalDirectionChanges,
    int DistinctYBuckets)
{
    public static TrajectoryMetrics Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        -1,
        -1,
        -1,
        0,
        0,
        0,
        0,
        0);
}
