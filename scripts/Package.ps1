param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$Version, [string]$ZipPath)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Expected stable semantic version' }
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($file in @('GameLocalizer.exe','GameLocalizer.ModelHost.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','Updater/GameLocalizer.Updater.exe','Updater/coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $file))) { throw "Required file missing: $file" }
}
if (Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Extension -in '.onnx','.spm') { throw 'Model weights must not be packaged' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md'),(Join-Path $PSScriptRoot '../LICENSE'),(Join-Path $PSScriptRoot '../THIRD_PARTY_NOTICES.md') -Destination $root
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Name -ne 'update-manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($root.Length + 1).Replace('\','/'); Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{Version=$Version; Files=$files} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $root 'update-manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
if ($ZipPath) { Compress-Archive -Path (Join-Path $root '*') -DestinationPath $ZipPath -Force }
