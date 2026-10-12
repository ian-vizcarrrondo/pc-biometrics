# 💓 Pulse

A fast, good-looking live dashboard for your Windows PC. See CPU, memory, GPU, disk, network and process activity at a glance, plus details most monitors skip. 🖥️✨

Everything runs on your device. No account, no cloud, no telemetry leaves your machine.

## Install

1. Download `Pulse-win-x64.zip` from the [latest release](https://github.com/ian-vizcarrrondo/pc-biometrics/releases/latest).
2. Unzip it anywhere and double-click `Pulse.App.exe`.

Requires Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

**From source:** `dotnet run --project src/Pulse.App`

## What you see

| Area | Details |
|---|---|
| **CPU** | Live load, plus load variation (σ) over the last 30 samples |
| **Memory** | Usage, file cache, and kernel paged / non-paged pools |
| **GPU** | Engine utilization and how many engine instances are busy |
| **Thermals** | ACPI thermal zones when Windows exposes them |
| **Storage** | Read/write throughput, disk queue depth, per-drive space |
| **Network** | Down/up rates, adapters, link speed and utilization |
| **Processes** | Top consumers, plus processes started and exited per minute |
| **Power** | Battery and power state |

Refresh every 1, 2 or 5 seconds. The sidebar scrolls smoothly to each section.

## Honest by design

If a sensor isn't available, Pulse says so instead of inventing a number. Some PCs block the ACPI thermal query without admin rights, and then the temperature card shows as unavailable. Thermal zones are never presented as CPU or GPU package temperatures.

## Build and test

```
dotnet build Pulse.sln
dotnet test Pulse.sln
```

Layout: `src/Pulse.Core` (collectors and metrics engine), `src/Pulse.App` (WPF UI), `tests/Pulse.Tests` (xUnit). The original web prototype is in [`docs/index.html`](docs/index.html).

## License

[PolyForm Noncommercial 1.0.0](LICENSE). Free for personal and noncommercial use.
