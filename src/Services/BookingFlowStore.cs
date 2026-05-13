namespace HumanLoopBooking.Services;

// BookingFlowStore is the demo application's state-machine coordinator.
// It owns booking-specific concerns (email tickets, browser sessions, validation
// grants, slot finalization, rate limits, and audit events) and delegates the
// reusable security work to IChallengeSessionFactory, IChallengeProtocolValidator,
// and IRiskEngine. In production this class would usually be split between a
// database-backed booking service and a Redis-backed temporary-state service.
public sealed class BookingFlowStore
{
    public const string VerificationCookieName = "__booking_verification";

    private static readonly TimeSpan EmailTicketTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan IntentTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VerificationSessionTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ValidationTokenTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AssetTokenTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan EmailSendWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AbuseWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SlotPressureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SlotPressureSnapshotTtl = TimeSpan.FromMinutes(5);

    private const int MaxEmailSendsPerEmail = 1;
    private const int MaxEmailSendsPerIp = 8;
    private const int MaxEmailSendsPerSubnet = 20;
    private const int MaxChallengeInitsPerTicket = 5;
    private const int MaxFailedChallengesPerTicket = 3;
    private const int MaxChallengeInitsPerIp = 30;
    private const int MaxChallengeInitsPerDevice = 12;
    private const int MaxChallengeVerifiesPerIp = 45;
    private const int MaxValidationTokensPerEmail = 3;
    private const int MaxValidationTokensPerIp = 12;
    private const int MaxValidationTokensPerDevice = 5;
    private const int MaxSlotGroupFinalizationsPerWindow = 8;
    private const int MaxSingleSlotFinalizationsPerWindow = 3;
    private const string DefaultSlotGroup = "serbian-permit-july-2026";

    private const string SlotsKey = "booking:slots";
    private const string AuditKey = "booking:audit";

    private readonly ITemporarySecurityStateStore _cache;
    private readonly IRiskEngine _riskEngine;
    private readonly IChallengeSessionFactory _challengeFactory;
    private readonly IChallengeProtocolValidator _challengeProtocolValidator;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingFlowStore> _logger;
    private readonly BotDefenseOptions _options;

    public BookingFlowStore(
        ITemporarySecurityStateStore cache,
        IRiskEngine riskEngine,
        IChallengeSessionFactory challengeFactory,
        IChallengeProtocolValidator challengeProtocolValidator,
        TimeProvider clock,
        ILogger<BookingFlowStore> logger,
        Microsoft.Extensions.Options.IOptions<BotDefenseOptions> options)
    {
        _cache = cache;
        _riskEngine = riskEngine;
        _challengeFactory = challengeFactory;
        _challengeProtocolValidator = challengeProtocolValidator;
        _clock = clock;
        _logger = logger;
        _options = options.Value;

        lock (_cache.SyncRoot)
        {
            if (!_cache.TryGet<List<BookingSlot>>(SlotsKey, out _))
            {
                _cache.Set(SlotsKey, SeedSlots());
            }
        }
    }

    public StoreResult<(BookingIntent Intent, EmailPreview Preview)> CreateBookingIntent(
        CreateBookingIntentRequest request,
        string baseUrl,
        RequestSecurityContext context)
    {
        var validation = ValidateBookingRequest(request);
        if (validation is not null)
        {
            return StoreResult<(BookingIntent Intent, EmailPreview Preview)>.Fail(validation);
        }

        var now = _clock.GetUtcNow();
        var email = NormalizeEmail(request.Email!);
        var emailHash = SecurityHelpers.Hash(email);
        var emailDomainHash = HashEmailDomain(email);
        var ticket = SecurityHelpers.NewToken("et");
        var risk = _riskEngine.ScoreForm(request.Telemetry);

        lock (_cache.SyncRoot)
        {
            if (!TryConsumeLimit($"rate:email:hash:{emailHash}", MaxEmailSendsPerEmail, EmailSendWindow) ||
                !TryConsumeOptionalLimit($"rate:email:ip:{context.IpHash}", MaxEmailSendsPerIp, EmailSendWindow) ||
                !TryConsumeOptionalLimit($"rate:email:subnet:{context.SubnetHash}", MaxEmailSendsPerSubnet, EmailSendWindow) ||
                !TryConsumeOptionalLimit($"rate:email:domain:{emailDomainHash}", 12, EmailSendWindow))
            {
                Audit("rate_limit_triggered", null, null, null, null, null, "email_rate_limited", context);
                return StoreResult<(BookingIntent Intent, EmailPreview Preview)>.Fail("email_rate_limited");
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
                CreatedIpHash = context.IpHash,
                CreatedDeviceHash = context.DeviceHash,
                RiskScore = Math.Clamp(risk, 0, 100)
            };

            SaveIntent(intent);
            _cache.Set(TicketKey(ticket), new CacheReference(intent.Id.ToString()), EmailTicketTtl);

            var preview = new EmailPreview(
                email,
                "Booking confirmation",
                $"{baseUrl}/confirm-booking?t={Uri.EscapeDataString(ticket)}",
                intent.ExpiresAt);

            Audit("booking_intent_created", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "email_sent", "Opaque email ticket issued.", context);
            return StoreResult<(BookingIntent Intent, EmailPreview Preview)>.Ok((intent, preview));
        }
    }

    public StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)> ConfirmEmailTicket(
        ConfirmEmailRequest request,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            return StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)>.Fail("missing_ticket");
        }

        var now = _clock.GetUtcNow();
        lock (_cache.SyncRoot)
        {
            if (!_cache.TryGet<CacheReference>(TicketKey(request.Ticket), out var reference) ||
                reference is null ||
                !Guid.TryParse(reference.Value, out var intentId) ||
                !TryGetIntent(intentId, out var intent))
            {
                return StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)>.Fail("invalid_ticket");
            }

            if (now > intent.ExpiresAt)
            {
                _cache.Delete(TicketKey(request.Ticket));
                return StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)>.Fail("ticket_expired");
            }

            if (intent.Status is BookingIntentStatus.BookingFinalized or BookingIntentStatus.HardDenied or BookingIntentStatus.SlotSelectionAllowed)
            {
                return StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)>.Fail("invalid_state");
            }

            var session = new VerificationSession
            {
                Id = SecurityHelpers.NewToken("vs"),
                IntentId = intent.Id,
                Nonce = SecurityHelpers.NewToken("sn"),
                IpHash = context.IpHash,
                SubnetHash = context.SubnetHash,
                DeviceHash = context.DeviceHash ?? SecurityHelpers.HashDevice(request.Browser),
                CreatedAt = now,
                ExpiresAt = now.Add(VerificationSessionTtl)
            };

            intent.VerificationSessionId = session.Id;
            intent.EmailConfirmedAt = now;
            intent.Status = BookingIntentStatus.EmailConfirmed;
            SaveIntent(intent);
            _cache.Set(SessionKey(session.Id), session, VerificationSessionTtl);

            Audit("email_confirmed", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "email_confirmed", "Email ticket exchanged for browser verification session.", context);
            return StoreResult<(BookingIntent Intent, string VerificationSessionId, string SessionNonce)>.Ok((intent, session.Id, session.Nonce));
        }
    }

    public StoreResult<ChallengeSession> InitializeChallenge(
        InitChallengeRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        var sessionResult = ResolveSession(cookieSessionId, context);
        if (!sessionResult.Success)
        {
            return StoreResult<ChallengeSession>.Fail(sessionResult.ErrorCode);
        }

        var resolved = sessionResult.Value;
        var now = _clock.GetUtcNow();
        lock (_cache.SyncRoot)
        {
            var (intent, session) = resolved;

            if (intent.Status is not (BookingIntentStatus.EmailConfirmed or BookingIntentStatus.ChallengeStarted))
            {
                return StoreResult<ChallengeSession>.Fail("invalid_state");
            }

            if (intent.ChallengeInitCount >= MaxChallengeInitsPerTicket || intent.ChallengeFailCount >= MaxFailedChallengesPerTicket)
            {
                intent.Status = BookingIntentStatus.TemporarilyDenied;
                SaveIntent(intent);
                Audit("rate_limit_triggered", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "temporarily_denied", "Challenge attempt limit reached.", context);
                return StoreResult<ChallengeSession>.Fail("too_many_attempts");
            }

            if (!TryConsumeOptionalLimit($"rate:challenge-init:ip:{context.IpHash}", MaxChallengeInitsPerIp, AbuseWindow) ||
                !TryConsumeOptionalLimit($"rate:challenge-init:device:{context.DeviceHash}", MaxChallengeInitsPerDevice, AbuseWindow))
            {
                intent.Status = BookingIntentStatus.TemporarilyDenied;
                SaveIntent(intent);
                Audit("rate_limit_triggered", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "temporarily_denied", "Challenge init velocity limit reached.", context);
                return StoreResult<ChallengeSession>.Fail("too_many_attempts");
            }

            ApplyDeviceMismatchPenalty(intent, session, context);

            // The challenge subsystem generates all visual/protocol parameters.
            // The booking flow only persists the resulting server-owned session.
            var challenge = _challengeFactory.Create(intent.Id, session.Id, now, ChallengeTtl, intent.RiskScore);

            intent.ChallengeInitCount++;
            intent.Status = BookingIntentStatus.ChallengeStarted;
            SaveIntent(intent);
            _cache.Set(ChallengeKey(challenge.Id), challenge, ChallengeTtl);
            _cache.SetIfNotExists(ChallengeConsumeKey(challenge.Id), new CacheReference(challenge.Id), ChallengeTtl);

            Audit("challenge_initialized", intent, challenge.Id, Bucket(intent.RiskScore), intent.RiskScore, "challenge_started", "One-time slider challenge created.", context);
            return StoreResult<ChallengeSession>.Ok(challenge);
        }
    }

    public StoreResult<ChallengeSession> StartChallenge(
        StartChallengeRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId))
        {
            return StoreResult<ChallengeSession>.Fail("missing_challenge");
        }

        var now = _clock.GetUtcNow();
        lock (_cache.SyncRoot)
        {
            if (!_cache.TryGet<ChallengeSession>(ChallengeKey(request.ChallengeId), out var challenge) ||
                challenge is null ||
                !TryGetIntent(challenge.IntentId, out var intent))
            {
                return StoreResult<ChallengeSession>.Fail("challenge_not_found");
            }

            var sessionResult = ResolveSessionLocked(challenge.VerificationSessionId, cookieSessionId, context);
            if (!sessionResult.Success)
            {
                return StoreResult<ChallengeSession>.Fail(sessionResult.ErrorCode);
            }

            if (intent.Status != BookingIntentStatus.ChallengeStarted)
            {
                return StoreResult<ChallengeSession>.Fail("invalid_state");
            }

            if (challenge.Consumed || now > challenge.ExpiresAt)
            {
                return StoreResult<ChallengeSession>.Fail("challenge_expired_or_consumed");
            }

            if (string.IsNullOrWhiteSpace(challenge.PhaseNonce))
            {
                var started = challenge with
                {
                    PhaseNonce = SecurityHelpers.NewToken("cp"),
                    StartedAt = now,
                    ActivatedAt = now.AddMilliseconds(challenge.ActivationDelayMs)
                };

                // The start mark is deliberately server-side. Compare-and-set is
                // the production semantic we want from Redis here: only one caller
                // may create the lifecycle nonce for this challenge.
                if (!_cache.CompareAndSet(ChallengeKey(challenge.Id), challenge, started, RemainingTtl(challenge.ExpiresAt, now)))
                {
                    return StoreResult<ChallengeSession>.Fail("invalid_challenge_lifecycle");
                }

                challenge = started;
                Audit("challenge_interaction_started", intent, challenge.Id, Bucket(intent.RiskScore), intent.RiskScore, "interaction_started", "Slider interaction lifecycle started.", context);
            }

            return StoreResult<ChallengeSession>.Ok(challenge);
        }
    }

    public StoreResult<ChallengeSession> StartFollowUpChallenge(
        FollowUpChallengeRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId) ||
            string.IsNullOrWhiteSpace(request.PhaseNonce))
        {
            return StoreResult<ChallengeSession>.Fail("missing_challenge");
        }

        var now = _clock.GetUtcNow();
        lock (_cache.SyncRoot)
        {
            if (!_cache.TryGet<ChallengeSession>(ChallengeKey(request.ChallengeId), out var challenge) ||
                challenge is null ||
                !TryGetIntent(challenge.IntentId, out var intent))
            {
                return StoreResult<ChallengeSession>.Fail("challenge_not_found");
            }

            var sessionResult = ResolveSessionLocked(challenge.VerificationSessionId, cookieSessionId, context);
            if (!sessionResult.Success)
            {
                return StoreResult<ChallengeSession>.Fail(sessionResult.ErrorCode);
            }

            if (challenge.Variant != ChallengeVariants.FollowUpShift ||
                challenge.StartedAt is null ||
                string.IsNullOrWhiteSpace(challenge.PhaseNonce) ||
                !string.Equals(request.PhaseNonce, challenge.PhaseNonce, StringComparison.Ordinal))
            {
                return StoreResult<ChallengeSession>.Fail("invalid_challenge_lifecycle");
            }

            if (challenge.Consumed || now > challenge.ExpiresAt)
            {
                return StoreResult<ChallengeSession>.Fail("challenge_expired_or_consumed");
            }

            if (string.IsNullOrWhiteSpace(challenge.FollowUpNonce))
            {
                var followed = challenge with
                {
                    FollowUpNonce = SecurityHelpers.NewToken("fu"),
                    FollowUpActivatedAt = now.AddMilliseconds(challenge.FollowUpDelayMs)
                };

                // The follow-up mark prevents a cheap bypass where automation
                // solves the active image once and posts a final answer without
                // proving that it stayed in the interaction lifecycle.
                if (!_cache.CompareAndSet(ChallengeKey(challenge.Id), challenge, followed, RemainingTtl(challenge.ExpiresAt, now)))
                {
                    return StoreResult<ChallengeSession>.Fail("invalid_challenge_lifecycle");
                }

                challenge = followed;
                Audit("challenge_follow_up_started", intent, challenge.Id, Bucket(intent.RiskScore), intent.RiskScore, "follow_up_started", "Follow-up target phase scheduled.", context);
            }

            return StoreResult<ChallengeSession>.Ok(challenge);
        }
    }

    public string CreateAssetUrl(ChallengeSession challenge, string kind, string phase)
    {
        var normalizedPhase = NormalizeAssetPhase(kind, phase);
        var token = SecurityHelpers.NewToken("at");
        var now = _clock.GetUtcNow();
        var grant = new AssetTokenGrant
        {
            Token = token,
            ChallengeId = challenge.Id,
            VerificationSessionId = challenge.VerificationSessionId,
            Phase = normalizedPhase,
            CreatedAt = now,
            ExpiresAt = now.Add(AssetTokenTtl)
        };

        _cache.Set(AssetTokenKey(token), grant, AssetTokenTtl);

        var escapedChallengeId = Uri.EscapeDataString(challenge.Id);
        return kind == "piece"
            ? $"/api/v1/challenge/assets/piece/{escapedChallengeId}?asset_token={Uri.EscapeDataString(token)}"
            : $"/api/v1/challenge/assets/bg/{escapedChallengeId}?phase={Uri.EscapeDataString(normalizedPhase)}&asset_token={Uri.EscapeDataString(token)}";
    }

    public StoreResult<ChallengeSession> GetChallengeForAsset(
        string challengeId,
        string? phase,
        string? assetToken,
        string? cookieSessionId)
    {
        lock (_cache.SyncRoot)
        {
            var normalizedPhase = NormalizeAssetPhase("bg", phase);
            if (string.IsNullOrWhiteSpace(challengeId) ||
                string.IsNullOrWhiteSpace(assetToken) ||
                string.IsNullOrWhiteSpace(cookieSessionId))
            {
                return StoreResult<ChallengeSession>.Fail("asset_forbidden");
            }

            if (!_cache.TryGet<AssetTokenGrant>(AssetTokenKey(assetToken), out var grant) ||
                grant is null ||
                _clock.GetUtcNow() > grant.ExpiresAt ||
                !string.Equals(grant.ChallengeId, challengeId, StringComparison.Ordinal) ||
                !string.Equals(grant.Phase, normalizedPhase, StringComparison.Ordinal) ||
                !string.Equals(grant.VerificationSessionId, cookieSessionId, StringComparison.Ordinal))
            {
                return StoreResult<ChallengeSession>.Fail("asset_forbidden");
            }

            if (!_cache.TryGet<ChallengeSession>(ChallengeKey(challengeId), out var challenge) ||
                challenge is null ||
                challenge.ExpiresAt <= _clock.GetUtcNow() ||
                !string.Equals(challenge.VerificationSessionId, cookieSessionId, StringComparison.Ordinal))
            {
                return StoreResult<ChallengeSession>.Fail("challenge_not_found");
            }

            if (normalizedPhase == "active" && challenge.StartedAt is null)
            {
                return StoreResult<ChallengeSession>.Fail("challenge_not_found");
            }

            if (normalizedPhase == "follow_up" &&
                (challenge.Variant != ChallengeVariants.FollowUpShift || string.IsNullOrWhiteSpace(challenge.FollowUpNonce)))
            {
                return StoreResult<ChallengeSession>.Fail("challenge_not_found");
            }

            return StoreResult<ChallengeSession>.Ok(challenge);
        }
    }

    public StoreResult<ChallengeSession> GetChallengePieceForAsset(
        string challengeId,
        string? assetToken,
        string? cookieSessionId)
    {
        lock (_cache.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(assetToken) ||
                string.IsNullOrWhiteSpace(cookieSessionId))
            {
                return StoreResult<ChallengeSession>.Fail("asset_forbidden");
            }

            if (!_cache.TryGet<AssetTokenGrant>(AssetTokenKey(assetToken), out var grant) ||
                grant is null ||
                _clock.GetUtcNow() > grant.ExpiresAt ||
                !string.Equals(grant.ChallengeId, challengeId, StringComparison.Ordinal) ||
                !string.Equals(grant.Phase, "piece", StringComparison.Ordinal) ||
                !string.Equals(grant.VerificationSessionId, cookieSessionId, StringComparison.Ordinal))
            {
                return StoreResult<ChallengeSession>.Fail("asset_forbidden");
            }

            return _cache.TryGet<ChallengeSession>(ChallengeKey(challengeId), out var challenge) &&
                challenge is not null &&
                challenge.ExpiresAt > _clock.GetUtcNow() &&
                string.Equals(challenge.VerificationSessionId, cookieSessionId, StringComparison.Ordinal)
                    ? StoreResult<ChallengeSession>.Ok(challenge)
                    : StoreResult<ChallengeSession>.Fail("challenge_not_found");
        }
    }

    public StoreResult<ChallengeVerifyResult> VerifyChallenge(
        VerifyChallengeRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId) || request.Solution is null)
        {
            return StoreResult<ChallengeVerifyResult>.Fail("invalid_request");
        }

        var now = _clock.GetUtcNow();
        lock (_cache.SyncRoot)
        {
            if (!_cache.TryGet<ChallengeSession>(ChallengeKey(request.ChallengeId), out var challenge) ||
                challenge is null ||
                !TryGetIntent(challenge.IntentId, out var intent))
            {
                return StoreResult<ChallengeVerifyResult>.Fail("challenge_not_found");
            }

            var sessionResult = ResolveSessionLocked(challenge.VerificationSessionId, cookieSessionId, context);
            if (!sessionResult.Success || sessionResult.Value is not { } session)
            {
                return StoreResult<ChallengeVerifyResult>.Fail(sessionResult.ErrorCode);
            }

            if (challenge.Consumed || now > challenge.ExpiresAt)
            {
                return StoreResult<ChallengeVerifyResult>.Fail("challenge_expired_or_consumed");
            }

            if (!_cache.TryConsumeOnce(ChallengeConsumeKey(challenge.Id)))
            {
                return StoreResult<ChallengeVerifyResult>.Fail("challenge_expired_or_consumed");
            }

            if (!TryConsumeOptionalLimit($"rate:challenge-verify:ip:{context.IpHash}", MaxChallengeVerifiesPerIp, AbuseWindow))
            {
                intent.Status = BookingIntentStatus.TemporarilyDenied;
                SaveIntent(intent);
                Audit("rate_limit_triggered", intent, challenge.Id, Bucket(intent.RiskScore), intent.RiskScore, "temporarily_denied", "Challenge verify velocity limit reached.", context);
                return StoreResult<ChallengeVerifyResult>.Fail("too_many_attempts");
            }

            challenge = challenge with { Consumed = true };
            _cache.Set(ChallengeKey(challenge.Id), challenge, RemainingTtl(challenge.ExpiresAt, now));

            ApplyDeviceMismatchPenalty(intent, session, context);

            // Verification has two layers: the visual answer must be close enough,
            // and the interactive protocol must have been completed. The risk engine
            // then decides whether that accepted solution is enough for this session.
            var xAccurate = Math.Abs(request.Solution.X - EffectiveTargetX(challenge)) <= challenge.Tolerance;
            var protocol = _challengeProtocolValidator.Assess(challenge, request.Solution, request.Telemetry);
            var acceptedSolution = xAccurate && protocol.IsSatisfied;
            var risk = _riskEngine.ScoreChallenge(intent, challenge, request, acceptedSolution, protocol);
            var enforced = BotDefensePolicy.Apply(risk.Decision, _options.EnforcementMode);
            TrackTrajectoryFingerprint(intent, risk.TrajectoryFingerprint);

            if (enforced.AppliedDecision == "allow")
            {
                if (!TryConsumeLimit($"rate:validation-token:email:{intent.EmailHash}", MaxValidationTokensPerEmail, AbuseWindow) ||
                    !TryConsumeOptionalLimit($"rate:validation-token:ip:{context.IpHash}", MaxValidationTokensPerIp, AbuseWindow) ||
                    !TryConsumeOptionalLimit($"rate:validation-token:device:{context.DeviceHash}", MaxValidationTokensPerDevice, AbuseWindow))
                {
                    intent.Status = BookingIntentStatus.TemporarilyDenied;
                    SaveIntent(intent);
                    Audit("rate_limit_triggered", intent, challenge.Id, Bucket(risk.Score), risk.Score, "temporarily_denied", "Validation token velocity limit reached.", context, risk.Signals);
                    return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                        "temporarily_denied",
                        null,
                        null,
                        600,
                        "too_many_validation_tokens"));
                }

                intent.RiskScore = Math.Max(intent.RiskScore, risk.Score);
                var token = SecurityHelpers.NewToken("vt");
                var pressure = GetSlotPressureLevel(DefaultSlotGroup);
                var tokenTtl = ValidationTokenTtlForPressure(pressure);
                var grant = new ValidationGrant
                {
                    Token = token,
                    IntentId = intent.Id,
                    VerificationSessionId = challenge.VerificationSessionId,
                    EmailHash = intent.EmailHash,
                    IpHash = context.IpHash,
                    DeviceHash = context.DeviceHash,
                    CreatedAt = now,
                    ExpiresAt = now.Add(tokenTtl),
                    PressureLevel = pressure,
                    MaxAvailabilityRequests = MaxAvailabilityRequestsForPressure(pressure)
                };

                _cache.Set(ValidationGrantKey(token), grant, tokenTtl);
                _cache.SetIfNotExists(ValidationConsumeKey(token), new CacheReference(token), tokenTtl);
                intent.Status = BookingIntentStatus.SlotSelectionAllowed;
                intent.ChallengePassedAt = now;
                SaveIntent(intent);
                RecordSlotPressureSignal(DefaultSlotGroup, "validation-token", intent, challenge.Id, context, risk);

                Audit("challenge_verified", intent, challenge.Id, risk.Bucket, risk.Score, enforced.AppliedDecision, "Validation token issued.", context, risk.Signals, enforced);
                Audit("validation_token_issued", intent, challenge.Id, risk.Bucket, risk.Score, enforced.AppliedDecision, "Short-lived booking validation token issued.", context, risk.Signals, enforced);
                return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                    "allow",
                    token,
                    (int)tokenTtl.TotalSeconds,
                    null,
                    null));
            }

            if (enforced.AppliedDecision == "retry_challenge")
            {
                // ChallengeFailCount already carries the failed-attempt history. Persisting the full
                // per-attempt score here would make one bad drag poison the next fresh challenge.
                intent.ChallengeFailCount++;
                intent.Status = BookingIntentStatus.EmailConfirmed;
                SaveIntent(intent);
                Audit("challenge_failed", intent, challenge.Id, risk.Bucket, risk.Score, enforced.AppliedDecision, "Challenge failed or risk was uncertain.", context, risk.Signals, enforced);
                return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                    "retry_challenge",
                    null,
                    null,
                    3,
                    risk.ReasonCode));
            }

            intent.ChallengeFailCount++;
            intent.Status = enforced.AppliedDecision == "hard_denied"
                ? BookingIntentStatus.HardDenied
                : BookingIntentStatus.TemporarilyDenied;
            SaveIntent(intent);
            Audit("risk_decision_made", intent, challenge.Id, risk.Bucket, risk.Score, enforced.AppliedDecision, "Risk threshold exceeded.", context, risk.Signals, enforced);
            return StoreResult<ChallengeVerifyResult>.Ok(new ChallengeVerifyResult(
                enforced.AppliedDecision,
                null,
                null,
                enforced.AppliedDecision == "hard_denied" ? null : 600,
                risk.ReasonCode));
        }
    }

    public StoreResult<IReadOnlyList<BookingSlot>> GetAvailableSlots(
        AvailableSlotsRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        var grantResult = ValidateGrant(request.ValidationToken, cookieSessionId, context, consume: false);
        if (!grantResult.Success)
        {
            return StoreResult<IReadOnlyList<BookingSlot>>.Fail(grantResult.ErrorCode);
        }

        lock (_cache.SyncRoot)
        {
            var grant = grantResult.Value!;
            if (!TryGetIntent(grant.IntentId, out var intent))
            {
                return StoreResult<IReadOnlyList<BookingSlot>>.Fail("invalid_state");
            }

            var requestedGroup = NormalizeSlotGroup(request.SlotGroup);
            if (grant.SlotGroup is null)
            {
                // Bind the token to the first queried slot group. This prevents a
                // single challenge pass from becoming a short-lived scraper token
                // across unrelated scarce inventories.
                grant.SlotGroup = requestedGroup;
            }
            else if (!string.Equals(grant.SlotGroup, requestedGroup, StringComparison.Ordinal))
            {
                return StoreResult<IReadOnlyList<BookingSlot>>.Fail("slot_group_mismatch");
            }

            grant.AvailabilityRequestCount++;
            if (grant.AvailabilityRequestCount > grant.MaxAvailabilityRequests)
            {
                return StoreResult<IReadOnlyList<BookingSlot>>.Fail("slot_pressure_cooldown");
            }

            var pressure = GetSlotPressureLevel(requestedGroup);
            if (pressure == SlotPressureLevel.Critical && intent.RiskScore >= 75)
            {
                Audit("slot_group_under_attack", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "slot_pressure_cooldown", $"Critical pressure cooldown for {requestedGroup}.", context);
                return StoreResult<IReadOnlyList<BookingSlot>>.Fail("slot_pressure_cooldown");
            }

            _cache.Set(ValidationGrantKey(grant.Token), grant, RemainingTtl(grant.ExpiresAt, _clock.GetUtcNow()));
            RecordSlotPressureSignal(requestedGroup, "slots-available", intent, null, context, null);

            var slots = GetSlotsLocked()
                .Where(slot => string.Equals(slot.SlotGroup, requestedGroup, StringComparison.Ordinal))
                .Where(slot => !slot.IsBooked)
                .OrderBy(slot => slot.Date)
                .ThenBy(slot => slot.Time)
                .ToArray();

            if (pressure >= SlotPressureLevel.High)
            {
                slots = slots.Take(4).ToArray();
            }

            return StoreResult<IReadOnlyList<BookingSlot>>.Ok(slots);
        }
    }

    public StoreResult<FinalizedBooking> FinalizeBooking(
        FinalizeBookingRequest request,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SlotId))
        {
            return StoreResult<FinalizedBooking>.Fail("missing_slot");
        }

        lock (_cache.SyncRoot)
        {
            var grantResult = ValidateGrantLocked(request.ValidationToken, cookieSessionId, context, consume: false);
            if (!grantResult.Success || grantResult.Value is null)
            {
                return StoreResult<FinalizedBooking>.Fail(grantResult.ErrorCode);
            }

            var grant = grantResult.Value;
            if (!TryGetIntent(grant.IntentId, out var intent))
            {
                return StoreResult<FinalizedBooking>.Fail("invalid_state");
            }

            var slot = GetSlotsLocked().FirstOrDefault(candidate => candidate.Id == request.SlotId);
            if (slot is null)
            {
                return StoreResult<FinalizedBooking>.Fail("slot_not_found");
            }

            if (slot.IsBooked)
            {
                return StoreResult<FinalizedBooking>.Fail("slot_unavailable");
            }

            if (!TryConsumeLimit($"rate:slot-group:{slot.SlotGroup}", MaxSlotGroupFinalizationsPerWindow, SlotPressureWindow) ||
                !TryConsumeLimit($"rate:slot:{slot.Id}", MaxSingleSlotFinalizationsPerWindow, SlotPressureWindow))
            {
                RecordSlotPressureSignal(slot.SlotGroup, "failed-finalize", intent, null, context, null);
                Audit("rate_limit_triggered", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "slot_pressure_cooldown", $"Slot pressure limit reached for {slot.SlotGroup}.", context);
                return StoreResult<FinalizedBooking>.Fail("slot_pressure_cooldown");
            }

            if (!string.Equals(grant.SlotGroup ?? slot.SlotGroup, slot.SlotGroup, StringComparison.Ordinal))
            {
                return StoreResult<FinalizedBooking>.Fail("slot_group_mismatch");
            }

            if (!_cache.TryConsumeOnce(ValidationConsumeKey(grant.Token)))
            {
                return StoreResult<FinalizedBooking>.Fail("validation_token_expired_or_used");
            }

            grant.Used = true;
            slot.IsBooked = true;
            intent.Status = BookingIntentStatus.BookingFinalized;
            intent.BookingFinalizedAt = _clock.GetUtcNow();
            SaveIntent(intent);
            _cache.Set(ValidationGrantKey(grant.Token), grant, RemainingTtl(grant.ExpiresAt, _clock.GetUtcNow()));
            _cache.Set(SlotsKey, GetSlotsLocked());

            var booking = new FinalizedBooking($"bk_{Guid.NewGuid():N}"[..15], slot);
            RecordSlotPressureSignal(slot.SlotGroup, "finalize", intent, null, context, null);
            Audit("booking_finalized", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "booked", $"Slot {slot.Id} finalized.", context);
            return StoreResult<FinalizedBooking>.Ok(booking);
        }
    }

    public IReadOnlyList<AuditEvent> GetRecentAuditEvents()
    {
        lock (_cache.SyncRoot)
        {
            return (_cache.Get<List<AuditEvent>>(AuditKey) ?? []).TakeLast(50).ToArray();
        }
    }

    private StoreResult<ValidationGrant> ValidateGrant(
        string? validationToken,
        string? cookieSessionId,
        RequestSecurityContext context,
        bool consume)
    {
        lock (_cache.SyncRoot)
        {
            return ValidateGrantLocked(validationToken, cookieSessionId, context, consume);
        }
    }

    private StoreResult<ValidationGrant> ValidateGrantLocked(
        string? validationToken,
        string? cookieSessionId,
        RequestSecurityContext context,
        bool consume)
    {
        if (string.IsNullOrWhiteSpace(validationToken))
        {
            return StoreResult<ValidationGrant>.Fail("missing_validation_token");
        }

        if (!_cache.TryGet<ValidationGrant>(ValidationGrantKey(validationToken), out var grant) || grant is null)
        {
            return StoreResult<ValidationGrant>.Fail("invalid_validation_token");
        }

        if (grant.Used || _clock.GetUtcNow() > grant.ExpiresAt)
        {
            return StoreResult<ValidationGrant>.Fail("validation_token_expired_or_used");
        }

        var sessionResult = ResolveSessionLocked(grant.VerificationSessionId, cookieSessionId, context);
        if (!sessionResult.Success)
        {
            return StoreResult<ValidationGrant>.Fail(sessionResult.ErrorCode);
        }

        if (!TryGetIntent(grant.IntentId, out var intent) ||
            intent.Status != BookingIntentStatus.SlotSelectionAllowed ||
            !string.Equals(intent.EmailHash, grant.EmailHash, StringComparison.Ordinal))
        {
            return StoreResult<ValidationGrant>.Fail("invalid_state");
        }

        if (grant.DeviceHash is not null &&
            context.DeviceHash is not null &&
            !string.Equals(grant.DeviceHash, context.DeviceHash, StringComparison.Ordinal))
        {
            return StoreResult<ValidationGrant>.Fail("device_mismatch");
        }

        if (consume)
        {
            if (!_cache.TryConsumeOnce(ValidationConsumeKey(grant.Token)))
            {
                return StoreResult<ValidationGrant>.Fail("validation_token_expired_or_used");
            }

            grant.Used = true;
            _cache.Set(ValidationGrantKey(grant.Token), grant, RemainingTtl(grant.ExpiresAt, _clock.GetUtcNow()));
        }

        return StoreResult<ValidationGrant>.Ok(grant);
    }

    private StoreResult<(BookingIntent Intent, VerificationSession Session)> ResolveSession(
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        lock (_cache.SyncRoot)
        {
            var result = ResolveSessionLocked(cookieSessionId, cookieSessionId, context);
            if (!result.Success || result.Value is not { } session)
            {
                return StoreResult<(BookingIntent Intent, VerificationSession Session)>.Fail(result.ErrorCode);
            }

            return TryGetIntent(session.IntentId, out var intent)
                ? StoreResult<(BookingIntent Intent, VerificationSession Session)>.Ok((intent, session))
                : StoreResult<(BookingIntent Intent, VerificationSession Session)>.Fail("invalid_verification_session");
        }
    }

    private StoreResult<VerificationSession> ResolveSessionLocked(
        string? verificationSessionId,
        string? cookieSessionId,
        RequestSecurityContext context)
    {
        if (string.IsNullOrWhiteSpace(verificationSessionId) ||
            string.IsNullOrWhiteSpace(cookieSessionId) ||
            verificationSessionId != cookieSessionId)
        {
            return StoreResult<VerificationSession>.Fail("invalid_verification_session");
        }

        if (string.IsNullOrWhiteSpace(context.SessionNonce))
        {
            return StoreResult<VerificationSession>.Fail("missing_session_nonce");
        }

        if (!_cache.TryGet<VerificationSession>(SessionKey(verificationSessionId), out var session) || session is null)
        {
            return StoreResult<VerificationSession>.Fail("invalid_verification_session");
        }

        if (_clock.GetUtcNow() > session.ExpiresAt)
        {
            return StoreResult<VerificationSession>.Fail("invalid_verification_session");
        }

        if (!string.Equals(session.Nonce, context.SessionNonce, StringComparison.Ordinal))
        {
            return StoreResult<VerificationSession>.Fail("invalid_session_nonce");
        }

        return StoreResult<VerificationSession>.Ok(session);
    }

    private void ApplyDeviceMismatchPenalty(
        BookingIntent intent,
        VerificationSession session,
        RequestSecurityContext context)
    {
        if (session.DeviceHash is null ||
            context.DeviceHash is null ||
            string.Equals(session.DeviceHash, context.DeviceHash, StringComparison.Ordinal))
        {
            return;
        }

        intent.RiskScore = Math.Min(100, intent.RiskScore + 8);
        SaveIntent(intent);
        Audit("risk_signal_observed", intent, null, Bucket(intent.RiskScore), intent.RiskScore, "device_mismatch", "Coarse browser/device hash changed during verification.", context);
    }

    private static void TrackTrajectoryFingerprint(BookingIntent intent, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return;
        }

        // Keep only a coarse, ticket-local trace fingerprint. A Redis-backed store
        // should preserve the same semantics with an atomic compare-and-set update.
        if (string.Equals(intent.LastTrajectoryFingerprint, fingerprint, StringComparison.Ordinal))
        {
            intent.RepeatedTrajectoryCount++;
            return;
        }

        intent.LastTrajectoryFingerprint = fingerprint;
        intent.RepeatedTrajectoryCount = 0;
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

    private bool TryConsumeLimit(string key, int maxCount, TimeSpan window)
    {
        return _cache.IncrementWithExpiry(key, window) <= maxCount;
    }

    private bool TryConsumeOptionalLimit(string key, int maxCount, TimeSpan window)
    {
        return key.EndsWith(":", StringComparison.Ordinal) || TryConsumeLimit(key, maxCount, window);
    }

    private bool TryGetIntent(Guid intentId, out BookingIntent intent)
    {
        if (_cache.TryGet<BookingIntent>(IntentKey(intentId), out var value) && value is not null)
        {
            intent = value;
            return true;
        }

        intent = null!;
        return false;
    }

    private void SaveIntent(BookingIntent intent)
    {
        _cache.Set(IntentKey(intent.Id), intent, IntentTtl);
    }

    private List<BookingSlot> GetSlotsLocked()
    {
        var slots = _cache.Get<List<BookingSlot>>(SlotsKey);
        if (slots is not null)
        {
            return slots;
        }

        slots = SeedSlots();
        _cache.Set(SlotsKey, slots);
        return slots;
    }

    private void Audit(
        string eventType,
        BookingIntent? intent,
        string? challengeId,
        string? riskBucket,
        int? riskScore,
        string? decision,
        string? message,
        RequestSecurityContext context,
        IReadOnlyList<string>? riskSignals = null,
        AppliedRiskDecision? appliedRiskDecision = null)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = _clock.GetUtcNow(),
            EventType = eventType,
            IntentId = intent?.Id,
            EmailHash = intent?.EmailHash,
            ChallengeId = challengeId,
            RiskBucket = riskBucket,
            RiskScore = riskScore,
            Decision = decision,
            AppliedDecision = appliedRiskDecision?.AppliedDecision ?? decision,
            WouldHaveDecision = appliedRiskDecision?.WouldHaveDecision,
            EnforcementMode = appliedRiskDecision?.EnforcementMode.ToString(),
            IpHash = context.IpHash,
            SubnetHash = context.SubnetHash,
            DeviceHash = context.DeviceHash,
            RiskSignals = riskSignals?.ToArray() ?? [],
            Message = message
        };

        var audit = _cache.Get<List<AuditEvent>>(AuditKey) ?? [];
        audit.Add(auditEvent);
        if (audit.Count > 200)
        {
            audit.RemoveRange(0, audit.Count - 200);
        }

        _cache.Set(AuditKey, audit);
        _logger.LogInformation(
            "Audit event {EventType} intent={IntentId} challenge={ChallengeId} bucket={RiskBucket} score={RiskScore} decision={Decision}",
            eventType,
            intent?.Id,
            challengeId,
            riskBucket,
            riskScore,
            decision);
    }

    private SlotPressureLevel GetSlotPressureLevel(string slotGroup)
    {
        var snapshot = _cache.Get<SlotPressureSnapshot>(SlotPressureKey(slotGroup));
        return snapshot is not null && snapshot.ExpiresAt > _clock.GetUtcNow()
            ? snapshot.Level
            : SlotPressureLevel.Normal;
    }

    private void RecordSlotPressureSignal(
        string slotGroup,
        string signal,
        BookingIntent? intent,
        string? challengeId,
        RequestSecurityContext context,
        RiskAssessment? risk)
    {
        var weight = signal switch
        {
            "validation-token" => 1,
            "slots-available" => 1,
            "finalize" => 2,
            "failed-finalize" => 3,
            _ => 1
        };

        var count = 0L;
        for (var index = 0; index < weight; index++)
        {
            count = _cache.IncrementWithExpiry($"pressure:{slotGroup}:score", SlotPressureWindow);
        }

        var level = count switch
        {
            >= 26 => SlotPressureLevel.Critical,
            >= 16 => SlotPressureLevel.High,
            >= 9 => SlotPressureLevel.Elevated,
            _ => SlotPressureLevel.Normal
        };

        var now = _clock.GetUtcNow();
        _cache.Set(SlotPressureKey(slotGroup), new SlotPressureSnapshot
        {
            SlotGroup = slotGroup,
            Level = level,
            UpdatedAt = now,
            ExpiresAt = now.Add(SlotPressureSnapshotTtl)
        }, SlotPressureSnapshotTtl);

        if (level >= SlotPressureLevel.High)
        {
            Audit(
                "slot_group_under_attack",
                intent,
                challengeId,
                risk?.Bucket ?? Bucket(intent?.RiskScore ?? 0),
                risk?.Score ?? intent?.RiskScore,
                "slot_pressure_observed",
                $"{slotGroup} pressure is {level}.",
                context,
                risk?.Signals);
        }
    }

    private static TimeSpan ValidationTokenTtlForPressure(SlotPressureLevel pressure)
    {
        return pressure switch
        {
            SlotPressureLevel.Elevated => TimeSpan.FromMinutes(2),
            SlotPressureLevel.High => TimeSpan.FromSeconds(90),
            SlotPressureLevel.Critical => TimeSpan.FromSeconds(60),
            _ => ValidationTokenTtl
        };
    }

    private static int MaxAvailabilityRequestsForPressure(SlotPressureLevel pressure)
    {
        return pressure switch
        {
            SlotPressureLevel.Elevated => 3,
            SlotPressureLevel.High => 2,
            SlotPressureLevel.Critical => 1,
            _ => 4
        };
    }

    private static int EffectiveTargetX(ChallengeSession challenge)
    {
        return challenge.Variant == ChallengeVariants.FollowUpShift
            ? challenge.FollowUpTargetX
            : challenge.TargetX;
    }

    private static string NormalizeAssetPhase(string kind, string? phase)
    {
        if (kind == "piece")
        {
            return "piece";
        }

        return string.IsNullOrWhiteSpace(phase)
            ? "preview"
            : phase.Trim().ToLowerInvariant() switch
            {
                "active" => "active",
                "follow_up" => "follow_up",
                _ => "preview"
            };
    }

    private static string NormalizeSlotGroup(string? slotGroup)
    {
        return string.IsNullOrWhiteSpace(slotGroup)
            ? DefaultSlotGroup
            : slotGroup.Trim().ToLowerInvariant();
    }

    private static List<BookingSlot> SeedSlots()
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

        return seed.Select(slot => new BookingSlot
        {
            Id = slot.Item1,
            Date = slot.Item2,
            Time = slot.Item3,
            SlotGroup = "serbian-permit-july-2026"
        }).ToList();
    }

    private static TimeSpan RemainingTtl(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        return expiresAt > now ? expiresAt - now : TimeSpan.FromSeconds(1);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string? HashEmailDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        return atIndex >= 0 && atIndex < email.Length - 1
            ? SecurityHelpers.Hash(email[(atIndex + 1)..])
            : null;
    }

    private static string IntentKey(Guid id) => $"intent:{id:N}";

    private static string TicketKey(string ticket) => $"ticket:{ticket}";

    private static string SessionKey(string sessionId) => $"session:{sessionId}";

    private static string ChallengeKey(string challengeId) => $"challenge:{challengeId}";

    private static string ChallengeConsumeKey(string challengeId) => $"once:challenge:{challengeId}";

    private static string ValidationGrantKey(string token) => $"validation:{token}";

    private static string ValidationConsumeKey(string token) => $"once:validation:{token}";

    private static string AssetTokenKey(string token) => $"asset-token:{token}";

    private static string SlotPressureKey(string slotGroup) => $"slot-pressure:{slotGroup}";

    private static string Bucket(int risk) => risk switch
    {
        < 30 => "low",
        < 55 => "elevated",
        < 75 => "high",
        _ => "critical"
    };
}
