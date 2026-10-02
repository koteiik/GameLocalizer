param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$manifestPath = Join-Path $root 'installer-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$setup = Join-Path $root 'GameLocalizer-Setup-x64.exe'
$manifest.SetupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$manifest.Signed = (Get-AuthenticodeSignature -LiteralPath $setup).Status.ToString()
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$lines = @('GameLocalizer-Setup-x64.exe','GameLocalizer-Portable-x64.zip' | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $_"
})
[IO.File]::WriteAllLines((Join-Path $root 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
