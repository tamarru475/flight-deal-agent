using FlightDeals;

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--init-db").ToArray());
builder.Services.AddFlightDeals(builder.Configuration);

var app = builder.Build();
if (args.Contains("--init-db"))
{
    await app.Services.GetRequiredService<PostgresStore>().Initialize(CancellationToken.None);
    Console.WriteLine("Project database schema initialized.");
    return;
}

app.UseLocalFlightDealsApi();
app.MapFlightDealsEndpoints();
await app.RunAsync();

public partial class Program;
