# Development

## Audited host environment

The initial implementation was established on:

- Windows build `26200.9168`, x64;
- Developer Mode enabled;
- Git `2.52.0`;
- .NET SDK `10.0.400`;
- no usable modern Visual Studio WinUI workload;
- no machine-wide Windows SDK required by the final CLI build.

The official .NET SDK and Microsoft WinUI CLI templates were installed. Windows
SDK tools, reference assemblies, Windows App SDK components, and packaged run
support are consumed from official Microsoft NuGet packages, which keeps the CLI
workflow independent of an incomplete Visual Studio installation.

Relevant Microsoft references:

- [Start building Windows apps](https://learn.microsoft.com/windows/apps/get-started/start-here)
- [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
- [Notification listener](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/notification-listener)
- [Extended window styles](https://learn.microsoft.com/windows/win32/winmsg/extended-window-styles)
- [StartupTask](https://learn.microsoft.com/uwp/api/windows.applicationmodel.startuptask)

## Package baseline

Windows App SDK 2.4 is modular. NyKur Edge references only the first-party
components it uses:

- Base `2.0.4`
- Foundation `2.3.9`
- Interactive Experiences `2.1.6`
- WinUI `2.3.6`
- Runtime `2.4.0`

This avoids pulling AI, ML, Search, Widgets, and DWrite components into the app.
Windows SDK Build Tools are pinned to `10.0.28000.2526`; packaged `dotnet run`
support uses `Microsoft.Windows.SDK.BuildTools.WinApp 0.3.1`.

## Commands

```powershell
dotnet restore NyKurEdge.slnx
dotnet build NyKurEdge.slnx --configuration Debug
dotnet test tests\NyKurEdge.Core.Tests\NyKurEdge.Core.Tests.csproj --configuration Debug
```

Launch the packaged app:

```powershell
dotnet run --project src\NyKurEdge.App\NyKurEdge.App.csproj --configuration Debug --no-build
```

The first run may register or refresh the local debug package identity. Close the
application through its Exit action or `Alt+F4` during a development session so
services can dispose normally.

## Restore note

The initial machine's NuGet service-index connection intermittently terminated
TLS early. Required packages were therefore fetched directly from official
`api.nuget.org` flat-container URLs into an ignored local cache. No package binary
is committed. A normal clean checkout should use standard NuGet restore; if it
fails, repair network/proxy access rather than checking binary packages into Git.

## Validation expectations

For meaningful changes:

1. run the core tests;
2. build the app with zero warnings;
3. launch the packaged identity;
4. check collapsed and expanded Edge states on both sides;
5. close the test instance immediately after visual validation.

The optional `NyKurEdgeVisualTest=true` MSBuild property creates a Debug-only,
targetable Edge window for bounded automation. Its compile-only accelerators are:

- `F6`: toggle idle/playing motion;
- `F7`: preview the notification orb/icon/ripple sequence;
- `F8`: cycle restrained accent samples;
- `F9`: toggle collapsed/expanded presentation;
- `F10`: mirror right/left placement.
- `F11`: toggle the intentional orb launcher/pinned state.

It must not be used as the normal runtime configuration. Always close the test
window and verify no `NyKurEdge.App`/`winapp` process remains after inspection.
