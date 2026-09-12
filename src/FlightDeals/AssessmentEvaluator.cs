namespace FlightDeals;

public static class AssessmentEvaluator
{
    public static CabinComposition Describe(IEnumerable<Segment> segments, Cabin requested)
    {
        var cabins = segments.Select(s => s.Cabin).ToArray();
        if (cabins.Length == 0 || cabins.Contains(Cabin.Unknown)) return CabinComposition.Unknown;
        if (cabins.All(c => c == requested)) return CabinComposition.AllSegmentsInRequestedCabin;
        return cabins.Distinct().Count() > 1 ? CabinComposition.MixedCabin : CabinComposition.OtherCabin;
    }

    public static Assessment Evaluate(SearchProfile profile, Journey outbound, Journey inbound,
        decimal price, IEnumerable<ManualBaseline> baselines, DateTimeOffset now)
    {
        var segments = outbound.Segments.Concat(inbound.Segments).ToArray();
        var composition = Describe(segments, profile.RequestedCabin);
        var applicable = baselines
            .Where(baseline => MatchesSearch(baseline, profile, outbound, inbound))
            .Where(baseline => baseline.Thresholds.Complete)
            .Where(baseline => AppliesToCabins(baseline, composition, segments, profile.RequestedCabin))
            .ToArray();

        var assessment = CreateUnclassifiedAssessment(profile, price, composition, now);
        if (applicable.Length != 1)
            return assessment with { Reasons = ExplainMissingBaseline(applicable.Length, composition) };

        return ApplyManualBaseline(assessment, applicable[0]);
    }

    private static bool MatchesSearch(ManualBaseline baseline, SearchProfile profile,
        Journey outbound, Journey inbound)
    {
        var tripDays = profile.ReturnDate.DayNumber - profile.OutboundDate.DayNumber;
        var destination = outbound.Segments[^1].ArrivalAirport;
        var returnOrigin = inbound.Segments[0].DepartureAirport;
        var matchesMarket = baseline.Origin == profile.Origin
            && baseline.Destinations.Contains(destination)
            && baseline.Destinations.Contains(returnOrigin);
        var matchesProduct = baseline.RequestedCabin == profile.RequestedCabin
            && baseline.Currency == profile.Currency;
        var matchesDuration = tripDays >= baseline.MinTripDays && tripDays <= baseline.MaxTripDays;
        return matchesMarket && matchesProduct && matchesDuration;
    }

    private static bool AppliesToCabins(ManualBaseline baseline, CabinComposition composition,
        Segment[] segments, Cabin requested)
    {
        // Main-long-haul applicability is representable but deliberately not evaluated yet.
        return baseline.Applicability switch
        {
            CabinApplicability.AllSegmentsInRequestedCabin =>
                composition == CabinComposition.AllSegmentsInRequestedCabin,
            CabinApplicability.MixedCabin => composition == CabinComposition.MixedCabin
                && segments.Any(segment => segment.Cabin == requested),
            _ => false
        };
    }

    private static Assessment CreateUnclassifiedAssessment(SearchProfile profile, decimal price,
        CabinComposition composition, DateTimeOffset now) => new(
            Classification: Classification.InsufficientBaseline,
            Source: AssessmentSource.None,
            BaselineId: null,
            BaselineVersion: null,
            AppliedThresholds: null,
            BaselineAssumption: null,
            TotalPartyPrice: price,
            AdultCount: profile.Adults,
            PricePerAdult: price / profile.Adults,
            Currency: profile.Currency,
            CabinComposition: composition,
            Reasons: [],
            Limitations:
            [
                "Quoted search price; not verified at checkout.",
                "Baggage information is optional and unverified.",
                "Connection protection is unknown.",
                "Local timestamps may have no time-zone offset.",
                "Premium cabin location and overnight value have not been scored; classification describes price, not premium-itinerary quality."
            ],
            EvaluatedAt: now);

    private static string[] ExplainMissingBaseline(int applicableCount, CabinComposition composition)
    {
        var reason = applicableCount > 1
            ? "Multiple baselines apply; assessment is ambiguous."
            : "Insufficient baseline/history: no complete, supported manual baseline applies.";
        return [reason, $"Actual cabin composition: {composition}; no mixed-cabin penalty applied."];
    }

    private static Assessment ApplyManualBaseline(Assessment assessment, ManualBaseline baseline) => assessment with
    {
        Classification = ClassifyPrice(assessment.PricePerAdult, baseline.Thresholds),
        Source = AssessmentSource.ManualBaseline,
        BaselineId = baseline.Id,
        BaselineVersion = baseline.Version,
        AppliedThresholds = baseline.Thresholds,
        BaselineAssumption = baseline.Assumption,
        Reasons =
        [
            "Compared against manually configured per-adult thresholds, not learned market history.",
            $"Cabin applicability: {baseline.Applicability}; no mixed-cabin penalty applied."
        ]
    };

    private static Classification ClassifyPrice(decimal perAdult, Thresholds thresholds)
    {
        if (perAdult < thresholds.DealBelow) return Classification.Deal;
        if (perAdult < thresholds.CheapBelow) return Classification.Cheap;
        if (perAdult < thresholds.ExpensiveAtOrAbove) return Classification.Normal;
        return Classification.Expensive;
    }
}
