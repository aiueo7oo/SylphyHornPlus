#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $PackageRoot,

	[Parameter()]
	[switch] $RequireSymbolicLink
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$packagePath = (Resolve-Path -LiteralPath $PackageRoot).Path
$launcherPath = Join-Path $packagePath "sylphyhorn-cli.WinGetLauncher.exe"
$clientPath = Join-Path $packagePath "sylphyhorn-cli.exe"
foreach ($path in @($launcherPath, $clientPath)) {
	if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
		throw "CLI alias test file is missing: $path"
	}
}

function Invoke-Cli {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path,

		[Parameter(Mandatory = $true)]
		[string[]] $Arguments
	)

	$startInfo = [Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $Path
	$startInfo.UseShellExecute = $false
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	foreach ($argument in $Arguments) {
		$startInfo.ArgumentList.Add($argument)
	}

	$process = [Diagnostics.Process]::Start($startInfo)
	try {
		$stdout = $process.StandardOutput.ReadToEndAsync()
		$stderr = $process.StandardError.ReadToEndAsync()
		if (-not $process.WaitForExit(10000)) {
			$process.Kill($true)
			throw "CLI alias test timed out: $Path"
		}
		return [pscustomobject]@{
			ExitCode = $process.ExitCode
			Stdout = $stdout.GetAwaiter().GetResult()
			Stderr = $stderr.GetAwaiter().GetResult()
		}
	}
	finally {
		$process.Dispose()
	}
}

function Assert-CliAlias {
	param([Parameter(Mandatory = $true)][string] $Path)

	$help = Invoke-Cli $Path @("--help")
	if ($help.ExitCode -ne 0 -or $help.Stderr -ne "" -or
		-not $help.Stdout.Contains("sylphyhorn-cli desktop list")) {
		throw "CLI alias help failed: $Path"
	}

	$invalid = Invoke-Cli $Path @("invalid-command")
	$result = $invalid.Stdout | ConvertFrom-Json
	if ($invalid.ExitCode -ne 2 -or $invalid.Stderr -ne "" -or
		$result.schemaVersion -ne 1 -or $result.success -ne $false -or
		$result.error.code -cne "invalid_arguments") {
		throw "CLI alias JSON or exit code was not preserved: $Path"
	}
}

Assert-CliAlias $launcherPath

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
	("SylphyHornPlus-CliAliasTest-{0}" -f [guid]::NewGuid().ToString("N"))
$aliasPath = Join-Path $temporaryRoot "sylphyhorn-cli.exe"
$missingClientPath = Join-Path $temporaryRoot "sylphyhorn-cli.WinGetLauncher.exe"
try {
	New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
	Copy-Item -LiteralPath $launcherPath -Destination $missingClientPath
	$missingClient = Invoke-Cli $missingClientPath @("desktop", "list")
	$missingClientResult = $missingClient.Stdout | ConvertFrom-Json
	if ($missingClient.ExitCode -ne 4 -or
		$missingClientResult.success -ne $false -or
		$missingClientResult.error.code -cne "launcher_failure") {
		throw "CLI alias startup failure was not reported as JSON."
	}

	try {
		New-Item -ItemType SymbolicLink -Path $aliasPath -Target $launcherPath | Out-Null
	}
	catch [UnauthorizedAccessException] {
		if ($RequireSymbolicLink) {
			throw
		}
		Write-Warning "CLI alias symbolic-link test skipped because creation is not permitted."
		return [pscustomobject]@{
			Status = "DirectOnly"
			Reason = "SymbolicLinkPrivilegeUnavailable"
		}
	}

	Assert-CliAlias $aliasPath
	return [pscustomobject]@{
		Status = "Passed"
		Reason = $null
	}
}
finally {
	if (Test-Path -LiteralPath $temporaryRoot) {
		if (Test-Path -LiteralPath $aliasPath) {
			Remove-Item -LiteralPath $aliasPath -Force
		}
		if (Test-Path -LiteralPath $missingClientPath) {
			Remove-Item -LiteralPath $missingClientPath -Force
		}
		Remove-Item -LiteralPath $temporaryRoot -Force
	}
}
