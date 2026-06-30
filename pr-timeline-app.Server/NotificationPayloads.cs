using System.Text.Json;

// Outcome of attempting to deliver one push message. Expired means the push service reported
// the subscription is gone (404/410) and the caller should delete it.
enum PushDeliveryOutcome
{
    Sent,
    Expired,
    Failed,
    Disabled
}

readonly record struct PushDeliveryResult(PushDeliveryOutcome Outcome, int? StatusCode);

// Sends an already-built JSON payload to a single subscription. Behind an interface so the
// detector and the /test endpoint share one implementation and tests can substitute a fake.
interface IPushSender
{
    bool IsEnabled { get; }

    Task<PushDeliveryResult> SendAsync(
        PushSubscriptionRecord subscription,
        string payloadJson,
        CancellationToken cancellationToken);
}

// The notification payload contract shared with the service worker (src/sw.ts). Property
// names serialize to lowercase, matching the fields the SW reads in its `push` handler.
sealed record NotificationPayload(string Title, string Body, string Url, string Tag, string Icon);

static class NotificationPayloads
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private const string DefaultIcon = "/pwa-192x192.png";

    public static string Test() =>
        Serialize(new NotificationPayload(
            Title: "Aspire Team App",
            Body: "Test notification — push is working. \uD83C\uDF89",
            Url: "/",
            Tag: "aspire-team-app-test",
            Icon: DefaultIcon));

    public static string ReviewRequested(string repository, int number, string title, string url) =>
        Serialize(new NotificationPayload(
            Title: $"Review requested · {repository}#{number}",
            Body: string.IsNullOrWhiteSpace(title) ? "You were added as a reviewer." : title,
            Url: url,
            // Coalesce repeats for the same PR into one notification slot.
            Tag: $"review-requested:{repository}#{number}",
            Icon: DefaultIcon));

    public static string ReadyToMerge(ReadyToMergeRole role, string repository, int number, string title, string url) =>
        Serialize(new NotificationPayload(
            Title: $"Ready to merge · {repository}#{number}",
            Body: ReadyToMergeBody(role, title),
            Url: url,
            // Coalesce repeats for the same PR into one notification slot.
            Tag: $"ready-to-merge:{repository}#{number}",
            Icon: DefaultIcon));

    public static string BuildBroken(string repository, string lane, string url) =>
        Serialize(new NotificationPayload(
            Title: $"Build broken \u00b7 {repository}",
            Body: $"{lane} is red at tip.",
            Url: url,
            // Coalesce repeats for the same lane into one notification slot.
            Tag: $"build-broken:{repository}:{lane}",
            Icon: DefaultIcon));

    private static string ReadyToMergeBody(ReadyToMergeRole role, string title)
    {
        var prefix = role == ReadyToMergeRole.Author
            ? "Your PR is approved and ready to merge."
            : "A PR you approved is ready to merge.";
        return string.IsNullOrWhiteSpace(title) ? prefix : $"{prefix} {title}";
    }

    private static string Serialize(NotificationPayload payload) =>
        JsonSerializer.Serialize(payload, s_jsonOptions);
}
