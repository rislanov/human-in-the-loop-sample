using System.Globalization;
using System.Text.Json;
using HumanLoopBooking.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RiskEngine>();
builder.Services.AddSingleton<BookingFlowStore>();
builder.Services.AddSingleton<ChallengeAssetRenderer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRouting();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.TryGetValue("Origin", out var originValues) &&
        Uri.TryCreate(originValues.ToString(), UriKind.Absolute, out var origin) &&
        !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "origin_rejected" });
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapPost("/api/v1/booking-intents", (CreateBookingIntentRequest request, HttpContext http, BookingFlowStore store) =>
{
    var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    var result = store.CreateBookingIntent(request, baseUrl);

    return result.Success
        ? Results.Ok(new { Status = "email_sent", DevEmailPreview = result.Value.Preview })
        : Results.BadRequest(new { Error = result.ErrorCode });
});

app.MapPost("/api/v1/verification/email/confirm", (ConfirmEmailRequest request, HttpContext http, BookingFlowStore store) =>
{
    var result = store.ConfirmEmailTicket(request.Ticket);
    if (!result.Success)
    {
        return Results.BadRequest(new { Error = result.ErrorCode });
    }

    var value = result.Value;
    http.Response.Cookies.Append(
        BookingFlowStore.VerificationCookieName,
        value.VerificationSessionId,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(15),
            Path = "/"
        });

    return Results.Ok(new
    {
        Status = "email_confirmed",
        VerificationSessionId = value.VerificationSessionId
    });
});

app.MapPost("/api/v1/challenge/init", (InitChallengeRequest request, HttpContext http, BookingFlowStore store) =>
{
    http.Request.Cookies.TryGetValue(BookingFlowStore.VerificationCookieName, out var cookieSessionId);
    var result = store.InitializeChallenge(request.VerificationSessionId, cookieSessionId);

    if (!result.Success || result.Value is not { } challenge)
    {
        return Results.BadRequest(new { Error = result.ErrorCode });
    }

    return Results.Ok(new
    {
        ChallengeId = challenge.Id,
        Type = "slider",
        ExpiresInSeconds = 120,
        RenderPayload = new RenderPayload(
            $"/api/v1/challenge/assets/bg/{Uri.EscapeDataString(challenge.Id)}",
            $"/api/v1/challenge/assets/piece/{Uri.EscapeDataString(challenge.Id)}",
            new RenderUiConfig(challenge.Width, challenge.Height, challenge.PieceSize, challenge.PieceY))
    });
});

app.MapGet("/api/v1/challenge/assets/bg/{challengeId}", (string challengeId, BookingFlowStore store, ChallengeAssetRenderer renderer) =>
{
    var result = store.GetChallengeForAsset(challengeId);
    return result.Success && result.Value is not null
        ? Results.Content(renderer.RenderBackground(result.Value), "image/svg+xml; charset=utf-8")
        : Results.NotFound();
});

app.MapGet("/api/v1/challenge/assets/piece/{challengeId}", (string challengeId, BookingFlowStore store, ChallengeAssetRenderer renderer) =>
{
    var result = store.GetChallengeForAsset(challengeId);
    return result.Success && result.Value is not null
        ? Results.Content(renderer.RenderPiece(result.Value), "image/svg+xml; charset=utf-8")
        : Results.NotFound();
});

app.MapPost("/api/v1/challenge/verify", (VerifyChallengeRequest request, HttpContext http, BookingFlowStore store) =>
{
    http.Request.Cookies.TryGetValue(BookingFlowStore.VerificationCookieName, out var cookieSessionId);
    var result = store.VerifyChallenge(request, cookieSessionId);

    return result.Success && result.Value is not null
        ? Results.Ok(result.Value)
        : Results.BadRequest(new { Error = result.ErrorCode });
});

app.MapPost("/api/v1/slots/available", (AvailableSlotsRequest request, HttpContext http, BookingFlowStore store) =>
{
    http.Request.Cookies.TryGetValue(BookingFlowStore.VerificationCookieName, out var cookieSessionId);
    var result = store.GetAvailableSlots(request.ValidationToken, cookieSessionId);

    if (!result.Success || result.Value is null)
    {
        return Results.BadRequest(new { Error = result.ErrorCode });
    }

    var slots = result.Value.Select(slot => new
    {
        slot.Id,
        Date = slot.Date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        IsoDate = slot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DayName = CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(slot.Date.DayOfWeek),
        Time = slot.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
        Status = "Free"
    });

    return Results.Ok(new { Slots = slots });
});

app.MapPost("/api/v1/bookings/finalize", (FinalizeBookingRequest request, HttpContext http, BookingFlowStore store) =>
{
    http.Request.Cookies.TryGetValue(BookingFlowStore.VerificationCookieName, out var cookieSessionId);
    var result = store.FinalizeBooking(request, cookieSessionId);

    return result.Success && result.Value is not null
        ? Results.Ok(new { Status = "booked", BookingId = result.Value.BookingId })
        : Results.BadRequest(new { Error = result.ErrorCode });
});

app.MapGet("/api/v1/audit/recent", (BookingFlowStore store) => Results.Ok(new { Events = store.GetRecentAuditEvents() }));

app.Run();
