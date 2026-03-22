using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StardewWeatherSync;
using System.Threading;

var mutex = new Mutex(true, "Global\\StardewWeatherSyncMutex", out bool createdNew);

if (!createdNew)
{
    return;
}

try
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            "logs/stardew-weather-sync.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger();

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddHttpClient("OpenWeather", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddHostedService<WeatherWallpaperWorker>();

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();

    var host = builder.Build();
    await host.RunAsync();
}
finally
{
    Log.CloseAndFlush();
    mutex.ReleaseMutex();
}