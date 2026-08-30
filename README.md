# NyKur Edge

NyKur Edge is a native Windows desktop companion that keeps media, notifications,
and ambient glances attached to the edge of the primary display. Its normal idle
surface is a genuinely transparent, vertically extended fluid field with a
half-embedded glass orb; the contextual glass bloom appears only when requested.

This repository contains the initial `0.1.0` vertical slice. It is a real packaged
WinUI 3 application, not a browser shell or throwaway prototype.

## Implemented vertical slice

- Borderless, topmost Edge window that stays out of Alt+Tab and does not activate
  merely because it appears.
- DPI-aware left/right anchoring to the primary monitor work area.
- A transparent Win2D idle surface for the wave and orb, with the desktop visible
  behind the effect and no intentional XAML/window-colored slab.
- Seventeen layered filaments over separated pressure, contour, interference,
  and edge-anchor traces, with restrained radial bloom and a deliberate optical
  half-orb whose center sits on the display boundary.
- Cubic-eased hover preview plus click-to-pin launcher behavior; the glass shell
  grows from the orb and keeps a collapse grace period.
- Windows global media-session discovery, live metadata, artwork, timeline, and
  previous/play-pause/next/seek commands.
- Artwork-derived automatic accents using OKLab candidate scoring and restrained
  chroma/lightness normalization.
- Persisted manual accent mode with a native WinUI color picker.
- Sparse procedural signal targets with continuously spring-interpolated rendering,
  reused sample buffers, bounded transient geometry lifetime, and an explicit seam
  for future real audio data; the current motion is not audio-reactive.
- Generic glance coordinator and scheduled/previewable clock glance.
- Supported `UserNotificationListener` integration, source filtering, privacy
  levels, permission state, notification preview state, and a bubble/icon/ripple
  arrival sequence.
- Guided notification setup that explains why Windows banners must be disabled
  per source application rather than intercepted by unsupported means.
- JSON settings persistence and packaged `StartupTask` integration.

## Toolchain

- C# and .NET 10 LTS (`10.0.400` SDK baseline)
- WinUI 3
- Windows App SDK 2.4 modular packages
- Windows SDK Build Tools `10.0.28000.2526`
- Single-project MSIX packaging and WinApp CLI run support
- MSTest 4 for pure core-logic tests

The project targets `net10.0-windows10.0.26100.0` and supports Windows build
`17763` or newer at the API-contract level. Development and visual validation are
performed on current Windows 11.

## Build and run

Requirements:

- Windows 10 version 1809 or newer; current Windows 11 is recommended.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- NuGet connectivity for the first restore.
- Windows Developer Mode for the debug package identity.

From the repository root:

```powershell
dotnet msbuild NyKurEdge.slnx -t:Restore -m:1 -nr:false -p:RestoreDisableParallel=true
dotnet msbuild NyKurEdge.slnx -t:Build -m:1 -nr:false -p:Configuration=Debug -p:UseSharedCompilation=false
dotnet vstest tests\NyKurEdge.Core.Tests\bin\Debug\net10.0\NyKurEdge.Core.Tests.dll
dotnet run --project src\NyKurEdge.App\NyKurEdge.App.csproj --configuration Debug --no-build
```

`dotnet run` registers a local debug identity, launches the packaged application,
and waits for it to exit. The normal Edge window is intentionally absent from
Alt+Tab and the taskbar.

For a bounded visual-inspection build that is targetable by UI automation and has
compile-only state-preview accelerators, build with:

```powershell
dotnet msbuild src\NyKurEdge.App\NyKurEdge.App.csproj -t:Build -m:1 -nr:false -p:Configuration=Debug -p:UseSharedCompilation=false -p:NyKurEdgeVisualTest=true
```

Rebuild without that property before normal use.

Packaged activation may not inherit shell environment variables. A visual-test
build can therefore read an optional `nykur-edge.visual-test` marker beside the
built executable. Comma-separated tokens such as `playing`, `notification`,
`expanded`, `left`, `purple`, `orange`, `rose`, `neutral`, and `fallback` select a
deterministic passive state; `fallback` explicitly exercises the Win2D mirror.
This marker is a local QA artifact and must not be committed or shipped.

An experimental native no-redirection composition target remains available for
compatibility work by setting `NYKUR_EDGE_NATIVE_COMPOSITION=1` before direct
development launch. It is deliberately opt-in: some Windows/GPU combinations can
accept native frames without presenting visible pixels, while the Win2D surface is
the currently validated production path.

## Notification setup

Windows does not expose a supported public API that intercepts arbitrary
notifications before their native banner appears. NyKur Edge therefore uses this
supported workflow:

1. Grant notification-listener permission to NyKur Edge.
2. Keep notifications enabled for each selected source application.
3. Disable that application's Windows banner, and optionally its native sound.
4. Let NyKur Edge read and present the notification from the listener API.

NyKur Edge does not change registry notification settings and does not request
notification permission on first launch.

## Repository layout

```text
src/
  NyKurEdge.Core/            State, events, policies, models, settings, glances
  NyKurEdge.Infrastructure/  Windows media/notification/display/startup adapters
  NyKurEdge.App/             WinUI composition root, Edge presentation, clock module
tests/
  NyKurEdge.Core.Tests/      Pure deterministic behavior tests
docs/
  ARCHITECTURE.md
  DEVELOPMENT.md
  STATUS.md
```

See [architecture](docs/ARCHITECTURE.md), [development setup](docs/DEVELOPMENT.md),
and [current status](docs/STATUS.md) for the design rationale and known boundaries.

## Privacy and telemetry

NyKur Edge stores settings locally. It has no analytics, ads, cloud database,
account system, paid API, or provider credential. Media artwork and notification
content remain in memory; no audio or notification content is recorded to disk.
