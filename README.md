# NyKur Edge

NyKur Edge is a native Windows desktop companion that keeps media, notifications,
and ambient glances attached to the edge of the primary display. Its normal idle
surface is a genuinely transparent, full-work-area-height fluid field with a
half-embedded glass orb; the compact contextual glass bloom appears only when
requested.

This repository contains the initial `0.1.0` vertical slice: a native WinUI 3
application with a packaged contributor workflow and self-contained public
distribution tooling for a per-user EXE installer and a portable ZIP.

## Download the Windows preview

**[Get NyKur Edge 0.1.0-preview.2](https://nykuredge.outerworldnwo.tr/download/)**
for **Windows 11 x64**. The single EXE installer installs for your account, adds
a Start Menu shortcut, offers an optional desktop shortcut, and registers normal
removal in Windows Installed apps. The [GitHub pre-release](https://github.com/NyKurr/NyKurEdge/releases/tag/v0.1.0-preview.2)
contains the installer, optional portable ZIP, checksums and release metadata.

Open `NyKur-Edge-0.1.0-preview.2-Setup-x64.exe`, follow Setup, and launch from its
final page or the Start Menu. No separate .NET/Windows App SDK installation,
Developer Mode or administrator access is requested. The notification-area icon
opens Settings and Exit. Exit before updating; newer installers replace the same
installation and preserve preferences. Uninstall normally through Windows
Settings. The optional ZIP still requires extracting its entire application folder.

This is an **unsigned early development preview**, not a stable commercial
release. **Notification integration and Launch with Windows are unavailable in
both public distributions**; they require the packaged application. Windows may apply
reputation checks to unsigned software. See [preview instructions and limits](docs/RELEASES.md)
before using it. No certificate installation or automatic updater is included.

Real silent installation, shortcut launch, upgrade/repair, running-app guards,
downgrade refusal, rapid clean close, uninstall/reinstall and preference retention
passed on the current Windows 11 x64 host, alongside 23 core tests. This bounded
CLI validation does not certify the interactive wizard, a clean machine, a
separate standard-user token or the hardware/long-session matrix. See
[release notes](docs/RELEASES.md) and [release tooling](tools/release/README.md).

The new original half-orb icon is shared across the executable, tray, windows and
MSIX assets. Its editable source and regeneration tool are in [tools/branding](tools/branding/README.md).

## Implemented vertical slice

- Borderless, topmost Edge window that stays out of Alt+Tab and does not activate
  merely because it appears.
- DPI-aware left/right anchoring to the primary monitor work area. The collapsed
  render surface is 152 DIPs wide and spans the available work-area height, while
  pointer input passes through everywhere except the orb and active organic bloom.
- A transparent Win2D idle surface for the wave and orb, with the desktop visible
  behind the effect and no intentional XAML/window-colored slab.
- Thirty-two layered filaments over separated pressure, contour, interference,
  and edge-anchor traces. Three separated traveling vertical pressure zones
  distribute activity across the full height instead of fixing it at the center;
  the optical half-orb remains anchored to the display boundary.
- Orb-only cubic-eased hover preview plus click-to-pin launcher behavior; the
  full-height ambient field remains click-through and cannot open the glass shell.
- A conventional NyKur Edge notification-area icon keeps Open Settings and Exit
  available from Windows hidden icons without adding taskbar or Alt+Tab clutter.
- Windows global media-session discovery, live metadata, artwork, timeline, and
  previous/play-pause/next/seek commands.
- Artwork-derived automatic accents using OKLab candidate scoring and restrained
  chroma/lightness normalization.
- Persisted manual accent mode with a native WinUI color picker.
- Sparse procedural idle targets with continuously spring-interpolated rendering,
  reused sample buffers, bounded transient geometry lifetime, and more responsive
  live in-memory WASAPI loopback energy plus low/mid/high-band input while media
  is playing.
- A brighter distributed atmospheric glow that travels with the pressure field,
  breathes with the idle tide, and follows real output energy without adding an
  opaque backdrop.
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
- NAudio WASAPI `3.0.1` for memory-only system-output analysis
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

This section applies to the **packaged application**. The public portable preview
disables the notification controls and reports the unavailable capability safely.

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
  RELEASES.md
tools/
  branding/                 Editable icon and reproducible Windows assets
  release/                  Self-contained ZIP builder and Windows smoke check
```

See [architecture](docs/ARCHITECTURE.md), [development setup](docs/DEVELOPMENT.md),
and [current status](docs/STATUS.md) for the design rationale and known boundaries.
See the [release workflow](tools/release/README.md) to reproduce the portable preview.

## Privacy and telemetry

NyKur Edge stores settings locally. It has no analytics, ads, cloud database,
account system, paid API, or provider credential. Media artwork, notification
content, and short audio-analysis buffers remain in memory; no audio or
notification content is recorded to disk.
