[CmdletBinding()]
param(
    [ValidateSet("Fast", "Full")]
    [string] $Mode = "Fast",

    [string] $SolutionPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SolutionPath)) {
    $resolvedSolutionPath = Join-Path $repositoryRoot "RelayCove.sln"
}
elseif ([System.IO.Path]::IsPathFullyQualified($SolutionPath)) {
    $resolvedSolutionPath = [System.IO.Path]::GetFullPath($SolutionPath)
}
else {
    $resolvedSolutionPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $SolutionPath))
}

if (-not (Test-Path -LiteralPath $resolvedSolutionPath -PathType Leaf)) {
    throw "Solution file not found: $resolvedSolutionPath"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

Push-Location $repositoryRoot
try {
    Invoke-Checked "dotnet" @("restore", $resolvedSolutionPath)

    if ($Mode -eq "Fast") {
        Invoke-Checked "dotnet" @("build", $resolvedSolutionPath, "--configuration", "Debug", "--no-restore")
        Invoke-Checked "dotnet" @("test", $resolvedSolutionPath, "--configuration", "Debug", "--no-build", "--no-restore", "--logger", "console;verbosity=minimal")
    }
    else {
        Invoke-Checked "dotnet" @("format", $resolvedSolutionPath, "--verify-no-changes", "--no-restore", "--verbosity", "minimal")
        Invoke-Checked "dotnet" @("build", $resolvedSolutionPath, "--configuration", "Release", "--no-restore")
        Invoke-Checked "dotnet" @("test", $resolvedSolutionPath, "--configuration", "Release", "--no-build", "--no-restore", "--logger", "console;verbosity=minimal")
        Invoke-Checked "git" @("diff", "--check")
    }

    Write-Host "RelayCove $Mode verification passed."
}
finally {
    Pop-Location
}
