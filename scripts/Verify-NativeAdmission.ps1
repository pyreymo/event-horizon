param(
    [Parameter(Mandatory = $true)]
    [string] $Executable
)

$ErrorActionPreference = 'Stop'
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Executable).Path)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or
    [BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20b) {
    throw 'Expected an x64 PE32+ image.'
}
$sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
$sectionTable = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
$imageBase = [BitConverter]::ToUInt64($bytes, $pe + 48)
$textSection = $null
for ($i = 0; $i -lt $sectionCount; $i++) {
    $section = $sectionTable + 40 * $i
    $name = [Text.Encoding]::ASCII.GetString($bytes, $section, 8).TrimEnd([char]0)
    if ($name -eq '.text') {
        $textSection = @{
            Rva = [BitConverter]::ToUInt32($bytes, $section + 12)
            Size = [BitConverter]::ToUInt32($bytes, $section + 16)
            Offset = [BitConverter]::ToUInt32($bytes, $section + 20)
        }
    }
}
if ($null -eq $textSection) { throw 'Missing .text section.' }
$textBytes = [Text.Encoding]::Latin1.GetString($bytes, $textSection.Offset, $textSection.Size)

function Find-UniquePattern([string] $Pattern) {
    $regex = (($Pattern -split ' ' | ForEach-Object {
        if ($_ -eq '??') { '.' } else { '\x' + $_ }
    }) -join '')
    $matches = [regex]::Matches($textBytes, $regex, [Text.RegularExpressions.RegexOptions]::Singleline)
    if ($matches.Count -ne 1) { throw "Expected one match, found $($matches.Count): $Pattern" }
    return [int]$matches[0].Index
}

function Resolve-Call([int] $Index) {
    if ($bytes[$textSection.Offset + $Index] -ne 0xe8) { throw 'Expected CALL rel32.' }
    return $Index + 5 + [BitConverter]::ToInt32($bytes, $textSection.Offset + $Index + 1)
}

$hookSource = Get-Content -Raw (Join-Path $PSScriptRoot '../EventHorizon/Culling/NativeDrawCandidateHook.cs')
$signature = [regex]::Match($hookSource, 'const string Signature\s*=\s*"([^"]+)"').Groups[1].Value
if (!$signature) { throw 'Hook signature not found in source.' }
$call = Find-UniquePattern $signature
$sort = Resolve-Call $call
$drawLimit = Find-UniquePattern '40 56 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 8B 41'
if ((Resolve-Call ($call + 20)) -ne $drawLimit) { throw 'Sort continuation no longer calls GetDrawLimit.' }

# Verify the decisive native admission and virtual draw dispatch, not just the sort prologue.
$admit = Find-UniquePattern '83 7B 08 0F 7F ?? 48 8B 01 FF 50 60'
$disable = Find-UniquePattern '48 8B 0B 48 8B 01 FF 50 68 48 83 C3 10'
if ($admit -le $call -or $admit -gt $call + 0x100 -or $disable -le $admit -or $disable -gt $admit + 0x100) {
    throw 'Admission/DisableDraw no longer follow the hooked sort.'
}
$displacement = [int]$bytes[$textSection.Offset + $admit + 5]
if ($displacement -ge 128) { $displacement -= 256 }
$jumpTarget = $admit + 6 + $displacement
if ($jumpTarget -ne $disable) { throw 'Priority > 15 no longer branches to DisableDraw.' }

[pscustomobject]@{
    SHA256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    SortCall = ('0x{0:X}' -f ($imageBase + $textSection.Rva + $call))
    Sort = ('0x{0:X}' -f ($imageBase + $textSection.Rva + $sort))
    GetDrawLimit = ('0x{0:X}' -f ($imageBase + $textSection.Rva + $drawLimit))
    Admission = ('0x{0:X}' -f ($imageBase + $textSection.Rva + $admit))
    DisableDraw = ('0x{0:X}' -f ($imageBase + $textSection.Rva + $disable))
}
