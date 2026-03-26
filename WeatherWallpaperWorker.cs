using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StardewWeatherSync;

public sealed class WeatherWallpaperWorker : BackgroundService
{
    private static readonly HashSet<int> GreenRainEligibleDays = new() { 5, 6, 7, 14, 15, 16, 18, 23 };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherWallpaperWorker> _logger;
    private readonly IConfiguration _config;

    private AppSettings _settings = new();
    private AppliedState _state = new();

    private bool _appliedThisLaunch;

    public WeatherWallpaperWorker(
        IHttpClientFactory httpClientFactory,
        ILogger<WeatherWallpaperWorker> logger,
        IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settings = _config.Get<AppSettings>() ?? new AppSettings();
        ValidateSettings(_settings);

        _state = await StateStore.LoadAsync(_settings.StateFile, stoppingToken) ?? new AppliedState();

        _logger.LogInformation("Starting Stardew weather sync.");
        _logger.LogInformation("Initial delay: {DelaySeconds}s", _settings.StartupDelaySeconds);

        await Task.Delay(TimeSpan.FromSeconds(_settings.StartupDelaySeconds), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.PollMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception escaped RunOneCycleAsync.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOneCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsWallpaperEngineRunning())
            {
                _logger.LogInformation("Wallpaper Engine is not running. Skipping.");
                return;
            }

            var weather = await FetchWeatherAsync(cancellationToken);

            var season = GetSeason(DateTime.Now);
            var localDate = DateTime.Now.Date;

            bool greenRainEnabledToday = ResolveGreenRainForToday(localDate, season);

            string weatherValue = MapWallpaperWeather(weather.Weather[0].Id, season, greenRainEnabledToday);
            string seasonValue = MapWallpaperSeason(season);

            double sunriseHour = ToLocalHour(weather.Sys.SunriseUnix, weather.TimezoneOffsetSeconds);
            double sunsetHour = ToLocalHour(weather.Sys.SunsetUnix, weather.TimezoneOffsetSeconds);

            sunriseHour = Math.Round(sunriseHour, 3);
            sunsetHour = Math.Round(sunsetHour, 3);

            string outfitValue = DetermineOutfitValue(
                temperatureC: weather.Main.TempC,
                wallpaperWeatherValue: weatherValue,
                settings: _settings);

            _logger.LogInformation(
                "Computed target values. Season={Season}, SeasonValue={SeasonValue}, WeatherValue={WeatherValue}, TempC={TempC}, SunriseHour={SunriseHour}, SunsetHour={SunsetHour}, OutfitValue={OutfitValue}",
                season,
                seasonValue,
                weatherValue,
                weather.Main.TempC,
                sunriseHour,
                sunsetHour,
                outfitValue);

            var target = new TargetState
            {
                SeasonValue = seasonValue,
                WeatherValue = weatherValue,
                SunriseHour = sunriseHour,
                SunsetHour = sunsetHour,
                OutfitValue = outfitValue
            };

            bool changedSinceLastApplied = !_state.Matches(target);
            bool shouldApply = !_appliedThisLaunch || changedSinceLastApplied;

            if (!shouldApply)
            {
                _logger.LogInformation(
                    "No change. Weather={WeatherValue}, Season={SeasonValue}, Outfit={OutfitValue}, Sunrise={Sunrise}, Sunset={Sunset}",
                    target.WeatherValue,
                    target.SeasonValue,
                    target.OutfitValue,
                    target.SunriseHour,
                    target.SunsetHour);
                return;
            }

            ApplyWallpaperProperties(target);

            _state.SeasonValue = target.SeasonValue;
            _state.WeatherValue = target.WeatherValue;
            _state.SunriseHour = target.SunriseHour;
            _state.SunsetHour = target.SunsetHour;
            _state.OutfitValue = target.OutfitValue;

            await StateStore.SaveAsync(_settings.StateFile, _state, cancellationToken);

            _appliedThisLaunch = true;

            _logger.LogInformation(
                "Applied update. Weather={WeatherValue}, Season={SeasonValue}, Outfit={OutfitValue}, TempC={TempC}, Sunrise={Sunrise}, Sunset={Sunset}, OpenWeatherId={OpenWeatherId}, GreenRainToday={GreenRainToday}",
                target.WeatherValue,
                target.SeasonValue,
                target.OutfitValue,
                weather.Main.TempC,
                target.SunriseHour,
                target.SunsetHour,
                weather.Weather[0].Id,
                greenRainEnabledToday);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cycle failed.");
        }
    }

    private async Task<OpenWeatherResponse> FetchWeatherAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("OpenWeather");

            string lat = _settings.Latitude.ToString(CultureInfo.InvariantCulture);
            string lon = _settings.Longitude.ToString(CultureInfo.InvariantCulture);

            string url =
                $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={_settings.ApiKey}&units=metric";

            _logger.LogInformation(
                "Fetching OpenWeather data. Latitude={Latitude}, Longitude={Longitude}",
                lat, lon);

            using var response = await client.GetAsync(url, cancellationToken);

            _logger.LogInformation(
                "OpenWeather responded. StatusCode={StatusCode}",
                (int)response.StatusCode);

            response.EnsureSuccessStatusCode();

            string rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "Received OpenWeather payload: {Payload}",
                rawJson);

            var result = JsonSerializer.Deserialize<OpenWeatherResponse>(rawJson);

            if (result?.Weather == null || result.Weather.Length == 0)
                throw new InvalidOperationException("OpenWeather returned no weather data.");

            var firstWeather = result.Weather[0];

            _logger.LogInformation(
                "Parsed OpenWeather data. WeatherId={WeatherId}, Main={Main}, Description={Description}, TempC={TempC}, SunriseUnix={SunriseUnix}, SunsetUnix={SunsetUnix}, TimezoneOffsetSeconds={TimezoneOffsetSeconds}",
                firstWeather.Id,
                firstWeather.Main,
                firstWeather.Description,
                result.Main.TempC,
                result.Sys.SunriseUnix,
                result.Sys.SunsetUnix,
                result.TimezoneOffsetSeconds);

            return result;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("OpenWeather request timed out.", ex);
        }
    }

    private bool IsWallpaperEngineRunning()
    {
        return Process.GetProcessesByName("wallpaper64").Length > 0 ||
               Process.GetProcessesByName("wallpaper32").Length > 0;
    }

    private void ApplyWallpaperProperties(TargetState target)
    {
        var values = new Dictionary<string, object>
        {
            ["currentweather"] = target.WeatherValue,
            ["guhhh"] = target.SeasonValue,

            ["daycyclesyncing"] = "0",
            ["sunrisestart"] = target.SunriseHour,
            ["duskstart"] = target.SunsetHour,

            ["villageroutfit"] = target.OutfitValue,
            ["villager2outfit"] = target.OutfitValue,
            ["villager3outfit"] = target.OutfitValue,
            ["spouseoutfit"] = target.OutfitValue,
            ["spouse2outfit"] = target.OutfitValue,
            ["spouse3outfit"] = target.OutfitValue
        };

        string json = JsonSerializer.Serialize(values);
        string payload = $"RAW~({json})~END";

        string script =
            $"& '{_settings.WallpaperExecutablePath}' -control applyProperties -monitor {_settings.MonitorIndex} -properties '{payload}'";

        _logger.LogInformation("Using Wallpaper Engine path: {Path}", _settings.WallpaperExecutablePath);
        _logger.LogInformation("Using monitor index: {Monitor}", _settings.MonitorIndex);
        _logger.LogInformation("Using payload: {Payload}", payload);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);

        if (process is null)
            throw new InvalidOperationException("Failed to start PowerShell for Wallpaper Engine command.");

        process.WaitForExit(5000);

        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(stdOut))
            _logger.LogInformation("Wallpaper Engine stdout: {StdOut}", stdOut);

        if (!string.IsNullOrWhiteSpace(stdErr))
            _logger.LogWarning("Wallpaper Engine stderr: {StdErr}", stdErr);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Wallpaper Engine PowerShell wrapper exited with code {process.ExitCode}.");
    }

    private bool ResolveGreenRainForToday(DateTime localDate, Season season)
    {
        string today = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_state.GreenRainRollDate != today)
        {
            bool eligible = season == Season.Summer && GreenRainEligibleDays.Contains(localDate.Day);
            bool enabled = eligible && Random.Shared.NextDouble() < _settings.GreenRainChance;

            _state.GreenRainRollDate = today;
            _state.GreenRainEnabledForDate = enabled;

            // Persist immediately so restarts during the day keep the same roll.
            File.WriteAllText(
                _settings.StateFile,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));

            _logger.LogInformation(
                "Green Rain daily roll. Date={Date}, Season={Season}, Eligible={Eligible}, Enabled={Enabled}, Chance={Chance}",
                today,
                season,
                eligible,
                enabled,
                _settings.GreenRainChance);
        }

        return _state.GreenRainEnabledForDate;
    }

    private static Season GetSeason(DateTime now)
    {
        int month = now.Month;
        int day = now.Day;

        if ((month == 3 && day >= 20) || month == 4 || month == 5 || (month == 6 && day <= 20))
            return Season.Spring;

        if ((month == 6 && day >= 21) || month == 7 || month == 8 || (month == 9 && day <= 22))
            return Season.Summer;

        if ((month == 9 && day >= 23) || month == 10 || month == 11 || (month == 12 && day <= 20))
            return Season.Fall;

        return Season.Winter;
    }

    private static string MapWallpaperSeason(Season season)
    {
        return season switch
        {
            Season.Spring => "1",
            Season.Summer => "2",
            Season.Fall => "3",
            Season.Winter => "4",
            _ => "1"
        };
    }

    private static string MapWallpaperWeather(int openWeatherId, Season season, bool greenRainEnabledToday)
    {
        // 2xx thunderstorm -> storm
        if (openWeatherId >= 200 && openWeatherId < 300)
            return "6";

        // 3xx drizzle -> light rain
        if (openWeatherId >= 300 && openWeatherId < 400)
            return "8";

        // 5xx rain -> rain (or green rain replacement)
        if (openWeatherId >= 500 && openWeatherId < 600)
            return greenRainEnabledToday ? "7" : "5";

        // 6xx snow -> snowy
        if (openWeatherId >= 600 && openWeatherId < 700)
            return "4";

        // Seasonal clear weather
        return season switch
        {
            Season.Spring => "1",
            Season.Summer => "2",
            Season.Fall => "3",
            Season.Winter => "0",
            _ => "0"
        };
    }

    private static string DetermineOutfitValue(double temperatureC, string wallpaperWeatherValue, AppSettings settings)
    {
        bool isSeasonalClear = wallpaperWeatherValue is "0" or "1" or "2" or "3";

        // Never beach when weather is not seasonal-clear.
        if (!isSeasonalClear)
        {
            double winterThreshold = settings.WinterOutfitThresholdC + GetWinterPenaltyC(wallpaperWeatherValue, settings);
            return temperatureC <= winterThreshold ? "1" : "0";
        }

        if (temperatureC >= settings.BeachOutfitThresholdC)
            return "2";

        if (temperatureC <= settings.WinterOutfitThresholdC)
            return "1";

        return "0";
    }

    private static double GetWinterPenaltyC(string wallpaperWeatherValue, AppSettings settings)
    {
        return wallpaperWeatherValue switch
        {
            "8" => settings.LightRainWinterPenaltyC,
            "5" => settings.RainWinterPenaltyC,
            "7" => settings.GreenRainWinterPenaltyC,
            "6" => settings.StormWinterPenaltyC,
            "4" => settings.SnowWinterPenaltyC,
            _ => 0
        };
    }

    private static double ToLocalHour(long unixUtcSeconds, int timezoneOffsetSeconds)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(unixUtcSeconds);
        var local = utc.ToOffset(TimeSpan.FromSeconds(timezoneOffsetSeconds));

        return local.Hour
             + (local.Minute / 60.0)
             + (local.Second / 3600.0);
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("ApiKey is missing.");

        if (!File.Exists(settings.WallpaperExecutablePath))
            throw new FileNotFoundException(
                "Wallpaper Engine executable not found.",
                settings.WallpaperExecutablePath);

        if (settings.PollMinutes <= 0)
            throw new InvalidOperationException("PollMinutes must be greater than 0.");

        if (settings.StartupDelaySeconds < 0)
            throw new InvalidOperationException("StartupDelaySeconds cannot be negative.");
    }
}

public enum Season
{
    Spring,
    Summer,
    Fall,
    Winter
}

public sealed class AppSettings
{
    public string ApiKey { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public int PollMinutes { get; set; } = 5;
    public int StartupDelaySeconds { get; set; } = 30;
    public int MonitorIndex { get; set; } = 0;

    public string WallpaperExecutablePath { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe";

    public string StateFile { get; set; } = "last-state.json";

    // Outfit thresholds based on typical Poland everyday wear.
    public double WinterOutfitThresholdC { get; set; } = 5.0;
    public double BeachOutfitThresholdC { get; set; } = 24.0;

    // Weather penalties make winter outfits kick in at warmer temperatures.
    public double LightRainWinterPenaltyC { get; set; } = 2.0;
    public double RainWinterPenaltyC { get; set; } = 4.0;
    public double GreenRainWinterPenaltyC { get; set; } = 4.0;
    public double StormWinterPenaltyC { get; set; } = 5.0;
    public double SnowWinterPenaltyC { get; set; } = 8.0;

    public double GreenRainChance { get; set; } = 0.25;
}

public sealed class TargetState
{
    public string WeatherValue { get; set; } = "0";
    public string SeasonValue { get; set; } = "1";
    public double SunriseHour { get; set; }
    public double SunsetHour { get; set; }
    public string OutfitValue { get; set; } = "0";
}

public sealed class AppliedState
{
    public string WeatherValue { get; set; } = "0";
    public string SeasonValue { get; set; } = "1";
    public double SunriseHour { get; set; }
    public double SunsetHour { get; set; }
    public string OutfitValue { get; set; } = "0";

    public string GreenRainRollDate { get; set; } = "";
    public bool GreenRainEnabledForDate { get; set; }

    public bool Matches(TargetState target)
    {
        return WeatherValue == target.WeatherValue
            && SeasonValue == target.SeasonValue
            && OutfitValue == target.OutfitValue
            && SunriseHour == target.SunriseHour
            && SunsetHour == target.SunsetHour;
    }
}

public static class StateStore
{
    public static async Task<AppliedState?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<AppliedState>(json);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveAsync(string path, AppliedState state, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}

public sealed class OpenWeatherResponse
{
    [JsonPropertyName("weather")]
    public WeatherInfo[] Weather { get; set; } = Array.Empty<WeatherInfo>();

    [JsonPropertyName("main")]
    public MainInfo Main { get; set; } = new();

    [JsonPropertyName("sys")]
    public SysInfo Sys { get; set; } = new();

    [JsonPropertyName("timezone")]
    public int TimezoneOffsetSeconds { get; set; }
}

public sealed class WeatherInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("main")]
    public string Main { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class MainInfo
{
    [JsonPropertyName("temp")]
    public double TempC { get; set; }
}

public sealed class SysInfo
{
    [JsonPropertyName("sunrise")]
    public long SunriseUnix { get; set; }

    [JsonPropertyName("sunset")]
    public long SunsetUnix { get; set; }
}