param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$names = @(Get-ChildItem -LiteralPath $root -Force | Sort-Object Name | ForEach-Object Name)
if (($names -join ',') -ne 'app,GameLocalizer.exe') { throw "Unexpected top-level layout: $($names -join ', ')" }
foreach ($name in @('GameLocalizer.dll','GameLocalizer.ModelHost.exe','GameLocalizer.ModelHost.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','e_sqlite3.dll','onnxruntime.dll','RuntimeCollector/GameLocalizer.RuntimeCollector.dll','Updater/GameLocalizer.Updater.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $root "app/$name") -PathType Leaf)) { throw "Missing internal file: $name" }
}
if (Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -in '.pdb','.lib','.onnx','.spm','.db','.sqlite','.map' -or $_.Name -eq 'createdump.exe' }) { throw 'Development output or user data shipped' }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Native layout validation requires Windows' }
if (!('GameLocalizerLayoutNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GameLocalizerLayoutNative {
  [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
  [DllImport("kernel32")] public static extern bool FreeLibrary(IntPtr module);
}
'@
}
foreach ($name in @('e_sqlite3.dll','onnxruntime.dll')) {
    $handle = [GameLocalizerLayoutNative]::LoadLibraryEx((Join-Path $root "app/$name"), [IntPtr]::Zero, 0x1100)
    if ($handle -eq [IntPtr]::Zero) { throw "Native load failed: $name; Win32=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    [GameLocalizerLayoutNative]::FreeLibrary($handle) | Out-Null
}
Write-Output 'PASS: clean root, ModelHost/updater/collector payload, SQLite and ONNX native loading, no models/databases/development garbage'
