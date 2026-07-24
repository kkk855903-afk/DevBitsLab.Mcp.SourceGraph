[CmdletBinding(DefaultParameterSetName = 'Scan')]
param(
    [Parameter(ParameterSetName = 'Scan')]
    [ValidateNotNullOrEmpty()]
    [string] $SolutionPath = 'DevBitsLab.Mcp.SourceGraph.slnx',

    [Parameter(Mandatory, ParameterSetName = 'Report')]
    [ValidateNotNullOrEmpty()]
    [string] $ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-Vulnerability {
    param(
        [AllowNull()]
        [object] $Node,

        [string] $JsonPath = '$'
    )

    if ($null -eq $Node -or
        $Node -is [string] -or
        $Node.GetType().IsValueType) {
        return
    }

    if ($Node -is [Collections.IEnumerable] -and
        $Node -isnot [Management.Automation.PSCustomObject]) {
        $index = 0
        foreach ($item in $Node) {
            Find-Vulnerability -Node $item -JsonPath "$JsonPath[$index]"
            $index++
        }
        return
    }

    foreach ($property in $Node.PSObject.Properties) {
        $propertyPath = "$JsonPath.$($property.Name)"
        if ($property.Name -ieq 'vulnerabilities') {
            foreach ($vulnerability in @($property.Value)) {
                if ($null -ne $vulnerability) {
                    [pscustomobject] @{
                        Path = $propertyPath
                        Value = $vulnerability
                    }
                }
            }
            continue
        }

        Find-Vulnerability -Node $property.Value -JsonPath $propertyPath
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Report') {
    $resolvedReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
    $jsonText = [IO.File]::ReadAllText($resolvedReportPath)
}
else {
    $resolvedSolutionPath = (Resolve-Path -LiteralPath $SolutionPath).Path
    $jsonLines = & dotnet package list `
        --project $resolvedSolutionPath `
        --vulnerable `
        --include-transitive `
        --format json `
        --output-version 1 `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet package list failed with exit code $LASTEXITCODE."
    }
    $jsonText = $jsonLines -join [Environment]::NewLine
}

if ([string]::IsNullOrWhiteSpace($jsonText)) {
    throw 'The vulnerability scan returned an empty report.'
}

try {
    $report = $jsonText | ConvertFrom-Json -Depth 100
}
catch {
    throw "The vulnerability scan did not return valid JSON: $($_.Exception.Message)"
}

if ($report.version -ne 1) {
    throw "Unsupported vulnerability report version '$($report.version)'."
}
if ($null -eq $report.PSObject.Properties['projects'] -or
    @($report.projects).Count -eq 0) {
    throw 'The vulnerability report did not contain any projects.'
}

$vulnerabilities = @(Find-Vulnerability -Node $report)
if ($vulnerabilities.Count -gt 0) {
    $details = $vulnerabilities |
        ConvertTo-Json -Depth 20 -Compress
    throw (
        "Found $($vulnerabilities.Count) vulnerable package finding(s): " +
        $details)
}

Write-Host (
    "No vulnerable top-level or transitive packages were reported across " +
    "$(@($report.projects).Count) projects.")
