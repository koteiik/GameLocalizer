param([Parameter(Mandatory)][string]$Setup, [string]$Version = '0.3.0')
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'GameLocalizer'
$install = Join-Path $env:LOCALAPPDATA 'Programs/GameLocalizer'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C341D9A2-48B9-4B79-AD30-2674B96BD760}_is1'
if ((Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $registration)) { throw 'This test requires a clean disposable Windows profile' }
$setupPath = (Resolve-Path -LiteralPath $Setup).Path
$process = Start-Process -FilePath $setupPath -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART' -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Production installation failed' }
if ((Get-ItemProperty -LiteralPath $registration).DisplayVersion -ne $Version) { throw 'Production registration version mismatch' }
$manifest = Get-Content -LiteralPath (Join-Path $install 'app/installer-payload.json') -Raw | ConvertFrom-Json
if ($manifest.Version -ne $Version) { throw 'Production payload version mismatch' }
foreach ($file in $manifest.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $install $file.Path))
    if (!$target.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path' }
    if ((Get-FileHash -LiteralPath $target).Hash -ne $file.Sha256) { throw "Installed hash mismatch: $($file.Path)" }
}
$app = Start-Process -FilePath (Join-Path $install 'GameLocalizer.exe') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 5
if ($app.HasExited) { throw 'Production app exited during startup' }
if ($app.MainWindowHandle -eq 0) { $app.Refresh(); if ($app.MainWindowHandle -eq 0) { throw 'Production WPF window did not open' } }
$app.Kill(); $app.WaitForExit()
$uninstall = Start-Process -FilePath (Join-Path $install 'app/unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait -WindowStyle Hidden
if ($uninstall.ExitCode -ne 0) { throw 'Production uninstall failed' }
Start-Sleep -Seconds 2
if ((Test-Path -LiteralPath (Join-Path $install 'GameLocalizer.exe')) -or (Test-Path -LiteralPath $registration)) { throw 'Production installation remains' }
if (!(Test-Path -LiteralPath $data)) { throw 'Production uninstall did not preserve profile' }
Write-Output 'PASS: exact production Setup install, payload SHA256, WPF launch, uninstall and user-data preservation on clean VM'
