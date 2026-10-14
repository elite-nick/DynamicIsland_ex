using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland;

sealed record WeatherPlace(string Name, string Country, string CountryCode, string Region, double Latitude, double Longitude)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Country)
        && CountryCode is { Length: 2 } && CountryCode.All(c => c is >= 'A' and <= 'Z')
        && double.IsFinite(Latitude) && Latitude is >= -90 and <= 90
        && double.IsFinite(Longitude) && Longitude is >= -180 and <= 180;
}

static class WeatherPlaces
{
    public sealed record Country(string Code, string Name);
    public static readonly Country[] Countries = BuildCountries();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static Country[] BuildCountries()
    {
        var countries = new Dictionary<string, Country>(StringComparer.OrdinalIgnoreCase);
        foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                string code = region.TwoLetterISORegionName;
                if (code.Length == 2 && code.All(c => c is >= 'A' and <= 'Z'))
                    countries.TryAdd(code, new Country(code, region.DisplayName));
            }
            catch (ArgumentException) { }
        }
        return countries.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<WeatherPlace[]> SearchAsync(string query, string countryCode, CancellationToken cancellation)
    {
        if (query.Trim().Length < 2 || countryCode.Length != 2) return [];
        string url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query.Trim())
            + "&count=50&language=ru&format=json&countryCode=" + Uri.EscapeDataString(countryCode);
        using var stream = await Http.GetStreamAsync(url, cancellation);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        return Parse(json.RootElement, countryCode);
    }

    internal static WeatherPlace[] Parse(JsonElement root, string countryCode)
    {
        if (!root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array) return [];
        var places = new List<WeatherPlace>();
        foreach (JsonElement item in results.EnumerateArray())
        {
            string code = Text(item, "country_code");
            if (!string.Equals(code, countryCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number || !lat.TryGetDouble(out double latitude)
                || !item.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number || !lon.TryGetDouble(out double longitude)) continue;
            var place = new WeatherPlace(Text(item, "name"), Text(item, "country"), code.ToUpperInvariant(),
                Text(item, "admin1"), latitude, longitude);
            if (place.IsValid) places.Add(place);
        }
        return places.Distinct().ToArray();
    }

    static string Text(JsonElement item, string property) => item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
