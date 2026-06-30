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

        return endpoints;
    }
}
