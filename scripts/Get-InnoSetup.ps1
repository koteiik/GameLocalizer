param([string]$ToolsDirectory = (Join-Path $env:LOCALAPPDATA 'GameLocalizerTools'))
$ErrorActionPreference = 'Stop'
$directory = Join-Path $ToolsDirectory 'InnoSetup-6.7.3'
$compiler = Join-Path $directory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { return $compiler }
New-Item -ItemType Directory -Path $ToolsDirectory -Force | Out-Null
$download = Join-Path $ToolsDirectory 'innosetup-6.7.3.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup SHA256 mismatch' }
$process = Start-Process -FilePath $download -ArgumentList @('/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR="'+$directory+'"')) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw "Inno Setup installation failed: $($process.ExitCode)" }
return $compiler
