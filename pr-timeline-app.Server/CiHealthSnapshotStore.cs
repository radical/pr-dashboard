using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

// Reads/writes the two shared CI-health snapshots in the existing github-cache Blob container.
// Server-written only; served to every visitor. Reuses GitHubPublicCacheStore.ConnectionName so no
// new Aspire container/resource is needed. A missing snapshot returns null (first-run / not yet computed).
sealed class CiHealthSnapshotStore(
    [FromKeyedServices(GitHubPublicCacheStore.ConnectionName)] BlobContainerClient container)
{
    private const string PulseBlobName = "ci-health/pulse.json";
    private const string WeeklyBlobName = "ci-health/weekly.json";
    private const string TriageBlobName = "ci-health/triage.json";
    private const string TriageHistoryBlobName = "ci-health/triage-history.json";
    private const string BotShepherdBlobName = "ci-health/bot-shepherd.json";

    public Task<CiHealthPulseSnapshot?> ReadPulseAsync(CancellationToken cancellationToken) =>
        ReadAsync(PulseBlobName, CiHealthJsonContext.Default.CiHealthPulseSnapshot, cancellationToken);

    public Task WritePulseAsync(CiHealthPulseSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(PulseBlobName, snapshot, CiHealthJsonContext.Default.CiHealthPulseSnapshot, cancellationToken);

    public Task<CiHealthWeeklySnapshot?> ReadWeeklyAsync(CancellationToken cancellationToken) =>
        ReadAsync(WeeklyBlobName, CiHealthJsonContext.Default.CiHealthWeeklySnapshot, cancellationToken);

    public Task WriteWeeklyAsync(CiHealthWeeklySnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(WeeklyBlobName, snapshot, CiHealthJsonContext.Default.CiHealthWeeklySnapshot, cancellationToken);

    public Task<CiTriageSnapshot?> ReadTriageAsync(CancellationToken cancellationToken) =>
        ReadAsync(TriageBlobName, CiHealthJsonContext.Default.CiTriageSnapshot, cancellationToken);

    public Task WriteTriageAsync(CiTriageSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(TriageBlobName, snapshot, CiHealthJsonContext.Default.CiTriageSnapshot, cancellationToken);

    public Task<CiTriageHistory?> ReadTriageHistoryAsync(CancellationToken cancellationToken) =>
        ReadAsync(TriageHistoryBlobName, CiHealthJsonContext.Default.CiTriageHistory, cancellationToken);

    public Task WriteTriageHistoryAsync(CiTriageHistory history, CancellationToken cancellationToken) =>
        WriteAsync(TriageHistoryBlobName, history, CiHealthJsonContext.Default.CiTriageHistory, cancellationToken);

    public Task<BotShepherdSnapshot?> ReadBotShepherdAsync(CancellationToken cancellationToken) =>
        ReadAsync(BotShepherdBlobName, CiHealthJsonContext.Default.BotShepherdSnapshot, cancellationToken);

    public Task WriteBotShepherdAsync(BotShepherdSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(BotShepherdBlobName, snapshot, CiHealthJsonContext.Default.BotShepherdSnapshot, cancellationToken);

    private async Task<T?> ReadAsync<T>(
        string blobName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            var blob = container.GetBlobClient(blobName);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                return null;
            }

            var download = await blob.DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToObjectFromJson(typeInfo);
        }
        catch (RequestFailedException)
        {
            return null;
        }
    }

    private async Task WriteAsync<T>(
        string blobName,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var data = BinaryData.FromObjectAsJson(value, typeInfo);
        await container.GetBlobClient(blobName).UploadAsync(data, overwrite: true, cancellationToken);
    }
}
