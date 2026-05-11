using HumanLoopBooking.Services;

namespace HumanLoopBooking.Tests;

public sealed class ValidationTokenTests
{
    [Fact]
    public void slot_access_without_validation_token_rejected()
    {
        var harness = TestHelpers.CreateStore();
        var flow = TestHelpers.CreateStartedChallenge(harness);

        var result = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.False(result.Success);
        Assert.Equal("missing_validation_token", result.ErrorCode);
    }

    [Fact]
    public void validation_token_is_short_lived()
    {
        var harness = TestHelpers.CreateStore(clock: new MutableTimeProvider(DateTimeOffset.UtcNow));
        var token = VerifyAndGetToken(harness, out var flow);
        harness.Clock.Advance(TimeSpan.FromMinutes(4));

        var result = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            ValidationToken = token,
            SlotGroup = "serbian-permit-july-2026",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.False(result.Success);
        Assert.Equal("invalid_validation_token", result.ErrorCode);
    }

    [Fact]
    public void validation_token_bound_to_session()
    {
        var harness = TestHelpers.CreateStore();
        var token = VerifyAndGetToken(harness, out var flow);

        var result = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            ValidationToken = token,
            SlotGroup = "serbian-permit-july-2026",
            Browser = TestHelpers.Browser
        }, "vs_wrong", flow.Context);

        Assert.False(result.Success);
        Assert.Equal("invalid_verification_session", result.ErrorCode);
    }

    [Fact]
    public void validation_token_bound_to_device_when_device_hash_exists()
    {
        var harness = TestHelpers.CreateStore();
        var token = VerifyAndGetToken(harness, out var flow);
        var wrongDeviceContext = flow.Context with
        {
            DeviceHash = SecurityHelpers.Hash("different-device")
        };

        var result = harness.Store.GetAvailableSlots(new AvailableSlotsRequest
        {
            ValidationToken = token,
            SlotGroup = "serbian-permit-july-2026",
            Browser = TestHelpers.Browser
        }, flow.SessionId, wrongDeviceContext);

        Assert.False(result.Success);
        Assert.Equal("device_mismatch", result.ErrorCode);
    }

    [Fact]
    public void validation_token_reuse_rejected()
    {
        var harness = TestHelpers.CreateStore();
        var token = VerifyAndGetToken(harness, out var flow);
        var first = harness.Store.FinalizeBooking(new FinalizeBookingRequest
        {
            ValidationToken = token,
            SlotId = "slot_0701_1130",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);
        var second = harness.Store.FinalizeBooking(new FinalizeBookingRequest
        {
            ValidationToken = token,
            SlotId = "slot_0708_0900",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(second.Success);
        Assert.Equal("validation_token_expired_or_used", second.ErrorCode);
    }

    [Fact]
    public void finalize_without_token_rejected()
    {
        var harness = TestHelpers.CreateStore();
        var flow = TestHelpers.CreateStartedChallenge(harness);

        var result = harness.Store.FinalizeBooking(new FinalizeBookingRequest
        {
            SlotId = "slot_0701_1130",
            Browser = TestHelpers.Browser
        }, flow.SessionId, flow.Context);

        Assert.False(result.Success);
        Assert.Equal("missing_validation_token", result.ErrorCode);
    }

    private static string VerifyAndGetToken(StoreHarness harness, out FlowState flow)
    {
        flow = TestHelpers.CreateStartedChallenge(harness);
        var verify = harness.Store.VerifyChallenge(TestHelpers.ValidVerifyRequest(flow.Challenge), flow.SessionId, flow.Context);
        Assert.True(verify.Success, verify.ErrorCode);
        Assert.Equal("allow", verify.Value!.Decision);
        return verify.Value.ValidationToken!;
    }
}
