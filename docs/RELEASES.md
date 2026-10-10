# Windows previews

## 0.1.0-preview.2 — 10 October 2026

[Download and instructions](https://nykuredge.outerworldnwo.tr/download/) ·
[GitHub pre-release, installer, ZIP and checksums](https://github.com/NyKurr/NyKurEdge/releases/tag/v0.1.0-preview.2)

This release adds a **single per-user EXE installer for Windows 11 x64** as the
primary distribution. It contains the same self-contained WinUI 3 application
and original icon as the portable alternative. The application's ambient edge,
media, appearance, clock and settings behavior stays intact. Startup/shutdown
ordering and queued UI lifetime guards address failures found when closing the
app immediately after launch: UI subscriptions exist before providers start,
and the window/tray appear only after startup completes.

### Installation experience

1. Download `NyKur-Edge-0.1.0-preview.2-Setup-x64.exe` from the official website
   and open it.
2. Follow the Windows light/dark-aware wizard. Installation is for your current
   account; the default location is `%LOCALAPPDATA%\Programs\NyKur Edge`.
3. Choose an optional desktop shortcut. A Start Menu shortcut is always created.
4. Keep **Launch NyKur Edge** checked on the final page, or open it later from
   the Start Menu. Use its notification-area icon for Settings and Exit.

The installer bundles .NET, Windows App SDK and Win2D. It does not request
administrator access, Developer Mode, a certificate installation, or a separate
runtime setup. Windows Installed apps lists **NyKur Edge** with the preview
version, icon and a normal uninstall action.

### Upgrades and removal

Exit NyKur Edge before running another installer. Setup refuses to replace or
remove a cooperating running application in the current Windows session; it does
not automatically close or force-kill it. Exit copies in other sign-in sessions
and older versions too, because the local mutex protocol does not cover them.
Newer preview installers update the same installation and Installed apps entry.
Older versions are
refused. Retired runtime files are removed only when the prior installer owns
them and their recorded hash still matches, after the new payload installs
successfully. Unrelated, modified or locked files remain; failed obsolete-file
cleanup is reported without pretending that installation rolled back.

Preferences stay at `%LOCALAPPDATA%\NyKur Edge\settings.json` during upgrades
and normal uninstall. To remove the installed app, choose **Uninstall** in
Windows Settings → Apps → Installed apps. Remove the separate settings folder
only if you deliberately want to reset preferences. There is no background
updater: download and run a newer installer when another release is published.

The portable ZIP is still optional. Extract its entire folder as before. A
portable and installed copy share preferences; use one app instance at a time.
To move from portable to installed, Exit the portable instance and run Setup.
No manual preference migration or deletion of the old folder is required.

### Verification

The actual installer passed bounded **silent CLI lifecycle checks on the current
Windows 11 x64 host**. These covered full payload hashes and installed-app
registration, Start Menu/desktop shortcut targets, launch through the shortcut,
edge/tray/Settings initialization, upgrade from a private lower-version installer
fixture, same-version repair, downgrade refusal, running-app install/uninstall
refusal, launch rejection while the mutation gate is held, three immediate clean
close cycles, normal uninstall/reinstall and unchanged existing preferences.
Cleanup reported no issues. All 23 core tests passed; the packaged build had zero
warnings and errors.

The lower-version installer is a private QA fixture, not a previously published
installer release. This verification does not certify interactive wizard
navigation or its final launch checkbox, installation on a clean VM or under a
separate standard-user token, other GPUs/display configurations, long sessions,
or closing during actively playing media with artwork. Those require separate
validation. The published files' exact hashes and source commit are recorded in
`SHA256SUMS.txt` and `release.json`.

### Distribution limits

The installer and bundled executable are **unsigned development previews**.
Windows or browsers may apply reputation checks. Verify the artifact against
`SHA256SUMS.txt` and review its source before deciding whether to run it. Do not
disable device protections or install a test certificate.

An EXE installer does not add Windows package identity. **Notification integration
and Launch with Windows remain unavailable** in both public distributions;
their controls are disabled and saved packaged preferences are preserved.
The packaged contributor workflow continues to implement those integrations.

The supported public target is Windows 11 x64. Other Windows versions, ARM64,
x86, hardware/display configurations and long sessions require separate testing.
Build, provenance, installer ownership and required validation instructions are
in [tools/release](../tools/release/README.md). Trusted publisher signing and
automatic update delivery remain future work.

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
