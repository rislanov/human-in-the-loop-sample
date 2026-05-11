using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HumanLoopBooking.Tests;

public sealed class SessionAndAssetBindingTests
{
    [Fact]
    public async Task challenge_init_without_cookie_is_rejected()
    {
        await using var factory = new TestBookingFactory();
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });
        var confirmed = await TestHelpers.CreateConfirmedSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Booking-Session-Nonce", confirmed.Nonce);

        var response = await client.PostAsJsonAsync("/api/v1/challenge/init", new
        {
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task challenge_init_without_nonce_is_rejected()
    {
        await using var factory = new TestBookingFactory();
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });
        var confirmed = await TestHelpers.CreateConfirmedSessionAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", confirmed.Cookie);

        var response = await client.PostAsJsonAsync("/api/v1/challenge/init", new
        {
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task challenge_init_with_cookie_and_nonce_succeeds()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/challenge/init", new
        {
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task challenge_init_ignores_body_session_id_and_uses_cookie_nonce_binding()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/challenge/init", new
        {
            verification_session_id = "attacker-visible-session-id",
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task preview_asset_requires_valid_asset_token()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var init = await InitChallengeAsync(client);
        var backgroundUrl = init.GetProperty("render_payload").GetProperty("background_image_url").GetString()!;

        var missingToken = await client.GetAsync(RemoveQuery(backgroundUrl));
        var valid = await client.GetAsync(backgroundUrl);

        Assert.Equal(HttpStatusCode.NotFound, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task piece_asset_requires_valid_asset_token()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var init = await InitChallengeAsync(client);
        var pieceUrl = init.GetProperty("render_payload").GetProperty("piece_image_url").GetString()!;

        var missingToken = await client.GetAsync(RemoveQuery(pieceUrl));
        var valid = await client.GetAsync(pieceUrl);

        Assert.Equal(HttpStatusCode.NotFound, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task active_asset_before_start_returns_404_or_403()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var init = await InitChallengeAsync(client);
        var challengeId = init.GetProperty("challenge_id").GetString()!;
        var previewUrl = init.GetProperty("render_payload").GetProperty("background_image_url").GetString()!;
        var activeWithPreviewToken = $"/api/v1/challenge/assets/bg/{Uri.EscapeDataString(challengeId)}?phase=active&asset_token={TokenFrom(previewUrl)}";

        var response = await client.GetAsync(activeWithPreviewToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task active_asset_after_start_with_valid_token_succeeds()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var init = await InitChallengeAsync(client);
        var start = await StartChallengeAsync(client, init.GetProperty("challenge_id").GetString()!);
        var activeUrl = start.GetProperty("active_background_image_url").GetString()!;

        var response = await client.GetAsync(activeUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task asset_token_from_other_challenge_is_rejected()
    {
        await using var factory = new TestBookingFactory();
        var client = await CreateAuthenticatedClientAsync(factory);
        var first = await InitChallengeAsync(client);
        var second = await InitChallengeAsync(client);
        var firstUrl = first.GetProperty("render_payload").GetProperty("background_image_url").GetString()!;
        var secondId = second.GetProperty("challenge_id").GetString()!;
        var mismatched = $"/api/v1/challenge/assets/bg/{Uri.EscapeDataString(secondId)}?phase=preview&asset_token={TokenFrom(firstUrl)}";

        var response = await client.GetAsync(mismatched);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task expired_asset_token_is_rejected()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new TestBookingFactory(clock);
        var client = await CreateAuthenticatedClientAsync(factory);
        var init = await InitChallengeAsync(client);
        var backgroundUrl = init.GetProperty("render_payload").GetProperty("background_image_url").GetString()!;
        clock.Advance(TimeSpan.FromSeconds(46));

        var response = await client.GetAsync(backgroundUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(TestBookingFactory factory)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });
        var confirmed = await TestHelpers.CreateConfirmedSessionAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", confirmed.Cookie);
        client.DefaultRequestHeaders.Add("X-Booking-Session-Nonce", confirmed.Nonce);
        return client;
    }

    private static async Task<JsonElement> InitChallengeAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/challenge/init", new
        {
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> StartChallengeAsync(HttpClient client, string challengeId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/challenge/start", new
        {
            challenge_id = challengeId,
            browser = TestHelpers.Browser
        }, TestHelpers.SnakeJson);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string RemoveQuery(string url)
    {
        var index = url.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? url : url[..index];
    }

    private static string TokenFrom(string url)
    {
        var query = new Uri($"https://local{url}").Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        return query
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0] == "asset_token")
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .Single();
    }
}
