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

public sealed class ChallengeSession
{
    public required string Id { get; init; }
    public required Guid IntentId { get; init; }
    public required string VerificationSessionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required int TargetX { get; init; }
    public required int PieceY { get; init; }
    public int Width { get; init; } = 360;
    public int Height { get; init; } = 180;
    public int PieceSize { get; init; } = 48;
    public int Tolerance { get; init; } = 9;
    public bool Consumed { get; set; }
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
    public bool Used { get; set; }
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
    public string? IpHash { get; init; }
    public string? SubnetHash { get; init; }
    public string? DeviceHash { get; init; }
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

public sealed class InitChallengeRequest
{
    public string? VerificationSessionId { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class VerifyChallengeRequest
{
    public string? ChallengeId { get; init; }
    public ChallengeSolution? Solution { get; init; }
    public ChallengeTelemetry? Telemetry { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class ChallengeSolution
{
    public int X { get; init; }
    public int TimeSpentMs { get; init; }
}

public sealed class ChallengeTelemetry
{
    public string? Modality { get; init; }
    public List<TelemetryPoint> Points { get; init; } = [];
    public ChallengeEvents Events { get; init; } = new();
}

public sealed class TelemetryPoint
{
    public int X { get; init; }
    public int Y { get; init; }
    public int T { get; init; }
}

public sealed class ChallengeEvents
{
    public bool FocusLost { get; init; }
    public bool VisibilityChanged { get; init; }
    public bool PointerCancelled { get; init; }
}

public sealed class FormTelemetry
{
    public int SubmitElapsedMs { get; init; }
    public int FirstInteractionMs { get; init; }
    public int KeyEventCount { get; init; }
    public int PasteCount { get; init; }
    public int PointerMoveCount { get; init; }
    public int FocusChangeCount { get; init; }
    public BrowserSignals? Browser { get; init; }
}

public sealed class BrowserSignals
{
    public bool WebDriver { get; init; }
    public int PluginsLength { get; init; }
    public int LanguagesLength { get; init; }
    public string? UserAgent { get; init; }
    public string? Platform { get; init; }
    public bool TouchCapable { get; init; }
}

public sealed class AvailableSlotsRequest
{
    public string? ValidationToken { get; init; }
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

public sealed record RenderPayload(
    string BackgroundImageUrl,
    string PieceImageUrl,
    RenderUiConfig UiConfig);

public sealed record RenderUiConfig(
    int Width,
    int Height,
    int PieceSize,
    int PieceY);

public sealed record RiskAssessment(int Score, string Bucket, string Decision, string ReasonCode);

public sealed record ChallengeVerifyResult(
    string Decision,
    string? ValidationToken,
    int? ExpiresInSeconds,
    int? CooldownSeconds,
    string? ReasonCode);

public sealed record FinalizedBooking(string BookingId, BookingSlot Slot);

public sealed record CacheReference(string Value);

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
