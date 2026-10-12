# Pulse PC Monitor

Pulse is a lightweight Windows desktop biometrics monitor built with **.NET 10 + WPF**.

It focuses on real, useful system telemetry with low overhead and a compact modern dark UI:

- Overall CPU usage
- Used/total physical RAM
- Used/total local fixed-drive space
- Network download/upload rates for active physical adapters
- System uptime
- Rolling charts (bounded to the latest 60 samples)
- Tray icon, minimize-to-tray, explicit Exit action

No Electron, no embedded browser, no cloud services, no telemetry, and no admin requirement.

## Prerequisites

- Windows 10 or 11
- .NET 10 SDK (for building)
- .NET 10 Desktop Runtime (for running published binaries)

## Build

```powershell
dotnet build Pulse.sln -c Release
```

## Run from source

```powershell
dotnet run --project src/Pulse.App -c Release
```

## Test

```powershell
dotnet test Pulse.sln -c Release
```

## Publish executable (folder output)

```powershell
dotnet publish src/Pulse.App/Pulse.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o .\publish\Pulse-win-x64
```

Published executable path:

- `publish\Pulse-win-x64\Pulse.App.exe`

## Metric definitions

- **CPU %**: overall processor busy percentage from Windows system time counters.
- **RAM used/total**: physical memory in use and total physical memory.
- **Disk used/total**: sum of local fixed drives that are ready.
- **Network down/up**: bytes-per-second rate from cumulative adapter byte counters on active physical adapters.
- **Uptime**: elapsed system time since last reboot.
- **Charts**: rolling 60-sample histories (bounded ring buffers) for CPU, RAM, and network.

## Efficiency design

- Collection runs asynchronously without blocking UI.
- Collectors are reused; counters are not recreated each refresh.
- Histories are bounded (60 samples).
- Polling slows while hidden/minimized.
- No continuous animations, no background service, no periodic disk writes for live monitoring.
- Baselines reset gracefully on sleep/resume and network changes.
- Timers, collectors, and tray resources are disposed on exit.

## Limitations

- Network rates are reported only for adapters that are active and classified as physical.
- Some counters may briefly show unavailable/warming states right after startup/resume.
- Small differences versus Task Manager are expected due to sampling interval and timing.

## Future optional features (not in v1 scope)

- GPU utilization panels
- Temperature monitoring
- Additional hardware-sensor integrations

## Repository layout

- `src/Pulse.Core`: metric collection, calculations, and monitoring pipeline
- `src/Pulse.App`: WPF UI, tray behavior, and app shell
- `tests/Pulse.Tests`: unit tests for math, resets, and bounded history behavior

## License

MIT (see [LICENSE](./LICENSE)).

