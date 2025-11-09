# SQL Memory Inspector

SQL Memory Inspector is an end-to-end toolkit for investigating "missing" RAM on busy SQL Server hosts. It combines a trusted kernel-mode snapshot driver with ETW-backed user-mode tooling so operations teams can attribute physical memory usage to SQL Server, WSL2/`vmmem`, Hyper-V guests, and other consumers even when SQL Server itself is hung or DMVs are inaccessible.

## Repository layout

```
├── agent/
│   ├── SqlMemDiag.Core/      # Shared .NET 7 library (driver interop, ETW tracker, heuristics)
│   ├── SqlMemDiag/           # Console client for on-demand snapshots (JSON/text)
│   ├── SqlMemMonitor/        # ASP.NET Core host with background monitor + web dashboard
│   └── SqlMemDiag.Tests/     # xUnit coverage for analyser heuristics
├── driver/
│   └── SqlMemInspector/      # Kernel-mode driver sources and packaging assets
└── README.md
```

## Kernel driver (`SqlMemInspector`)

1. Install the [Windows Driver Kit (WDK)](https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk) and Visual Studio 2022 with the *Kernel Mode Driver, Windows 10* workload.
2. Open `driver/SqlMemInspector/SqlMemInspector.vcxproj` in Visual Studio.
3. Choose the appropriate configuration (Debug/Release) and platform (`x64`).
4. Build the project. The output `SqlMemInspector.sys` driver will appear in the configuration's build directory.
5. Use the provided `SqlMemInspector.inf` to stage and install the driver. The driver exposes a device interface at `\\.\SqlMemInspector`.

### Capabilities

* Captures system-wide physical memory counters using `ZwQuerySystemInformation`.
* Enumerates all processes, returning working set and private commit usage.
* Flags SQL Server instances and WSL2/Hyper-V processes by image name and lock-pages privilege.
* Returns non-paged/paged pool metrics, system cache usage, and hints when forensic PFN parsing is active.
* Exposes `IOCTL_SQLMEM_GET_SUMMARY` so user-mode callers can fetch snapshots without depending on SQL DMVs.

> **Note:** Locked and large-page byte counters are best-effort when the driver is running in the default, supportable mode. The ETW correlation layer augments the snapshot with allocation history to produce high-confidence attribution.

## On-demand diagnostics (`agent/SqlMemDiag`)

The console client issues a single snapshot and prints a human-friendly or JSON report.

1. Install the .NET 7 SDK on a Windows machine.
2. Restore and build the project:

   ```powershell
   dotnet restore agent/SqlMemDiag/SqlMemDiag.csproj
   dotnet build agent/SqlMemDiag/SqlMemDiag.csproj -c Release
   ```

3. Ensure `SqlMemInspector.sys` is installed and running.
4. Run the diagnostics client as an administrator:

   ```powershell
   dotnet run --project agent/SqlMemDiag -- --json       # machine readable
   dotnet run --project agent/SqlMemDiag --              # rich text report
   dotnet run --project agent/SqlMemDiag -- --no-etw     # fallback when ETW cannot be enabled
   ```

The client reuses the shared core library for driver communication, ETW processing, and the heuristic analyser that calls out locked/large-page SQL usage, `vmmem` dominance, and large unexplained gaps.

## Continuous monitoring + dashboard (`agent/SqlMemMonitor`)

`SqlMemMonitor` turns the collector into a background service and serves a lightweight HTML dashboard with Chart.js visualisations of SQL Server demand over time.

* A hosted background service captures snapshots at a configurable interval (default 30 seconds), merges ETW history, and stores the results in SQLite (`sqlmem_monitor.db`).
* Historical data is retained for a configurable number of days (default 14) and exposed via minimal APIs:
  * `GET /api/timeseries/sql?hours=24` – aggregated SQL Server working set/private/locked usage.
  * `GET /api/snapshots/latest` – latest summary plus top processes.
  * `GET /api/findings` – recent heuristic findings with timestamps.
* The static dashboard (`wwwroot/index.html`) renders the SQL trend graph, highlights the latest snapshot, and lists noteworthy findings for rapid triage.

### Running the monitor

```powershell
# Restore dependencies
 dotnet restore agent/SqlMemMonitor/SqlMemMonitor.csproj

# Launch the monitor (requires administrator rights for driver + ETW access)
 dotnet run --project agent/SqlMemMonitor --urls http://localhost:5000
```

Open `http://localhost:5000` in a browser to view the live chart. Adjust `appsettings.json` to change the polling cadence, retention, ETW usage, or database location. Package the project as a Windows service or container by using the standard ASP.NET Core hosting options.

## Automated tests

`agent/SqlMemDiag.Tests` exercises the heuristic analyser to ensure SQL locked-memory detection, WSL2 attribution, and gap analysis remain stable. Execute with:

```powershell
dotnet test agent/SqlMemDiag.Tests/SqlMemDiag.Tests.csproj
```

> Tests and managed projects require the .NET 7 SDK and run on Windows when the driver is available. The driver itself can only be built and exercised on Windows.

## Safety and supportability considerations

* The driver uses documented kernel APIs in its default mode. Advanced PFN parsing should remain gated by OS build checks.
* ETW capture requires administrative privileges; the tooling automatically degrades when ETW cannot be enabled.
* Always test driver builds in a non-production environment before rolling into production.

## Licensing

This repository is provided as-is for diagnostic purposes. Review and adapt it to comply with your organisation's policies before deploying to production environments.
