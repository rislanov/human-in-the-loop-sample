using System.Text.Json.Serialization;

namespace HumanLoopBooking.Services;

public enum BookingIntentStatus
{
    EmailSent,
    EmailConfirmed,
    ChallengeStarted,
    ChallengePassed,
    SlotSelectionAllowed,
    BookingFinalized,
    TemporarilyDenied,
    HardDenied
}

public sealed class BookingIntent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required BookingData BookingData { get; init; }
    public required string EmailHash { get; init; }
    public required string EmailTicket { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? CreatedIpHash { get; init; }
    public string? CreatedDeviceHash { get; init; }
    public BookingIntentStatus Status { get; set; } = BookingIntentStatus.EmailSent;
    public string? VerificationSessionId { get; set; }
    public int EmailSendCount { get; set; } = 1;
    public int ChallengeInitCount { get; set; }
    public int ChallengeFailCount { get; set; }
    public int RiskScore { get; set; }
    public string? LastTrajectoryFingerprint { get; set; }
    public int RepeatedTrajectoryCount { get; set; }
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public DateTimeOffset? ChallengePassedAt { get; set; }
    public DateTimeOffset? BookingFinalizedAt { get; set; }
}

public sealed class VerificationSession
{
    public required string Id { get; init; }
    public required Guid IntentId { get; init; }
    public required string Nonce { get; init; }
    public string? IpHash { get; init; }
    public string? SubnetHash { get; init; }
    public string? DeviceHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class BookingData
{
    public string Name { get; init; } = "";
    public string DateOfBirth { get; init; } = "";
    public int NumberOfApplicants { get; init; } = 1;
    public string PhoneNumber { get; init; } = "";
    public string Email { get; init; } = "";
    public string PassportNumber { get; init; } = "";
    public string Citizenship { get; init; } = "";
    public string ResidencePermit { get; init; } = "";
}

public sealed class ValidationGrant
{
    public required string Token { get; init; }
    public required Guid IntentId { get; init; }
    public required string VerificationSessionId { get; init; }
    public required string EmailHash { get; init; }
    public string? IpHash { get; init; }
    public string? DeviceHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? SlotGroup { get; set; }
    public SlotPressureLevel PressureLevel { get; init; } = SlotPressureLevel.Normal;
    public int MaxAvailabilityRequests { get; init; } = 4;
    public int AvailabilityRequestCount { get; set; }
    public bool Used { get; set; }
}

public enum SlotPressureLevel
{
    Normal,
    Elevated,
    High,
    Critical
}

public sealed class BookingSlot
{
    public required string Id { get; init; }
    public required DateOnly Date { get; init; }
    public required TimeOnly Time { get; init; }
    public required string SlotGroup { get; init; }
    public bool IsBooked { get; set; }
}

public sealed class AuditEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string EventType { get; init; }
    public Guid? IntentId { get; init; }
    public string? EmailHash { get; init; }
    public string? ChallengeId { get; init; }
    public string? RiskBucket { get; init; }
    public int? RiskScore { get; init; }
    public string? Decision { get; init; }
    public string? AppliedDecision { get; init; }
    public string? WouldHaveDecision { get; init; }
    public string? EnforcementMode { get; init; }
    public string? IpHash { get; init; }
    public string? SubnetHash { get; init; }
    public string? DeviceHash { get; init; }
    public IReadOnlyList<string> RiskSignals { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class CreateBookingIntentRequest
{
    public string? Name { get; init; }
    public string? DateOfBirth { get; init; }
    public int? NumberOfApplicants { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Email { get; init; }
    public string? ReenteredEmail { get; init; }
    public string? PassportNumber { get; init; }
    public string? Citizenship { get; init; }
    public string? ResidencePermit { get; init; }
    public FormTelemetry? Telemetry { get; init; }
}

public sealed class ConfirmEmailRequest
{
    public string? Ticket { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class AvailableSlotsRequest
{
    public string? ValidationToken { get; init; }
    public string? SlotGroup { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class FinalizeBookingRequest
{
    public string? ValidationToken { get; init; }
    public string? SlotId { get; init; }
    public BookingData? BookingData { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed record EmailPreview(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("continue_booking_url")] string ContinueBookingUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record ChallengeVerifyResult(
    string Decision,
    string? ValidationToken,
    int? ExpiresInSeconds,
    int? CooldownSeconds,
    string? ReasonCode);

public sealed record FinalizedBooking(string BookingId, BookingSlot Slot);

public sealed record CacheReference(string Value);

public sealed class AssetTokenGrant
{
    public required string Token { get; init; }
    public required string ChallengeId { get; init; }
    public required string VerificationSessionId { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class SlotPressureSnapshot
{
    public required string SlotGroup { get; init; }
    public required SlotPressureLevel Level { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record RequestSecurityContext(
    string? IpHash,
    string? SubnetHash,
    string? DeviceHash,
    string? SessionNonce,
    string? UserAgent);

public sealed record StoreResult<T>(bool Success, T? Value, string ErrorCode)
{
    public static StoreResult<T> Ok(T value) => new(true, value, "");
    public static StoreResult<T> Fail(string errorCode) => new(false, default, errorCode);
}
