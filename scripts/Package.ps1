param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$Version, [string]$ZipPath)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Expected stable semantic version' }
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$internal = if (Test-Path -LiteralPath (Join-Path $root 'app/GameLocalizer.dll')) { 'app' } else { '.' }
foreach ($file in @('GameLocalizer.exe','GameLocalizer.ModelHost.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','Updater/GameLocalizer.Updater.exe','Updater/coreclr.dll')) {
    $relative = if ($file -eq 'GameLocalizer.exe') { $file } else { Join-Path $internal $file }
    if (!(Test-Path -LiteralPath (Join-Path $root $relative))) { throw "Required file missing: $relative" }
}
if (Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -in '.onnx','.spm','.safetensors','.db','.sqlite','.sqlite3' -or $_.Name -eq 'dev-install-files.txt' }) { throw 'Models, user data and local manifests must not be packaged' }
if ($internal -eq 'app') { & (Join-Path $PSScriptRoot 'Test-DistributionLayout.ps1') -Directory $root }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md'),(Join-Path $PSScriptRoot '../LICENSE'),(Join-Path $PSScriptRoot '../docs/project/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $root $internal)
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Name -ne 'update-manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($root.Length + 1).Replace('\','/'); Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{Version=$Version; Files=$files} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path (Join-Path $root $internal) 'update-manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
if ($ZipPath) { Compress-Archive -Path (Join-Path $root '*') -DestinationPath $ZipPath -Force }
