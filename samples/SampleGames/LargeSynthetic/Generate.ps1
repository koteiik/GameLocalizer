param(
    [ValidateRange(50001, 1000000)][int]$Count = 120005,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/large-synthetic')
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)) {
    throw "Choose an empty output directory: $target"
}
$localization = Join-Path $target 'Localization'
New-Item -ItemType Directory -Force $localization, (Join-Path $target 'Demo_Data/StreamingAssets') | Out-Null
for ($part = 0; $part * 20000 -lt $Count; $part++) {
    $path = Join-Path $localization ('dialogue-{0:d2}.csv' -f $part)
    $writer = [IO.StreamWriter]::new($path, $false, [Text.UTF8Encoding]::new($false))
    try {
        $writer.WriteLine('id,text')
        for ($row = $part * 20000; $row -lt [Math]::Min($Count, ($part + 1) * 20000); $row++) {
            $writer.WriteLine("line$row,Welcome to the village number $row")
        }
    } finally { $writer.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $target 'output_log.txt'), "GfxDevice: creating device client`nDirect3D`nInitialized input`n")
[IO.File]::WriteAllText((Join-Path $target 'Player.log'), "Start Game`nRenderer`nVRAM`n")
[IO.File]::WriteAllText((Join-Path $target 'menu.ini'), "enabled=true`nother=false`nempty=null`naccept=yes`nreject=no`n")
Write-Output "Created $Count synthetic localization rows in $target"
