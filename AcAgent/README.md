# AcAgent – Assetto Corsa Session Agent

A production-ready **C# .NET 6** console agent that integrates directly with the
[AcTools / Content Manager](https://github.com/gro-ove/actools) library to
**launch, time-limit, and report** Assetto Corsa sessions on one or more Windows PCs.

> **No Content Manager UI is modified.** AcAgent is a pure wrapper that uses the
> AcTools library's public APIs to write `race.ini` / `assists.ini` and launch
> `acs.exe` through the battle-tested `TrickyStarter` mechanism.

---

## Architecture

```
AcAgent/
├── AcAgent.csproj                 ← .NET 6 console project (x86)
├── Program.cs                     ← DI wiring, CLI parsing, entry-point
│
├── Models/
│   ├── GameConfig.cs              ← Input: car, track, mode, duration, assists
│   └── Session.cs                 ← Output: recorded playtime data
│
├── Infrastructure/
│   └── AcToolsIntegration.cs      ← Façade over AcTools (content discovery +
│                                    Game.StartProperties builder)
│
└── Services/
    ├── GameLauncherService.cs     ← Orchestrates launch, timer, early-exit detection
    ├── SessionManager.cs          ← Creates / closes Session records
    └── ReportingService.cs        ← SQLite + JSONL persistence, aggregation queries
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10/11 | AcTools and `acs.exe` are Windows-only |
| .NET 6 SDK | `winget install Microsoft.DotNet.SDK.6` |
| Assetto Corsa | Installed via Steam |
| Visual Studio 2022 **or** `msbuild` v15+ | Required to build the AcTools DLL |
| Steam running | `TrickyStarter` can auto-launch it; or start it manually |

---

## Step 1 – Build AcTools.dll

AcTools targets **.NET Framework 4.5.2** and must be built before AcAgent can
reference it.

```powershell
# From the repository root
cd "C:\Users\sajid\Desktop\Freelacing\actools"

# Restore NuGet packages for the AcTools project
nuget restore AcManager.sln

# Build AcTools only (Debug | x86 — matches AcAgent's PlatformTarget)
msbuild AcTools\AcTools.csproj /p:Configuration=Debug /p:Platform=x86

# Output will be at:
#   Output\x86\Debug\AcTools.dll
#   Libraries\Newtonsoft.Json\Newtonsoft.Json.dll  (already present)
```

> **Tip:** If you don't have `nuget.exe`, download it from https://www.nuget.org/downloads
> and add it to your `PATH`.

---

## Step 2 – Configure the AC Root Path

AcAgent needs to know where Assetto Corsa is installed.
Set it via **any** of these methods (evaluated in this priority order):

### Option A – CLI argument (recommended for scripting)
```powershell
AcAgent.exe --ac-root "D:\Steam\steamapps\common\assettocorsa" --car ferrari_458 --track magione
```

### Option B – Environment variable (recommended for always-on setups)
```powershell
$env:ACTOOLS_ROOT = "D:\Steam\steamapps\common\assettocorsa"
AcAgent.exe --car ferrari_458 --track magione
```

### Option C – Default
If neither is set, the agent falls back to:
```
C:\Program Files (x86)\Steam\steamapps\common\assettocorsa
```

---

## Step 3 – Build and Run AcAgent

```powershell
cd AcAgent

# Restore NuGet packages (Newtonsoft.Json, Microsoft.Data.Sqlite, etc.)
dotnet restore

# Build
dotnet build -c Debug

# Run
dotnet run -- --car lotus_elise_sc --track magione --duration 30
```

Or publish a self-contained executable:
```powershell
dotnet publish -c Release -r win-x86 --self-contained true -o publish\
publish\AcAgent.exe --car lotus_elise_sc --track magione --duration 30
```

---

## CLI Reference

```
AcAgent.exe [options]

Content discovery:
  --list-cars              Print all installed car IDs and exit
  --list-tracks            Print all installed track IDs (+ layouts) and exit
  --report                 Print the playtime report and exit

Session options:
  --car       <id>         Car folder name  (e.g. ferrari_458)
  --track     <id>         Track folder name (e.g. ks_nordschleife)
  --layout    <id>         Layout sub-folder (e.g. nordschleife)
  --mode      <mode>       Practice | HotLap | TimeAttack | Drift | QuickRace
                           Default: Practice
  --duration  <minutes>    Hard time limit (1–1440). Default: 30
  --easy-assists           Enable ABS, TC, ideal line, auto-clutch, auto-shift

Environment:
  --ac-root   <path>       Path to Assetto Corsa root directory
```

### Examples

```powershell
# List all cars
AcAgent.exe --list-cars

# List all tracks + layouts
AcAgent.exe --list-tracks

# 45-minute practice at Nordschleife
AcAgent.exe --car bmw_m3_e30 --track ks_nordschleife --layout nordschleife --duration 45

# 20-minute hotlap with easy assists
AcAgent.exe --car abarth500 --track magione --mode HotLap --duration 20 --easy-assists

# Print playtime report
AcAgent.exe --report
```

---

## Output / Reporting

All session data is written to:

```
<exe-directory>/data/
    sessions.db     ← SQLite database (primary store)
    sessions.jsonl  ← JSON Lines log  (resilient fallback / audit trail)
```

### SQLite schema

```sql
CREATE TABLE sessions (
    id                       TEXT PRIMARY KEY,
    pc_id                    TEXT NOT NULL,   -- machine name / custom ID
    car_id                   TEXT NOT NULL,
    track_id                 TEXT NOT NULL,
    mode                     TEXT NOT NULL,
    start_time_utc           TEXT NOT NULL,
    end_time_utc             TEXT,
    duration_minutes         REAL NOT NULL,
    configured_duration_min  INTEGER NOT NULL,
    timer_ended              INTEGER NOT NULL,  -- 1 = agent killed the game
    player_exited_early      INTEGER NOT NULL   -- 1 = player quit before timer
);
```

### Playtime report (console)

```
=== Playtime Per Day ===
  2026-04-23  →  87.3 min
  2026-04-22  →  45.0 min

=== Playtime Per PC ===
  GAMING-RIG-01  →  132.3 min  (4 sessions)
  GAMING-RIG-02  →   60.0 min  (2 sessions)
```

---

## Multi-PC Setup

Each instance of AcAgent automatically tags sessions with `Environment.MachineName`.
Override this by setting `GameConfig.PcId` programmatically:

```csharp
var config = new GameConfig
{
    PcId    = "Lounge-PC-1",
    CarId   = "ferrari_458",
    TrackId = "magione",
    DurationMinutes = 60,
};
var session = await launcher.LaunchAsync(config);
```

Sessions from all PCs share the same SQLite database if you point `data/` at a
network share, or each PC can keep its own local database and sync later.

**Future API integration** – `ReportingService.SaveSessionAsync` contains a
`// TODO: POST to central API when ready` comment.  Replace it with an
`HttpClient.PostAsJsonAsync` call to push sessions to a central server:

```csharp
// Example stub (uncomment and implement when the server is ready)
await _httpClient.PostAsJsonAsync("https://your-api/sessions", session);
```

---

## How It Works Internally

```
Program.cs
    │
    ├─► AcToolsIntegration.BuildStartProperties(config)
    │       Reads AC content dirs to validate car/track
    │       Builds Game.BasicProperties  (car, track, driver)
    │       Builds Game.PracticeProperties / HotlapProperties / … (mode)
    │       Builds Game.ConditionProperties  (weather, temperature)
    │       Builds Game.AssistsProperties    (ABS, TC, …)
    │       Returns Game.StartProperties
    │
    ├─► AcToolsIntegration.CreateStarter()
    │       Returns TrickyStarter(acRoot)
    │       TrickyStarter temporarily replaces AssettoCorsa.exe with
    │       AcStarter.exe (embedded in AcTools.dll) to satisfy Steam,
    │       then launches acs.exe directly.
    │
    └─► Game.StartAsync(starter, startProps, progress, cancellationToken)
            1. Writes  %USERPROFILE%\Documents\Assetto Corsa\cfg\race.ini
            2. Writes  %USERPROFILE%\Documents\Assetto Corsa\cfg\assists.ini
            3. Calls   starter.RunAsync()   → launches AssettoCorsa stub
            4. Awaits  starter.WaitUntilGameAsync() → polls for acs.exe PID
            5. Awaits  starter.WaitGameAsync()      → blocks until acs.exe exits
            6. On exit: CleanUpAsync() restores AssettoCorsa.exe, reads race_out.json
```

The **session timer** is implemented via `CancellationTokenSource` with a
`TimeSpan` delay.  When it fires, the linked token cancels `Game.StartAsync`,
which causes `TrickyStarter.CleanUp()` to call `Process.Kill()` on `acs.exe`.

---

## Known Limitations

| Limitation | Reason / Workaround |
|---|---|
| Requires Steam to be running | AcTools' `TrickyStarter` is designed for the Steam version of AC. Set `RunSteamIfNeeded = true` to auto-launch Steam. |
| x86 only | `acs.exe` is a 32-bit binary; AcAgent must also target x86. |
| Single concurrent session | AcAgent enforces one session at a time per process. Run multiple processes for multiple PCs. |
| TrickyStarter modifies AssettoCorsa.exe | It temporarily replaces it with a stub and restores on cleanup. If AcAgent crashes mid-session, run `AcAgent.exe --repair` (future feature) or manually restore `AssettoCorsa_backup_ts.exe`. |

---

## License

This agent uses code from [gro-ove/actools](https://github.com/gro-ove/actools)
which is licensed under the **MIT License**.
