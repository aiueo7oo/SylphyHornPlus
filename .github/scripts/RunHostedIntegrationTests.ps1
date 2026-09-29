#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ExpectedTestCount = 16
$TargetFramework = "net10.0-windows10.0.26100.0"
$Platform = "x64"
$repositoryRoot = [System.IO.Path]::GetFullPath(
	(Join-Path $PSScriptRoot "../.."))
$testProject = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WindowsIntegrationTests/SylphyHorn.WindowsIntegrationTests.csproj"
$solutionDirectory = Join-Path $repositoryRoot "source/"
$runtimeIdentifiers = '-p:RuntimeIdentifiers="win-x86;win-x64;win-arm64"'

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
	New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
}
$resultsPath = [System.IO.Path]::GetFullPath(
	(Join-Path $ResultsDirectory "HostedCI.trx"))
if (Test-Path -LiteralPath $resultsPath) {
	throw "Test result already exists: $resultsPath"
}

dotnet restore $testProject `
	-p:Configuration=Release `
	-p:Platform=$Platform `
	$runtimeIdentifiers `
	-p:RuntimeIdentifier=win-x64 `
	-p:RunSylphyHornPostBuild=false `
	-p:SolutionDir="$solutionDirectory" `
	--locked-mode
if ($LASTEXITCODE -ne 0) {
	throw "Windows integration-test restore failed with exit code $LASTEXITCODE."
}

# dotnet test reads global.json from the current directory, and its test runner
# setting enables the Microsoft.Testing.Platform options used below.
Push-Location $repositoryRoot
try {
	dotnet test --project $testProject `
		-c Release `
		-f $TargetFramework `
		-p:Platform=$Platform `
		-p:RunSylphyHornPostBuild=false `
		-p:SolutionDir="$solutionDirectory" `
		--no-restore `
		--results-directory $ResultsDirectory `
		--filter-trait "ExecutionEnvironment=HostedCI" `
		--report-xunit-trx `
		--report-xunit-trx-filename HostedCI.trx
}
finally {
	Pop-Location
}
if ($LASTEXITCODE -ne 0) {
	throw "Hosted-CI Windows integration tests failed with exit code $LASTEXITCODE."
}

[xml] $testRun = Get-Content -LiteralPath $resultsPath -Raw
$testResults = @($testRun.SelectNodes("//*[local-name()='UnitTestResult']"))
if ($testResults.Count -ne $ExpectedTestCount) {
	throw "Expected $ExpectedTestCount HostedCI tests, but TRX contains $($testResults.Count)."
}
$nonPassingResults = @($testResults | Where-Object { $_.outcome -cne "Passed" })
if ($nonPassingResults.Count -ne 0) {
	throw "TRX contains $($nonPassingResults.Count) non-passing HostedCI tests."
}

Write-Output "HostedCI integration-test contract passed: $ExpectedTestCount/$ExpectedTestCount."
