param([Parameter(Mandatory)][string]$PublishDirectory, [string]$OutputDirectory = 'artifacts/installer', [string]$Compiler)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$inputDirectory = Join-Path $output 'input'
New-Item -ItemType Directory -Path $inputDirectory -Force | Out-Null
$version = (Get-Item -LiteralPath (Join-Path $publish 'GameLocalizer.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected stable app version' }
$core = [Reflection.Assembly]::LoadFile((Join-Path $publish 'GameLocalizer.Core.dll'))
$metadata = @{}
$core.GetCustomAttributesData() | Where-Object {$_.AttributeType.Name -eq 'AssemblyMetadataAttribute'} | ForEach-Object { $metadata[$_.ConstructorArguments[0].Value] = $_.ConstructorArguments[1].Value }
$identity = $metadata['InstallerAppId']; $dataName = $metadata['UserDataDirectoryName']
if ($identity -notmatch '^[a-fA-F0-9-]{36}$' -or $dataName -notmatch '^GameLocalizer(?:-[A-Za-z0-9-]+)?$') { throw 'Invalid build identity' }
foreach ($required in @('GameLocalizer.exe','GameLocalizer.ModelHost.exe','coreclr.dll','README.md','LICENSE')) { if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Missing $required; run Package.ps1 first" } }
$paths = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object { $_.FullName.Substring($publish.Length+1) } | Where-Object {$_ -ne 'update-manifest.json'} | Sort-Object)
if ($paths | Where-Object { $_ -match '(?i)(^|\\)(Models|Settings|Glossary|logs|jobs|GameLocalizer_Backup)(\\|$)|\.(onnx|spm|db)$|(^|\\)settings.json$' }) { throw 'User data in installer payload' }
[IO.File]::WriteAllLines((Join-Path $inputDirectory 'install-files.txt'), @($paths + @('install-files.txt','installation.ini','installer-payload.json')), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $inputDirectory 'installation.ini'), "[Installation]`r`nAppId=$identity`r`nVersion=$version`r`n", [Text.UTF8Encoding]::new($false))
$files = @($paths | ForEach-Object {
    $file = Get-Item -LiteralPath (Join-Path $publish $_)
    [ordered]@{Path=$_.Replace('\','/'); Size=$file.Length; Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
})
foreach ($name in @('install-files.txt','installation.ini')) {
    $file = Get-Item -LiteralPath (Join-Path $inputDirectory $name)
    $files += [ordered]@{Path=$name;Size=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$manifest = [ordered]@{Version=$version; AppId=$identity; Files=@($files | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $inputDirectory 'installer-payload.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
if (!$Compiler) { $Compiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
& $Compiler "/DAppVersion=$version" "/DAppIdentity=$identity" "/DDataDirectoryName=$dataName" "/DDisplayName=$dataName" "/DPublishDir=$publish" "/DInputDir=$inputDirectory" "/DOutputDir=$output" (Join-Path $PSScriptRoot '../installer/GameLocalizer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed: $LASTEXITCODE" }
$setup = Join-Path $output 'GameLocalizer-Setup.exe'
if (!(Test-Path -LiteralPath $setup)) { throw 'Setup EXE not produced' }
$releaseManifest = [ordered]@{Version=$version; AppId=$identity; SetupSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash; PayloadManifestSha256=(Get-FileHash -LiteralPath (Join-Path $inputDirectory 'installer-payload.json')).Hash; Files=$manifest.Files; Signed=(Get-AuthenticodeSignature -LiteralPath $setup).Status.ToString()}
[IO.File]::WriteAllText((Join-Path $output 'installer-manifest.json'), ($releaseManifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output $setup
