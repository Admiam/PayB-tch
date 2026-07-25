namespace Paybitch.Api.Features.Notifications.Push;

/// <summary>
/// The dev/default <see cref="IApnsSender"/>: builds the real wire payload (so the no-PII invariant is
/// exercised) and logs it instead of opening an HTTP/2 connection to Apple. The device token is
/// truncated in the log; the payload is logged at Debug only. A prod deployment registers a real
/// <c>.p8</c>-token APNs sender that supersedes this via DI.
/// </summary>
public sealed class LogApnsSender(ILogger<LogApnsSender> logger) : IApnsSender
{
    public Task SendAsync(ApnsPush push, CancellationToken ct = default)
    {
        var payload = ApnsPayload.Serialize(push);
        logger.LogInformation(
            "APNs push (dev log transport). kind={Kind} groupId={GroupId} device={Device}",
            push.Kind, push.GroupId, Truncate(push.DeviceToken));
        logger.LogDebug("APNs payload {Payload}", payload);
        return Task.CompletedTask;
    }

    private static string Truncate(string token)
        => token.Length <= 8 ? token : token[..8] + "…";
}
