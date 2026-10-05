using System.Text.Json;
using FlightDeals;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FlightDeals.Tests;

public class ConfigurationLoadingTests
{
    [Fact]
    public void ExtractedConfigurationMatchesCompleteOrderedLegacyConfiguration()
    {
        var legacy = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Fixtures/configuration-before-split.json").Build();
        var expected = legacy.GetSection("FlightDeals").Get<AppSettings>()!;
        var actual = HistoricalSamplingTests.Settings();
        Assert.Equal(JsonSerializer.Serialize(expected, JsonDefaults.Options), JsonSerializer.Serialize(actual, JsonDefaults.Options));
        Assert.Equal(JsonSerializer.Serialize(expected, JsonDefaults.Options),
            JsonSerializer.Serialize(FlightDealsConfiguration.Load(legacy, AppContext.BaseDirectory), JsonDefaults.Options));
        Assert.Equal("tokyo-premium", actual.DefaultProfile.Id);
    }

    [Theory]
    [InlineData("Profiles:0:Search:Id", "conflict")]
    [InlineData("Profile:Id", "conflict")]
    [InlineData("ManualBaselines:0:Id", "conflict")]
    public void InlineAndFileDefinitionsCannotBeCombined(string key, string value)
    {
        var config = Builder().AddInMemoryCollection(new Dictionary<string, string?> { ["FlightDeals:" + key] = value }).Build();
        Assert.Contains("not both", Assert.Throws<ScanException>(() => FlightDealsConfiguration.Load(config, AppContext.BaseDirectory)).Message);
    }

    [Fact]
    public void HigherPriorityProviderReplacesWholeFileListRatherThanMergingIndices()
    {
        var config = Builder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FlightDeals:ProfileFiles:0"] = "config/profiles/core.json" }).Build();
        var settings = FlightDealsConfiguration.Load(config, AppContext.BaseDirectory);
        Assert.Equal(2, settings.Profiles.Length);
        Assert.Equal("tokyo-premium", settings.Profiles[0].Search.Id);
        Assert.Equal("europe-economy", settings.Profiles[1].Search.Id);
    }

    [Fact]
    public void ExplicitlyEmptyInlineArrayStillConflictsWithFiles()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"FlightDeals\":{\"Profiles\":[]}}"));
        var config = Builder().AddJsonStream(json).Build();
        Assert.Contains("not both", Assert.Throws<ScanException>(() =>
            FlightDealsConfiguration.Load(config, AppContext.BaseDirectory)).Message);
    }

    [Fact]
    public void BaselinesCanUseFilesWhileProfilesRemainInline()
    {
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Fixtures/configuration-before-split.json").Build();
        var source = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/configuration-before-split.json")))!;
        var flight = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source["FlightDeals"].GetRawText())!;
        flight.Remove("ManualBaselines");
        flight["BaselineFile"] = JsonSerializer.SerializeToElement("config/baselines.json");
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { FlightDeals = flight }));
        var mixed = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal(JsonSerializer.Serialize(FlightDealsConfiguration.Load(config, AppContext.BaseDirectory), JsonDefaults.Options),
            JsonSerializer.Serialize(FlightDealsConfiguration.Load(mixed, AppContext.BaseDirectory), JsonDefaults.Options));
    }

    [Fact]
    public void MissingFileFailsClearly()
    {
        var config = Builder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FlightDeals:BaselineFile"] = "missing-baselines.json" }).Build();
        Assert.Contains("Cannot read configuration data file", Assert.Throws<ScanException>(() =>
            FlightDealsConfiguration.Load(config, AppContext.BaseDirectory)).Message);
    }

    [Fact]
    public void MalformedFileFailsWithoutEchoingItsContents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "broken.json"), "sensitive invalid JSON");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FlightDeals:ProfileFiles:0"] = "broken.json" }).Build();
            var error = Assert.Throws<ScanException>(() => FlightDealsConfiguration.Load(config, directory));
            Assert.Contains("Malformed configuration JSON: broken.json", error.Message);
            Assert.DoesNotContain("sensitive", error.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DuplicateProfilesAcrossFilesFailValidation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlightDeals:ProfileFiles:0"] = "config/profiles/core.json",
            ["FlightDeals:ProfileFiles:1"] = "config/profiles/core.json"
        }).Build();
        Assert.Contains("IDs must be unique", Assert.Throws<ScanException>(() =>
            FlightDealsConfiguration.Load(config, AppContext.BaseDirectory)).Message);
    }

    [Fact]
    public void EnvironmentOverridesStillControlApplicationSettings()
    {
        var prefix = "FLIGHT_TEST_" + Guid.NewGuid().ToString("N") + "_";
        Environment.SetEnvironmentVariable(prefix + "FlightDeals__LiveSearchEnabled", "true");
        Environment.SetEnvironmentVariable(prefix + "FlightDeals__MonthlyCreditLimit", "190");
        try
        {
            var settings = FlightDealsConfiguration.Load(Builder().AddEnvironmentVariables(prefix).Build(), AppContext.BaseDirectory);
            Assert.True(settings.LiveSearchEnabled);
            Assert.Equal(190, settings.MonthlyCreditLimit);
            Assert.Equal(23, settings.Profiles.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "FlightDeals__LiveSearchEnabled", null);
            Environment.SetEnvironmentVariable(prefix + "FlightDeals__MonthlyCreditLimit", null);
        }
    }

    [Fact]
    public void InvalidApplicationSettingsStillFailValidation()
    {
        var config = Builder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FlightDeals:MonthlyCreditLimit"] = "201" }).Build();
        Assert.Throws<ScanException>(() => FlightDealsConfiguration.Load(config, AppContext.BaseDirectory));
    }

    private static IConfigurationBuilder Builder() => new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json");
}
