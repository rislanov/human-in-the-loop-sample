using System.Security.Cryptography;
using System.Text;

namespace HumanLoopBooking.Services;

public sealed class BookingFlowStore
{
    public const string VerificationCookieName = "__booking_verification";

    private static readonly TimeSpan EmailTicketTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ValidationTokenTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EmailSendWindow = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly RiskEngine _riskEngine;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingFlowStore> _logger;
    private readonly Dictionary<Guid, BookingIntent> _intents = [];
    private readonly Dictionary<string, Guid> _ticketToIntent = [];
    private readonly Dictionary<string, Guid> _sessionToIntent = [];
    private readonly Dictionary<string, ChallengeSession> _challenges = [];
    private readonly Dictionary<string, ValidationGrant> _validationGrants = [];
    private readonly Dictionary<string, DateTimeOffset> _emailWindow = [];
    private readonly List<BookingSlot> _slots = [];
    private readonly List<AuditEvent> _audit = [];

    public BookingFlowStore(RiskEngine riskEngine, TimeProvider clock, ILogger<BookingFlowStore> logger)
    {
        _riskEngine = riskEngine;
        _clock = clock;
        _logger = logger;
        SeedSlots();
    }

    public StoreResult<(BookingIntent Intent, EmailPreview Preview)> CreateBookingIntent(
        CreateBookingIntentRequest request,
        string baseUrl)
    {
        var validation = ValidateBookingRequest(request);
        if (validation is not null)
        {
            return StoreResult<(BookingIntent Intent, EmailPreview Preview)>.Fail(validation);
        }

        var now = _clock.GetUtcNow();
        var email = NormalizeEmail(request.Email!);
        var emailHash = Hash(email);
        var ticket = NewToken("et");
        var risk = _riskEngine.ScoreForm(request.Telemetry);

        lock (_gate)
        {
            CleanupExpired(now);

            if (_emailWindow.TryGetValue(emailHash, out var lastSentAt) && now - lastSentAt < EmailSendWindow)
            {
                risk += 10;
            }

            var intent = new BookingIntent
            {
                BookingData = new BookingData
                {
                    Name = request.Name!.Trim(),
                    DateOfBirth = request.DateOfBirth?.Trim() ?? "",
                    NumberOfApplicants = Math.Clamp(request.NumberOfApplicants ?? 1, 1, 8),
                    PhoneNumber = request.PhoneNumber?.Trim() ?? "",
                    Email = email,
                    PassportNumber = request.PassportNumber?.Trim() ?? "",
                    Citizenship = request.Citizenship?.Trim() ?? "",
                    ResidencePermit = request.ResidencePermit?.Trim() ?? ""
                },
                EmailHash = emailHash,
                EmailTicket = ticket,
                CreatedAt = now,
                ExpiresAt = now.Add(EmailTicketTtl),
                RiskScore = Math.Clamp(risk, 0, 100)
            };

            _intents[intent.Id] = intent;
            _ticketToIntent[ticket] = intent.Id;
            _emailWindow[emailHash] = now;

            var preview = new EmailPreview(
                email,
                "Booking confirmation",
                $"{baseUrl}/confirm-booking?t={Uri.EscapeDataString(ticket)}",
                intent.ExpiresAt);

            Audit("booking_intent_created", intent.Id, null, Bucket(intent.RiskScore), "email_sent", "Opaque email ticket issued.");
            return StoreResult<(BookingIntent Intent, EmailPreview Preview)>.Ok((intent, preview));
        }
    }

    public StoreResult<(BookingIntent Intent, string VerificationSessionId)> ConfirmEmailTicket(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return StoreResult<(BookingIntent Intent, string VerificationSessionId)>.Fail("missing_ticket");
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            CleanupExpired(now);

            if (!_ticketToIntent.TryGetValue(ticket, out var intentId) || !_intents.TryGetValue(intentId, out var intent))
            {
                return StoreResult<(BookingIntent Intent, string VerificationSessionId)>.Fail("invalid_ticket");
            }

            if (now > intent.ExpiresAt)
            {
                return StoreResult<(BookingIntent Intent, string VerificationSessionId)>.Fail("ticket_expired");
            }

            if (intent.Status is BookingIntentStatus.BookingFinalized or BookingIntentStatus.HardDenied)
            {
                return StoreResult<(BookingIntent Intent, string VerificationSessionId)>.Fail("invalid_state");
            }

            var sessionId = NewToken("vs");
            intent.VerificationSessionId = sessionId;
            intent.EmailConfirmedAt = now;
            intent.Status = BookingIntentStatus.EmailConfirmed;
            _sessionToIntent[sessionId] = intent.Id;

            Audit("email_confirmed", intent.Id, null, Bucket(intent.RiskScore), "email_confirmed", "Email ticket exchanged for browser verification session.");
            return StoreResult<(BookingIntent Intent, string VerificationSessionId)>.Ok((intent, sessionId));
        }
    }

    public StoreResult<ChallengeSession> InitializeChallenge(string? verificationSessionId, string? cookieSessionId)
    {
        if (string.IsNullOrWhiteSpace(verificationSessionId) || verificationSessionId != cookieSessionId)
        {
            return StoreResult<ChallengeSession>.Fail("invalid_verification_session");
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            CleanupExpired(now);

            if (!_sessionToIntent.TryGetValue(verificationSessionId, out var intentId) || !_intents.TryGetValue(intentId, out var intent))
            {
                return StoreResult<ChallengeSession>.Fail("invalid_verification_session");
            }

            if (intent.Status is not (BookingIntentStatus.EmailConfirmed or BookingIntentStatus.ChallengeStarted))
            {
                return StoreResult<ChallengeSession>.Fail("invalid_state");
            }

            if (intent.ChallengeInitCount >= 5 || intent.ChallengeFailCount >= 3)
            {
                intent.Status = BookingIntentStatus.TemporarilyDenied;
                Audit("rate_limit_triggered", intent.Id, null, Bucket(intent.RiskScore), "temporarily_denied", "Challenge attempt limit reached.");
                return StoreResult<ChallengeSession>.Fail("too_many_attempts");
            }

            var challenge = new ChallengeSession
            {
                Id = NewToken("ch"),
                IntentId = intent.Id,
                VerificationSessionId = verificationSessionId,
                CreatedAt = now,
                ExpiresAt = now.Add(ChallengeTtl),
                TargetX = RandomNumberGenerator.GetInt32(126, 288),
                PieceY = RandomNumberGenerator.GetInt32(48, 102)
            };

            intent.ChallengeInitCount++;
            intent.Status = BookingIntentStatus.ChallengeStarted;
            _challenges[challenge.Id] = challenge;

            Audit("challenge_initialized", intent.Id, challenge.Id, Bucket(intent.RiskScore), "challenge_started", "One-time slider challenge created.");
            return StoreResult<ChallengeSession>.Ok(challenge);
        }
    }

    public StoreResult<ChallengeSession> GetChallengeForAsset(string challengeId)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            CleanupExpired(now);
            return _challenges.TryGetValue(challengeId, out var challenge)
                ? StoreResult<ChallengeSession>.Ok(challenge)
                : StoreResult<ChallengeSession>.Fail("challenge_not_found");
        }
    }

    public StoreResult<ChallengeVerifyResult> VerifyChallenge(VerifyChallengeRequest request, string? cookieSessionId)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId) || request.Solution is null)
        {
            return StoreResult<ChallengeVerifyResult>.Fail("invalid_request");
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            CleanupExpired(now);

            if (!_challenges.TryGetValue(request.ChallengeId, out var challenge) ||
                !_intents.TryGetValue(challenge.IntentId, out var intent))
            {
                return StoreResult<ChallengeVerifyResult>.Fail("challenge_not_found");
            }

            if (challenge.VerificationSessionId != cookieSessionId)
            {
                return StoreResult<ChallengeVerifyResult>.Fail("invalid_verification_session");
            }

            if (challenge.Consumed || now > challenge.ExpiresAt)
            {
                return StoreResult<ChallengeVerifyResult>.Fail("challenge_expired_or_consumed");
            }

            challenge.Consumed = true;
            var accurate = Math.Abs(request.Solution.X - challenge.TargetX) <= challenge.Tolerance;
            var risk = _riskEngine.ScoreChallenge(intent, challenge, request, accurate);
            intent.RiskScore = Math.Max(intent.RiskScore, risk.Score);

            if (risk.Decision == "allow")
            {
                var token = NewToken("vt");
                var grant = new ValidationGrant
                {
                    Token = token,
                    IntentId = intent.Id,
                    VerificationSessionId = challenge.VerificationSessionId,
                    EmailHash = intent.EmailHash,
                    CreatedAt = now,
                    ExpiresAt = now.Add(ValidationTokenTtl)
                };

                _validationGrants[token] = grant;
                intent.Status = BookingIntentStatus.SlotSelectionAllowed;
                intent.ChallengePassedAt = now;

                Audit("challenge_verified", intent.Id, challenge.Id, risk.Bucket, "allow", "Validation token issued.");
                return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                    "allow",
                    token,
                    (int)ValidationTokenTtl.TotalSeconds,
                    null,
                    null));
            }

            if (risk.Decision == "retry_challenge")
            {
                intent.ChallengeFailCount++;
                intent.Status = BookingIntentStatus.EmailConfirmed;
                Audit("challenge_failed", intent.Id, challenge.Id, risk.Bucket, "retry_challenge", "Challenge failed or risk was uncertain.");
                return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                    "retry_challenge",
                    null,
                    null,
                    3,
                    risk.ReasonCode));
            }

            intent.ChallengeFailCount++;
            intent.Status = BookingIntentStatus.TemporarilyDenied;
            Audit("risk_decision_made", intent.Id, challenge.Id, risk.Bucket, "temporarily_denied", "Risk threshold exceeded.");
            return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                "temporarily_denied",
                null,
                null,
                600,
                risk.ReasonCode));
        }
    }

    public StoreResult<IReadOnlyList<BookingSlot>> GetAvailableSlots(string? validationToken, string? cookieSessionId)
    {
        var grantResult = ValidateGrant(validationToken, cookieSessionId, consume: false);
        if (!grantResult.Success)
        {
            return StoreResult<IReadOnlyList<BookingSlot>>.Fail(grantResult.ErrorCode);
        }

        lock (_gate)
        {
            var slots = _slots
                .Where(slot => !slot.IsBooked)
                .OrderBy(slot => slot.Date)
                .ThenBy(slot => slot.Time)
                .ToArray();

            return StoreResult<IReadOnlyList<BookingSlot>>.Ok(slots);
        }
    }

    public StoreResult<FinalizedBooking> FinalizeBooking(FinalizeBookingRequest request, string? cookieSessionId)
    {
        if (string.IsNullOrWhiteSpace(request.SlotId))
        {
            return StoreResult<FinalizedBooking>.Fail("missing_slot");
        }

        lock (_gate)
        {
            var grantResult = ValidateGrantLocked(request.ValidationToken, cookieSessionId, consume: false);
            if (!grantResult.Success || grantResult.Value is null)
            {
                return StoreResult<FinalizedBooking>.Fail(grantResult.ErrorCode);
            }

            var grant = grantResult.Value;
            if (!_intents.TryGetValue(grant.IntentId, out var intent))
            {
                return StoreResult<FinalizedBooking>.Fail("invalid_state");
            }

            var slot = _slots.FirstOrDefault(candidate => candidate.Id == request.SlotId);
            if (slot is null)
            {
                return StoreResult<FinalizedBooking>.Fail("slot_not_found");
            }

            if (slot.IsBooked)
            {
                return StoreResult<FinalizedBooking>.Fail("slot_unavailable");
            }

            grant.Used = true;
            slot.IsBooked = true;
            intent.Status = BookingIntentStatus.BookingFinalized;
            intent.BookingFinalizedAt = _clock.GetUtcNow();

            var booking = new FinalizedBooking($"bk_{Guid.NewGuid():N}"[..15], slot);
            Audit("booking_finalized", intent.Id, null, Bucket(intent.RiskScore), "booked", $"Slot {slot.Id} finalized.");
            return StoreResult<FinalizedBooking>.Ok(booking);
        }
    }

    public IReadOnlyList<AuditEvent> GetRecentAuditEvents()
    {
        lock (_gate)
        {
            return _audit.TakeLast(50).ToArray();
        }
    }

    private StoreResult<ValidationGrant> ValidateGrant(string? validationToken, string? cookieSessionId, bool consume)
    {
        lock (_gate)
        {
            return ValidateGrantLocked(validationToken, cookieSessionId, consume);
        }
    }

    private StoreResult<ValidationGrant> ValidateGrantLocked(string? validationToken, string? cookieSessionId, bool consume)
    {
        if (string.IsNullOrWhiteSpace(validationToken))
        {
            return StoreResult<ValidationGrant>.Fail("missing_validation_token");
        }

        CleanupExpired(_clock.GetUtcNow());

        if (!_validationGrants.TryGetValue(validationToken, out var grant))
        {
            return StoreResult<ValidationGrant>.Fail("invalid_validation_token");
        }

        if (grant.Used || _clock.GetUtcNow() > grant.ExpiresAt)
        {
            return StoreResult<ValidationGrant>.Fail("validation_token_expired_or_used");
        }

        if (grant.VerificationSessionId != cookieSessionId)
        {
            return StoreResult<ValidationGrant>.Fail("invalid_verification_session");
        }

        if (!_intents.TryGetValue(grant.IntentId, out var intent) || intent.Status != BookingIntentStatus.SlotSelectionAllowed)
        {
            return StoreResult<ValidationGrant>.Fail("invalid_state");
        }

        if (consume)
        {
            grant.Used = true;
        }

        return StoreResult<ValidationGrant>.Ok(grant);
    }

    private static string? ValidateBookingRequest(CreateBookingIntentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.ReenteredEmail) ||
            string.IsNullOrWhiteSpace(request.PassportNumber) ||
            string.IsNullOrWhiteSpace(request.Citizenship))
        {
            return "missing_required_fields";
        }

        if (!NormalizeEmail(request.Email).Equals(NormalizeEmail(request.ReenteredEmail), StringComparison.Ordinal))
        {
            return "email_mismatch";
        }

        if (!request.Email.Contains('@', StringComparison.Ordinal) || request.Email.Length > 254)
        {
            return "invalid_email";
        }

        return null;
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var ticket in _ticketToIntent
                     .Where(pair => !_intents.TryGetValue(pair.Value, out var intent) || intent.ExpiresAt < now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _ticketToIntent.Remove(ticket);
        }

        foreach (var challengeId in _challenges
                     .Where(pair => pair.Value.ExpiresAt.AddMinutes(5) < now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _challenges.Remove(challengeId);
        }

        foreach (var token in _validationGrants
                     .Where(pair => pair.Value.ExpiresAt.AddMinutes(5) < now || pair.Value.Used)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _validationGrants.Remove(token);
        }

        foreach (var emailHash in _emailWindow
                     .Where(pair => now - pair.Value > EmailSendWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _emailWindow.Remove(emailHash);
        }
    }

    private void Audit(string eventType, Guid? intentId, string? challengeId, string? riskBucket, string? decision, string? message)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = _clock.GetUtcNow(),
            EventType = eventType,
            IntentId = intentId,
            ChallengeId = challengeId,
            RiskBucket = riskBucket,
            Decision = decision,
            Message = message
        };

        _audit.Add(auditEvent);
        _logger.LogInformation(
            "Audit event {EventType} intent={IntentId} challenge={ChallengeId} bucket={RiskBucket} decision={Decision}",
            eventType,
            intentId,
            challengeId,
            riskBucket,
            decision);
    }

    private void SeedSlots()
    {
        var seed = new[]
        {
            ("slot_0701_1130", new DateOnly(2026, 7, 1), new TimeOnly(11, 30)),
            ("slot_0708_0900", new DateOnly(2026, 7, 8), new TimeOnly(9, 0)),
            ("slot_0708_0930", new DateOnly(2026, 7, 8), new TimeOnly(9, 30)),
            ("slot_0708_1000", new DateOnly(2026, 7, 8), new TimeOnly(10, 0)),
            ("slot_0708_1030", new DateOnly(2026, 7, 8), new TimeOnly(10, 30)),
            ("slot_0708_1100", new DateOnly(2026, 7, 8), new TimeOnly(11, 0)),
            ("slot_0708_1130", new DateOnly(2026, 7, 8), new TimeOnly(11, 30)),
            ("slot_0715_0900", new DateOnly(2026, 7, 15), new TimeOnly(9, 0)),
            ("slot_0715_0930", new DateOnly(2026, 7, 15), new TimeOnly(9, 30)),
            ("slot_0715_1000", new DateOnly(2026, 7, 15), new TimeOnly(10, 0)),
            ("slot_0715_1030", new DateOnly(2026, 7, 15), new TimeOnly(10, 30)),
            ("slot_0715_1100", new DateOnly(2026, 7, 15), new TimeOnly(11, 0)),
            ("slot_0715_1130", new DateOnly(2026, 7, 15), new TimeOnly(11, 30))
        };

        _slots.AddRange(seed.Select(slot => new BookingSlot
        {
            Id = slot.Item1,
            Date = slot.Item2,
            Time = slot.Item3,
            SlotGroup = "serbian-permit-july-2026"
        }));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NewToken(string prefix)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{prefix}_{token}";
    }

    private static string Bucket(int risk) => risk switch
    {
        < 30 => "low",
        < 55 => "elevated",
        < 75 => "high",
        _ => "critical"
    };
}
