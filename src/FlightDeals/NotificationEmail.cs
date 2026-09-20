using System.Net;
using System.Net.Mail;
using System.Text;

namespace FlightDeals;

public sealed class NotificationSettings
{
    public bool Enabled { get; init; }
    public decimal MaterialImprovementFraction { get; init; } = 0.10m;
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public string SmtpUsername { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Recipient { get; init; } = "";

    public void Validate()
    {
        if (MaterialImprovementFraction is <= 0 or >= 1)
            throw new InvalidOperationException("Notification improvement fraction must be between zero and one.");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(SmtpHost) || SmtpPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(SmtpUsername) || string.IsNullOrWhiteSpace(SmtpPassword) ||
            !MailAddress.TryCreate(Sender, out _) || !MailAddress.TryCreate(Recipient, out _) ||
            Sender.Contains('\n') || Sender.Contains('\r') || Recipient.Contains('\n') || Recipient.Contains('\r'))
            throw new InvalidOperationException("Enabled notifications require valid private SMTP settings and sender/recipient addresses.");
    }
}

public sealed record NotificationEmail(string Subject, string Body);
public interface INotificationSender
{
    Task<DeliveryStatus> Send(NotificationRecord notification, NotificationEmail email, CancellationToken ct);
}

public static class NotificationEmailBuilder
{
    public static NotificationEmail Build(Observation observation)
    {
        var profile = observation.Profile;
        var assessment = observation.Assessment;
        var route = $"{profile.Origin} → {string.Join('/', profile.Destinations)}";
        var body = new StringBuilder();
        body.AppendLine($"Profile: {profile.Id} ({route})");
        body.AppendLine($"Classification: {assessment.Classification}");
        body.AppendLine(FormattableString.Invariant($"Quoted party total: {profile.Currency} {observation.TotalPartyPrice:F2} ({profile.Adults} adults)"));
        body.AppendLine(FormattableString.Invariant($"Per adult: {profile.Currency} {observation.PricePerAdult:F2}"));
        body.AppendLine($"Travel: {profile.OutboundDate:yyyy-MM-dd} to {profile.ReturnDate:yyyy-MM-dd}");
        body.AppendLine($"Observed: {observation.ObservedAt:O}; quote is not a booking or verified availability.");
        body.AppendLine($"Requested cabin: {profile.RequestedCabin}; composition: {assessment.CabinComposition}");
        AppendJourney(body, "Outbound", observation.Outbound);
        AppendJourney(body, "Return", observation.Return);
        body.AppendLine($"Baseline: {assessment.Source} ({assessment.BaselineId}, version {assessment.BaselineVersion})");
        body.AppendLine(assessment.BaselineAssumption);
        foreach (var reason in assessment.Reasons) body.AppendLine(reason);
        body.AppendLine($"Baggage: {observation.BaggageStatus}; connection protection: {observation.ConnectionProtection}.");
        foreach (var limitation in assessment.Limitations) body.AppendLine(limitation);
        body.AppendLine($"Observation: {observation.Id}");
        return new($"Flight deal: {assessment.Classification} — {profile.Id}", body.ToString());
    }

    private static void AppendJourney(StringBuilder body, string label, Journey journey)
    {
        body.AppendLine($"{label}: {journey.DurationMinutes} minutes total");
        foreach (var segment in journey.Segments)
            body.AppendLine($"  {segment.DepartureAirport} → {segment.ArrivalAirport}: {segment.Airline} {segment.FlightNumber}; " +
                $"{segment.Cabin}, {segment.DurationMinutes} minutes; local times {segment.DepartureLocal} → {segment.ArrivalLocal}");
        foreach (var connection in journey.Connections)
            body.AppendLine($"  Connection {connection.ArrivalAirport} → {connection.DepartureAirport}; " +
                $"reported wait {connection.ReportedDurationMinutes} minutes; airport change: {connection.AirportChange}");
    }
}

public sealed class SmtpNotificationSender(NotificationSettings settings) : INotificationSender
{
    public async Task<DeliveryStatus> Send(NotificationRecord notification, NotificationEmail email, CancellationToken ct)
    {
        if (!settings.Enabled) return DeliveryStatus.Failed;
        using var message = new MailMessage(settings.Sender, settings.Recipient, email.Subject, email.Body);
        message.Headers.Add("Message-ID", notification.MessageId);
        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = true, // Require STARTTLS; never fall back to plaintext SMTP.
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await client.SendMailAsync(message, timeout.Token);
            return DeliveryStatus.Sent;
        }
        catch
        {
            // SMTP cannot guarantee exactly-once delivery. Even a lost acceptance response is ambiguous.
            // Never expose server responses, credentials, recipient addresses, or exception text in logs.
            return DeliveryStatus.Unknown;
        }
    }
}
