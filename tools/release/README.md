# Windows preview distribution

The primary distribution is a single **per-user EXE installer for Windows 11
x64**. A portable ZIP remains an alternative. Both use the same self-contained,
untrimmed Release payload; the installer does not change the WinUI application,
its renderer, or its runtime dependencies.

## Build from a release commit

Run in PowerShell 7 on Windows with the repository's pinned .NET SDK and NuGet
access. Commit the intended source before building so the release metadata
identifies it exactly:

```powershell
dotnet test tests/NyKurEdge.Core.Tests/NyKurEdge.Core.Tests.csproj -c Release --disable-build-servers
./tools/release/Build-Portable.ps1 -Version 0.1.0-preview.2
./tools/release/Build-Installer.ps1 -Version 0.1.0-preview.2
```

The first builder runs the real edge/tray/settings smoke check and writes the
complete portable folder, ZIP, checksum and `release.json` under
`artifacts/releases/<version>/`. The installer builder verifies the ZIP's exact
size and hash, compares every loose payload file against its ZIP entry, checks
the current Git commit, generates a file-ownership manifest, and compiles the
installer. Neither builder overwrites an existing artifact. The portable folder
is not modified by the installer build.

Inno Setup **6.7.3** is pinned. `Get-InnoSetup.ps1` downloads its official GitHub
release, requires the pinned SHA-256 and valid Authenticode signature before
executing it, and obtains the compiler in ignored `artifacts/toolchain/` using
Inno's own portable mode. The compiler's exact SHA-256 is also checked. This tool
installation does not register another application in Windows Installed apps.
An existing compiler can be supplied with `-CompilerPath`; its digest must still
match. No tool binary is committed.

- [Official Inno Setup release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3)
- [Per-user privileges](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- [Running-application mutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm)

## Installation and upgrades

`NyKurEdge.iss` installs into `%LOCALAPPDATA%\Programs\NyKur Edge`, creates a Start
Menu shortcut and an Installed apps entry, and offers an unchecked desktop
shortcut option. A checked launch option appears on the final wizard page.
Silent installs do not launch the application. The wizard follows the Windows
light/dark setting and uses the original NyKur Edge icon. All required .NET,
Windows App SDK and Win2D files are bundled; no shared runtime installation,
administrator access or Developer Mode is requested.

The permanent installer AppId is
`{5D969E16-7FC9-4F13-9030-43864C2F110C}`. Keep it unchanged. Preview version
`0.1.0-preview.2` maps to numeric installer version `0.1.0.2`; each component must
fit `0..65535`. The HKCU uninstall entry stores both the public DisplayVersion
and numeric `VersionInfoVersion`, plus the source commit. The custom values are
written after Inno creates its uninstall registration, while the lifecycle gate
is still held. A metadata-write failure is reported and must be resolved before
publishing or another update. Newer previews install over the same location
and keep a single Installed apps entry. An older numeric version is rejected
before changes. Reinstalling the same version repairs its owned payload files.

Exit NyKur Edge from the notification-area icon before installing or removing it.
The application's lifetime mutex `Local\NyKurEdge.App.Running` lets Setup and
Uninstall detect it. Running-app silent operations fail without changing files;
interactive Setup also checks again immediately before installation. Windows
Restart Manager closure is disabled (`CloseApplications=no`); Setup does not
force-kill an app, close it automatically, or silently discard settings.

A separate `Local\NyKurEdge.App.Lifecycle` mutex is held throughout installation
or removal. Application startup briefly acquires that gate before creating its
lifetime running marker, then releases it before runtime initialization. This
coordinates app launch and file mutation atomically within the same Windows
session. Preparation errors release the gate for a safe retry, and successful
Setup releases it before offering to launch the app. The `Local\` namespace is
session-scoped: these guards do not claim coordination across different Windows
sign-in sessions or older app versions that lack the protocol. Exit all copies
before maintenance; Windows file-in-use errors remain possible.

The installed `.nykur-edge-payload.sha256` records each owned payload file.
After the new payload has installed successfully, an upgrade removes an obsolete
file only when the previous manifest records it, the new manifest does not
contain it, and its contents still match the recorded hash. A copy failure does
not trigger obsolete-file cleanup. Modified or unrecorded files stay; locked or
redirected retired files are retained with a cleanup warning rather than implying
that a successful installation rolled back. No recursive application-folder
cleanup or user-data wildcard deletion is used. Redirected app folders are
refused and redirected retired-file paths are kept. Inno's own uninstall log
removes the files and shortcuts it installed;
`%LOCALAPPDATA%\NyKur Edge\settings.json` is retained through upgrades
and uninstall. Remove that settings folder separately only for an intentional
preference reset. There is no automatic updater.

## Validate before publishing

**Compilation does not establish installer lifecycle verification.**
`release.json` records `installer.lifecycleValidated=false` after a build. Do not
make the website's installer download available until real Windows validation
has passed and the exact built artifact has been published.

At minimum, validate on a Windows 11 x64 desktop:

1. Install as the current user, check Installed apps, Start Menu, optional desktop
   shortcut, original icons and the final-page launch option.
2. Launch from the installed shortcut and from a different working folder;
   verify edge, tray, Settings, bundled runtime loading and clean Exit.
3. Verify installing and uninstalling while the app runs refuse without mutation.
4. Install a lower-version fixture, then the final installer; verify the same
   installation/registry entry and unchanged existing settings. Check obsolete
   owned-file cleanup and downgrade refusal.
5. Uninstall normally; verify owned files and shortcuts are removed while
   preferences and unrelated files remain. Reinstall and confirm preferences.

The portable smoke check can also be rerun directly:

```powershell
./tools/release/Test-Portable.ps1 -Executable 'path/to/NyKur Edge/NyKurEdge.App.exe'
```

Run the real installer lifecycle harness on a desktop with no existing installed
NyKur Edge instance. Use a private lower-version installer fixture and, when
testing retired-file cleanup, a file owned only by that fixture:

```powershell
./tools/release/Test-Installer.ps1 `
  -Installer 'artifacts/releases/0.1.0-preview.2/NyKur-Edge-0.1.0-preview.2-Setup-x64.exe' `
  -BaselineInstaller 'path/to/private-baseline-Setup.exe' `
  -RetiredRelativePath 'obsolete-fixture.dll' `
  -ReportPath 'artifacts/release-validation/installer-validation.json'
```

The harness uses actual **silent** installation/uninstallation. It checks payload
hashes, Installed apps registration, shortcut targets/launch, edge/tray/Settings,
three immediate current-version close cycles, duplicate launch, running-app
maintenance guards, launch refusal during mutation, private-fixture upgrade,
repair, downgrade refusal, uninstall/reinstall, unchanged preferences and
unrecorded-file retention. It refuses existing installed/running state, owns only
its test installation/fixtures, restores prior preferences and reports cleanup
issues. A stuck installer/app is not force-killed.

Use the JSON evidence only when `passed=true` and `cleanupIssues` is empty.
Repeat after the final clean-source build; align the report's installer SHA-256
with release metadata and the verified source commit before marking validation.
The private fixture is not a previously published installer release.

This validates the silent CLI path on the current host. Interactive wizard
navigation/final launch checkbox, a clean VM, a separate standard-user token,
other graphics/display hardware, long sessions and close during active media with
artwork require separate testing. The manifest requests `asInvoker` and uses
HKCU/current-user folders; this is not a separate standard-user acceptance run.

`Build-Portable.ps1 -SkipRuntimeCheck` exists for headless CI and marks the
portable metadata accordingly. The GitHub workflow builds both distributions
and runs core tests, but **does not publish** or certify interactive Windows
installation. Record actual lifecycle evidence separately; keep its artifact
hash and source commit aligned with `release.json` before marking validation.

Publish a GitHub **pre-release** containing the exact `*-Setup-x64.exe`, portable
ZIP, `SHA256SUMS.txt` and `release.json`. The checksum file lists both artifacts.
After verifying the public files' sizes and hashes, update the private website
repository's `src/data/release.ts` with the exact version, source commit, date,
download URLs, sizes, hashes, signed state and verified lifecycle status. Its
Cloudflare Pages deployment remains separate from this application repository.

## Distribution boundaries

This is an **unsigned early development preview**. The toolchain's trusted
signature does not sign NyKur Edge or its installer. Windows and browsers may
apply reputation checks. No fake/test certificate, certificate installation,
security bypass or protection-disable instructions are supplied.

Both the EXE installer and portable ZIP deploy an **unpackaged application**.
Notification integration and Launch with Windows require package identity;
their UI controls remain unavailable without erasing packaged-build preferences.
The packaged contributor workflow remains in [docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md).

Current public support is Windows 11 x64. Bounded lifecycle checks on one host
do not certify clean machines, the interactive wizard, a separate standard-user
token, every media provider, GPU, DPI configuration or long session. Trusted
signed MSIX/store distribution and background update
delivery remain future work requiring a real publisher identity and their own QA.
