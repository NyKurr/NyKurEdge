# Windows previews

## 0.1.0-preview.1 — 9 October 2026

[Download and instructions](https://nykuredge.outerworldnwo.tr/download/) ·
[GitHub pre-release, ZIP and checksum](https://github.com/NyKurr/NyKurEdge/releases/tag/v0.1.0-preview.1)

The first public download is an **unsigned, self-contained portable ZIP for
Windows 11 x64**. It is an early development preview of the existing native
WinUI 3 application, with original glass half-orb iconography. It has no installer
or automatic updater.

### Getting started

1. Download the ZIP from the official download page or this repository's release.
2. Extract **the entire archive** to a folder you can write to.
3. Open the extracted `NyKur Edge` folder and launch `NyKurEdge.App.exe`.
4. Use the orb at the primary display's edge. Hover previews; click pins the panel.
5. Find the NyKur Edge icon in Windows' notification area for Settings and Exit.
   Windows may place it under the hidden-icons arrow.

The bundled .NET and Windows App SDK runtimes remove separate runtime setup.
Administrator access, a certificate installation and Developer Mode are not
needed. Do not move the executable away from its companion files. A normal
desktop shortcut targeting the executable is fine.

### Included

- Transparent edge field, half-orb launcher, hover preview and click-to-pin.
- Windows media-session metadata, artwork, timeline and playback commands.
- Artwork-derived or manual accent, three motion choices and clock glances.
- Memory-only default-output audio analysis during compatible media playback.
- Local settings, original executable/tray/window icon and conventional tray exit.

### Preview boundaries

**Notification integration and Launch with Windows require package identity and
are unavailable in this portable preview.** Their controls are disabled and
saved preferences from packaged builds are kept. The packaged source workflow
continues to contain those integrations.

The executable is unsigned. Windows or browsers may apply reputation checks.
Verify the SHA-256 in `SHA256SUMS.txt` against the downloaded ZIP before deciding
whether to run it. No instructions to disable protections or trust a test
certificate are necessary for this archive.

The tested environment is current Windows 11 x64. Older Windows versions, x86,
ARM64, multiple monitors, graphics drivers and long sessions are not certified.
Media metadata/control availability depends on the player publishing a Windows
media session; cross-provider controls need broader field testing. The audio
response uses the default system-output mix, not an isolated application stream.

### Verification

A Release portable build and 23 core tests passed. A bounded real Windows 11 run
checked launch from another working folder, loading the bundled .NET/WinUI/Win2D
libraries, creation of the edge and tray host, opening Settings through the tray
callback, and shutdown with exit code zero. This does not replace clean-machine,
manual interaction or long-session testing.

### Updates and removal

Exit before replacing the application. Extract future previews into a new folder;
settings stay at `%LOCALAPPDATA%\NyKur Edge\settings.json`. There is no background
update delivery. To remove it, Exit and delete the extracted folder and shortcuts.
Delete the local settings folder only if you want to remove preferences too.

Report problems through [GitHub issues](https://github.com/NyKurr/NyKurEdge/issues)
or `founder@nykuredge.outerworldnwo.tr`, including Windows version, display scaling,
graphics hardware and the affected media player. Avoid sending private notification
content or unrelated desktop captures.

Bundled component license terms and notices are retained under `licenses/` in the
ZIP. Release build and validation instructions are in [tools/release](../tools/release/README.md).
