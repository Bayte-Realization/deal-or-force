param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../..'),
    [ValidateRange(16,512)][int]$Points = 180
)
$ErrorActionPreference = 'Stop'
$mediaRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$inventory = Get-Content -LiteralPath (Join-Path $mediaRoot 'game/presentation/10_atlas_library.json') -Raw | ConvertFrom-Json
$measurements = [ordered]@{}
foreach ($entry in $inventory.assets.PSObject.Properties) {
    if ($entry.Value.kind -notin @('music','sound')) { continue }
    $mediaFile = Join-Path (Join-Path $mediaRoot 'game') $entry.Value.file
    $durationText = & ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $mediaFile
    if ($LASTEXITCODE -ne 0) { throw ('Cannot measure '+$entry.Name) }
    $seconds = [double]::Parse($durationText, [Globalization.CultureInfo]::InvariantCulture)
    if (![double]::IsFinite($seconds) -or $seconds -le 0) { throw 'Invalid recording duration' }
    $window = [int][Math]::Ceiling($seconds * 8000 / $Points)
    $filter = 'aresample=8000,aformat=channel_layouts=mono,asetnsamples=n='+$window+':p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.Peak_level:file=-'
    $metadata = & ffmpeg -v error -i $mediaFile -af $filter -f null -
    if ($LASTEXITCODE -ne 0) { throw ('Cannot decode '+$entry.Name) }
    $peaks = @($metadata | Where-Object { $_ -like 'lavfi.astats.Overall.Peak_level=*' } | ForEach-Object {
        $db = $_.Substring($_.IndexOf('=')+1)
        if ($db -eq '-inf') { 0.0 } else {
            $level = [double]::Parse($db, [Globalization.CultureInfo]::InvariantCulture)
            if (![double]::IsFinite($level)) { throw 'Non-finite measured peak' }
            [Math]::Round([Math]::Clamp([Math]::Pow(10.0,$level/20.0),0.0,1.0),5)
        }
    })
    if ($peaks.Count -lt 1 -or $peaks.Count -gt $Points+1) { throw 'Unexpected measurement count' }
    $measurements[$entry.Name] = [ordered]@{ duration_seconds=[Math]::Round($seconds,5); peaks=$peaks }
}
# stdout only: callers choose whether and where the derived data is saved.
[ordered]@{ measurement='FFmpeg mono 8kHz, equal-duration peak windows, linear full-scale amplitude'; assets=$measurements } | ConvertTo-Json -Depth 6
