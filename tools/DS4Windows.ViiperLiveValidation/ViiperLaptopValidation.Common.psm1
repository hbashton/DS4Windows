Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('DS4Windows.ViiperHarness.StrictJson' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DS4Windows.ViiperHarness
{
    public static class StrictJson
    {
        public static void Validate(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024)
                throw new FormatException("Evidence JSON exceeds 2 MiB.");
            new Parser(json).ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string text;
            private int index;
            private int values;

            internal Parser(string text) { this.text = text; }

            internal void ParseDocument()
            {
                SkipWhitespace();
                ParseValue(0);
                SkipWhitespace();
                if (index != text.Length)
                    throw Error("Trailing JSON content");
            }

            private void ParseValue(int depth)
            {
                if (depth > 64) throw Error("JSON nesting exceeds 64");
                if (++values > 100000) throw Error("JSON value count exceeds 100000");
                SkipWhitespace();
                if (index >= text.Length) throw Error("Missing JSON value");
                char value = text[index];
                if (value == '{') { ParseObject(depth + 1); return; }
                if (value == '[') { ParseArray(depth + 1); return; }
                if (value == '"') { ParseString(); return; }
                if (value == 't') { ParseLiteral("true"); return; }
                if (value == 'f') { ParseLiteral("false"); return; }
                if (value == 'n') { ParseLiteral("null"); return; }
                if (value == '-' || IsDigit(value)) { ParseNumber(); return; }
                throw Error("Invalid JSON value");
            }

            private void ParseObject(int depth)
            {
                Expect('{');
                SkipWhitespace();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Take('}')) return;
                for (;;)
                {
                    SkipWhitespace();
                    if (index >= text.Length || text[index] != '"')
                        throw Error("Object property name must be a string");
                    string name = ParseString();
                    if (!names.Add(name))
                        throw Error("Duplicate JSON property: " + name);
                    SkipWhitespace();
                    Expect(':');
                    ParseValue(depth);
                    SkipWhitespace();
                    if (Take('}')) return;
                    Expect(',');
                }
            }

            private void ParseArray(int depth)
            {
                Expect('[');
                SkipWhitespace();
                if (Take(']')) return;
                for (;;)
                {
                    ParseValue(depth);
                    SkipWhitespace();
                    if (Take(']')) return;
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                var result = new StringBuilder();
                while (index < text.Length)
                {
                    char current = text[index++];
                    if (current == '"') return result.ToString();
                    if (current < 0x20) throw Error("Control character in JSON string");
                    if (current != '\\') { result.Append(current); continue; }
                    if (index >= text.Length) throw Error("Incomplete JSON escape");
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u': result.Append(ParseUnicodeEscape()); break;
                        default: throw Error("Invalid JSON escape");
                    }
                }
                throw Error("Unterminated JSON string");
            }

            private char ParseUnicodeEscape()
            {
                if (index > text.Length - 4) throw Error("Incomplete Unicode escape");
                int value = 0;
                for (int count = 0; count < 4; count++)
                {
                    char digit = text[index++];
                    value <<= 4;
                    if (digit >= '0' && digit <= '9') value += digit - '0';
                    else if (digit >= 'a' && digit <= 'f') value += digit - 'a' + 10;
                    else if (digit >= 'A' && digit <= 'F') value += digit - 'A' + 10;
                    else throw Error("Invalid Unicode escape");
                }
                return (char)value;
            }

            private void ParseNumber()
            {
                Take('-');
                if (Take('0'))
                {
                    if (index < text.Length && IsDigit(text[index]))
                        throw Error("JSON number has a leading zero");
                }
                else
                {
                    if (index >= text.Length || text[index] < '1' || text[index] > '9')
                        throw Error("Invalid JSON number");
                    while (index < text.Length && IsDigit(text[index])) index++;
                }
                if (Take('.'))
                {
                    if (index >= text.Length || !IsDigit(text[index]))
                        throw Error("Invalid JSON fraction");
                    while (index < text.Length && IsDigit(text[index])) index++;
                }
                if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
                {
                    index++;
                    if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                    if (index >= text.Length || !IsDigit(text[index]))
                        throw Error("Invalid JSON exponent");
                    while (index < text.Length && IsDigit(text[index])) index++;
                }
            }

            private void ParseLiteral(string literal)
            {
                if (index > text.Length - literal.Length ||
                    string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    throw Error("Invalid JSON literal");
                index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (index < text.Length)
                {
                    char current = text[index];
                    if (current != ' ' && current != '\t' && current != '\r' && current != '\n') return;
                    index++;
                }
            }

            private bool Take(char expected)
            {
                if (index < text.Length && text[index] == expected) { index++; return true; }
                return false;
            }

            private void Expect(char expected)
            {
                if (!Take(expected)) throw Error("Expected '" + expected + "'");
            }

            private static bool IsDigit(char value) { return value >= '0' && value <= '9'; }
            private FormatException Error(string message)
            {
                return new FormatException(string.Format(CultureInfo.InvariantCulture,
                    "{0} at character {1}.", message, index));
            }
        }
    }
}
'@
}

function Get-ViiperStreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream]$Stream)

    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Stream)
    }
    finally {
        $sha.Dispose()
        $Stream.Position = 0
    }
    return (-join ($hash | ForEach-Object { $_.ToString('x2') }))
}

function Assert-ViiperNoReparsePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not ($item -is [IO.FileInfo])) {
        throw "The $Role is not a regular file: '$Path'."
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The $Role is reparse-backed: '$($item.FullName)'."
    }
    $cursor = $item.Directory
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The $Role traverses reparse-backed path '$($cursor.FullName)'."
        }
        $cursor = $cursor.Parent
    }
}

function New-ViiperLockedFileBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $full = [IO.Path]::GetFullPath($Path)
    Assert-ViiperNoReparsePath -Path $full -Role $Role
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha256 = Get-ViiperStreamSha256 -Stream $stream
        return [pscustomobject]@{
            Role = $Role
            Path = $item.FullName
            Length = [long]$stream.Length
            Sha256 = $sha256
            Stream = $stream
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Close-ViiperLockedFileBinding {
    param([AllowNull()]$Binding)
    if ($null -ne $Binding -and $null -ne $Binding.Stream) {
        $Binding.Stream.Dispose()
    }
}

function Assert-ViiperLockedFileBinding {
    param([Parameter(Mandatory = $true)]$Binding)
    if ([long]$Binding.Stream.Length -ne [long]$Binding.Length -or
        (Get-ViiperStreamSha256 -Stream $Binding.Stream) -cne
            [string]$Binding.Sha256) {
        throw "Locked $($Binding.Role) bytes changed during validation."
    }
}

function Read-ViiperStrictEvidence {
    param([Parameter(Mandatory = $true)]$Binding)

    if ([long]$Binding.Length -le 0 -or
        [long]$Binding.Length -gt 2MB) {
        throw 'Evidence JSON must contain 1 through 2097152 bytes.'
    }
    $bytes = New-Object byte[] ([int]$Binding.Length)
    $Binding.Stream.Position = 0
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $read = $Binding.Stream.Read($bytes, $offset, $bytes.Length - $offset)
        if ($read -le 0) { throw 'Evidence JSON ended before its bound length.' }
        $offset += $read
    }
    if ($Binding.Stream.ReadByte() -ne -1) {
        throw 'Evidence JSON grew after its exact length was bound.'
    }
    $Binding.Stream.Position = 0
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $raw = $utf8.GetString($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    [DS4Windows.ViiperHarness.StrictJson]::Validate($raw)
    try {
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        throw "Evidence JSON could not be materialized: $($_.Exception.Message)"
    }
}

function New-ViiperStdoutEvidenceReceipt {
    param([Parameter(Mandatory = $true)][object[]]$Lines)

    if ($Lines.Count -ne 1 -or -not ($Lines[0] -is [string]) -or
        [string]::IsNullOrWhiteSpace([string]$Lines[0]) -or
        [string]$Lines[0] -match '[\r\n]') {
        throw 'Runner stdout must contain exactly one bounded JSON receipt line.'
    }
    $line = [string]$Lines[0]
    [DS4Windows.ViiperHarness.StrictJson]::Validate($line)
    try {
        $evidence = $line | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Runner stdout receipt could not be materialized: $($_.Exception.Message)"
    }
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($line + "`n")
    if ($bytes.Length -le 1 -or $bytes.Length -gt 2MB) {
        [Array]::Clear($bytes, 0, $bytes.Length)
        throw 'Runner stdout receipt exceeds the finalized evidence byte bound.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $sha256 = -join ($sha.ComputeHash($bytes) |
            ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    return [pscustomobject]@{
        Length = [long]([Text.UTF8Encoding]::new(
            $false, $true).GetByteCount($line + "`n"))
        Sha256 = $sha256
        Evidence = $evidence
    }
}

function Assert-ViiperStdoutEvidenceContinuity {
    param(
        [Parameter(Mandatory = $true)]$Receipt,
        [Parameter(Mandatory = $true)]$EvidenceBinding
    )
    if ([long]$Receipt.Length -ne [long]$EvidenceBinding.Length -or
        [string]$Receipt.Sha256 -cne [string]$EvidenceBinding.Sha256) {
        throw 'Locked evidence is not byte-identical to the exact child stdout receipt.'
    }
}

function Get-ViiperRequiredProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $properties = @($Object.PSObject.Properties | Where-Object {
        $_.Name -ceq $Name
    })
    if ($properties.Count -ne 1) {
        throw "Evidence omitted exact property '$Name'."
    }
    return $properties[0].Value
}

function Assert-ViiperExactString {
    param($Object, [string]$Name, [string]$Expected,
        [switch]$WindowsPath)
    $actual = Get-ViiperRequiredProperty -Object $Object -Name $Name
    if (-not ($actual -is [string])) {
        throw "Evidence property '$Name' is not a string."
    }
    $comparison = if ($WindowsPath) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    if (-not [string]::Equals([string]$actual, $Expected, $comparison)) {
        throw "Evidence property '$Name' does not match its exact binding."
    }
}

function Assert-ViiperExactBoolean {
    param($Object, [string]$Name, [bool]$Expected)
    $actual = Get-ViiperRequiredProperty -Object $Object -Name $Name
    if (-not ($actual -is [bool]) -or [bool]$actual -ne $Expected) {
        throw "Evidence property '$Name' is not exact Boolean '$Expected'."
    }
}

function Assert-ViiperExactInteger {
    param($Object, [string]$Name, [long]$Expected)
    $actual = Get-ViiperRequiredProperty -Object $Object -Name $Name
    if (-not ($actual -is [ValueType]) -or [long]$actual -ne $Expected -or
        [double]$actual -ne [long]$actual) {
        throw "Evidence property '$Name' is not exact integer '$Expected'."
    }
}

function Assert-ViiperEvidenceFileBinding {
    param(
        [Parameter(Mandatory = $true)]$EvidenceBinding,
        [Parameter(Mandatory = $true)]$ExpectedBinding
    )
    Assert-ViiperExactString $EvidenceBinding 'path' $ExpectedBinding.Path -WindowsPath
    Assert-ViiperExactInteger $EvidenceBinding 'length' $ExpectedBinding.Length
    Assert-ViiperExactString $EvidenceBinding 'sha256' $ExpectedBinding.Sha256
    Assert-ViiperExactBoolean $EvidenceBinding 'exactMatch' $true
}

function Assert-ViiperLiveEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$ExpectedNonceSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedOutputPath,
        [Parameter(Mandatory = $true)]$RunnerBinding,
        [Parameter(Mandatory = $true)]$MetadataBinding,
        [Parameter(Mandatory = $true)][DateTimeOffset]$LaunchWindowStartUtc,
        [Parameter(Mandatory = $true)][DateTimeOffset]$LaunchWindowEndUtc
    )

    Assert-ViiperExactInteger $Evidence 'schemaVersion' 2
    Assert-ViiperExactString $Evidence 'tool' 'DS4Windows.ViiperLiveValidation'
    Assert-ViiperExactString $Evidence 'status' 'pass'
    Assert-ViiperExactBoolean $Evidence 'finalized' $true
    Assert-ViiperExactString $Evidence 'consentNonceSha256' $ExpectedNonceSha256

    $canonicalOutput = [IO.Path]::GetFullPath(
        [string](Get-ViiperRequiredProperty $Evidence 'outputPath'))
    Assert-ViiperExactString $Evidence 'outputPath' $canonicalOutput
    if (-not [string]::Equals($canonicalOutput, $ExpectedOutputPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence outputPath is not the exact fresh reserved output.'
    }

    $startedText = Get-ViiperRequiredProperty $Evidence 'startedUtc'
    $endedText = Get-ViiperRequiredProperty $Evidence 'endedUtc'
    $started = [DateTimeOffset]::MinValue
    $ended = [DateTimeOffset]::MinValue
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $style = [Globalization.DateTimeStyles]::RoundtripKind
    if (-not ($startedText -is [string]) -or
        -not [DateTimeOffset]::TryParseExact($startedText, 'O', $culture,
            $style, [ref]$started) -or
        -not ($endedText -is [string]) -or
        -not [DateTimeOffset]::TryParseExact($endedText, 'O', $culture,
            $style, [ref]$ended) -or $ended -lt $started -or
        $started -lt $LaunchWindowStartUtc.AddSeconds(-5) -or
        $started -gt $LaunchWindowEndUtc.AddSeconds(5) -or
        $ended -gt $LaunchWindowEndUtc.AddSeconds(5)) {
        throw 'Evidence timestamps are malformed, inconsistent, or stale.'
    }

    $failures = @(Get-ViiperRequiredProperty $Evidence 'failures')
    if ($failures.Count -ne 0) { throw 'Passing evidence contains failures.' }
    $controllers = @(Get-ViiperRequiredProperty $Evidence 'controllers')
    if ($controllers.Count -ne 3 -or @($controllers | Where-Object {
            [string](Get-ViiperRequiredProperty $_ 'status') -cne 'pass'
        }).Count -ne 0) {
        throw 'Evidence does not contain three passing production controller validations.'
    }

    $bindings = Get-ViiperRequiredProperty $Evidence 'bindings'
    Assert-ViiperEvidenceFileBinding `
        (Get-ViiperRequiredProperty $bindings 'runnerExecutable') $RunnerBinding
    Assert-ViiperEvidenceFileBinding `
        (Get-ViiperRequiredProperty $bindings 'metadata') $MetadataBinding

    foreach ($artifact in @(Get-ViiperRequiredProperty $bindings 'packageArtifacts')) {
        Assert-ViiperExactBoolean $artifact 'exactMatch' $true
    }
    foreach ($name in @('inputProbeExecution', 'mediaProbeExecution')) {
        $probe = Get-ViiperRequiredProperty $bindings $name
        Assert-ViiperExactBoolean $probe 'allLaunchesExact' $true
        $launches = Get-ViiperRequiredProperty $probe 'launchCount'
        if (-not ($launches -is [ValueType]) -or [long]$launches -le 0 -or
            [double]$launches -ne [long]$launches) {
            throw "Evidence $name has no exact launched process."
        }
    }

    $installed = Get-ViiperRequiredProperty $bindings 'installedRuntime'
    Assert-ViiperExactBoolean $installed 'exactPackageMatch' $true
    $broker = Get-ViiperRequiredProperty $installed 'broker'
    Assert-ViiperExactBoolean $broker 'exactPackageMatch' $true
    Assert-ViiperExactBoolean $broker 'configuredImageIsRunningImage' $true
    Assert-ViiperExactString $broker 'state' 'running'
    $driver = Get-ViiperRequiredProperty $installed 'driver'
    Assert-ViiperExactBoolean $driver 'exactPackageMatch' $true
    Assert-ViiperExactBoolean $driver 'started' $true
    Assert-ViiperExactString $driver 'serviceState' 'running'
    Assert-ViiperExactInteger $driver 'problemCode' 0
    foreach ($fileName in @('runningImage')) {
        Assert-ViiperExactBoolean `
            (Get-ViiperRequiredProperty $broker $fileName) 'exactMatch' $true
    }
    foreach ($fileName in @('publishedInf', 'driverStoreInf',
            'driverStoreCat', 'driverStoreSys', 'loadedServiceImage')) {
        Assert-ViiperExactBoolean `
            (Get-ViiperRequiredProperty $driver $fileName) 'exactMatch' $true
    }
}

Export-ModuleMember -Function New-ViiperLockedFileBinding,
    Close-ViiperLockedFileBinding, Assert-ViiperLockedFileBinding,
    Read-ViiperStrictEvidence, New-ViiperStdoutEvidenceReceipt,
    Assert-ViiperStdoutEvidenceContinuity, Assert-ViiperLiveEvidence
