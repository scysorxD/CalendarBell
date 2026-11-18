using System.Text.Json;

namespace CalendarBell.Infrastructure.Storage;

/// <summary>
/// Development/testing overrides read from <c>CalendarBell.dev.json</c> next to the executable:
/// <c>{ "dataDirectory": "...", "allowHttpLocalhost": true }</c>. Needed because Windows relaunches the app for a
/// notification click without the developer's environment variables. The installer never copies this file, so an
/// installed copy cannot be affected by it.
/// </summary>
public sealed record DevOverrides(string? DataDirectory, bool AllowHttpLocalhost)
{
    public const string FileName = "CalendarBell.dev.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static DevOverrides? Current { get; } = Load(AppContext.BaseDirectory);

    public static DevOverrides? Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DevOverrides>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
