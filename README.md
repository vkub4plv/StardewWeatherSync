# StardewWeatherSync

A small Windows background app that syncs the **Stardew Valley Dynamic Day/Night | 16:9** Wallpaper Engine wallpaper with real-world weather from OpenWeather.

It updates the wallpaper automatically with:

- current season
- current weather
- sunrise and sunset times
- villager and spouse outfit styles

The app is designed to run quietly in the background after login and only applies changes when needed, while still forcing one apply each launch so the wallpaper always matches the current real-world state.

---

## Features

- Syncs wallpaper season automatically
- Syncs wallpaper weather automatically
- Supports:
  - clear
  - spring petals
  - summer leaves
  - fall leaves
  - snow
  - light rain
  - rain
  - storm
  - green rain
- Updates sunrise and sunset based on OpenWeather data
- Automatically changes villager and spouse outfits based on:
  - temperature
  - current weather
- Applies once every launch, even if nothing changed since the previous PC session
- Only reapplies later when a value actually changes
- Daily Green Rain roll logic with persistence
- File logging with Serilog
- Single-instance protection via mutex

---

## Requirements

- Windows 11 or Windows 10
- [.NET 9 SDK](https://dotnet.microsoft.com/) for building
- Wallpaper Engine installed
- The Wallpaper Engine wallpaper:
  - **Stardew Valley Dynamic Day/Night | 16:9**
- An OpenWeather API key

---

## How it works

The app starts after a configurable delay, checks whether Wallpaper Engine is running, fetches current weather data from OpenWeather, computes the correct wallpaper values, and applies them through the Wallpaper Engine CLI.

### Season mapping

- Spring → `1`
- Summer → `2`
- Fall → `3`
- Winter → `4`

### Weather mapping

- Thunderstorm (`2xx`) → `6` Storm
- Drizzle (`3xx`) → `8` Light Rain
- Rain (`5xx`) → `5` Rain
- Snow (`6xx`) → `4` Snowy
- Everything else uses seasonal clear weather:
  - Spring → `1`
  - Summer → `2`
  - Fall → `3`
  - Winter → `0`

### Green Rain logic

Green Rain can replace **normal rain only**.

Rules:

- only possible in **summer**
- only on these days of the month:
  - `5, 6, 7, 14, 15, 16, 18, 23`
- rolls once per day
- default chance is `25%`
- if the roll succeeds, all normal rain that day becomes Green Rain
- drizzle, storm, and snow are never replaced by Green Rain

### Outfit logic

All automated outfit slots are always set to the same style:

- `0` = Standard
- `1` = Winter
- `2` = Beach

Base logic:

- at or below `WinterOutfitThresholdC` → Winter
- at or above `BeachOutfitThresholdC` → Beach
- otherwise → Standard

Weather adjustments:

- beach outfits are never used in non-clear weather
- bad weather makes winter outfits trigger at warmer temperatures

Default penalties:

- Light Rain → `+2°C`
- Rain → `+4°C`
- Green Rain → `+4°C`
- Storm → `+5°C`
- Snow → `+8°C`

---

## Project structure

```text
StardewWeatherSync/
├─ Program.cs
├─ WeatherWallpaperWorker.cs
├─ StardewWeatherSync.csproj
├─ appsettings.json
└─ README.md
```

---

## Configuration

Create `appsettings.json` based on `appsettings.example.json` but without any comments:

```json
{
  "ApiKey": "YOUR_OPENWEATHER_API_KEY",
  "Latitude": 0.0,
  "Longitude": 0.0,
  "PollMinutes": 5,
  "StartupDelaySeconds": 30,
  "MonitorIndex": 0,
  "WallpaperExecutablePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\wallpaper_engine\\wallpaper64.exe",
  "StateFile": "last-state.json",

  "WinterOutfitThresholdC": 5.0,
  "BeachOutfitThresholdC": 24.0,

  "LightRainWinterPenaltyC": 2.0,
  "RainWinterPenaltyC": 4.0,
  "GreenRainWinterPenaltyC": 4.0,
  "StormWinterPenaltyC": 5.0,
  "SnowWinterPenaltyC": 8.0,

  "GreenRainChance": 0.25
}
```

### Notes

* `ApiKey`
  Get it from OpenWeather.

* `Latitude` / `Longitude`
  Use your actual location.

* `PollMinutes`
  How often the app checks weather updates.

* `StartupDelaySeconds`
  Delay after login before the app starts processing.

* `MonitorIndex`
  `0` for primary monitor, `1` for second, etc.

* `WallpaperExecutablePath`
  Path to `wallpaper64.exe`.

* `StateFile`
  Stores last applied values and Green Rain daily state.

---

## Build

From the project folder:

```bash
dotnet build -c Release
```

---

## Publish

This project is configured as a single-file self-contained Windows executable.

Publish it with:

```bash
dotnet publish -c Release
```

The output will be in something like:

```text
bin\Release\net9.0\win-x64\publish\
```

---

## Run manually

You can run it from the project folder:

```bash
dotnet run
```

Or run the published `.exe` directly.

---

## Run automatically on login

The recommended method is **Windows Task Scheduler**.

### Suggested task settings

* Trigger: **At log on**
* Run only for your user account
* Run with highest privileges
* Action: start the published `.exe`
* Start in: the publish folder

This works best because the app:

* waits before starting
* stays in the background
* avoids duplicate instances using a mutex

---

## Logging

Logs are written to:

```text
logs/stardew-weather-sync.log
```

Rolling interval:

* daily

Retention:

* 7 files

The app logs:

* startup
* Wallpaper Engine checks
* OpenWeather fetches
* parsed weather data
* computed wallpaper values
* Green Rain daily roll
* apply/no-change decisions
* errors

---

## Wallpaper properties controlled by the app

The app updates these Wallpaper Engine properties:

* `currentweather`
* `guhhh` (current season)
* `daycyclesyncing`
* `sunrisestart`
* `duskstart`
* `villageroutfit`
* `villager2outfit`
* `villager3outfit`
* `spouseoutfit`
* `spouse2outfit`
* `spouse3outfit`

---

## Single-instance behavior

The app uses a global mutex:

```text
Global\StardewWeatherSyncMutex
```

So if it is already running, launching it again does nothing.

---

## Troubleshooting

### Wallpaper does not update

Check:

* Wallpaper Engine is running
* `wallpaper64.exe` path is correct
* `MonitorIndex` is correct
* your wallpaper supports CLI property updates
* the log file for errors

### API errors

Check:

* API key is valid
* coordinates are correct
* internet connection is available
* OpenWeather response in the logs

### App starts but does nothing after reboot

This should already be handled: the app forces one apply on the first successful cycle after every launch.

If not, check:

* Task Scheduler task settings
* startup delay
* whether Wallpaper Engine is already running when the app checks

### `appsettings.json` breaks startup

Make sure the file is valid JSON and does not contain comments.

---

## Disclaimer

This project is an unofficial helper app for Wallpaper Engine and OpenWeather.
It depends on the target wallpaper exposing the required CLI-updatable properties.

---

## Credits

* Wallpaper: [**Stardew Valley Dynamic Day/Night | 16:9**](https://steamcommunity.com/sharedfiles/filedetails/?id=3248239912) by [thebes](https://steamcommunity.com/id/thebooshy/)
* Weather data: **OpenWeather**
* Logging: **Serilog**
