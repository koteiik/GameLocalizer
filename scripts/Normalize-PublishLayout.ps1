param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$app = Join-Path $root 'app'
if (!(Test-Path -LiteralPath (Join-Path $root 'GameLocalizer.exe')) -or !(Test-Path -LiteralPath (Join-Path $app 'GameLocalizer.dll'))) { throw 'Clean publish layout missing' }
# Remove only known build/development output inside the publish directory.
foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Extension -in '.pdb','.lib' -or $_.Name -eq 'createdump.exe' }) {
    if (!$file.FullName.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe publish path' }
    Remove-Item -LiteralPath $file.FullName -Force
}
foreach ($directory in Get-ChildItem -LiteralPath $root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending) {
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked publish directory' }
    if (!(Get-ChildItem -LiteralPath $directory.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $directory.FullName }
}
