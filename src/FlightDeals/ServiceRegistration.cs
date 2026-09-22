using System.Text.Json.Serialization;
using Npgsql;

namespace FlightDeals;

public static class ServiceRegistration
{
    public static IServiceCollection AddFlightDeals(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("FlightDeals").Get<AppSettings>() ?? new();
        settings.Validate();
        var connectionString = configuration.GetConnectionString("FlightDeals")
            ?? throw new InvalidOperationException("Set ConnectionStrings__FlightDeals to your project PostgreSQL database.");
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<PostgresStore>();
        services.AddSingleton<IScanStore>(s => s.GetRequiredService<PostgresStore>());
        AddSerpApi(services);
        services.AddSingleton<ScanService>();
        services.AddHostedService<MonitoringWorker>();
        var notifications = configuration.GetSection("Notifications").Get<NotificationSettings>() ?? new();
        notifications.Validate();
        services.AddSingleton(notifications);
        services.AddSingleton<NotificationStore>();
        services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        services.AddSingleton<NotificationProcessor>();
        services.AddHostedService<NotificationWorker>();
        var digests = configuration.GetSection("WeeklyDigest").Get<WeeklyDigestSettings>() ?? new();
        digests.Validate();
        if (digests.Enabled) notifications.ValidateSmtp();
        services.AddSingleton(digests);
        services.AddSingleton<WeeklyDigestStore>();
        services.AddSingleton<IWeeklyDigestSender, SmtpNotificationSender>();
        services.AddSingleton<WeeklyDigestProcessor>();
        services.AddHostedService<WeeklyDigestWorker>();
        return services;
    }

    private static void AddSerpApi(IServiceCollection services)
    {
        // Direct HttpClient avoids factory URI logging: the provider requires the secret in its query string.
        // No retry handler, redirects, cookies, or automatic background calls.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
            { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(90) });
        services.AddSingleton<IFlightProvider>(s => new SerpApiProvider(s.GetRequiredService<HttpClient>(),
            Environment.GetEnvironmentVariable("SERPAPI_API_KEY") ?? ""));
    }
}
