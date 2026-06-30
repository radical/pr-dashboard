using Microsoft.Extensions.Options;

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
            var triage = await store.ReadTriageAsync(cancellationToken);
            var shepherd = await store.ReadBotShepherdAsync(cancellationToken);
            return Results.Ok(new CiHealthResponse(pulse, weekly, triage, shepherd));
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

        // Auth-gated: a signed-in user triggers an on-demand LLM triage of the currently-failing lanes.
        // "For now" this shells out to the Copilot CLI (CiTriageRunner); disabled unless CiHealth:Triage
        // is enabled (the deployed env has no CLI). 401 when no GitHub token is resolvable so it can't be
        // driven anonymously; 503 when triage is disabled.
        endpoints.MapPost("/api/ci-health/triage", async (
            CiHealthSnapshotStore store,
            CiTriageRunner runner,
            GitHubTokenProvider tokenProvider,
            IOptions<CiHealthOptions> options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Triage.Enabled)
            {
                return Results.Problem("CI triage is disabled (set CiHealth:Triage:Enabled).", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (await tokenProvider.GetTokenAsync(cancellationToken) is null)
            {
                return Results.Unauthorized();
            }

            var pulse = await store.ReadPulseAsync(cancellationToken);
            var weekly = await store.ReadWeeklyAsync(cancellationToken);
            var triage = await runner.RunAsync(pulse?.FailingNow ?? [], timeProvider.GetUtcNow(), cancellationToken);
            var shepherd = await store.ReadBotShepherdAsync(cancellationToken);
            return Results.Ok(new CiHealthResponse(pulse, weekly, triage, shepherd));
        });

        // Auth-gated: a signed-in user triggers an on-demand LLM shepherd of the open bot PRs/issues.
        // Shells out to the Copilot CLI (BotShepherdRunner) but caches by input fingerprint, so a re-run
        // with no relevant change returns the cached snapshot without invoking the CLI. Same gating as
        // triage: 401 without a GitHub token, 503 when triage is disabled.
        endpoints.MapPost("/api/ci-health/shepherd", async (
            CiHealthSnapshotStore store,
            BotShepherdRunner shepherdRunner,
            GitHubTokenProvider tokenProvider,
            IOptions<CiHealthOptions> options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Triage.Enabled)
            {
                return Results.Problem("CI triage is disabled (set CiHealth:Triage:Enabled).", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (await tokenProvider.GetTokenAsync(cancellationToken) is null)
            {
                return Results.Unauthorized();
            }

            var pulse = await store.ReadPulseAsync(cancellationToken);
            var weekly = await store.ReadWeeklyAsync(cancellationToken);
            var triage = await store.ReadTriageAsync(cancellationToken);
            var shepherd = await shepherdRunner.RunAsync(
                pulse?.BotPrs ?? [], pulse?.BotIssues ?? [], timeProvider.GetUtcNow(), cancellationToken);
            return Results.Ok(new CiHealthResponse(pulse, weekly, triage, shepherd));
        });

        return endpoints;
    }
}
