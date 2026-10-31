<p align="center">
  <img src="docs/logo.png" width="96" height="96" alt="Ember logo" />
</p>

<h1 align="center">Ember</h1>

<p align="center"><b>A clean, hyper-lightweight hardware monitor and task manager for Windows.</b></p>

<p align="center">
  <a href="../../releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/larosa023/Ember?color=ff6a2b&label=download" /></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1f1f26" />
  <img alt="MIT license" src="https://img.shields.io/badge/license-MIT-1f1f26" />
</p>

Ember shows your CPU, GPU and memory at a glance (usage, temperatures, clocks, power) like NZXT CAM, without the account, the bloat or the background services. It also does the everyday Task Manager jobs: find a process, end it, and control what starts with Windows.

## Features

- **Dashboard**: animated ring gauges and 60-second history graphs for CPU, GPU and RAM, with temps, clocks, power and VRAM
- **Detail pages**: click the CPU, GPU or Memory card for a 2-minute graph, key stats, per-core usage and every sensor for that part
- **Storage & network**: every drive letter with free space; per-disk health, temperature, life remaining and activity; live download/upload per network adapter
- **Processes**: Apps / Background / Windows sections like Task Manager, live CPU, GPU, memory, disk and network (same numbers as Task Manager) with system totals in the header, a Status column (efficiency mode, suspended) and columns you can show or hide, search, sortable resizable columns, and a right-click menu (end task/tree, priority, efficiency mode, open location, properties, search online)
- **History**: a reading every 5 seconds saved to daily CSV logs; charts for temperatures, usage, power and network over 10 minutes to 24 hours, with CSV export
- **Temperature alerts**: a pop-up when the CPU or GPU stays above your limit, even while Ember is in the tray
- **Services**: browse, start, stop, restart and change the startup type of Windows services
- **Fans**: per-fan Auto (BIOS), fixed speed or a drag-to-edit curve linked to any temperature, with safety rails (minimum speed, 100% if the sensor drops out, fans handed back to BIOS when Ember exits)
- **Mini overlay**: always-on-top CPU/GPU/RAM/network panel for games; drag it anywhere, lock it click-through, toggle from the sidebar, tray or a shortcut
- **System**: your PC's specs (Windows, CPU, motherboard/BIOS, RAM sticks, GPU and driver, drives) with a one-click **Copy specs**
- **Shortcuts**: system-wide hotkeys for Ember and the overlay, remappable in Settings
- **Sensors**: every sensor your hardware exposes (temps, fans, voltages, clocks, loads, power) with min and max
- **Startup apps**: enable or disable apps that launch with Windows, same as Task Manager
- **Themes and units**: five built-in themes that switch instantly (plus your own as JSON files); choose °C/°F, MB or GB for memory, Mbps or MB/s for network, and more
- **Tray and startup**: minimize or close to the tray with live temps in the tooltip, start with Windows (optionally with admin rights, no UAC prompt), collapsible sidebar (`Ctrl+B`)
- **Built-in updater**: checks GitHub Releases, downloads in the background, installs on restart
- **Light on resources**: one background sampling loop, process sampling only while that page is open, adjustable refresh rate

## Install

Download `Ember-win-Setup.exe` from the [latest release](../../releases/latest) and run it. Ember installs per-user (no admin needed) and keeps itself up to date.

> **CPU temperatures and fan control need two things:** Ember running as admin, and the free [PawnIO](https://github.com/namazso/PawnIO.Setup) sensor driver (Windows Defender blocks the old WinRing0 driver). If it's missing, Ember shows an **Install driver** button in the sidebar that installs it with one prompt.

## Themes

Built-in: **Ember**, **Midnight**, **Aurora**, **Graphite**, **Daylight**.

To make your own, go to **Settings → Open themes folder** (`%AppData%\Ember\themes`), copy `sakura.json`, change the colors, then click **Reload themes**:

```json
{
  "Name": "Sakura",
  "Dark": true,
  "Background": "#141014",
  "Surface": "#1C161C",
  "SurfaceAlt": "#251D25",
  "Stroke": "#33283A",
  "Text": "#F7EEF3",
  "TextMuted": "#9C8A95",
  "Accent": "#FF7EB6",
  "OnAccent": "#1A0A12",
  "Good": "#3DDC97",
  "Warn": "#FFB020",
  "Hot": "#FF4D4F"
}
```

Any color you leave out falls back to the default.

## Build from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/larosa023/Ember
cd ember
dotnet run --project src/Ember
```

Or open `Ember.sln` in Visual Studio 2022 or Rider.

## Releasing a new version

1. Bump `<Version>` in `src/Ember/Ember.csproj` (for example `0.1.0` → `0.1.1`).
2. Commit and push to `main`.
3. The **Release** workflow builds Ember, packages it with Velopack and publishes a GitHub Release tagged `v0.1.1`. Installed copies see the update on their next launch.

Pushes that don't change the version are skipped, so normal commits don't create releases.

## Tech stack

| Piece | Used for |
|---|---|
| [.NET 10](https://dotnet.microsoft.com) + [Avalonia UI](https://avaloniaui.net) | Fast, GPU-rendered native UI |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Temperatures, fans, clocks, power |
| [Velopack](https://velopack.io) | Installer and auto-updates via GitHub Releases |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View-models without boilerplate |

## Project layout

```
src/Ember/
  Controls/     RingGauge, Sparkline (custom-drawn, cheap to redraw)
  Models/       Snapshots, settings, theme definition
  Services/     Sensors, processes, startup apps, themes, settings, updater, monitor loop
  ViewModels/   One per page
  Views/        One per page
  Styles/       Shared styles (cards, nav, gauges)
```

## Roadmap

- [x] Tray icon with live temps tooltip
- [x] Compact always-on-top overlay
- [x] Per-process GPU usage
- [x] Per-process disk and network usage
- [x] Fan curves
- [ ] GPU tuning (power limit, clocks)
- [x] Temperature alerts
- [x] Services page
- [x] Storage, network and history

## License

MIT. Ember uses LibreHardwareMonitorLib, which is licensed under MPL-2.0.
