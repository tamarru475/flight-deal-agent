namespace FlightDeals;

public static class LocalApiMiddleware
{
    public static WebApplication UseLocalFlightDealsApi(this WebApplication app)
    {
        // Explicitly local debugging API, including when someone changes the bind address.
        app.Use(async (context, next) =>
        {
            var ip = context.Connection.RemoteIpAddress;
            if (ip is not null && !System.Net.IPAddress.IsLoopback(ip))
            {
                context.Response.StatusCode = 403;
                return;
            }
            try { await next(context); }
            catch (ScanException e)
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = e.Message });
            }
            catch (Exception)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "Operation failed. Check database availability and configuration. No automatic retry was made." });
            }
        });
        return app;
    }
}
