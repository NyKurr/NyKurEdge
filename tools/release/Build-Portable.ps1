param(
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.\d+$')][string]$Version = '0.1.0-preview.2',
    [switch]$SkipRuntimeCheck
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskReleaseRoot = Join-Path $taskRoot "artifacts/releases/$Version"
$taskStage = Join-Path $taskReleaseRoot 'NyKur Edge'
$taskAsset = "NyKur-Edge-$Version-win-x64.zip"
$taskSourceCommit = & git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required for release provenance.' }
& git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" diff --quiet HEAD
if ($LASTEXITCODE -ne 0) { throw 'Commit tracked changes before building a release so its source provenance is accurate.' }
$taskUntrackedSource = & git -C $taskRoot -c "safe.directory=$($taskRoot.Replace('\', '/'))" ls-files --others --exclude-standard -- src tools .github
if ($LASTEXITCODE -ne 0 -or $taskUntrackedSource) { throw 'Commit new application or release-tool files before building a release.' }
if (Test-Path -LiteralPath $taskReleaseRoot) {
    throw "Release directory already exists. Preserve or remove it explicitly before rebuilding: $taskReleaseRoot"
}
New-Item -ItemType Directory -Force $taskStage | Out-Null
Push-Location $taskRoot
try {
    & dotnet publish src/NyKurEdge.App/NyKurEdge.App.csproj -c Release `
        -p:Platform=x64 -p:NyKurEdgePortable=true -p:PublishProfile=win-x64-portable `
        -p:NyKurEdgeVisualTest=false -p:UseSharedCompilation=false `
        "-p:Version=$Version" "-p:InformationalVersion=$Version" "-p:ApplicationDisplayVersion=$Version" `
        -p:DebugType=None -p:DebugSymbols=false -o $taskStage --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    foreach ($taskRequired in @('NyKurEdge.App.exe', 'NyKurEdge.App.dll', 'NyKurEdge.App.pri',
        'NyKurEdge.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll',
        'Microsoft.Graphics.Canvas.Interop.dll', 'Assets/AppIcon.ico')) {
        if (!(Test-Path -LiteralPath (Join-Path $taskStage $taskRequired))) { throw "Missing release file: $taskRequired" }
    }
    $taskRuntime = Get-Content (Join-Path $taskStage 'NyKurEdge.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$taskRuntime.runtimeOptions.includedFrameworks -or $taskRuntime.runtimeOptions.framework) {
        throw 'The publish still depends on an installed .NET runtime.'
    }
    if (Get-ChildItem $taskStage -Recurse -File | Where-Object { $_.Extension -in @('.pfx', '.cer', '.pdb') -or $_.Name -eq 'nykur-edge.visual-test' }) {
        throw 'Release contains development/signing files.'
    }

    Copy-Item (Join-Path $PSScriptRoot 'START-HERE.txt') (Join-Path $taskStage 'START-HERE.txt')
    Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') (Join-Path $taskStage 'THIRD-PARTY-NOTICES.txt')
    $taskLicenses = Join-Path $taskStage 'licenses'
    New-Item -ItemType Directory -Force $taskLicenses | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'licenses/*') $taskLicenses

    # Preserve the notices delivered with the exact restored packages.
    $taskAssets = Get-Content src/NyKurEdge.App/obj/project.assets.json -Raw | ConvertFrom-Json
    $taskPackages = @($taskAssets.libraries.PSObject.Properties | Where-Object {
        $_.Value.type -eq 'package' -and $_.Name -notmatch '^Microsoft.Windows.SDK.BuildTools'
    } | ForEach-Object Name)
    $taskRuntimeVersion = ($taskRuntime.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App').version
    $taskPackages += "Microsoft.NETCore.App.Runtime.win-x64/$taskRuntimeVersion"
    foreach ($taskPackage in $taskPackages) {
        foreach ($taskFolder in $taskAssets.packageFolders.PSObject.Properties.Name) {
            $taskPackagePath = Join-Path $taskFolder $taskPackage.ToLowerInvariant()
            if (!(Test-Path -LiteralPath $taskPackagePath)) { continue }
            $taskNotices = @(Get-ChildItem -LiteralPath $taskPackagePath -File | Where-Object Name -Match '^(license|notice|third.party)')
            if ($taskNotices.Count) {
                $taskDestination = Join-Path $taskLicenses ($taskPackage -replace '/', '-')
                New-Item -ItemType Directory -Force $taskDestination | Out-Null
                $taskNotices | Copy-Item -Destination $taskDestination
            }
            break
        }
    }
    if (!$SkipRuntimeCheck) {
        & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -Executable (Join-Path $taskStage 'NyKurEdge.App.exe') `
            -ReportPath (Join-Path $taskReleaseRoot 'runtime-validation.json')
    }

    $taskZip = Join-Path $taskReleaseRoot $taskAsset
    Compress-Archive -LiteralPath $taskStage -DestinationPath $taskZip -CompressionLevel Optimal
    $taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $taskAsset" | Set-Content (Join-Path $taskReleaseRoot 'SHA256SUMS.txt') -Encoding ascii
    $taskMetadata = [ordered]@{
        version = $Version
        sourceCommit = $taskSourceCommit
        asset = $taskAsset
        sizeBytes = (Get-Item -LiteralPath $taskZip).Length
        sha256 = $taskHash
        expandedBytes = (Get-ChildItem -LiteralPath $taskStage -Recurse -File | Measure-Object Length -Sum).Sum
        platform = 'Windows 11 x64'
        format = 'Portable ZIP'
        signed = $false
        runtimeSmokeTested = !$SkipRuntimeCheck
        releaseUrl = "https://github.com/NyKurr/NyKurEdge/releases/tag/v$Version"
        downloadUrl = "https://github.com/NyKurr/NyKurEdge/releases/download/v$Version/$taskAsset"
    }
    $taskMetadata | ConvertTo-Json | Set-Content (Join-Path $taskReleaseRoot 'release.json') -Encoding utf8
    $taskMetadata | ConvertTo-Json
}
finally { Pop-Location }
