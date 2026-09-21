[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exercise the real entry-point parameter declarations with an inert body.
# Never execute the hosted installation body, access Windows Installer/COM,
# create an MSI, or alter environment variables to impersonate a hosted runner.
$source = Join-Path $PSScriptRoot 'test-previous-msi-upgrade.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Hosted upgrade fixture has parser errors.' }
$parameters = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -ceq 'ExpectedVersion'
})
if ($parameters.Count -ne 1) { throw 'ExpectedVersion must be an explicit entry-point parameter.' }
$mandatory = @($parameters[0].Attributes | Where-Object {
    $_ -is [Management.Automation.Language.AttributeAst] -and
    $_.TypeName.FullName -eq 'Parameter'
} | ForEach-Object { $_.NamedArguments } | Where-Object {
    $_.ArgumentName -eq 'Mandatory' -and
    ($_.ExpressionOmitted -or $_.Argument.Extent.Text -ieq '$true')
})
if ($mandatory.Count -ne 1) { throw 'ExpectedVersion must remain mandatory.' }
$guard = $ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text -match 'ephemeral GitHub-hosted Windows runner'
} | Select-Object -First 1
if (-not $guard -or $ast.ParamBlock.Extent.EndOffset -ge $guard.Extent.StartOffset) {
    throw 'Parameter validation must precede the hosted-only guard.'
}
$assignments = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left.Extent.Text -ceq '$newVersion'
})
if ($assignments.Count -ne 1 -or $assignments[0].Right.Extent.Text.Trim() -cne '$ExpectedVersion') {
    throw 'Current MSI identity must use the caller version, not a pinned or MSI-derived version.'
}

$probe = [ScriptBlock]::Create($ast.ParamBlock.Extent.Text + "`n" +
    '$script:VersionProbeBodyEntered = $true' + "`n" +
    $assignments[0].Extent.Text + "`n" + 'return $newVersion')
$caseCount = 0
try {
    foreach ($invalid in @('', '5.0.9', '5.0.9.0.1', 'v5.0.9.0',
        '5.0.9.0-rc4.6.3', ' 5.0.9.0', '5.0.9.0 ', "5.0.9.0`n",
        '5.0.-9.0', '5.0.+9.0', '5.0.９.0', '5/0/9/0',
        '5.0.9.0;Write-Output unexpected')) {
        $script:VersionProbeBodyEntered = $false
        $failure = $null
        try { [void](& $probe -NewMsi 'never-opened.msi' -ExpectedVersion $invalid) }
        catch { $failure = $_.Exception }
        if ($failure -isnot [Management.Automation.ParameterBindingException] -or
            $script:VersionProbeBodyEntered) {
            throw "Malformed version was not rejected before the fixture body: '$invalid'"
        }
        $caseCount++
    }
    foreach ($expected in @('5.0.9.0', '5.0.10.0', '6.2.34.567')) {
        $script:VersionProbeBodyEntered = $false
        $actual = @(& $probe -NewMsi 'never-opened.msi' -ExpectedVersion $expected)
        if (-not $script:VersionProbeBodyEntered -or $actual.Count -ne 1 -or
            $actual[0] -cne $expected) {
            throw 'The real fixture assignment did not preserve the explicitly expected future version.'
        }
        $caseCount++
    }

    $workflowPath = Join-Path $PSScriptRoot '..\.github\workflows\ci-build.yml'
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $invocations = @($workflow -split "`r?`n" | Where-Object {
        $_ -match '^\s+\.\\utils\\test-previous-msi-upgrade\.ps1\s'
    })
    if ($invocations.Count -ne 1 -or $invocations[0].Trim() -cne
        '.\utils\test-previous-msi-upgrade.ps1 -NewMsi $msi -ExpectedVersion "${{ env.PRODUCT_VERSION }}"') {
        throw 'CI must pass its build PRODUCT_VERSION explicitly to the hosted upgrade fixture.'
    }
    if ($workflow -notmatch '(?m)^\s+run: \.\\utils\\test-previous-msi-upgrade-parameters\.ps1\s*$') {
        throw 'CI must run the native-free entry-point regression.'
    }
    $caseCount++
    Write-Output "$caseCount hosted MSI version-contract cases passed without installation, native calls or hosted-environment overrides."
}
finally {
    Remove-Variable -Name VersionProbeBodyEntered -Scope Script -ErrorAction SilentlyContinue
}
