#requires -Version 7.0

param(
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.\d+$')][string]$Version = '0.1.0-preview.2',
    [string]$CompilerPath = ''
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskReleaseRoot = Join-Path $taskRoot "artifacts/releases/$Version"
$taskStage = Join-Path $taskReleaseRoot 'NyKur Edge'
$taskMetadataPath = Join-Path $taskReleaseRoot 'release.json'
$taskInstallerAsset = "NyKur-Edge-$Version-Setup-x64.exe"
$taskInstallerPath = Join-Path $taskReleaseRoot $taskInstallerAsset
$taskManifest = Join-Path $taskReleaseRoot 'installer-payload-manifest.txt'
$taskExpectedCompilerHash = '0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7'
if (Test-Path -LiteralPath $taskInstallerPath) {
    throw "Installer already exists. Preserve or remove it explicitly before rebuilding: $taskInstallerPath"
}
if (!(Test-Path -LiteralPath $taskMetadataPath) -or !(Test-Path -LiteralPath $taskStage)) {
    throw 'Build the portable payload with Build-Portable.ps1 first. This builder does not republish or alter that payload.'
}
$taskMetadata = Get-Content -LiteralPath $taskMetadataPath -Raw | ConvertFrom-Json
if ($taskMetadata.version -ne $Version -or $taskMetadata.format -ne 'Portable ZIP' -or
    $taskMetadata.asset -ne "NyKur-Edge-$Version-win-x64.zip" -or
    $taskMetadata.sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Portable release metadata does not match the requested installer version or expected format.'
}
$taskCurrentCommit = (& git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $taskCurrentCommit -ne $taskMetadata.sourceCommit) {
    throw 'The portable payload does not match the current source commit. Build from the intended release commit first.'
}
& git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" diff --quiet HEAD
if ($LASTEXITCODE -ne 0) { throw 'Commit tracked changes before compiling a release installer.' }
$taskUntrackedSource = & git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" ls-files --others --exclude-standard -- src tools .github
if ($LASTEXITCODE -ne 0 -or $taskUntrackedSource) { throw 'Commit new application or release-tool files before compiling a release installer.' }
$taskZip = Join-Path $taskReleaseRoot $taskMetadata.asset
if (!(Test-Path -LiteralPath $taskZip) -or
    (Get-Item -LiteralPath $taskZip).Length -ne $taskMetadata.sizeBytes -or
    (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskMetadata.sha256) {
    throw 'The portable ZIP does not match its release metadata.'
}
# Compare every staged file to its ZIP entry before compiling. A matching ZIP hash
# alone does not establish that the loose directory has remained unchanged.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    $taskArchiveFiles = @{}
    foreach ($taskEntry in $taskArchive.Entries) {
        if (!$taskEntry.Name) { continue }
        $taskEntryName = $taskEntry.FullName.Replace('\', '/')
        if (!$taskEntryName.StartsWith('NyKur Edge/', [StringComparison]::Ordinal)) {
            throw "Unexpected ZIP entry: $taskEntryName"
        }
        $taskRelative = $taskEntryName.Substring('NyKur Edge/'.Length)
        if ($taskArchiveFiles.ContainsKey($taskRelative)) { throw "Duplicate ZIP entry: $taskRelative" }
        $taskArchiveFiles[$taskRelative] = $taskEntry
    }
    if (Get-ChildItem -LiteralPath $taskStage -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'The payload contains a redirected file or directory.'
    }
    $taskFiles = @(Get-ChildItem -LiteralPath $taskStage -Recurse -File | Sort-Object FullName)
    if ($taskFiles.Count -ne $taskArchiveFiles.Count -or !$taskFiles.Count) {
        throw 'Loose payload files no longer match the portable ZIP file inventory.'
    }
    $taskManifestLines = foreach ($taskFile in $taskFiles) {
        if ($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Payload contains a reparse point: $($taskFile.Name)" }
        $taskRelative = [IO.Path]::GetRelativePath($taskStage, $taskFile.FullName).Replace('\', '/')
        if ($taskRelative -eq '.nykur-edge-payload.sha256' -or $taskRelative -match '[|\r\n\x80-\uffff]') {
            throw "Reserved or invalid payload name: $taskRelative"
        }
        if (!$taskArchiveFiles.ContainsKey($taskRelative)) { throw "Unrecorded staged file: $taskRelative" }
        $taskStream = $taskArchiveFiles[$taskRelative].Open()
        $taskAlgorithm = [Security.Cryptography.SHA256]::Create()
        try { $taskZipHash = ([BitConverter]::ToString($taskAlgorithm.ComputeHash($taskStream))).Replace('-', '').ToLowerInvariant() }
        finally { $taskStream.Dispose(); $taskAlgorithm.Dispose() }
        $taskFileHash = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($taskZipHash -ne $taskFileHash) { throw "Loose payload was modified after ZIP creation: $taskRelative" }
        "$taskFileHash|$taskRelative"
    }
} finally { $taskArchive.Dispose() }
if (!$CompilerPath) { $CompilerPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
$taskCompiler = (Resolve-Path -LiteralPath $CompilerPath).Path
if ((Get-FileHash -LiteralPath $taskCompiler -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpectedCompilerHash) {
    throw 'Compiler does not match pinned Inno Setup 6.7.3. Use Get-InnoSetup.ps1.'
}
$taskVersionParts = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)-preview\.(\d+)$').Groups
$taskNumericParts = @(1..4 | ForEach-Object { [uint32]::Parse($taskVersionParts[$_].Value) })
if ($taskNumericParts | Where-Object { $_ -gt 65535 }) { throw 'Every installer version component must fit a Windows WORD (0..65535).' }
$taskVersionInfo = $taskNumericParts -join '.'
$taskManifestLines | Set-Content -LiteralPath $taskManifest -Encoding ascii
& $taskCompiler "/DReleaseVersion=$Version" "/DVersionInfo=$taskVersionInfo" "/DPayloadDir=$taskStage" `
    "/DPayloadManifest=$taskManifest" "/DOutputDir=$taskReleaseRoot" "/DSourceCommit=$taskCurrentCommit" `
    (Join-Path $PSScriptRoot 'NyKurEdge.iss')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $taskInstallerPath)) { throw 'Inno Setup compilation failed.' }
$taskInstallerHash = (Get-FileHash -LiteralPath $taskInstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$taskInstaller = [ordered]@{
    version = $Version
    versionInfo = $taskVersionInfo
    sourceCommit = $taskCurrentCommit
    asset = $taskInstallerAsset
    sizeBytes = (Get-Item -LiteralPath $taskInstallerPath).Length
    sha256 = $taskInstallerHash
    platform = 'Windows 11 x64'
    format = 'Per-user EXE installer'
    signed = $false
    lifecycleValidated = $false
    compilerVersion = 'Inno Setup 6.7.3'
    compilerSha256 = $taskExpectedCompilerHash
    payloadManifestSha256 = (Get-FileHash -LiteralPath $taskManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    downloadUrl = "https://github.com/NyKurr/NyKurEdge/releases/download/v$Version/$taskInstallerAsset"
}
$taskMetadata | Add-Member -MemberType NoteProperty -Name installer -Value $taskInstaller -Force
$taskMetadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskMetadataPath -Encoding utf8
@("$taskInstallerHash  $taskInstallerAsset", "$($taskMetadata.sha256)  $($taskMetadata.asset)") |
    Set-Content -LiteralPath (Join-Path $taskReleaseRoot 'SHA256SUMS.txt') -Encoding ascii
$taskInstaller | ConvertTo-Json -Depth 4
