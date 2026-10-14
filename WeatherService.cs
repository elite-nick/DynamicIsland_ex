using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace DynamicIsland;

enum Sky
{
    Clear,
    Cloudy,
    Overcast,
    Fog,
    Rain,
    Snow,
    Storm,
}

sealed class WeatherService
{
    public enum Stage { Off, Loading, Ready, NoPlace, ChoosePlace, Failed }

    public readonly record struct Hour(int At, int Degrees, Sky Sky, bool Night);

    public sealed record Report(int Degrees, int Feels, int Wind, Sky Sky, bool Night, Hour[] Hours);

    public const int ShownHours = 5;

    const string ForecastUrl = "https://api.open-meteo.com/v1/forecast"
        + "?current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day"
        + "&hourly=temperature_2m,weather_code,is_day&forecast_hours=6&wind_speed_unit=ms&timezone=auto";
    const uint PlaceAccuracyMeters = 5000;
    const int PlaceDigits = 2;
    static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(15);
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    static readonly TimeSpan PlaceAge = TimeSpan.FromHours(1);
    static readonly TimeSpan PlaceTimeout = TimeSpan.FromSeconds(20);
    static readonly HttpClient Http = CreateClient();

    Geolocator? _locator;
    DateTime _readAgainAt, _reportAt;
    sealed record LocationConfig(bool Enabled, bool Manual, WeatherPlace? Place);
    LocationConfig? _configuration;
    CancellationTokenSource? _pending;
    int _requestId;

    public event Action? Changed;

    public Stage State { get; private set; } = Settings.Weather ? Stage.Loading : Stage.Off;

    public Report? Now { get; private set; }

    public async Task RefreshAsync(bool force = false)
    {
        var config = new LocationConfig(Settings.Weather, Settings.WeatherManual,
            Settings.WeatherManual ? Settings.WeatherPlace : null);
        bool configChanged = config != _configuration;
        if (configChanged)
        {
            _configuration = config;
            _requestId++;
            _pending?.Cancel();
            _pending = null;
            _readAgainAt = default;
            Now = null;
        }
        if (!config.Enabled)
        {
            if (State != Stage.Off || configChanged) Set(Stage.Off);
            return;
        }
        if (config.Manual && config.Place == null)
        {
            if (State != Stage.ChoosePlace) Set(Stage.ChoosePlace);
            return;
        }
        if (_pending != null || !force && DateTime.UtcNow < _readAgainAt) return;

        using var request = new CancellationTokenSource();
        _pending = request;
        int version = _requestId;
        if (Now == null) Set(Stage.Loading);
        Stage reached;
        try
        {
            var (stage, report) = await ReadAsync(config, request.Token);
            if (version != _requestId) return;
            reached = stage;
            if (report != null)
            {
                Now = report;
                _reportAt = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != _requestId) return;
            if (ex is not (HttpRequestException or TaskCanceledException)) App.Log(ex);
            reached = Stage.Failed;
        }
        finally
        {
            if (ReferenceEquals(_pending, request)) _pending = null;
        }

        if (version != _requestId) return;
        _readAgainAt = DateTime.UtcNow + (reached == Stage.Ready ? ReadEvery : RetryAfter);
        bool fresh = Now != null && DateTime.UtcNow - _reportAt < StaleAfter;
        Set(!Settings.Weather ? Stage.Off : fresh ? Stage.Ready : reached);
    }

    void Set(Stage stage)
    {
        State = stage;
        if (stage != Stage.Ready) Now = null;
        Changed?.Invoke();
    }

    async Task<(Stage Stage, Report? Report)> ReadAsync(LocationConfig config, CancellationToken cancellation)
    {
        BasicGeoposition? found = config.Manual && config.Place is { } selected
            ? new BasicGeoposition { Latitude = selected.Latitude, Longitude = selected.Longitude }
            : await LocateAsync().WaitAsync(cancellation);
        if (found is not { } place) return (Stage.NoPlace, null);

        string url = string.Create(CultureInfo.InvariantCulture,
            $"{ForecastUrl}&latitude={Math.Round(place.Latitude, PlaceDigits)}&longitude={Math.Round(place.Longitude, PlaceDigits)}");
        using var stream = await Http.GetStreamAsync(url, cancellation);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        return (Stage.Ready, Parse(json.RootElement));
    }

    async Task<BasicGeoposition?> LocateAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            _locator ??= new Geolocator { DesiredAccuracyInMeters = PlaceAccuracyMeters };
            Geoposition found = await _locator.GetGeopositionAsync(PlaceAge, PlaceTimeout);
            return found.Coordinate.Point.Position;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static Report Parse(JsonElement root)
    {
        JsonElement now = root.GetProperty("current"), hourly = root.GetProperty("hourly");
        JsonElement times = hourly.GetProperty("time"), degrees = hourly.GetProperty("temperature_2m");
        JsonElement codes = hourly.GetProperty("weather_code"), days = hourly.GetProperty("is_day");

        var hours = new List<Hour>();
        for (int i = 1; i < times.GetArrayLength() && hours.Count < ShownHours; i++)
        {
            int at = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture).Hour;
            hours.Add(new Hour(at, Whole(degrees[i]), SkyOf(codes[i].GetInt32()), days[i].GetInt32() == 0));
        }
        return new Report(Whole(now.GetProperty("temperature_2m")), Whole(now.GetProperty("apparent_temperature")),
            Whole(now.GetProperty("wind_speed_10m")), SkyOf(now.GetProperty("weather_code").GetInt32()),
            now.GetProperty("is_day").GetInt32() == 0, hours.ToArray());
    }

    static int Whole(JsonElement number) => (int)Math.Round(number.GetDouble(), MidpointRounding.AwayFromZero);

    static Sky SkyOf(int code) => code switch
    {
        0 or 1 => Sky.Clear,
        2 => Sky.Cloudy,
        3 => Sky.Overcast,
        45 or 48 => Sky.Fog,
        >= 71 and <= 77 or 85 or 86 => Sky.Snow,
        >= 95 => Sky.Storm,
        _ => Sky.Rain,
    };

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0");
        return http;
    }
}
