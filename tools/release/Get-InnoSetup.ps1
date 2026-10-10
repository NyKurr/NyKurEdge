#requires -Version 7.0

param(
    [string]$ToolchainDirectory = ''
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$ToolchainDirectory) { $ToolchainDirectory = Join-Path $taskRoot 'artifacts/toolchain' }
$taskToolchain = [IO.Path]::GetFullPath($ToolchainDirectory)
$taskOfficialUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$taskExpectedDownloadHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$taskExpectedCompilerHash = '0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7'
$taskDownload = Join-Path $taskToolchain 'innosetup-6.7.3.exe'
$taskInstall = Join-Path $taskToolchain 'InnoSetup6.7.3'
$taskCompiler = Join-Path $taskInstall 'ISCC.exe'
New-Item -ItemType Directory -Force $taskToolchain | Out-Null
if (!(Test-Path -LiteralPath $taskDownload)) {
    Invoke-WebRequest -Uri $taskOfficialUrl -OutFile $taskDownload
}
if ((Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpectedDownloadHash) {
    throw 'Inno Setup download does not match the pinned official release SHA-256. Nothing has been executed.'
}
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskDownload
if ($taskSignature.Status -ne 'Valid') {
    throw "The pinned Inno Setup installer has no valid trusted Authenticode signature: $($taskSignature.Status). Nothing has been executed."
}
if (!(Test-Path -LiteralPath $taskCompiler)) {
    # Inno's own /PORTABLE=1 mode leaves installed-program registration untouched.
    $taskLog = Join-Path $taskToolchain 'inno-install.log'
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        '/PORTABLE=1', '/TASKS=', "/DIR=`"$taskInstall`"", "/LOG=`"$taskLog`"")
    $taskProcess = Start-Process -FilePath $taskDownload -ArgumentList $taskArguments -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw "Inno Setup tool installation failed: $($taskProcess.ExitCode)" }
}
if (!(Test-Path -LiteralPath $taskCompiler) -or
    (Get-FileHash -LiteralPath $taskCompiler -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpectedCompilerHash) {
    throw 'Installed ISCC.exe does not match the pinned Inno Setup 6.7.3 compiler SHA-256.'
}
Write-Output $taskCompiler
