param([ValidateSet('All','Build','InstallOld','Upgrade','Rollback','Update','Uninstall','ReinstallDelete')][string]$Phase = 'All', [string]$Dotnet = 'dotnet', [switch]$InteractiveUpdate, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$qa = Join-Path $workspace 'artifacts/installer-qa'
$identity = '20C638A2-AE40-41E8-8B69-2726DDD48BBB'
$dataName = 'GameLocalizer-InstallerQA'
$install = Join-Path $env:LOCALAPPDATA "Programs/$dataName"
$data = Join-Path $env:LOCALAPPDATA $dataName
$registry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$identity}_is1"
New-Item -ItemType Directory -Path $qa -Force | Out-Null
function RunSetup([string]$Name, [string[]]$Extra = @()) {
    $setup = Join-Path $qa "setup-$Name/GameLocalizer-Setup-x64.exe"
    $log = Join-Path $qa "install-$Name-$([guid]::NewGuid().ToString('N')).log"
    $process = Start-Process -FilePath $setup -ArgumentList (@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="'+$log+'"')) + $Extra) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Installer failed ($Name): $($process.ExitCode), $log" }
}
function AssertVersion([string]$Version) {
    $entry = Get-ItemProperty -LiteralPath $registry
    if ($entry.DisplayVersion -ne $Version -or $entry.InstallLocation.TrimEnd('\') -ne $install) { throw 'Installed Apps metadata mismatch' }
    if ((Get-Item -LiteralPath (Join-Path $install 'GameLocalizer.exe')).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'EXE version mismatch' }
    if ($Version -ne '0.2.3') {
        if (!(Test-Path -LiteralPath (Join-Path $install 'app/unins000.exe'))) { throw 'Internal uninstaller missing' }
        if (Get-ChildItem -LiteralPath $install -File | Where-Object Name -notin 'GameLocalizer.exe','unknown-user-file.txt') { throw 'Legacy owned files remain in installation root' }
    }
}
function CloseQaApp {
    foreach ($process in Get-Process GameLocalizer -ErrorAction SilentlyContinue) {
        if ($process.Path -eq (Join-Path $install 'GameLocalizer.exe')) { $process.Kill(); $process.WaitForExit() }
    }
}
function CheckUserFiles {
    $before = Get-Content -LiteralPath (Join-Path $qa 'preserved.json') -Raw | ConvertFrom-Json
    foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw "User file changed: $($file.Path)" } }
}
function SnapshotUserFiles {
    $files = @(Get-ChildItem -LiteralPath $data -Recurse -File | Where-Object { $_.FullName -notmatch '\\(Updates|scans|logs)\\' })
    $files += Get-Item -LiteralPath (Join-Path $qa 'synthetic-game/GameLocalizer_Backup/original.txt')
    @($files | ForEach-Object { @{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash} }) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $qa 'preserved.json')
}
if ($Phase -in 'All','Build' -and !$SkipBuild) {
    foreach ($item in @(@('old','0.2.3'),@('current','0.3.0'),@('next','0.3.1'))) {
        $name=$item[0]; $version=$item[1]; $publish=Join-Path $qa $name
        $layoutOption = if ($name -eq 'old') { '-p:UseCleanDistributionLayout=false' } else { '-p:UseCleanDistributionLayout=true' }
        & $Dotnet publish $layoutOption (Join-Path $workspace 'src/GameLocalizer.UI') -c Release -r win-x64 --self-contained true "-p:Version=$version" "-p:InstallerAppId=$identity" "-p:UserDataDirectoryName=$dataName" -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'QA publish failed' }
        & (Join-Path $PSScriptRoot 'Package.ps1') -PublishDirectory $publish -Version $version
        if ($name -eq 'old') { [IO.File]::WriteAllText((Join-Path $publish 'obsolete-owned.dll'),'Synthetic obsolete owned DLL') }
        # Each compiler invocation reads metadata from a separate assembly load context.
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -PublishDirectory $publish -OutputDirectory (Join-Path $qa "setup-$name")
    }
    $nextPublish = Join-Path $qa 'next'
    $runtimeFile = Join-Path $nextPublish 'app/GameLocalizer.runtimeconfig.json'
    $originalRuntime = [IO.File]::ReadAllBytes($runtimeFile)
    try {
        [IO.File]::WriteAllText($runtimeFile, 'INVALID RUNTIME CONFIGURATION: intentional rollback fixture')
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -PublishDirectory $nextPublish -OutputDirectory (Join-Path $qa 'setup-broken')
    } finally { [IO.File]::WriteAllBytes($runtimeFile, $originalRuntime) }
    & $Dotnet publish (Join-Path $workspace 'tools/GameLocalizer.InstallerSmoke') -c Release -r win-x64 --self-contained true -p:Version=0.3.0 "-p:InstallerAppId=$identity" "-p:UserDataDirectoryName=$dataName" -o (Join-Path $qa 'driver')
    if ($LASTEXITCODE -ne 0) { throw 'Update-button harness build failed' }
    & $Dotnet publish (Join-Path $workspace 'tools/GameLocalizer.Smoke') -c Release -r win-x64 --self-contained true -o (Join-Path $qa 'offline-driver')
    if ($LASTEXITCODE -ne 0) { throw 'Offline harness build failed' }
}
if ($Phase -in 'All','InstallOld') {
    if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $data)) { throw 'QA profile already exists; preserve or explicitly clean the isolated fixture before starting again' }
    RunSetup old @('/TASKS=desktopicon'); AssertVersion '0.2.3'
    $shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) "$dataName/GameLocalizer.lnk"
    $shell=New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $install 'GameLocalizer.exe')) { throw 'Start Menu shortcut target mismatch' }
    if (!(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) "$dataName/Uninstall GameLocalizer.lnk"))) { throw 'Uninstall shortcut missing' }
    if (!(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$dataName.lnk"))) { throw 'Desktop shortcut missing' }
    Start-Process -FilePath $shortcut -WindowStyle Hidden
    Start-Sleep -Seconds 4
    if (!(Get-Process GameLocalizer -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $install 'GameLocalizer.exe'))) { throw 'Shortcut launch failed' }
    CloseQaApp
    & (Join-Path $qa 'offline-driver/GameLocalizer.Smoke.exe') $data --host (Join-Path $install 'GameLocalizer.ModelHost.exe') > (Join-Path $qa 'offline-initial.log')
    if ($LASTEXITCODE -ne 0) { throw 'Real model / SQLite initialization failed' }
    foreach ($name in @('Settings/preferences.json','Glossary/names.json','jobs/preserved.fixture','settings.json')) {
        $file=Join-Path $data $name; New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
        if (!(Test-Path -LiteralPath $file)) { [IO.File]::WriteAllText($file, $(if ($name -eq 'settings.json') {'{"CheckUpdatesOnStartup":false}'} else {'Synthetic preservation fixture'})) }
    }
    $backup=Join-Path $qa 'synthetic-game/GameLocalizer_Backup/original.txt'; New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null; [IO.File]::WriteAllText($backup,'Unrelated game backup')
    [IO.File]::WriteAllText((Join-Path $install 'unknown-user-file.txt'),'Unknown portable file must survive update')
    SnapshotUserFiles
    Write-Output 'PASS: fresh per-user install, metadata, Start Menu, desktop shortcut and shortcut launch'
}
if ($Phase -in 'All','Upgrade') {
    CloseQaApp; SnapshotUserFiles; RunSetup current; AssertVersion '0.3.0'; CheckUserFiles
    if (Test-Path -LiteralPath (Join-Path $install 'obsolete-owned.dll')) { throw 'Old owned DLL was not cleaned' }
    if (!(Test-Path -LiteralPath (Join-Path $install 'unknown-user-file.txt'))) { throw 'Unknown file was removed' }
    $old=Join-Path $qa 'setup-old/GameLocalizer-Setup-x64.exe'
    $blocked=Start-Process -FilePath $old -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -Wait -PassThru -WindowStyle Hidden
    if ($blocked.ExitCode -eq 0) { throw 'Silent downgrade was allowed' }; AssertVersion '0.3.0'
    Write-Output 'PASS: 0.2.3 -> 0.3.0, model/memory/settings/game backup preserved, old owned DLL removed, downgrade blocked'
}
if ($Phase -in 'All','Rollback') {
    CloseQaApp; SnapshotUserFiles
    $report=Join-Path $qa 'rollback-button.txt'
    $driver=Start-Process -FilePath (Join-Path $qa 'driver/GameLocalizer.InstallerSmoke.exe') -ArgumentList @(('"'+$install+'"'),('"'+(Join-Path $qa 'setup-broken/GameLocalizer-Setup-x64.exe')+'"'),('"'+$report+'"'),'--auto') -PassThru -WindowStyle Hidden
    if (!$driver.WaitForExit(120000) -or $driver.ExitCode -ne 0) { throw 'Rollback update handoff failed' }
    $deadline=[DateTime]::UtcNow.AddMinutes(4)
    do {
        Start-Sleep -Milliseconds 500
        $rolledBack = @(Get-ChildItem -LiteralPath (Join-Path $data 'Updates/InstalledTransactions') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object Phase -eq 'RolledBack')
    } until ($rolledBack.Count -gt 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($rolledBack.Count -ne 1) { throw 'No confirmed rollback' }
    AssertVersion '0.3.0'; CheckUserFiles
    Start-Sleep -Seconds 3
    if (!(Get-Process GameLocalizer -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $install 'GameLocalizer.exe'))) { throw 'Restored application did not restart' }
    CloseQaApp
    Write-Output 'PASS: actual broken installed update -> failed startup -> byte-verified recovery -> registry restored -> previous app restarted'
}
if ($Phase -in 'All','Update') {
    CloseQaApp; SnapshotUserFiles
    $report=Join-Path $qa 'update-button.txt'; $exe=Join-Path $qa 'driver/GameLocalizer.InstallerSmoke.exe'; $next=Join-Path $qa 'setup-next/GameLocalizer-Setup-x64.exe'
    $arguments=@(('"'+$install+'"'),('"'+$next+'"'),('"'+$report+'"')); if (!$InteractiveUpdate) { $arguments+='--auto' }
    $driver=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (!$driver.WaitForExit(120000)) { throw 'Update button harness timeout' }
    if ($driver.ExitCode -ne 0 -or !(Get-Content -LiteralPath $report -Raw).StartsWith('PASS:')) { throw 'Update command failed' }
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        $new=Get-Process GameLocalizer -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $install 'GameLocalizer.exe')
    } until ($new -or [DateTime]::UtcNow -gt $deadline)
    if (!$new) { throw 'New app did not restart' }; AssertVersion '0.3.1'; CheckUserFiles
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $complete = @(Get-ChildItem -LiteralPath (Join-Path $data 'Updates/InstalledTransactions') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object Phase -eq 'Complete')
    } until ($complete.Count -gt 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($complete.Count -eq 0) { throw 'Updated app startup was not confirmed' }
    Write-Output 'PASS: UpdateCommand -> synthetic GitHub response -> Setup download -> SHA256 -> app exits -> Inno updates -> new app starts'
    CloseQaApp
    & (Join-Path $qa 'offline-driver/GameLocalizer.Smoke.exe') $data --host (Join-Path $install 'app/GameLocalizer.ModelHost.exe') --replay > (Join-Path $qa 'offline-replay.log')
    if ($LASTEXITCODE -ne 0) { throw 'Persisted SQLite cache/model replay failed' }
}
if ($Phase -in 'All','Uninstall') {
    CloseQaApp; SnapshotUserFiles
    $uninstall=Join-Path $install 'app/unins000.exe'; $process=Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Uninstall failed' }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath (Join-Path $install 'GameLocalizer.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if ((Test-Path -LiteralPath (Join-Path $install 'GameLocalizer.exe')) -or (Test-Path -LiteralPath $registry)) { throw 'Application remains installed' }
    CheckUserFiles
    if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) "$dataName/GameLocalizer.lnk")) { throw 'Shortcut remains' }
    Write-Output 'PASS: uninstall removes owned application/registration/shortcuts and preserves user data by default'
}
if ($Phase -in 'All','ReinstallDelete') {
    # The uninstaller deliberately preserves unknown files. Remove only our known synthetic fixture.
    $unknown = Join-Path $install 'unknown-user-file.txt'
    if (Test-Path -LiteralPath $unknown) {
        if ([IO.File]::ReadAllText($unknown) -ne 'Unknown portable file must survive update') { throw 'Unexpected fixture content' }
        Remove-Item -LiteralPath $unknown
    }
    RunSetup next; AssertVersion '0.3.1'; CheckUserFiles
    Write-Output 'PASS: reinstall with preserved data'
    $process=Start-Process -FilePath (Join-Path $install 'app/unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/DELETEUSERDATA' -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Explicit data removal uninstall failed' }
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    while ((Test-Path -LiteralPath $data) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if ((Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath $registry)) { throw 'Explicitly selected data removal failed' }
    if ([IO.File]::ReadAllText((Join-Path $qa 'synthetic-game/GameLocalizer_Backup/original.txt')) -ne 'Unrelated game backup') { throw 'External game backup changed' }
    Write-Output 'PASS: explicitly consented data deletion; external game backup unchanged'
}
