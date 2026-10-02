param([string]$Dotnet = 'dotnet', [switch]$Launch)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath "$env:LOCALAPPDATA\GameLocalizerTools\dotnet\dotnet.exe")) {
    $Dotnet = "$env:LOCALAPPDATA\GameLocalizerTools\dotnet\dotnet.exe"
}
$target = [IO.Path]::GetFullPath('E:\ProjectAI\GameLocalizer')
$publish = Join-Path $root ('artifacts\dev\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$backup = 'E:\ProjectAI\GameLocalizer_dev_backup_' + [guid]::NewGuid().ToString('N')
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function Assert-Closed {
    if (Get-Process -Name GameLocalizer,GameLocalizer.ModelHost,GameLocalizer.Updater -ErrorAction SilentlyContinue) {
        throw 'Close GameLocalizer, ModelHost and Updater before updating.'
    }
}
function Close-ApplicationInstances {
    # Close UI copies from every checkout/artifact/install location; let WPF save edits and cancel work.
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (Get-Process -Name GameLocalizer -ErrorAction SilentlyContinue) {
        foreach ($appProcess in @(Get-Process -Name GameLocalizer -ErrorAction SilentlyContinue)) {
            if (!$appProcess.HasExited) { $null = $appProcess.CloseMainWindow() }
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'GameLocalizer did not close gracefully; installation stopped.' }
        Start-Sleep -Milliseconds 500
    }
    foreach ($helper in @(Get-Process -Name GameLocalizer.ModelHost,GameLocalizer.Updater -ErrorAction SilentlyContinue)) {
        # The user explicitly requested closing all application instances; UI parents are now closed.
        Stop-Process -Id $helper.Id -ErrorAction Stop
        $helper.WaitForExit(10000) | Out-Null
    }
    Assert-Closed
}
function Verify-InstalledBinaries {
    $sourceExe = Join-Path $publish 'GameLocalizer.exe'
    $installedExe = Join-Path $target 'GameLocalizer.exe'
    $sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    Write-Output "Source publish path: $publish"
    Write-Output "Installed target: $target"
    Write-Output "Installed EXE: $installedExe"
    Write-Output "Source EXE SHA256: $sourceHash"
    Write-Output "Installed EXE SHA256: $installedHash"
    Write-Output "Hashes match: $(if ($sourceHash -eq $installedHash) { 'YES' } else { 'NO' })"
    Write-Output "Installed EXE last write: $((Get-Item -LiteralPath $installedExe).LastWriteTimeUtc.ToString('O'))"
    if ($sourceHash -ne $installedHash) { throw 'FAIL: installed executable SHA256 mismatch; launch blocked.' }
    $proof = @()
    foreach ($relative in @('GameLocalizer.exe','app\GameLocalizer.dll','app\GameLocalizer.Core.dll','app\GameLocalizer.Infrastructure.dll')) {
        $source = Get-Item -LiteralPath (Owned-Path $publish $relative)
        $installed = Get-Item -LiteralPath (Owned-Path $target $relative)
        $sourceBinaryHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        $installedBinaryHash = (Get-FileHash -LiteralPath $installed.FullName -Algorithm SHA256).Hash
        if ($sourceBinaryHash -ne $installedBinaryHash) { throw "FAIL: installed binary mismatch: $relative; launch blocked." }
        if ($source.LastWriteTimeUtc -ne $installed.LastWriteTimeUtc) { throw "FAIL: installed binary timestamp mismatch: $relative; launch blocked." }
        Write-Output "Verified binary: $relative; SHA256: $installedBinaryHash; last write UTC: $($installed.LastWriteTimeUtc.ToString('O'))"
        $proof += [ordered]@{ Binary=$relative; SourceSHA256=$sourceBinaryHash; InstalledSHA256=$installedBinaryHash; HashesMatch=$true; SourceLastWriteUtc=$source.LastWriteTimeUtc.ToString('O'); InstalledLastWriteUtc=$installed.LastWriteTimeUtc.ToString('O') }
    }
    $script:installationProof = [ordered]@{ SourcePublishPath=$publish; InstalledTarget=$target; InstalledExecutable=$installedExe; Binaries=$proof; RunningExecutablePath=$null; RunningExecutableVerified=$false }
    $script:proofPath = $publish + '-verification.json'
    [IO.File]::WriteAllText($script:proofPath, ($script:installationProof | ConvertTo-Json -Depth 5))
}
function Start-VerifiedInstalledApplication {
    Assert-Closed
    $installedExe = [IO.Path]::GetFullPath('E:\ProjectAI\GameLocalizer\GameLocalizer.exe')
    $launched = Start-Process -FilePath $installedExe -WorkingDirectory $target -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $actualPath = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($launched.HasExited) { throw 'FAIL: installed application exited before runtime path verification.' }
        try { $actualPath = $launched.MainModule.FileName } catch { $actualPath = $null }
        if ($actualPath) { break }
        Start-Sleep -Milliseconds 250
    }
    Write-Output "Running executable path: $actualPath"
    if (!$actualPath -or !([IO.Path]::GetFullPath($actualPath).Equals($installedExe, [StringComparison]::OrdinalIgnoreCase))) { throw 'FAIL: running executable path differs from installed executable.' }
    foreach ($instance in @(Get-Process -Name GameLocalizer -ErrorAction SilentlyContinue)) {
        if (!$instance.MainModule.FileName.Equals($installedExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'FAIL: a bin/artifacts/publish application instance is running.' }
    }
    $script:installationProof.RunningExecutablePath = $actualPath
    $script:installationProof.RunningExecutableVerified = $true
    [IO.File]::WriteAllText($script:proofPath, ($script:installationProof | ConvertTo-Json -Depth 5))
    Write-Output 'Running executable verified: PASS'
}
function Owned-Path([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative -match '(^|[\\/])(\.\.?|Models|Settings|Glossary|Logs|Jobs|Updates|Translation jobs|GameLocalizer_Backup)([\\/]|$)|[:*?]|\.(db|sqlite|sqlite3)(-(wal|shm|journal))?$|\.(onnx|spm|safetensors)$|(^|[\\/])settings[^\\/]*$') {
        throw "Unsafe application manifest entry: $Relative"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if (!$resolved.StartsWith($Base.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside target' }
    $part = $resolved
    while ($part) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked path rejected: $part" }
        $part = Split-Path -Parent $part
    }
    return $resolved
}
Push-Location $root
try {
    Run-Dotnet @('restore','GameLocalizer.sln')
    Run-Dotnet @('build','GameLocalizer.sln','-c','Release')
    Run-Dotnet @('test','GameLocalizer.sln','-c','Release','--no-build','--logger','trx','--results-directory',"${publish}-test-results")
    Run-Dotnet @('publish','src/GameLocalizer.UI','-c','Release','-r','win-x64','--self-contained','true','-o',$publish)
    foreach ($required in @('GameLocalizer.exe','app\GameLocalizer.ModelHost.exe','app\Updater\GameLocalizer.Updater.exe','app\coreclr.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish missing $required" }
    }
    & (Join-Path $PSScriptRoot 'Package.ps1') -PublishDirectory $publish -Version ((Get-Item (Join-Path $publish 'GameLocalizer.exe')).VersionInfo.ProductVersion.Split('+')[0])
    $newFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object { $_.FullName.Substring($publish.Length + 1) })
    $manifestName = 'app\dev-install-files.txt'
    $oldManifest = Join-Path $target $manifestName
    foreach ($candidate in @('dev-install-files.txt','app\install-files.txt','install-files.txt')) { if (!(Test-Path -LiteralPath $oldManifest)) { $oldManifest = Join-Path $target $candidate } }
    if ((Test-Path -LiteralPath $target) -and !(Test-Path -LiteralPath $oldManifest)) { throw 'Existing install requires an application file manifest.' }
    $oldFiles = @()
    if (Test-Path -LiteralPath $oldManifest) {
        $null = Owned-Path $target ($oldManifest.Substring($target.Length + 1))
        $oldFiles = @(Get-Content -LiteralPath $oldManifest | Where-Object { (Split-Path $_ -Leaf) -notin @('install-files.txt','installation.ini','installer-payload.json') })
    }
    if ((Test-Path -LiteralPath $oldManifest) -and (Split-Path $oldManifest -Leaf) -eq 'dev-install-files.txt') { $oldFiles += $oldManifest.Substring($target.Length + 1) }
    $allFiles = @(@($newFiles) + @($oldFiles) + @($manifestName) | Sort-Object -Unique)
    foreach ($relative in $allFiles) { $null = Owned-Path $target $relative }
    Close-ApplicationInstances
    New-Item -ItemType Directory -Path $target,$backup -Force | Out-Null
    $existing = @($allFiles | Where-Object { Test-Path -LiteralPath (Owned-Path $target $_) -PathType Leaf })
    foreach ($relative in $existing) {
        $destination = Owned-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Owned-Path $target $relative) -Destination $destination
    }
    try {
        Assert-Closed
        foreach ($relative in $newFiles) {
            $destination = Owned-Path $target $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath (Owned-Path $publish $relative) -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath (Owned-Path $publish $relative)).Hash) { throw "Copy verification failed: $relative" }
        }
        Verify-InstalledBinaries
        foreach ($relative in $oldFiles) {
            if ($relative -notin $newFiles) {
                $obsolete = Owned-Path $target $relative
                if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
            }
        }
        [IO.File]::WriteAllLines((Join-Path $target $manifestName), [string[]]$newFiles)
        foreach ($relative in $oldFiles) {
            $directory = Split-Path (Owned-Path $target $relative) -Parent
            while ($directory -ne $target -and (Test-Path -LiteralPath $directory -PathType Container)) {
                if (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1) { break }
                Remove-Item -LiteralPath $directory
                $directory = Split-Path $directory -Parent
            }
        }

    } catch {
        foreach ($relative in $allFiles) {
            $destination = Owned-Path $target $relative
            if ($relative -in $existing) {
                New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
                Copy-Item -LiteralPath (Owned-Path $backup $relative) -Destination $destination -Force
            }
            elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination -Force }
        }
        throw
    }
    Write-Output "Published: $publish; installed: $target; backup retained: $backup"
    if ($Launch) { Start-VerifiedInstalledApplication }
} finally { Pop-Location }
