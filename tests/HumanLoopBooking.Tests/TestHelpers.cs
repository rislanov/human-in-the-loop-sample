using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HumanLoopBooking.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HumanLoopBooking.Tests;

internal static partial class TestHelpers
{
    public static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static BrowserSignals Browser => new()
    {
        PluginsLength = 3,
        LanguagesLength = 2,
        Platform = "MacIntel",
        UserAgent = "Mozilla/5.0 Test",
        TouchCapable = false,
        WebDriver = false
    };

    public static RequestSecurityContext Context(string? nonce = null)
    {
        return new RequestSecurityContext(
            SecurityHelpers.HashIp(IPAddress.Loopback),
            SecurityHelpers.HashSubnet(IPAddress.Loopback),
            SecurityHelpers.HashDevice(Browser),
            nonce,
            Browser.UserAgent);
    }

    public static CreateBookingIntentRequest BookingRequest(string? email = null)
    {
        var actualEmail = email ?? $"user-{Guid.NewGuid():N}@example.test";
        return new CreateBookingIntentRequest
        {
            Name = "Test User",
            DateOfBirth = "30/01/1990",
            NumberOfApplicants = 1,
            PhoneNumber = "+3612345678",
            Email = actualEmail,
            ReenteredEmail = actualEmail,
            PassportNumber = $"P{Guid.NewGuid():N}"[..10],
            Citizenship = "Serbia",
            ResidencePermit = "Permit 123",
            Telemetry = new FormTelemetry
            {
                SubmitElapsedMs = 3500,
                FirstInteractionMs = 600,
                KeyEventCount = 20,
                PointerMoveCount = 12,
                Browser = Browser
            }
        };
    }

    public static StoreHarness CreateStore(
        EnforcementMode mode = EnforcementMode.Full,
        string variant = ChallengeVariants.RevealTarget,
        MutableTimeProvider? clock = null)
    {
        clock ??= new MutableTimeProvider(DateTimeOffset.UtcNow);
        var cache = new InMemoryTemporarySecurityStateStore(clock);
        var store = new BookingFlowStore(
            cache,
            new RiskEngine(new TrajectoryAnalyzer()),
            new FixedChallengeFactory(variant),
            new ChallengeProtocolValidator(),
            clock,
            NullLogger<BookingFlowStore>.Instance,
            Options.Create(new BotDefenseOptions { EnforcementMode = mode }));

        return new StoreHarness(store, cache, clock);
    }

    public static FlowState CreateStartedChallenge(StoreHarness harness)
    {
        var intentResult = harness.Store.CreateBookingIntent(BookingRequest(), "https://localhost", Context());
        Assert.True(intentResult.Success, intentResult.ErrorCode);

        var confirmResult = harness.Store.ConfirmEmailTicket(
            new ConfirmEmailRequest { Ticket = intentResult.Value!.Intent.EmailTicket, Browser = Browser },
            Context());
        Assert.True(confirmResult.Success, confirmResult.ErrorCode);

        var sessionId = confirmResult.Value!.VerificationSessionId;
        var nonce = confirmResult.Value!.SessionNonce;
        var context = Context(nonce);

        var challengeResult = harness.Store.InitializeChallenge(
            new InitChallengeRequest { Browser = Browser },
            sessionId,
            context);
        Assert.True(challengeResult.Success, challengeResult.ErrorCode);

        var startedResult = harness.Store.StartChallenge(
            new StartChallengeRequest { ChallengeId = challengeResult.Value!.Id, Browser = Browser },
            sessionId,
            context);
        Assert.True(startedResult.Success, startedResult.ErrorCode);

        return new FlowState(intentResult.Value.Intent, sessionId, nonce, startedResult.Value!, context);
    }

    public static VerifyChallengeRequest ValidVerifyRequest(ChallengeSession challenge)
    {
        var targetX = challenge.Variant == ChallengeVariants.FollowUpShift
            ? challenge.FollowUpTargetX
            : challenge.TargetX;

        return new VerifyChallengeRequest
        {
            ChallengeId = challenge.Id,
            Browser = Browser,
            Solution = new ChallengeSolution
            {
                X = targetX,
                TimeSpentMs = 1450,
                PhaseNonce = challenge.PhaseNonce,
                FollowUpNonce = challenge.FollowUpNonce,
                InteractionPhase = challenge.Variant == ChallengeVariants.FollowUpShift ? "follow_up" : "active",
                ActiveElapsedMs = 950,
                FollowUpElapsedMs = challenge.Variant == ChallengeVariants.FollowUpShift ? 450 : 0,
                HoldMs = challenge.Variant == ChallengeVariants.HoldAndRelease ? challenge.HoldRequirementMs + 120 : 0,
                LastAdjustmentMs = 780,
                LastFollowUpAdjustmentMs = challenge.Variant == ChallengeVariants.FollowUpShift ? 330 : 0
            },
            Telemetry = HumanTelemetry(targetX, challenge.Variant == ChallengeVariants.FollowUpShift)
        };
    }

    public static ChallengeTelemetry HumanTelemetry(int targetX, bool includeFollowUp)
    {
        var points = new List<TelemetryPoint>
        {
            new() { X = 8, Y = 88, T = 0, Phase = "down", State = "pre_active" },
            new() { X = 24, Y = 89, T = 110, Phase = "move", State = "pre_active" }
        };

        var activeEnd = includeFollowUp ? Math.Max(80, targetX - 34) : targetX;
        for (var index = 0; index < 8; index++)
        {
            var ratio = (index + 1) / 8.0;
            points.Add(new TelemetryPoint
            {
                X = (int)Math.Round(24 + ((activeEnd - 24) * ratio)),
                Y = 88 + (index % 3),
                T = 220 + (index * 95),
                Phase = "move",
                State = "active"
            });
        }

        if (includeFollowUp)
        {
            for (var index = 0; index < 5; index++)
            {
                var ratio = (index + 1) / 5.0;
                points.Add(new TelemetryPoint
                {
                    X = (int)Math.Round(activeEnd + ((targetX - activeEnd) * ratio)),
                    Y = 91 - (index % 2),
                    T = 1050 + (index * 90),
                    Phase = index == 4 ? "up" : "move",
                    State = "follow_up"
                });
            }
        }
        else
        {
            points.Add(new TelemetryPoint { X = targetX, Y = 90, T = 1350, Phase = "up", State = "active" });
        }

        return new ChallengeTelemetry
        {
            Modality = "mouse",
            Points = points,
            Events = new ChallengeEvents()
        };
    }

    public static async Task<ConfirmedSession> CreateConfirmedSessionAsync(HttpClient client)
    {
        var intentResponse = await client.PostAsJsonAsync("/api/v1/booking-intents", BookingRequest(), SnakeJson);
        intentResponse.EnsureSuccessStatusCode();
        var intentJson = await intentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var continueUrl = intentJson
            .GetProperty("dev_email_preview")
            .GetProperty("continue_booking_url")
            .GetString()!;
        var ticket = Regex.Match(continueUrl, @"[?&]t=([^&]+)").Groups[1].Value;

        var confirmResponse = await client.PostAsJsonAsync("/api/v1/verification/email/confirm", new
        {
            ticket,
            browser = Browser
        }, SnakeJson);
        confirmResponse.EnsureSuccessStatusCode();
        var confirmJson = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>();
        var nonce = confirmJson.GetProperty("session_nonce").GetString()!;
        var cookie = confirmResponse.Headers
            .GetValues("Set-Cookie")
            .Single(value => value.StartsWith(BookingFlowStore.VerificationCookieName, StringComparison.Ordinal))
            .Split(';')[0];

        return new ConfirmedSession(cookie, nonce);
    }
}

internal sealed record StoreHarness(
    BookingFlowStore Store,
    ITemporarySecurityStateStore State,
    MutableTimeProvider Clock);

internal sealed record FlowState(
    BookingIntent Intent,
    string SessionId,
    string Nonce,
    ChallengeSession Challenge,
    RequestSecurityContext Context);

internal sealed record ConfirmedSession(string Cookie, string Nonce);

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan value)
    {
        _now = _now.Add(value);
    }
}

internal sealed class FixedChallengeFactory : IChallengeSessionFactory
{
    private readonly string _variant;

    public FixedChallengeFactory(string variant)
    {
        _variant = variant;
    }

    public ChallengeSession Create(
        Guid intentId,
        string verificationSessionId,
        DateTimeOffset now,
        TimeSpan ttl,
        int currentRiskScore)
    {
        var followUpTarget = _variant == ChallengeVariants.FollowUpShift ? 214 : 0;
        return new ChallengeSession
        {
            Id = SecurityHelpers.NewToken("ch"),
            IntentId = intentId,
            VerificationSessionId = verificationSessionId,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
            Variant = _variant,
            TargetX = 176,
            PreviewTargetX = 232,
            FollowUpTargetX = followUpTarget,
            FollowUpDelayMs = _variant == ChallengeVariants.FollowUpShift ? 420 : 0,
            RequiredPostFollowUpAdjustmentPx = _variant == ChallengeVariants.FollowUpShift ? 12 : 0,
            PieceY = 72,
            Width = 360,
            Height = 180,
            PieceSize = 48,
            Tolerance = 8,
            TrackWidth = 360,
            HandleSize = 54,
            ActivationDelayMs = 260,
            HoldRequirementMs = _variant == ChallengeVariants.HoldAndRelease ? 520 : 0,
            StripeOffset = 3
        };
    }
}

internal sealed class TestBookingFactory : WebApplicationFactory<Program>
{
    private readonly TimeProvider? _clock;
    private readonly EnforcementMode _mode;

    public TestBookingFactory(TimeProvider? clock = null, EnforcementMode mode = EnforcementMode.Full)
    {
        _clock = clock;
        _mode = mode;
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            if (_clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_clock);
            }

            services.RemoveAll<IOptions<BotDefenseOptions>>();
            services.AddSingleton(Options.Create(new BotDefenseOptions { EnforcementMode = _mode }));
        });
    }
}
