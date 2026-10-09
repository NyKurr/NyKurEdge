# Portable preview release workflow

Run on Windows 11 x64 with the repository's pinned .NET SDK and NuGet access:

```powershell
dotnet test tests/NyKurEdge.Core.Tests/NyKurEdge.Core.Tests.csproj -c Release --disable-build-servers
./tools/release/Build-Portable.ps1 -Version 0.1.0-preview.1
```

The builder produces a self-contained, untrimmed **Release** app, tests its real
edge/tray/settings initialization and clean shutdown from another working folder,
then writes the ZIP, `SHA256SUMS.txt`, `release.json` and local QA evidence under
`artifacts/releases/<version>/`. It refuses to overwrite an existing release.
Artifacts are ignored; publish the ZIP and checksum to a GitHub **pre-release**.
The authoritative download metadata comes from the exact ZIP being uploaded.

`-SkipRuntimeCheck` is available for headless CI. It marks the metadata accordingly
and produces a build artifact, not a claim of runtime verification. Run the smoke
test and broader manual QA on Windows before public publication:

```powershell
./tools/release/Test-Portable.ps1 -Executable 'path/to/NyKur Edge/NyKurEdge.App.exe'
```

The ZIP contains the complete application folder, launch instructions and bundled
component notices. It contains no installer, certificate, signing key, debug
symbols or visual-test marker. Notifications and packaged StartupTask integration
are unavailable without package identity. Their UI controls are disabled while
packaged builds retain the existing integration behavior and stored preferences.

Current public support is **Windows 11 x64**. The SDK API minimum is not a tested
support promise for older Windows versions. The smoke test does not certify clean
machines, all media providers, graphics drivers, DPI changes or long sessions.

Future signed MSIX/store distribution requires a real trusted publisher identity
and its own installation/update QA. Do not ship a test certificate as a substitute
for trusted signing. Keep the packaged contributor workflow in `docs/DEVELOPMENT.md`.
