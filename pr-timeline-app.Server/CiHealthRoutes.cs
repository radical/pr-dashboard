public static class CiHealthRoutes
{
    public static IEndpointRouteBuilder MapCiHealthRoutes(this IEndpointRouteBuilder endpoints)
    {
        // Public by design: serves the shared, server-computed snapshots to every visitor.
        endpoints.MapGet("/api/ci-health", async (
            CiHealthSnapshotStore store,
            CancellationToken cancellationToken) =>
        {
            var pulse = await store.ReadPulseAsync(cancellationToken);
            var weekly = await store.ReadWeeklyAsync(cancellationToken);
            return Results.Ok(new CiHealthResponse(pulse, weekly));
        });

        // Auth-gated: a signed-in user triggers an on-demand pulse refresh using their own GitHub token
        // (the same token path PR fetching uses). 401 when no token is resolvable so the producer can't
        // be driven anonymously.
        endpoints.MapPost("/api/ci-health/refresh", async (
            CiHealthProducer producer,
            GitHubTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (await tokenProvider.GetTokenAsync(cancellationToken) is null)
            {
                return Results.Unauthorized();
            }

            var response = await producer.RefreshNowAsync(cancellationToken);
            return Results.Ok(response);
        });

        return endpoints;
    }
}
