namespace FlightDeals;

public static class FlightDealsEndpoints
{
    public static WebApplication MapFlightDealsEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AppSettings>();
        app.MapGet("/health", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/notifications", (NotificationStore store, CancellationToken ct) => store.Recent(ct));
        app.MapGet("/profile", () => settings.DefaultProfile);
        app.MapGet("/profiles", () => settings.MonitoredProfiles);
        app.MapPost("/profiles/{profileId}/scans", async (string profileId, HttpRequest request, ScanService scans, CancellationToken ct) =>
        {
            if (request.Headers["X-Confirm-Scan"] != "true")
                return Results.BadRequest(new { error = "Send X-Confirm-Scan: true. A scan reserves at most two credits." });
            return Results.Ok(await scans.Run(profileId, ct));
        });
        app.MapGet("/baselines", () => settings.ManualBaselines);
        app.MapPost("/scans", async (HttpRequest request, ScanService scans, CancellationToken ct) =>
        {
            // Custom header prevents browser cross-origin forms from accidentally spending credits.
            if (request.Headers["X-Confirm-Scan"] != "true")
                return Results.BadRequest(new { error = "Send X-Confirm-Scan: true. A scan reserves at most two credits." });
            return Results.Ok(await scans.Run(ct));
        });
        app.MapGet("/runs", (PostgresStore store, CancellationToken ct) => store.Runs(ct));
        app.MapGet("/runs/{id:guid}", async (Guid id, PostgresStore store, CancellationToken ct) =>
            await store.Run(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        app.MapGet("/observations", (PostgresStore store, CancellationToken ct) => store.Observations(ct));
        app.MapGet("/assessments", async (PostgresStore store, CancellationToken ct) =>
            (await store.Observations(ct)).Select(o => new { o.Id, o.RunId, o.Assessment }));
        app.MapGet("/observations/{id:guid}", async (Guid id, PostgresStore store, CancellationToken ct) =>
            await store.Observation(id, ct) is { } observation ? Results.Ok(observation) : Results.NotFound());
        app.MapGet("/quota", async (PostgresStore store, IFlightProvider provider, CancellationToken ct) =>
            Results.Ok(new { provider = await provider.GetAccount(ct), local = await store.Quota(ct),
                operatingLimit = settings.MonthlyCreditLimit, reserve = settings.ReserveCredits }));
        return app;
    }
}
