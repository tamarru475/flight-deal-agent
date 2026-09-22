using FlightDeals;
using Xunit;

namespace FlightDeals.Tests;

public class DigestComparisonKeyTests
{
    [Fact]
    public void SearchDefinitionComparesEveryExistingFieldAndDestinationOrder()
    {
        var profile = TestData.Profile;
        var key = DigestSearchDefinitionKey.From(profile);
        var copy = DigestSearchDefinitionKey.From(profile with { Destinations = [.. profile.Destinations] });
        Assert.Equal(key, copy);
        Assert.Equal(key.GetHashCode(), copy.GetHashCode());
        SearchProfile[] different =
        [
            profile with { Id = "other" }, profile with { Origin = "WLG" },
            profile with { Destinations = ["HND", "NRT"] },
            profile with { OutboundDate = profile.OutboundDate.AddDays(1) },
            profile with { ReturnDate = profile.ReturnDate.AddDays(1) },
            profile with { MinTripDays = profile.MinTripDays + 1 },
            profile with { MaxTripDays = profile.MaxTripDays + 1 },
            profile with { Adults = 1 }, profile with { Currency = "USD" },
            profile with { RequestedCabin = Cabin.Business }, profile with { MaxStops = 2 },
            profile with { MaxDurationMinutes = profile.MaxDurationMinutes + 1 },
            profile with { ExcludeKnownSeparateTickets = false },
            profile with { ExcludeAirportChanges = false }
        ];
        foreach (var changed in different) Assert.NotEqual(key, DigestSearchDefinitionKey.From(changed));
    }

    [Fact]
    public void ProductComparisonPreservesNoteSortingDuplicatesAndSegmentFields()
    {
        var original = WeeklyDigestTests.Fare(1, 3500);
        var quote = original with { Outbound = original.Outbound with { FareNotes = ["b", "a"] } };
        var key = DigestComparableProductKey.From(quote);
        var reordered = quote with { Outbound = quote.Outbound with { FareNotes = ["a", "b"] } };
        Assert.Equal(key, DigestComparableProductKey.From(reordered));
        Assert.Equal(key.GetHashCode(), DigestComparableProductKey.From(reordered).GetHashCode());
        var duplicate = quote with { Outbound = quote.Outbound with { FareNotes = ["a", "b", "b"] } };
        Assert.NotEqual(key, DigestComparableProductKey.From(duplicate));
        Assert.NotEqual(key, DigestComparableProductKey.From(quote with { BaggageStatus = "Different" }));
        Assert.NotEqual(key, DigestComparableProductKey.From(quote with { ConnectionProtection = "Different" }));
        Assert.NotEqual(key, DigestComparableProductKey.From(quote with { Return = quote.Return with { FareNotes = ["note"] } }));

        var segment = quote.Outbound.Segments[0];
        Segment[] changes = [segment with { DepartureAirport = "WLG" }, segment with { ArrivalAirport = "HND" },
            segment with { Airline = "Other" }, segment with { Cabin = Cabin.Business }];
        foreach (var changed in changes)
        {
            Assert.NotEqual(key, DigestComparableProductKey.From(quote with { Outbound = quote.Outbound with { Segments = [changed] } }));
            Assert.NotEqual(key, DigestComparableProductKey.From(quote with { Return = quote.Return with { Segments = [changed] } }));
        }
        var twoSegments = quote with { Outbound = quote.Outbound with { Segments = [segment, changes[0]] } };
        var reversed = quote with { Outbound = quote.Outbound with { Segments = [changes[0], segment] } };
        Assert.NotEqual(DigestComparableProductKey.From(twoSegments), DigestComparableProductKey.From(reversed));

        // These fields did not participate in the previous comparison; this cleanup must not add them.
        var changedTiming = segment with { DurationMinutes = 999, DepartureLocal = "different", FlightNumber = "other" };
        Assert.Equal(key, DigestComparableProductKey.From(quote with
        {
            PricePerAdult = 1, Outbound = quote.Outbound with { Segments = [changedTiming] }
        }));
    }
}
