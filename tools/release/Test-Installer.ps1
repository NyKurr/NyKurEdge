param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$BaselineInstaller,
    [string]$ReportPath = 'artifacts/release-validation/installer-validation.json',
    [string]$RetiredRelativePath
)

# This is an interactive-desktop integration test, not a headless build check.
# It refuses to touch an existing installation and restores pre-existing settings.
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
$taskReport = [IO.Path]::GetFullPath($ReportPath)
if (!$taskReport.StartsWith($taskArtifacts + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer validation reports and logs must stay inside this repository artifacts directory.'
}
$taskInstaller = (Resolve-Path -LiteralPath $Installer).Path
$taskBaseline = if ($BaselineInstaller) { (Resolve-Path -LiteralPath $BaselineInstaller).Path } else { $null }
$taskInstallerInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskInstaller)
$taskFinalVersion = $taskInstallerInfo.ProductVersion.Trim()
$taskFinalNumericVersion = $taskInstallerInfo.FileVersion.Trim()
if ($taskFinalVersion -notmatch '^\d+\.\d+\.\d+-preview\.\d+$' -or
    $taskFinalNumericVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Setup must have an exact preview ProductVersion and four-part numeric FileVersion.'
}
$taskBaselineVersion = $null
$taskBaselineNumericVersion = $null
if ($taskBaseline) {
    $taskBaselineInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskBaseline)
    $taskBaselineVersion = $taskBaselineInfo.ProductVersion.Trim()
    $taskBaselineNumericVersion = $taskBaselineInfo.FileVersion.Trim()
    if ($taskBaselineVersion -notmatch '^\d+\.\d+\.\d+-preview\.\d+$' -or
        $taskBaselineNumericVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        [version]$taskBaselineNumericVersion -ge [version]$taskFinalNumericVersion) {
        throw 'The private baseline installer must have an older, valid numeric version.'
    }
}

$taskAppId = '{5D969E16-7FC9-4F13-9030-43864C2F110C}_is1'
$taskUninstallPath = "Software\Microsoft\Windows\CurrentVersion\Uninstall\$taskAppId"
$taskAppFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\NyKur Edge'
$taskAppExecutable = Join-Path $taskAppFolder 'NyKurEdge.App.exe'
$taskUninstaller = Join-Path $taskAppFolder 'unins000.exe'
$taskStartMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'NyKur Edge.lnk'
$taskDesktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'NyKur Edge.lnk'
$taskSettingsFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NyKur Edge'
$taskSettingsPath = Join-Path $taskSettingsFolder 'settings.json'
$taskUserFixture = Join-Path $taskAppFolder 'nykur-installer-validation-user-file.txt'
$taskFixtureContent = 'Unrecorded user file created only by NyKur Edge installer validation.'
$taskRetiredFile = $null
if ($RetiredRelativePath) {
    $taskRetiredFile = [IO.Path]::GetFullPath((Join-Path $taskAppFolder $RetiredRelativePath))
    if (!$taskRetiredFile.StartsWith($taskAppFolder + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::IsPathRooted($RetiredRelativePath) -or $RetiredRelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'RetiredRelativePath must identify a fixture strictly inside the application directory.'
    }
    if (!$taskBaseline) { throw 'An obsolete payload fixture requires BaselineInstaller.' }
}
$taskLogFolder = Join-Path (Split-Path $taskReport) 'installer-validation-logs'
$taskChecks = [Collections.Generic.List[object]]::new()
$taskOwnedProcesses = [Collections.Generic.HashSet[int]]::new()
$taskActiveInstallerProcesses = [Collections.Generic.HashSet[int]]::new()
$taskCleanupIssues = [Collections.Generic.List[string]]::new()
$taskOwnsInstallation = $false
$taskInstallationTouched = $false
$taskUserFixtureCreated = $false
$taskSettingsCaptured = $false
$taskSettingsExisted = $false
$taskSettingsFolderExisted = $false
$taskOriginalSettings = $null
$taskOriginalSettingsWriteTime = $null
$taskExpectedSettingsHash = $null
$taskSeedSettingsHash = $null
$taskFailure = $null

function Assert-InstallerCheck([bool]$Condition, [string]$Name, $Evidence = $null) {
    $taskChecks.Add([ordered]@{ name = $Name; passed = $Condition; evidence = $Evidence })
    if (!$Condition) { throw "Installer validation failed: $Name" }
}

function Read-UninstallValues([Microsoft.Win32.RegistryHive]$Hive = [Microsoft.Win32.RegistryHive]::CurrentUser) {
    $taskRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, [Microsoft.Win32.RegistryView]::Registry64)
    $taskKey = $null
    try {
        $taskKey = $taskRegistry.OpenSubKey($taskUninstallPath, $false)
        if (!$taskKey) { return $null }
        $taskValues = [ordered]@{}
        foreach ($taskName in ($taskKey.GetValueNames() | Sort-Object)) {
            $taskValues[$taskName] = $taskKey.GetValue($taskName, $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
        return $taskValues
    }
    finally {
        if ($taskKey) { $taskKey.Dispose() }
        $taskRegistry.Dispose()
    }
}

function Count-UserUninstallEntries {
    $taskRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $taskRootKey = $null
    try {
        $taskRootKey = $taskRegistry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall', $false)
        if (!$taskRootKey) { return 0 }
        $taskCount = 0
        foreach ($taskName in $taskRootKey.GetSubKeyNames()) {
            $taskKey = $taskRootKey.OpenSubKey($taskName, $false)
            try {
                if ($taskKey -and $taskKey.GetValue('DisplayName') -eq 'NyKur Edge') { $taskCount++ }
            }
            finally { if ($taskKey) { $taskKey.Dispose() } }
        }
        return $taskCount
    }
    finally {
        if ($taskRootKey) { $taskRootKey.Dispose() }
        $taskRegistry.Dispose()
    }
}

function Get-SettingsHash {
    if (!(Test-Path -LiteralPath $taskSettingsPath -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $taskSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SettingsPreserved([string]$Stage) {
    Assert-InstallerCheck ((Get-SettingsHash) -eq $taskExpectedSettingsHash) "preferences preserved after $Stage" $taskExpectedSettingsHash
}

function Invoke-InstallerExecutable([string]$Path, [string]$Operation, [switch]$Uninstall) {
    $taskLog = Join-Path $taskLogFolder "$Operation.log"
    $taskArguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $taskLog + '"'
    if (!$Uninstall) { $taskArguments += ' /TASKS=desktopicon' }
    $taskProcess = Start-Process -FilePath $Path -ArgumentList $taskArguments -WorkingDirectory $env:TEMP `
        -WindowStyle Hidden -PassThru
    [void]$taskActiveInstallerProcesses.Add($taskProcess.Id)
    try {
        if (!$taskProcess.WaitForExit(120000)) {
            $taskChecks.Add([ordered]@{ name = "$Operation process timeout"; passed = $false; processId = $taskProcess.Id; log = $taskLog })
            throw "Owned $Operation process PID $($taskProcess.Id) exceeded 120 seconds. It was not force-killed."
        }
        [void]$taskActiveInstallerProcesses.Remove($taskProcess.Id)
        $taskChecks.Add([ordered]@{ name = "$Operation process"; exitCode = $taskProcess.ExitCode; log = $taskLog })
        return $taskProcess.ExitCode
    }
    finally { $taskProcess.Dispose() }
}

function Assert-InstalledState([string]$Version, [string]$NumericVersion, [string]$Stage) {
    $taskValues = Read-UninstallValues
    Assert-InstallerCheck ($null -ne $taskValues) "$Stage has HKCU installed-app entry"
    Assert-InstallerCheck ($taskValues.DisplayName -eq 'NyKur Edge' -and $taskValues.DisplayVersion -eq $Version -and
        $taskValues.VersionInfoVersion -eq $NumericVersion) "$Stage registry version and identity" $taskValues
    Assert-InstallerCheck ($taskValues.SourceCommit -match '^[0-9a-f]{40}$') "$Stage records source commit" $taskValues.SourceCommit
    Assert-InstallerCheck ([IO.Path]::GetFullPath([string]$taskValues.InstallLocation).TrimEnd('\') -eq $taskAppFolder) "$Stage uses per-user program directory" $taskValues.InstallLocation
    Assert-InstallerCheck ((Count-UserUninstallEntries) -eq 1) "$Stage has one installed-app entry"
    Assert-InstallerCheck ($null -eq (Read-UninstallValues -Hive LocalMachine)) "$Stage does not register a machine-wide installation"
    Assert-InstallerCheck ($taskValues.DisplayIcon -like "*$taskAppFolder*" -and
        $taskValues.UninstallString -like "*$taskUninstaller*") "$Stage has branded uninstall integration"
    foreach ($taskRequired in @('NyKurEdge.App.exe', 'NyKurEdge.App.dll', 'NyKurEdge.App.pri',
        'NyKurEdge.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll',
        'Microsoft.Graphics.Canvas.Interop.dll', 'Assets\AppIcon.ico', 'THIRD-PARTY-NOTICES.txt', 'unins000.exe')) {
        Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $taskAppFolder $taskRequired) -PathType Leaf) "$Stage includes $taskRequired"
    }
    $taskRuntime = Get-Content -LiteralPath (Join-Path $taskAppFolder 'NyKurEdge.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    Assert-InstallerCheck ($null -ne $taskRuntime.runtimeOptions.includedFrameworks -and
        $null -eq $taskRuntime.runtimeOptions.framework) "$Stage bundles .NET runtime"
    $taskManifestPath = Join-Path $taskAppFolder '.nykur-edge-payload.sha256'
    Assert-InstallerCheck (Test-Path -LiteralPath $taskManifestPath -PathType Leaf) "$Stage has managed payload manifest"
    $taskManifestLines = @(Get-Content -LiteralPath $taskManifestPath)
    Assert-InstallerCheck ($taskManifestLines.Count -gt 0) "$Stage managed payload manifest is nonempty"
    foreach ($taskManifestLine in $taskManifestLines) {
        if ($taskManifestLine -notmatch '^([0-9a-f]{64})\|(.+)$') { throw "Invalid installed payload manifest line: $taskManifestLine" }
        $taskFileHash = $Matches[1]
        $taskRelativeFile = $Matches[2]
        $taskManagedFile = [IO.Path]::GetFullPath((Join-Path $taskAppFolder $taskRelativeFile))
        if ([IO.Path]::IsPathRooted($taskRelativeFile) -or
            !$taskManagedFile.StartsWith($taskAppFolder + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Installed payload manifest contains a path outside the application directory.'
        }
        if (!(Test-Path -LiteralPath $taskManagedFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $taskManagedFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskFileHash) {
            throw "Installed payload failed checksum validation: $taskRelativeFile"
        }
    }
    Assert-InstallerCheck ($true) "$Stage managed payload files match manifest SHA-256" $taskManifestLines.Count
    Assert-SettingsPreserved $Stage
}

function Assert-Shortcut([string]$Shortcut, [string]$Name) {
    Assert-InstallerCheck (Test-Path -LiteralPath $Shortcut -PathType Leaf) "$Name shortcut exists" $Shortcut
    $taskShell = New-Object -ComObject WScript.Shell
    $taskLink = $null
    try {
        $taskLink = $taskShell.CreateShortcut($Shortcut)
        Assert-InstallerCheck ([IO.Path]::GetFullPath($taskLink.TargetPath) -eq $taskAppExecutable) "$Name shortcut targets installed application" $taskLink.TargetPath
        $taskIconPath = ($taskLink.IconLocation -replace ',\s*-?\d+$', '').Trim('"')
        Assert-InstallerCheck ($taskIconPath -in @($taskAppExecutable, (Join-Path $taskAppFolder 'Assets\AppIcon.ico')) -and
            (Test-Path -LiteralPath $taskIconPath -PathType Leaf)) "$Name shortcut uses application icon" $taskLink.IconLocation
    }
    finally {
        if ($taskLink) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell)
    }
}

if (!('NyKurInstallerWindows' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NyKurInstallerWindows {
    private delegate bool Callback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    public sealed class Window { public long Handle; public string Title; public string Class; }
    public static Window[] ForProcess(int pid) {
        var windows = new List<Window>();
        EnumWindows((handle, state) => {
            GetWindowThreadProcessId(handle, out uint process);
            if (process == pid) {
                var title = new StringBuilder(512); var name = new StringBuilder(512);
                GetWindowText(handle, title, title.Capacity); GetClassName(handle, name, name.Capacity);
                windows.Add(new Window { Handle=handle.ToInt64(), Title=title.ToString(), Class=name.ToString() });
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static string ReadExecutableManifest(string path) {
        // LOAD_LIBRARY_AS_DATAFILE maps resources without executing entry points.
        var module = LoadLibraryEx(path, IntPtr.Zero, 0x00000002);
        if (module == IntPtr.Zero) throw new InvalidOperationException("Cannot read installer resources.");
        try {
            var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new InvalidOperationException("Installer has no embedded manifest.");
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero || size == 0 || size > 1048576) throw new InvalidOperationException("Invalid installer manifest resource.");
            var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\0');
        }
        finally { FreeLibrary(module); }
    }
}
'@
}

function Get-InstalledProcesses {
    foreach ($taskProcess in @(Get-Process -Name 'NyKurEdge.App' -ErrorAction SilentlyContinue)) {
        try {
            if ([IO.Path]::GetFullPath($taskProcess.Path) -eq $taskAppExecutable) { $taskProcess }
            else { $taskProcess.Dispose() }
        }
        catch { $taskProcess.Dispose() }
    }
}

function Start-InstalledShortcut([string]$Shortcut, [string]$Stage) {
    Assert-InstallerCheck (@(Get-InstalledProcesses).Count -eq 0) "$Stage starts without another installed instance"
    Start-Process -FilePath $Shortcut -WorkingDirectory $env:TEMP -WindowStyle Hidden
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(20)
    $taskFound = $null
    while ([DateTime]::UtcNow -lt $taskDeadline) {
        $taskCandidates = @(Get-InstalledProcesses)
        foreach ($taskCandidate in $taskCandidates) { [void]$taskOwnedProcesses.Add($taskCandidate.Id) }
        if ($taskCandidates.Count -gt 1) { throw "$Stage created more than one installed instance." }
        if ($taskCandidates.Count -eq 1) {
            $taskWindows = [NyKurInstallerWindows]::ForProcess($taskCandidates[0].Id)
            if (($taskWindows | Where-Object Title -EQ 'NyKur Edge') -and
                ($taskWindows | Where-Object Class -EQ 'NyKurEdge.NotificationAreaHost')) {
                $taskFound = $taskCandidates[0]
                break
            }
            $taskCandidates[0].Dispose()
        }
        Start-Sleep -Milliseconds 250
    }
    Assert-InstallerCheck ($null -ne $taskFound) "$Stage launches edge and tray from shortcut" $Shortcut
    # Get-Process does not eagerly retain an OS process handle. Open it while the
    # app is alive so ExitCode remains available after graceful termination.
    $taskNativeHandle = $taskFound.Handle
    Assert-InstallerCheck ($taskNativeHandle -ne [IntPtr]::Zero -and $taskNativeHandle -ne [IntPtr](-1)) "$Stage retains process handle for exact exit-code validation" $taskFound.Id
    $taskModules = @($taskFound.Modules | Where-Object ModuleName -In @('coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.Graphics.Canvas.Interop.dll') | ForEach-Object FileName)
    Assert-InstallerCheck ($taskModules.Count -eq 3 -and @($taskModules | Where-Object {
        ![IO.Path]::GetFullPath($_).StartsWith($taskAppFolder + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count -eq 0) "$Stage loads bundled runtime/rendering modules" $taskModules
    return $taskFound
}

function Close-InstalledProcess([Diagnostics.Process]$Process, [string]$Stage) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "$Stage application exited unexpectedly." }
    if (!$taskOwnedProcesses.Contains($Process.Id) -or [IO.Path]::GetFullPath($Process.Path) -ne $taskAppExecutable) {
        throw 'Refusing to close a process not launched by this validation.'
    }
    $taskMain = [NyKurInstallerWindows]::ForProcess($Process.Id) | Where-Object Title -EQ 'NyKur Edge' | Select-Object -First 1
    Assert-InstallerCheck ($null -ne $taskMain) "$Stage main window available for graceful close"
    [void][NyKurInstallerWindows]::PostMessage([IntPtr]$taskMain.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Assert-InstallerCheck ($Process.WaitForExit(15000)) "$Stage exits after normal WM_CLOSE"
    Assert-InstallerCheck ($Process.ExitCode -eq 0) "$Stage exits cleanly" $Process.ExitCode
    [void]$taskOwnedProcesses.Remove($Process.Id)
    $Process.Dispose()
}

function Get-InstalledSnapshot {
    return [ordered]@{
        executableSha256 = (Get-FileHash -LiteralPath $taskAppExecutable -Algorithm SHA256).Hash
        registry = (Read-UninstallValues | ConvertTo-Json -Depth 8 -Compress)
        settingsSha256 = Get-SettingsHash
    } | ConvertTo-Json -Depth 8 -Compress
}

try {
    # Refuse all pre-existing state before creating settings or invoking any setup.
    foreach ($taskExisting in @($taskAppFolder, $taskStartMenu, $taskDesktop)) {
        if (Test-Path -LiteralPath $taskExisting) { throw "Refusing existing installation or shortcut: $taskExisting" }
    }
    if ((Read-UninstallValues) -or (Read-UninstallValues -Hive LocalMachine) -or (Count-UserUninstallEntries) -ne 0) {
        throw 'Refusing to replace an existing NyKur Edge installed-app entry.'
    }
    if (@(Get-Process -Name 'NyKurEdge.App' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Exit existing NyKur Edge processes before isolated installer validation.'
    }
    $taskSetupManifest = [xml][NyKurInstallerWindows]::ReadExecutableManifest($taskInstaller)
    $taskExecutionLevel = $taskSetupManifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    Assert-InstallerCheck ($taskExecutionLevel -and $taskExecutionLevel.GetAttribute('level') -eq 'asInvoker' -and
        $taskExecutionLevel.GetAttribute('uiAccess') -eq 'false') 'setup executable manifest requests no administrator elevation' $taskExecutionLevel.OuterXml
    $taskSettingsFolderExisted = Test-Path -LiteralPath $taskSettingsFolder -PathType Container
    $taskSettingsExisted = Test-Path -LiteralPath $taskSettingsPath -PathType Leaf
    if (Test-Path -LiteralPath $taskSettingsPath -PathType Container) { throw 'Settings path unexpectedly names a directory.' }
    if ($taskSettingsExisted) {
        $taskOriginalSettings = [IO.File]::ReadAllBytes($taskSettingsPath)
        $taskOriginalSettingsWriteTime = [IO.File]::GetLastWriteTimeUtc($taskSettingsPath)
    }
    $taskSettingsCaptured = $true
    if (!$taskSettingsExisted) {
        [void][IO.Directory]::CreateDirectory($taskSettingsFolder)
        [IO.File]::WriteAllText($taskSettingsPath, @'
{"SchemaVersion":1,"LaunchOnStartup":false,"EdgeSide":"Left","Appearance":{"AccentMode":"Manual","ManualAccent":"#21AABB","AnimationIntensity":"Quiet","EdgeThickness":17},"Media":{"Enabled":true},"Notifications":{"Enabled":false,"Privacy":"AppOnly","SourceOverrides":{}},"Clock":{"Enabled":true,"IntervalMinutes":75,"HoldSeconds":6}}
'@, [Text.UTF8Encoding]::new($false))
    }
    $taskExpectedSettingsHash = Get-SettingsHash
    if (!$taskSettingsExisted) { $taskSeedSettingsHash = $taskExpectedSettingsHash }
    [void][IO.Directory]::CreateDirectory($taskLogFolder)

    if ($taskBaseline) {
        $taskInstallationTouched = $true
        $taskOwnsInstallation = $true
        Assert-InstallerCheck ((Invoke-InstallerExecutable $taskBaseline 'baseline-install') -eq 0) 'private older-version fixture installs'
        Assert-InstalledState $taskBaselineVersion $taskBaselineNumericVersion 'baseline installation'
        Assert-Shortcut $taskStartMenu 'baseline Start Menu'
        Assert-Shortcut $taskDesktop 'baseline desktop'
        if ($taskRetiredFile) {
            Assert-InstallerCheck (Test-Path -LiteralPath $taskRetiredFile -PathType Leaf) 'baseline includes obsolete managed-file fixture' $RetiredRelativePath
        }
        $taskProcess = Start-InstalledShortcut $taskStartMenu 'baseline Start Menu'
        # The previously published preview was validated after startup settled;
        # it predates the lifecycle fixes and is not a rapid-close test target.
        Start-Sleep -Seconds 5
        Assert-InstallerCheck ($true) 'baseline startup settles before normal close' ([ordered]@{ seconds = 5; scope = 'Matches bounded prior-preview startup testing; no claim of rapid-close robustness.' })
        Close-InstalledProcess $taskProcess 'baseline Start Menu'
        [IO.File]::WriteAllText($taskUserFixture, $taskFixtureContent, [Text.UTF8Encoding]::new($false))
        $taskUserFixtureCreated = $true
    }

    $taskInstallationTouched = $true
    $taskOwnsInstallation = $true
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskInstaller 'current-install-or-upgrade') -eq 0) 'current installer completes'
    Assert-InstalledState $taskFinalVersion $taskFinalNumericVersion 'current installation'
    Assert-Shortcut $taskStartMenu 'current Start Menu'
    Assert-Shortcut $taskDesktop 'current desktop'
    $taskApplicationVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskAppExecutable).ProductVersion.Trim()
    Assert-InstallerCheck ($taskApplicationVersion -eq $taskFinalVersion -or $taskApplicationVersion.StartsWith($taskFinalVersion + '+')) 'installed application version matches installer' $taskApplicationVersion
    if ($taskRetiredFile) {
        Assert-InstallerCheck (!(Test-Path -LiteralPath $taskRetiredFile)) 'upgrade removes obsolete unchanged managed fixture' $RetiredRelativePath
    }
    if (!$taskUserFixtureCreated) {
        [IO.File]::WriteAllText($taskUserFixture, $taskFixtureContent, [Text.UTF8Encoding]::new($false))
        $taskUserFixtureCreated = $true
    }
    Assert-InstallerCheck ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) 'upgrade preserves unrecorded user file'

    $taskProcess = Start-InstalledShortcut $taskStartMenu 'current Start Menu'
    $taskDuplicate = Start-Process -FilePath $taskAppExecutable -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    [void]$taskOwnedProcesses.Add($taskDuplicate.Id)
    try {
        Assert-InstallerCheck ($taskDuplicate.WaitForExit(15000)) 'duplicate launch exits instead of creating another edge'
        Assert-InstallerCheck ($taskDuplicate.ExitCode -eq 0) 'duplicate launch exits successfully' $taskDuplicate.ExitCode
        $taskProcess.Refresh()
        Assert-InstallerCheck (!$taskProcess.HasExited -and @(Get-InstalledProcesses).Count -eq 1) 'first instance remains alive after duplicate launch'
    }
    finally {
        $taskDuplicate.Refresh()
        if ($taskDuplicate.HasExited) { [void]$taskOwnedProcesses.Remove($taskDuplicate.Id) }
        $taskDuplicate.Dispose()
    }

    $taskBefore = Get-InstalledSnapshot
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskInstaller 'running-upgrade-blocked') -ne 0) 'running current application blocks silent upgrade or repair'
    Assert-InstallerCheck ((Get-InstalledSnapshot) -eq $taskBefore) 'blocked upgrade preserves executable, registry and preferences'
    $taskProcess.Refresh()
    Assert-InstallerCheck (!$taskProcess.HasExited) 'blocked upgrade leaves current application running'
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskUninstaller 'running-uninstall-blocked' -Uninstall) -ne 0) 'running application blocks silent uninstall'
    Assert-InstallerCheck ((Get-InstalledSnapshot) -eq $taskBefore) 'blocked uninstall preserves executable, registry and preferences'
    $taskProcess.Refresh()
    Assert-InstallerCheck (!$taskProcess.HasExited) 'blocked uninstall leaves current application running'
    Close-InstalledProcess $taskProcess 'current Start Menu'

    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskInstaller 'same-version-repair') -eq 0) 'same-version repair completes after exit'
    Assert-InstalledState $taskFinalVersion $taskFinalNumericVersion 'repaired installation'
    Assert-InstallerCheck ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) 'repair preserves unrecorded user file'
    if ($taskBaseline) {
        $taskBefore = Get-InstalledSnapshot
        Assert-InstallerCheck ((Invoke-InstallerExecutable $taskBaseline 'downgrade-blocked') -ne 0) 'older private fixture is rejected after current installation'
        Assert-InstallerCheck ((Get-InstalledSnapshot) -eq $taskBefore) 'blocked downgrade preserves executable, registry and preferences'
    }

    # Simulate a setup holding its transaction gate. App launch must decline before
    # settings/providers/windows are created, and must never escape into an instance.
    $taskLifecycleGate = [Threading.Mutex]::new($false, 'Local\NyKurEdge.App.Lifecycle')
    $taskLifecycleOwned = $false
    $taskDeclinedLaunch = $null
    try {
        try { $taskLifecycleOwned = $taskLifecycleGate.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $taskLifecycleOwned = $true }
        Assert-InstallerCheck $taskLifecycleOwned 'validation acquires lifecycle transaction gate'
        $taskDeclinedLaunch = Start-Process -FilePath $taskAppExecutable -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
        [void]$taskOwnedProcesses.Add($taskDeclinedLaunch.Id)
        $taskSawApplicationWindow = $false
        $taskDeclinedDeadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!$taskDeclinedLaunch.HasExited -and [DateTime]::UtcNow -lt $taskDeclinedDeadline) {
            $taskGateWindows = [NyKurInstallerWindows]::ForProcess($taskDeclinedLaunch.Id)
            if (($taskGateWindows | Where-Object Title -EQ 'NyKur Edge') -or
                ($taskGateWindows | Where-Object Class -EQ 'NyKurEdge.NotificationAreaHost')) {
                $taskSawApplicationWindow = $true
            }
            Start-Sleep -Milliseconds 100
            $taskDeclinedLaunch.Refresh()
        }
        Assert-InstallerCheck $taskDeclinedLaunch.HasExited 'application declines launch during installation transaction'
        Assert-InstallerCheck ($taskDeclinedLaunch.ExitCode -eq 0 -and !$taskSawApplicationWindow) 'declined transaction-time launch exits cleanly without edge or tray' $taskDeclinedLaunch.ExitCode
        Assert-SettingsPreserved 'declined transaction-time launch'
    }
    finally {
        if ($taskLifecycleOwned) { $taskLifecycleGate.ReleaseMutex() }
        $taskLifecycleGate.Dispose()
        if ($taskDeclinedLaunch) {
            $taskDeclinedLaunch.Refresh()
            if ($taskDeclinedLaunch.HasExited) { [void]$taskOwnedProcesses.Remove($taskDeclinedLaunch.Id) }
            $taskDeclinedLaunch.Dispose()
        }
    }

    # This also exercises the real tray/settings callback and normal cleanup.
    & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -Executable $taskAppExecutable `
        -ReportPath (Join-Path (Split-Path $taskReport) 'installed-native-runtime.json') | Out-Null
    Assert-InstallerCheck ($true) 'installed application passes native edge, tray, settings and graceful-close smoke test'
    Assert-SettingsPreserved 'installed native smoke test'
    $taskProcess = Start-InstalledShortcut $taskDesktop 'current desktop'
    Close-InstalledProcess $taskProcess 'current desktop'

    $taskInstalledManifestPath = Join-Path $taskAppFolder '.nykur-edge-payload.sha256'
    $taskInstalledManagedPaths = @(Get-Content -LiteralPath $taskInstalledManifestPath | ForEach-Object {
        if ($_ -match '^[0-9a-f]{64}\|(.+)$') { Join-Path $taskAppFolder $Matches[1] }
        else { throw 'Installed payload manifest changed before uninstall validation.' }
    })
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskUninstaller 'normal-uninstall' -Uninstall) -eq 0) 'normal uninstall completes'
    # The uninstaller can schedule removal of its own bootstrap executable at exit.
    $taskUninstallDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $taskUninstaller) -and [DateTime]::UtcNow -lt $taskUninstallDeadline) {
        Start-Sleep -Milliseconds 250
    }
    Assert-InstallerCheck ($null -eq (Read-UninstallValues) -and (Count-UserUninstallEntries) -eq 0) 'uninstall removes installed-app registration'
    $taskRetainedManagedFiles = @($taskInstalledManagedPaths | Where-Object { Test-Path -LiteralPath $_ })
    Assert-InstallerCheck ($taskRetainedManagedFiles.Count -eq 0) 'uninstall removes every managed payload file' $taskRetainedManagedFiles
    foreach ($taskRemoved in @($taskInstalledManifestPath, $taskUninstaller, $taskStartMenu, $taskDesktop)) {
        Assert-InstallerCheck (!(Test-Path -LiteralPath $taskRemoved)) 'uninstall removes owned program file or shortcut' $taskRemoved
    }
    Assert-InstallerCheck ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) 'uninstall preserves unrecorded user file'
    Assert-SettingsPreserved 'uninstall'
    $taskOwnsInstallation = $false

    # A normal reinstall must find retained preferences, including after Windows
    # has removed the installed-app entry and its previous uninstaller.
    $taskOwnsInstallation = $true
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskInstaller 'reinstall-after-uninstall') -eq 0) 'reinstallation after removal completes'
    Assert-InstalledState $taskFinalVersion $taskFinalNumericVersion 'reinstalled application'
    Assert-Shortcut $taskStartMenu 'reinstalled Start Menu'
    Assert-Shortcut $taskDesktop 'reinstalled desktop'
    Assert-InstallerCheck ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) 'reinstallation preserves unrecorded user file'
    for ($taskRapidLaunch = 1; $taskRapidLaunch -le 3; $taskRapidLaunch++) {
        # Close immediately once edge/tray/modules are observed. Delaying here
        # would hide launch-versus-shutdown races in the current application.
        $taskRapidStage = "reinstalled rapid Start Menu launch $taskRapidLaunch"
        $taskProcess = Start-InstalledShortcut $taskStartMenu $taskRapidStage
        Close-InstalledProcess $taskProcess $taskRapidStage
        Assert-SettingsPreserved $taskRapidStage
    }
    Assert-InstallerCheck ((Invoke-InstallerExecutable $taskUninstaller 'reinstalled-normal-uninstall' -Uninstall) -eq 0) 'reinstalled application uninstalls cleanly'
    $taskUninstallDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $taskUninstaller) -and [DateTime]::UtcNow -lt $taskUninstallDeadline) {
        Start-Sleep -Milliseconds 250
    }
    Assert-InstallerCheck ($null -eq (Read-UninstallValues) -and (Count-UserUninstallEntries) -eq 0) 'second uninstall removes installed-app registration'
    $taskRetainedManagedFiles = @($taskInstalledManagedPaths | Where-Object { Test-Path -LiteralPath $_ })
    Assert-InstallerCheck ($taskRetainedManagedFiles.Count -eq 0) 'second uninstall removes every managed payload file' $taskRetainedManagedFiles
    foreach ($taskRemoved in @($taskInstalledManifestPath, $taskUninstaller, $taskStartMenu, $taskDesktop)) {
        Assert-InstallerCheck (!(Test-Path -LiteralPath $taskRemoved)) 'second uninstall removes owned integration file or shortcut' $taskRemoved
    }
    Assert-InstallerCheck ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) 'second uninstall preserves unrecorded user file'
    Assert-SettingsPreserved 'second uninstall'
    $taskOwnsInstallation = $false
}
catch { $taskFailure = $_.Exception.ToString() }
finally {
    # Close only processes launched by this script, after rechecking executable ownership.
    foreach ($taskId in @($taskOwnedProcesses)) {
        try {
            $taskProcess = Get-Process -Id $taskId -ErrorAction SilentlyContinue
            if ($taskProcess -and [IO.Path]::GetFullPath($taskProcess.Path) -eq $taskAppExecutable) {
                $taskMain = [NyKurInstallerWindows]::ForProcess($taskId) | Where-Object Title -EQ 'NyKur Edge' | Select-Object -First 1
                if ($taskMain) { [void][NyKurInstallerWindows]::PostMessage([IntPtr]$taskMain.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
                if (!$taskProcess.WaitForExit(15000)) { $taskCleanupIssues.Add("Owned application PID $taskId did not close gracefully; it was not force-killed.") }
                $taskProcess.Dispose()
            }
        }
        catch { $taskCleanupIssues.Add("Owned process cleanup: $($_.Exception.Message)") }
    }
    foreach ($taskId in @($taskActiveInstallerProcesses)) {
        $taskStillRunning = Get-Process -Id $taskId -ErrorAction SilentlyContinue
        if ($taskStillRunning) {
            $taskCleanupIssues.Add("Owned setup/uninstaller PID $taskId remains running; no second uninstaller was started and it was not force-killed.")
            $taskStillRunning.Dispose()
        }
        else { [void]$taskActiveInstallerProcesses.Remove($taskId) }
    }
    if ($taskOwnsInstallation -and $taskActiveInstallerProcesses.Count -eq 0 -and
        (Test-Path -LiteralPath $taskUninstaller -PathType Leaf)) {
        try {
            $taskExit = Invoke-InstallerExecutable $taskUninstaller 'cleanup-uninstall' -Uninstall
            if ($taskExit -ne 0) { $taskCleanupIssues.Add("Owned installation cleanup exited $taskExit.") }
        }
        catch { $taskCleanupIssues.Add("Owned installation cleanup: $($_.Exception.Message)") }
    }
    if ($taskUserFixtureCreated -and (Test-Path -LiteralPath $taskUserFixture -PathType Leaf)) {
        try {
            # Delete only the exact fixture we wrote, and only when its content is unchanged.
            if ([IO.File]::ReadAllText($taskUserFixture) -eq $taskFixtureContent) { [IO.File]::Delete($taskUserFixture) }
            else { $taskCleanupIssues.Add('User-file fixture was modified; it was preserved for inspection.') }
        }
        catch { $taskCleanupIssues.Add("User fixture cleanup: $($_.Exception.Message)") }
    }
    if ($taskOwnsInstallation -and ((Read-UninstallValues) -or (Test-Path -LiteralPath $taskAppExecutable))) {
        $taskCleanupIssues.Add("Owned installation remains for inspection at $taskAppFolder.")
    }
    # Never recursively delete an installation or preferences directory.
    if ($taskInstallationTouched -and (Test-Path -LiteralPath $taskAppFolder -PathType Container)) {
        try {
            if ([IO.Directory]::GetFileSystemEntries($taskAppFolder).Length -eq 0) { [IO.Directory]::Delete($taskAppFolder, $false) }
        }
        catch { $taskCleanupIssues.Add("Empty installation directory cleanup: $($_.Exception.Message)") }
    }
    if ($taskSettingsCaptured) {
        try {
            if ($taskSettingsExisted) {
                if (![IO.File]::Exists($taskSettingsPath) -or (Get-SettingsHash) -ne $taskExpectedSettingsHash) {
                    [void][IO.Directory]::CreateDirectory($taskSettingsFolder)
                    [IO.File]::WriteAllBytes($taskSettingsPath, $taskOriginalSettings)
                    [IO.File]::SetLastWriteTimeUtc($taskSettingsPath, $taskOriginalSettingsWriteTime)
                }
            }
            elseif (Test-Path -LiteralPath $taskSettingsPath -PathType Leaf) {
                if ($taskSeedSettingsHash -and (Get-SettingsHash) -eq $taskSeedSettingsHash) {
                    [IO.File]::Delete($taskSettingsPath)
                }
                else { $taskCleanupIssues.Add('Seed settings changed during validation; they were preserved for inspection.') }
            }
            if (!$taskSettingsFolderExisted -and (Test-Path -LiteralPath $taskSettingsFolder -PathType Container) -and
                [IO.Directory]::GetFileSystemEntries($taskSettingsFolder).Length -eq 0) {
                [IO.Directory]::Delete($taskSettingsFolder, $false)
            }
        }
        catch { $taskCleanupIssues.Add("Original settings restoration: $($_.Exception.Message)") }
    }
    if ($taskSettingsCaptured) {
        [void][IO.Directory]::CreateDirectory((Split-Path $taskReport))
        $taskEvidence = [ordered]@{
            installer = $taskInstaller
            installerVersion = $taskFinalVersion
            installerSha256 = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
            baselineInstaller = $taskBaseline
            baselineVersion = $taskBaselineVersion
            baselineIsPrivateUpgradeFixture = [bool]$taskBaseline
            operatingSystem = [Environment]::OSVersion.Version.ToString()
            defaultInstallDirectory = $taskAppFolder
            perUserEvidence = 'Setup executable manifest requests asInvoker; installation uses HKCU and LocalApplicationData Programs. This test does not certify installation under a separate standard-user token.'
            settingsWerePreExisting = $taskSettingsExisted
            preservedSettingsSha256 = $taskExpectedSettingsHash
            obsoleteManagedFixture = $RetiredRelativePath
            checks = $taskChecks.ToArray()
            cleanupIssues = $taskCleanupIssues.ToArray()
            passed = !$taskFailure -and $taskCleanupIssues.Count -eq 0
            failure = $taskFailure
            checkedAtUtc = [DateTime]::UtcNow.ToString('o')
            scope = 'Real silent per-user installation, shortcuts, native launch/close including three immediate current-version close cycles, duplicate launch, running guards, transaction-time launch rejection, repair, optional private-baseline upgrade/downgrade, uninstall, reinstall and preference retention. The older baseline gets an explicit five-second startup settling wait. Interactive wizard and clean-machine compatibility require separate verification.'
        }
        $taskEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskReport -Encoding utf8
        $taskEvidence | ConvertTo-Json -Depth 12
    }
}
if ($taskFailure -or $taskCleanupIssues.Count -ne 0) {
    throw "Installer lifecycle validation failed. Read $taskReport. $taskFailure $($taskCleanupIssues -join ' ')"
}
