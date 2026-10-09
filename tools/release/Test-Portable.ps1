param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$ReportPath = "artifacts/release-validation/portable-smoke.json"
)

$ErrorActionPreference = 'Stop'
$taskExecutable = (Resolve-Path -LiteralPath $Executable).Path
$taskReport = [IO.Path]::GetFullPath($ReportPath)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NyKurSmokeWindows {
    public delegate bool Callback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
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
}
'@

$taskProcess = $null
try {
    # Launch outside the application folder to catch relative resource/icon errors.
    $taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 8
    $taskProcess.Refresh()
    if ($taskProcess.HasExited) { throw "Preview exited during startup: $($taskProcess.ExitCode)" }
    $taskWindows = [NyKurSmokeWindows]::ForProcess($taskProcess.Id)
    $taskMain = $taskWindows | Where-Object Title -EQ 'NyKur Edge' | Select-Object -First 1
    $taskTray = $taskWindows | Where-Object Class -EQ 'NyKurEdge.NotificationAreaHost' | Select-Object -First 1
    if (!$taskMain -or !$taskTray) { throw 'Edge window or native tray host failed to initialize.' }
    $taskLoaded = @($taskProcess.Modules | Where-Object ModuleName -In @('coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.Graphics.Canvas.Interop.dll') | ForEach-Object FileName)
    if ($taskLoaded.Count -ne 3) { throw 'Required rendering/runtime modules did not load.' }
    foreach ($taskModule in $taskLoaded) {
        if (![IO.Path]::GetFullPath($taskModule).StartsWith((Split-Path $taskExecutable) + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime loaded outside the self-contained folder: $taskModule"
        }
    }

    # Exercise the real tray callback that opens the normal Settings window.
    [NyKurSmokeWindows]::PostMessage([IntPtr]$taskTray.Handle, 0x804E, [IntPtr]::Zero, [IntPtr]0x0203) | Out-Null
    Start-Sleep -Seconds 3
    $taskSettings = [NyKurSmokeWindows]::ForProcess($taskProcess.Id) | Where-Object Title -EQ 'NyKur Edge Settings' | Select-Object -First 1
    if (!$taskSettings) { throw 'Settings did not open through the tray callback.' }
    [NyKurSmokeWindows]::PostMessage([IntPtr]$taskSettings.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 1
    [NyKurSmokeWindows]::PostMessage([IntPtr]$taskMain.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $taskCleanExit = $taskProcess.WaitForExit(10000)
    if (!$taskCleanExit -or $taskProcess.ExitCode -ne 0) { throw 'Preview did not shut down cleanly.' }
    $taskEvidence = [ordered]@{
        executable = $taskExecutable
        operatingSystem = [Environment]::OSVersion.Version.ToString()
        launchedOutsideAppFolder = $true
        edgeWindowCreated = $true
        trayHostCreated = $true
        settingsOpenedViaTray = $true
        bundledRuntimeModules = $taskLoaded
        cleanExit = $true
        exitCode = $taskProcess.ExitCode
        checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        scope = 'Bounded launch, rendering initialization, tray/settings and clean shutdown. Not a clean-machine or long-session compatibility certification.'
    }
    New-Item -ItemType Directory -Force (Split-Path $taskReport) | Out-Null
    $taskEvidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $taskReport -Encoding utf8
    $taskEvidence | ConvertTo-Json -Depth 4
}
finally {
    if ($taskProcess) {
        $taskProcess.Refresh()
        if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id -Force }
        $taskProcess.Dispose()
    }
}
