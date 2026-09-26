using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightDeals;

public enum Cabin { Unknown, Economy, PremiumEconomy, Business, First }
public enum CabinApplicability { AllSegmentsInRequestedCabin, MainLongHaulSegmentsInRequestedCabin, MixedCabin }
public enum CabinComposition { Unknown, AllSegmentsInRequestedCabin, MixedCabin, OtherCabin }
public enum AssessmentSource { None, ManualBaseline, HistoricalBaseline }
public enum Classification { InsufficientBaseline, Deal, Cheap, Normal, Expensive }
public enum RunStatus { Running, Completed, NoSuitableOutbound, NoSuitableReturn, Failed }

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record Segment(string DepartureAirport, string ArrivalAirport,
    string? DepartureLocal, string? ArrivalLocal, int DurationMinutes,
    string? Airline, string? FlightNumber, Cabin Cabin, string? ReportedCabin, bool? Overnight);

public sealed record Connection(string ArrivalAirport, string DepartureAirport,
    bool AirportChange, int? ReportedDurationMinutes, string? ReportedAirport, string? ReportedName);

public sealed record Journey(int DurationMinutes, Segment[] Segments, Connection[] Connections,
    bool? SeparateTickets, string[] FareNotes);

// Tokens are ephemeral provider state. They must never be persisted or exposed through the API.
public sealed record FlightOption(decimal? Price, Journey Journey, [property: JsonIgnore] string? DepartureToken);

public sealed record Thresholds(decimal? DealBelow, decimal? CheapBelow, decimal? ExpensiveAtOrAbove)
{
    public bool Complete => DealBelow.HasValue && CheapBelow.HasValue && ExpensiveAtOrAbove.HasValue;
}

public sealed record ManualBaseline
{
    public string Id { get; init; } = "";
    public int Version { get; init; } = 1;
    public string Origin { get; init; } = "";
    public string[] Destinations { get; init; } = [];
    public string TripType { get; init; } = "Return";
    public int MinTripDays { get; init; }
    public int MaxTripDays { get; init; }
    public Cabin RequestedCabin { get; init; }
    public CabinApplicability Applicability { get; init; }
    public string PassengerType { get; init; } = "Adult";
    public string Currency { get; init; } = "NZD";
    public string PriceBasis { get; init; } = "PerAdult";
    public string BaggageBasis { get; init; } = "AsQuoted";
    public string Assumption { get; init; } = "Manually supplied starting assumption; not learned market truth.";
    public Thresholds Thresholds { get; init; } = new(null, null, null);
}

public sealed record SearchProfile
{
    public string Id { get; init; } = "tokyo-premium";
    public string Origin { get; init; } = "AKL";
    public string[] Destinations { get; init; } = ["NRT", "HND"];
    public DateOnly OutboundDate { get; init; }
    public DateOnly ReturnDate { get; init; }
    public int MinTripDays { get; init; } = 10;
    public int MaxTripDays { get; init; } = 30;
    public int Adults { get; init; } = 2;
    public string Currency { get; init; } = "NZD";
    public Cabin RequestedCabin { get; init; } = Cabin.PremiumEconomy;
    public int MaxStops { get; init; } = 1;
    public int MaxDurationMinutes { get; init; } = 1800;
    public bool ExcludeKnownSeparateTickets { get; init; } = true;
    public bool ExcludeAirportChanges { get; init; } = true;
    // Omit unset values so existing serialized search identities and notification scopes stay stable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequiredDestinationNights { get; init; }
}

public sealed record Assessment(Classification Classification, AssessmentSource Source,
    string? BaselineId, int? BaselineVersion, Thresholds? AppliedThresholds, string? BaselineAssumption,
    decimal TotalPartyPrice, int AdultCount, decimal PricePerAdult, string Currency,
    CabinComposition CabinComposition, string[] Reasons, string[] Limitations,
    DateTimeOffset EvaluatedAt, string EvaluatorVersion = "manual-v1");

public sealed record Observation(Guid Id, Guid RunId, DateTimeOffset ObservedAt,
    SearchProfile Profile, decimal TotalPartyPrice, decimal PricePerAdult,
    Journey Outbound, Journey Return, Assessment Assessment,
    string Provider = "SerpApi", string RetrievalStage = "ReturnOptions",
    string BaggageStatus = "IncompleteUnverified", string ConnectionProtection = "Unknown");

public sealed record SearchAttempt(int Number, string Stage, DateTimeOffset StartedAt, string Status);
public sealed record ScanRun(Guid Id, DateTimeOffset StartedAt, SearchProfile Profile,
    RunStatus Status, int ReservedCredits, SearchAttempt[] Attempts,
    Guid? ObservationId = null, string? Message = null, DateTimeOffset? FinishedAt = null);

public sealed class ScanException(string message) : Exception(message);
