#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hope.Agent security checks runner (Phase 19C).
.DESCRIPTION
    Runs dependency vulnerability scanning and optional SBOM generation.
.PARAMETER Solution
    Solution file path. Default: Hope.Agent.sln
.PARAMETER IncludeTransitive
    Include transitive packages in vulnerability check.
.PARAMETER GenerateSbom
    Attempt SBOM generation if sbom-tool is available.
.EXAMPLE
    ./tools/hope-security.ps1
    ./tools/hope-security.ps1 -IncludeTransitive -GenerateSbom
#>
param(
    [string]$Solution = "Hope.Agent.sln",
    [switch]$IncludeTransitive,
    [switch]$GenerateSbom,
    [ValidateSet("None", "Low", "Moderate", "High", "Critical")]
    [string]$FailOnSeverity = "High",
    [string]$ArtifactsDir = "artifacts/security",
    [string]$MinimumSemanticKernelVersion = "1.71.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Run-Step([string]$name, [scriptblock]$step) {
    Write-Host "`n[$name]" -ForegroundColor Cyan
    & $step
}

function Assert-MinimumSemanticKernelVersion([string]$propsPath, [string]$minVersion) {
    if (-not (Test-Path $propsPath)) {
        Write-Warning "Central package file not found at $propsPath; skipping Semantic Kernel version floor check."
        return
    }

    [xml]$props = Get-Content -Path $propsPath
    $packages = $props.Project.ItemGroup.PackageVersion
    $targets = @("Microsoft.SemanticKernel", "Microsoft.SemanticKernel.Core")
    $min = [Version]$minVersion

    foreach ($pkg in $targets) {
        $node = $packages | Where-Object { $_.Include -eq $pkg } | Select-Object -First 1
        if ($null -eq $node) {
            continue
        }

        $versionText = [string]$node.Version
        if ([string]::IsNullOrWhiteSpace($versionText)) {
            continue
        }

        $normalized = ($versionText -split '-') | Select-Object -First 1
        $current = [Version]$normalized
        if ($current -lt $min) {
            Write-Error "$pkg version $versionText is below minimum allowed $minVersion."
            exit 1
        }
    }
}

if (-not (Test-Path $Solution)) {
    Write-Error "Solution not found: $Solution"
    exit 2
}

if (-not (Test-Path $ArtifactsDir)) {
    New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
}

Run-Step "Dependency version floor checks" {
    Assert-MinimumSemanticKernelVersion -propsPath "Directory.Packages.props" -minVersion $MinimumSemanticKernelVersion
}

Run-Step "Restore" {
    dotnet restore $Solution
}

$includeTransitiveArg = if ($IncludeTransitive) { "--include-transitive" } else { "" }
$scanTextPath = Join-Path $ArtifactsDir "vulnerability-scan.txt"
$scanJsonPath = Join-Path $ArtifactsDir "vulnerability-scan.json"

Run-Step "Dependency vulnerability scan" {
    $textCmd = "dotnet list $Solution package --vulnerable $includeTransitiveArg"
    $textOutput = Invoke-Expression $textCmd | Out-String
    $textOutput | Tee-Object -FilePath $scanTextPath

    $jsonCmd = "dotnet list $Solution package --vulnerable --format json $includeTransitiveArg"
    $jsonOutput = Invoke-Expression $jsonCmd | Out-String
    $jsonOutput | Out-File -FilePath $scanJsonPath -Encoding utf8

    $rank = @{ "none" = 0; "low" = 1; "moderate" = 2; "high" = 3; "critical" = 4 }
    $threshold = $rank[$FailOnSeverity.ToLowerInvariant()]
    if ($threshold -gt 0) {
        $matches = [regex]::Matches($textOutput, "\b(Critical|High|Moderate|Low)\b")
        $severities = $matches | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique
        $blocking = $severities | Where-Object { $rank[$_] -ge $threshold }
        if (@($blocking).Count -gt 0) {
            Write-Error "Vulnerability threshold failed. Found severities: $(@($blocking) -join ', ') (threshold: $FailOnSeverity)."
            exit 1
        }
    }
}

if ($GenerateSbom) {
    Run-Step "SBOM generation" {
        $outDir = Join-Path (Get-Location) "artifacts/sbom"
        if (-not (Test-Path $outDir)) {
            New-Item -ItemType Directory -Path $outDir | Out-Null
        }

        $cyclone = Get-Command dotnet-CycloneDX -ErrorAction SilentlyContinue
        if ($null -ne $cyclone) {
            dotnet-CycloneDX $Solution -o $outDir
            return
        }

        $sbomTool = Get-Command sbom-tool -ErrorAction SilentlyContinue
        if ($null -ne $sbomTool) {
            sbom-tool generate -b $outDir -bc src -pn Hope.Agent -pv 1.0.0
            return
        }

        Write-Warning "No SBOM tool found (dotnet-CycloneDX or sbom-tool). Skipping SBOM generation."
    }
}

Write-Host "`nSecurity checks completed." -ForegroundColor Green
exit 0
