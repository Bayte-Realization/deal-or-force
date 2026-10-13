param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Output,
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'
$taskSource = (Resolve-Path -LiteralPath $Source).Path
$taskOutput = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $taskOutput) -and !$Replace) { throw 'Output exists; choose a new path or explicitly use -Replace.' }
$taskScratch = Join-Path ([IO.Path]::GetTempPath()) ('dof-audio-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskScratch | Out-Null
$taskStem = Join-Path $taskScratch 'shepard.wav'
& dotnet run --project (Join-Path $PSScriptRoot 'AudioCompiler.csproj') -- $taskStem 58.125
if ($LASTEXITCODE -ne 0) { throw 'Shepard stem generation failed.' }
$taskFilter = '[0:a]atrim=start=1.875:end=58.125,asetpts=PTS-STARTPTS[body];[0:a]atrim=start=58.125:end=60,asetpts=PTS-STARTPTS[tail];[0:a]atrim=start=0:end=1.875,asetpts=PTS-STARTPTS[head];[tail][head]acrossfade=d=1.875:c1=qsin:c2=qsin[seam];[seam][body]concat=n=2:v=0:a=1[bed];[1:a]volume=0.35[rise];[bed][rise]amix=inputs=2:normalize=0,loudnorm=I=-20:TP=-2:LRA=9,aresample=48000,atrim=end_sample=2790000[out]'
& ffmpeg -hide_banner -loglevel error -y -i $taskSource -i $taskStem -filter_complex $taskFilter -map '[out]' -ar 48000 -ac 2 -c:a libvorbis -q:a 5 $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'World loop encoding failed.' }
Write-Output ('Loop written: ' + $taskOutput + ' · 58.125s · stereo/48kHz · source and stem retained in ' + $taskScratch)
