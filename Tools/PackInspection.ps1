# Read the file table out of a Godot 4 .pck, so a build can ask what is actually inside it.
#
# WHY NOT GREP. The same bytes that name `game/map_data/provinces.png` appear as a real archive
# entry, as an `.import` sidecar, and inside every JSON document that names it - so counting
# matches tells you nothing about how many copies shipped. The PCK has a file table; read it.
#
# Godot 4 pack v2+ layout (little-endian), verified against Godot 4.8-dev7 exports:
#   0x00: "GDPC" (4B)
#   0x04: pack format version (int32, 2+)
#   0x08: engine major, minor, patch (int32 x 3)
#   0x14: pack flags (uint32)
#   0x18: file_base offset (uint64)
#   0x20: directory_offset (uint64) -> where the file table starts
#   0x28..0x6F: reserved (16 x int32)
#   At directory_offset:
#     file_count (int32)
#     per file:
#       path_length (int32, 4-byte padded, null-terminated UTF-8, without 'res://')
#       path (path_length bytes)
#       offset (uint64, relative to file_base)
#       size (uint64)
#       md5 (16 bytes)
#       flags (uint32)

function Get-PackEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Filter = '*'
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "No pack file at $Path" }
    $full = [IO.Path]::GetFullPath($Path)
    $stream = [IO.File]::OpenRead($full)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $magic = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if ($magic -ne 'GDPC') { throw "$full is not a Godot pack (magic '$magic')." }
        $formatVersion = $reader.ReadInt32()
        $major = $reader.ReadInt32()
        $minor = $reader.ReadInt32()
        $null = $reader.ReadInt32() # patch
        if ($major -ne 4 -or $formatVersion -lt 2) {
            throw "$full is Godot $major.$minor (pack format $formatVersion); expected Godot 4 pack v2+."
        }
        $null = $reader.ReadUInt32() # pack flags
        $fileBase = $reader.ReadUInt64()
        $dirOffset = $reader.ReadInt64()
        if ($dirOffset -le 0 -or $dirOffset -ge $stream.Length) {
            throw "$full has an out-of-range directory offset ($dirOffset)."
        }

        $null = $stream.Seek($dirOffset, [IO.SeekOrigin]::Begin)
        $count = $reader.ReadInt32()
        if ($count -lt 0 -or $count -gt 1000000) {
            throw "$full has an implausible entry count ($count)."
        }

        $entries = New-Object 'System.Collections.Generic.List[object]' ($count)
        for ($i = 0; $i -lt $count; $i++) {
            $pathLength = $reader.ReadInt32()
            if ($pathLength -le 0 -or $pathLength -gt 4096) {
                throw "Corrupt pack at entry ${i}: path length $pathLength."
            }
            $bytes = $reader.ReadBytes($pathLength)
            $name = ([Text.Encoding]::UTF8.GetString($bytes)).TrimEnd([char]0)
            if ($name.StartsWith('res://', [StringComparison]::OrdinalIgnoreCase)) {
                $name = $name.Substring(6)
            }
            $offset = $reader.ReadUInt64() + $fileBase
            $size = $reader.ReadUInt64()
            $null = $reader.ReadBytes(16) # md5
            $entryFlags = $reader.ReadUInt32()
            $entries.Add([pscustomobject]@{
                Path   = $name
                Offset = [int64]$offset
                Size   = [int64]$size
                Flags  = $entryFlags
                Engine = $name.StartsWith('.godot/', [StringComparison]::OrdinalIgnoreCase)
            })
        }
        return $entries | Where-Object { $_.Path -like $Filter }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PackSize {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Filter = '*'
    )
    $entries = @(Get-PackEntry -Path $Path -Filter $Filter)
    $bytes = ($entries | Measure-Object -Property Size -Sum).Sum
    return [pscustomobject]@{ Count = $entries.Count; Bytes = [int64]$bytes; Entries = $entries }
}
