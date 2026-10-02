param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
function Is-Managed([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C; $pe = $reader.ReadInt32()
        if ($pe -lt 0 -or $pe + 256 -gt $stream.Length) { return $false }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { return $false }
        $stream.Position = $pe + 24; $magic = $reader.ReadUInt16()
        $offset = if ($magic -eq 0x20B) { 112 } elseif ($magic -eq 0x10B) { 96 } else { return $false }
        $stream.Position = $pe + 24 + $offset + 14 * 8
        return $reader.ReadUInt32() -ne 0
    } finally { $reader.Dispose(); $stream.Dispose() }
}
$records = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\','/')
    $binary = if ($_.Extension -in '.dll','.exe') { if (Is-Managed $_.FullName) { 'Managed' } else { 'Native' } } else { 'Other' }
    $category = if ($relative -eq 'GameLocalizer.exe') { 'Main executable' }
        elseif ($_.Name -match 'GameLocalizer.ModelHost') { 'ModelHost' }
        elseif ($relative -match 'RuntimeCollector') { 'RuntimeCollector' }
        elseif ($relative -match 'Updater/') { 'Updater' }
        elseif ($_.Name -match 'sqlite') { 'SQLite' }
        elseif ($_.Name -match 'DirectML') { 'DirectML' }
        elseif ($_.Name -match 'onnxruntime') { 'ONNX Runtime / DirectML provider' }
        elseif ($_.Name -match '\.(deps|runtimeconfig)\.json$') { 'Runtime configuration' }
        elseif ($_.Name -match '^(install|dev-install)-files|^installation.ini$|^installer-payload|^unins') { 'Installer metadata' }
        elseif ($_.Extension -in '.pdb','.lib' -or $_.Name -eq 'createdump.exe') { 'Development / diagnostic output' }
        elseif ($_.Extension -eq '.dll') { "$binary DLL" } else { 'Documentation / other' }
    if ($_.Extension -in '.pdb','.lib' -or $_.Name -eq 'createdump.exe') { $category = 'Development / diagnostic output' }
    $decision = if ($category -eq 'Development / diagnostic output') { 'REMOVE_FROM_DISTRIBUTION' }
        elseif ($_.Name -eq 'dev-install-files.txt') { 'DEV_ONLY' }
        elseif ($relative -eq 'GameLocalizer.exe') { 'REQUIRED' } else { 'CAN_MOVE_TO_SUBFOLDER' }
    [pscustomobject]@{ Path=$relative; Bytes=$_.Length; Category=$category; Binary=$binary; Disposition=$decision; CanEmbed=($binary -eq 'Managed'); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$records | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$records | Group-Object Category | Select-Object Name,Count
