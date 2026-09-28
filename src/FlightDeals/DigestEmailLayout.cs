using System.Net;
using System.Text;

namespace FlightDeals;

// Both alternatives receive the same content; only typography differs.
internal sealed class DigestEmailLayout(string subject)
{
    private readonly StringBuilder text = new(subject + Environment.NewLine);
    private readonly StringBuilder html = new("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"></head>" +
        "<body style=\"margin:0;padding:24px;color:#202124;background:#ffffff;font-family:Arial,sans-serif;line-height:1.5\">" +
        "<div style=\"max-width:640px;margin:0 auto\"><h1 style=\"font-size:24px;margin:0 0 16px\">" + WebUtility.HtmlEncode(subject) + "</h1>");

    public void Heading(string value) => Add(value, "h2", "font-size:18px;margin:24px 0 8px", true);
    public void Route(string value) => Add(value, "h3", "font-size:16px;margin:20px 0 4px", true);
    public void Price(string value) => Add(value, "p", "font-size:18px;font-weight:bold;margin:0 0 4px");
    public void Line(string value) => Add(value, "p", "font-size:14px;margin:3px 0");

    private void Add(string value, string tag, string style, bool blank = false)
    {
        if (blank) text.AppendLine();
        text.AppendLine(value);
        html.Append($"<{tag} style=\"{style}\">{WebUtility.HtmlEncode(value)}</{tag}>");
    }

    public NotificationEmail Build() => new(subject, text.ToString(), html + "</div></body></html>");
}
