using System.Text.Json;

namespace FlightDeals;

public static class FlightDealsConfiguration
{
    public static AppSettings Load(IConfiguration configuration, string contentRoot)
    {
        var section = configuration.GetSection("FlightDeals");
        var profileFiles = ProfileFiles(configuration);
        var baselineFile = section["BaselineFile"];
        if (profileFiles is not null && (Defined(configuration, "FlightDeals:Profiles") || Defined(configuration, "FlightDeals:Profile")))
            throw new ScanException("Configure profile files or inline profiles, not both.");
        if (baselineFile is not null && Defined(configuration, "FlightDeals:ManualBaselines"))
            throw new ScanException("Configure a baseline file or inline baselines, not both.");

        var inline = section.Get<AppSettings>() ?? new();
        var settings = new AppSettings
        {
            LiveSearchEnabled = inline.LiveSearchEnabled,
            SchedulerEnabled = inline.SchedulerEnabled,
            MonthlyCreditLimit = inline.MonthlyCreditLimit,
            ReserveCredits = inline.ReserveCredits,
            Profile = inline.Profile,
            Profiles = profileFiles is null ? inline.Profiles
                : profileFiles.SelectMany(path => Read<MonitoredProfile[]>(contentRoot, path)).ToArray(),
            ManualBaselines = baselineFile is null ? inline.ManualBaselines
                : Read<ManualBaseline[]>(contentRoot, baselineFile)
        };
        if (profileFiles is not null && settings.Profiles.Length == 0)
            throw new ScanException("Profile files must contain at least one profile.");
        if (settings.Profiles.Any(p => p is null || p.Search is null) || settings.ManualBaselines.Any(b => b is null))
            throw new ScanException("Profiles and baselines must not contain null entries.");
        settings.Validate();
        return settings;
    }

    private static bool Defined(IConfiguration configuration, string key) =>
        configuration is IConfigurationRoot root && root.Providers.Any(provider =>
            provider.TryGet(key, out _) || provider.GetChildKeys([], key).Any());

    private static string[]? ProfileFiles(IConfiguration configuration)
    {
        const string key = "FlightDeals:ProfileFiles";
        if (configuration is not IConfigurationRoot root)
            throw new ArgumentException("Pass the application configuration root.", nameof(configuration));
        // Choose one provider's entire list. Never merge array indices across providers.
        foreach (var provider in root.Providers.Reverse())
        {
            var children = provider.GetChildKeys([], key).Distinct().ToArray();
            if (children.Length == 0 && !provider.TryGet(key, out _)) continue;
            if (children.Length == 0) throw new ScanException("ProfileFiles must be a nonempty ordered list.");
            var paths = new List<string>();
            for (var index = 0; index < children.Length; index++)
            {
                if (!provider.TryGet($"{key}:{index}", out var path) || string.IsNullOrWhiteSpace(path))
                    throw new ScanException("ProfileFiles must use consecutive indices and nonempty paths.");
                paths.Add(path);
            }
            return paths.ToArray();
        }
        return null;
    }

    private static T Read<T>(string contentRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ScanException("Configuration data paths must be nonempty and relative to the content root.");
        try
        {
            var json = File.ReadAllText(Path.Combine(contentRoot, relativePath));
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)
                ?? throw new ScanException($"Configuration data file is empty: {relativePath}");
        }
        catch (IOException) { throw new ScanException($"Cannot read configuration data file: {relativePath}"); }
        catch (UnauthorizedAccessException) { throw new ScanException($"Cannot read configuration data file: {relativePath}"); }
        catch (JsonException) { throw new ScanException($"Malformed configuration JSON: {relativePath}"); }
    }
}
