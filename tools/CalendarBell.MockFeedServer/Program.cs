// CalendarBell mock ICS feed server (development and smoke tests only).
//
//   dotnet run --project tools/CalendarBell.MockFeedServer -- --urls http://localhost:5199
//
//   GET  /a.ics, /b.ics, /{name}.ics          current feed (ETag + 304 supported; any ?token=... is ignored)
//   POST /{name}/events?inSeconds=930&minutes=30&busy=BUSY&uid=x&title=T   add/replace an event starting now+inSeconds
//   POST /{name}/move?uid=x&inSeconds=...     move an event (start-time change)
//   POST /{name}/cancel?uid=x                 mark STATUS:CANCELLED
//   POST /{name}/clear                        remove all events (valid empty feed)
//   GET  /status/{code}                       reply with that HTTP status
//   GET  /invalid.ics                         200 OK with an HTML sign-in page
//   GET  /slow.ics                            waits 30 s (exercises the 15 s timeout)
//   GET  /fixtures/{file}                     serves files from ../../fixtures
//
// Only synthetic "Calendar A" / "Calendar B" data. Never put real calendar URLs here.
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var feeds = new ConcurrentDictionary<string, ConcurrentDictionary<string, MockEvent>>(StringComparer.OrdinalIgnoreCase);
var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures"));

app.MapGet("/{name}.ics", (string name, HttpRequest request) =>
{
    if (name.Equals("invalid", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Content("<!DOCTYPE html><html><body>Please sign in</body></html>", "text/html");
    }

    var body = Render(name, feeds.GetOrAdd(name, _ => new()));
    var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)).AsSpan(0, 8)) + "\"";
    if (request.Headers.IfNoneMatch.ToString() == etag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(body, "text/calendar; charset=utf-8", Encoding.UTF8, statusCode: 200) is var result
        ? new EtagResult(result, etag)
        : result;
});

app.MapGet("/slow.ics", async (CancellationToken ct) =>
{
    await Task.Delay(TimeSpan.FromSeconds(30), ct);
    return Results.Text(Render("slow", new()), "text/calendar");
});

app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));

app.MapGet("/fixtures/{file}", (string file) =>
{
    var path = Path.Combine(fixtures, Path.GetFileName(file));
    return File.Exists(path) ? Results.File(path, "text/calendar") : Results.NotFound();
});

app.MapPost("/{name}/events", (string name, int inSeconds, int? minutes, string? busy, string? uid, string? title, string? status) =>
{
    var feed = feeds.GetOrAdd(name, _ => new());
    var start = DateTimeOffset.UtcNow.AddSeconds(inSeconds);
    start = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second, TimeSpan.Zero);
    var e = new MockEvent(uid ?? Guid.NewGuid().ToString("N") + "@" + name + ".example", start, start.AddMinutes(minutes ?? 30), busy ?? "BUSY", title ?? "Busy", status);
    feed[e.Uid] = e;
    return Results.Ok(new { e.Uid, StartUtc = e.Start });
});

app.MapPost("/{name}/move", (string name, string uid, int inSeconds) =>
{
    var feed = feeds.GetOrAdd(name, _ => new());
    if (!feed.TryGetValue(uid, out var e))
    {
        return Results.NotFound();
    }

    var start = DateTimeOffset.UtcNow.AddSeconds(inSeconds);
    feed[uid] = e with { Start = start, End = start + (e.End - e.Start) };
    return Results.Ok(new { uid, StartUtc = start });
});

app.MapPost("/{name}/cancel", (string name, string uid) =>
{
    var feed = feeds.GetOrAdd(name, _ => new());
    if (!feed.TryGetValue(uid, out var e))
    {
        return Results.NotFound();
    }

    feed[uid] = e with { Status = "CANCELLED" };
    return Results.Ok();
});

app.MapPost("/{name}/clear", (string name) =>
{
    feeds.GetOrAdd(name, _ => new()).Clear();
    return Results.Ok();
});

app.Run();

static string Render(string name, ConcurrentDictionary<string, MockEvent> events)
{
    static string F(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    var sb = new StringBuilder();
    sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CalendarBell//Mock feed//EN\r\n");
    sb.Append("X-WR-CALNAME:Calendar ").Append(name.ToUpperInvariant()).Append("\r\n");
    foreach (var e in events.Values.OrderBy(e => e.Start))
    {
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append("UID:").Append(e.Uid).Append("\r\n");
        sb.Append("DTSTAMP:").Append(F(e.Created)).Append("\r\n");
        sb.Append("DTSTART:").Append(F(e.Start)).Append("\r\n");
        sb.Append("DTEND:").Append(F(e.End)).Append("\r\n");
        sb.Append("SUMMARY:").Append(e.Title.Replace(",", "\\,", StringComparison.Ordinal)).Append("\r\n");
        sb.Append("X-MICROSOFT-CDO-BUSYSTATUS:").Append(e.Busy).Append("\r\n");
        if (e.Status is not null)
        {
            sb.Append("STATUS:").Append(e.Status).Append("\r\n");
        }

        sb.Append("END:VEVENT\r\n");
    }

    sb.Append("END:VCALENDAR\r\n");
    return sb.ToString();
}

internal sealed record MockEvent(string Uid, DateTimeOffset Start, DateTimeOffset End, string Busy, string Title, string? Status)
{
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class EtagResult(IResult inner, string etag) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = etag;
        return inner.ExecuteAsync(httpContext);
    }
}
