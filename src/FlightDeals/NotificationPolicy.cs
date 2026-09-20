using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlightDeals;

public enum DeliveryStatus { Suppressed, Pending, Sending, Sent, Failed, Unknown }

public sealed record NotificationState
{
    public DateTimeOffset EnabledAt { get; init; }
    public Guid? LastProcessedObservationId { get; init; }
    public Classification? LastClassification { get; init; }
    public Guid? LastNotifiedObservationId { get; init; }
    public Classification? LastNotifiedClassification { get; init; }
    public decimal? LastNotifiedPricePerAdult { get; init; }
    public DateTimeOffset? LastNotifiedAt { get; init; }
}

public sealed record NotificationDecision(bool Send, string Reason);
public sealed record NotificationRecord(Guid ObservationId, string Scope, DeliveryStatus Status,
    string Reason, string MessageId, DateTimeOffset CreatedAt,
    DateTimeOffset? AttemptedAt = null, DateTimeOffset? SentAt = null);

public static class NotificationPolicy
{
    // Include the complete configured product so date/currency/passenger changes reset comparisons.
    public static string Scope(SearchProfile profile) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, JsonDefaults.Options))));

    public static NotificationDecision Evaluate(Observation observation, NotificationState state,
        decimal improvementFraction, bool unresolvedDelivery)
    {
        var classification = observation.Assessment.Classification;
        if (!Interesting(classification)) return new(false, "Classification does not warrant an alert.");
        var segments = observation.Outbound.Segments.Concat(observation.Return.Segments).ToArray();
        var premium = observation.Profile.RequestedCabin is Cabin.PremiumEconomy or Cabin.Business or Cabin.First;
        if (premium && (segments.Length == 0 || segments.Any(s => s.Cabin != observation.Profile.RequestedCabin)))
            return new(false, "Premium placement on the main long-haul segments is not yet verified.");
        if (unresolvedDelivery) return new(false, "An earlier delivery requires review; further alerts are blocked.");
        if (state.LastNotifiedObservationId is null) return new(true, "First eligible fare.");
        if (state.LastClassification is null || !Interesting(state.LastClassification.Value))
            return new(true, "Classification entered Cheap or Deal.");
        if (state.LastClassification == Classification.Cheap && classification == Classification.Deal)
            return new(true, "Classification improved from Cheap to Deal.");
        var targetPrice = state.LastNotifiedPricePerAdult * (1 - improvementFraction);
        if (observation.PricePerAdult <= targetPrice)
            return new(true, "Price improved materially against the last successfully notified fare.");
        return new(false, "No qualifying transition or material price improvement.");
    }

    private static bool Interesting(Classification value) => value is Classification.Cheap or Classification.Deal;
}
