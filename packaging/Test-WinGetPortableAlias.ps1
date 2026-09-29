#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $LauncherPath,

	[Parameter(Mandatory = $true)]
	[string] $ProbePath,

	[Parameter()]
	[switch] $RequireSymbolicLink,

	[Parameter()]
	[switch] $Cli
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-Condition {
	param(
		[Parameter(Mandatory = $true)]
		[bool] $Condition,

		[Parameter(Mandatory = $true)]
		[string] $Message
	)

	if (-not $Condition) {
		throw $Message
	}
}

$resolvedLauncher = (Resolve-Path -LiteralPath $LauncherPath).Path
$resolvedProbe = (Resolve-Path -LiteralPath $ProbePath).Path
$temporaryRoot = Join-Path `
	([System.IO.Path]::GetTempPath()) `
	("SylphyHornPlus-AliasTest-{0}" -f ([guid]::NewGuid().ToString("N")))
$packageRoot = Join-Path $temporaryRoot "package with spaces"
$linksRoot = Join-Path $temporaryRoot "WinGet Links"
# The CLI launcher lets the child inherit the caller's working directory, while
# the GUI launcher starts the child in its own installation directory.
$variant = if ($Cli) {
	@{
		AliasName = "sylphyhorn-cli.exe"
		TargetName = "sylphyhorn-cli.exe"
		LauncherName = "sylphyhorn-cli.WinGetLauncher.exe"
		ExpectedDirectory = $linksRoot
	}
}
else {
	@{
		AliasName = "SylphyHornPlus.exe"
		TargetName = "SylphyHorn.exe"
		LauncherName = "SylphyHorn.WinGetLauncher.exe"
		ExpectedDirectory = $packageRoot
	}
}
$launcherName = $variant.LauncherName
$expectedDirectory = $variant.ExpectedDirectory
$aliasPath = Join-Path $linksRoot $variant.AliasName
$probePath = Join-Path $packageRoot $variant.TargetName
$usedSymbolicLink = $true
$markerPath = Join-Path $temporaryRoot "alias-launched.txt"
$launcherProcess = $null
$createdRoot = $null
# The working directory reported by the probe need not be spelled like the
# expected path: the GUI launcher derives it from the normalized final path, and
# the temporary directory may be spelled with 8.3 names. A uniquely named marker
# file identifies the directory without comparing path strings.
$directoryMarkerName = "cwd-{0}.txt" -f [guid]::NewGuid().ToString("N")
$directoryMarkerValue = [guid]::NewGuid().ToString("N")

try {
	New-Item -ItemType Directory -Path $packageRoot, $linksRoot | Out-Null
	$createdRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path
	Set-Content `
		-LiteralPath (Join-Path $expectedDirectory $directoryMarkerName) `
		-Value $directoryMarkerValue `
		-NoNewline
	Copy-Item -LiteralPath $resolvedLauncher -Destination `
		(Join-Path $packageRoot $launcherName)
	Copy-Item -LiteralPath $resolvedProbe -Destination $probePath
	try {
		New-Item -ItemType SymbolicLink -Path $aliasPath -Target `
			(Join-Path $packageRoot $launcherName) | Out-Null
	}
	catch [System.UnauthorizedAccessException] {
		if ($RequireSymbolicLink) {
			throw
		}

		Write-Warning "Symbolic-link creation is not permitted; testing the launcher directly."
		$aliasPath = Join-Path $packageRoot $launcherName
		$usedSymbolicLink = $false
	}

	$expectedArguments = @(
		"",
		"value with spaces",
		'embedded"quote',
		'trailing\',
		'slashes\\\"quote',
		"日本語")
	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $aliasPath
	$startInfo.UseShellExecute = $false
	$startInfo.WorkingDirectory = $linksRoot
	$startInfo.Environment["SYLPHYHORN_ALIAS_TEST_RESULT"] = $markerPath
	foreach ($argument in $expectedArguments) {
		$startInfo.ArgumentList.Add($argument)
	}
	$launcherProcess = [System.Diagnostics.Process]::Start($startInfo)
	$deadline = [DateTime]::UtcNow.AddSeconds(10)
	while (-not $launcherProcess.HasExited) {
		if ([DateTime]::UtcNow -ge $deadline) {
			$launcherProcess.Kill()
			$launcherProcess.WaitForExit()
			throw "The WinGet alias launcher did not exit before the timeout."
		}

		Start-Sleep -Milliseconds 25
	}
	Assert-Condition ($launcherProcess.ExitCode -eq 0) `
		"WinGet alias launcher returned exit code $($launcherProcess.ExitCode)."

	while (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
		if ([DateTime]::UtcNow -ge $deadline) {
			throw "The WinGet alias did not launch the sibling SylphyHorn.exe probe."
		}

		Start-Sleep -Milliseconds 25
	}

	$stream = [System.IO.File]::OpenRead($markerPath)
	$probeProcessId = 0
	try {
		$reader = [System.IO.BinaryReader]::new($stream)
		try {
			Assert-Condition ($reader.ReadUInt32() -eq 0x31414853) `
				"The alias probe result has an invalid header."
			$probeProcessId = $reader.ReadUInt32()
			Assert-Condition ($probeProcessId -ne 0) `
				"The alias probe result contains an invalid process ID."
			$argumentCount = $reader.ReadUInt32()
			Assert-Condition ($argumentCount -eq $expectedArguments.Count) `
				"The alias probe received $argumentCount arguments instead of $($expectedArguments.Count)."
			$actualArguments = @()
			for ($index = 0; $index -lt $argumentCount; ++$index) {
				$characterCount = $reader.ReadUInt32()
				$bytes = $reader.ReadBytes($characterCount * 2)
				Assert-Condition ($bytes.Length -eq $characterCount * 2) `
					"The alias probe result ended inside argument $index."
				$actualArguments += [Text.Encoding]::Unicode.GetString($bytes)
			}
			$directoryLength = $reader.ReadUInt32()
			$workingDirectory = [Text.Encoding]::Unicode.GetString(
				$reader.ReadBytes($directoryLength * 2))
			$directoryMarker = Join-Path $workingDirectory $directoryMarkerName
			$isExpectedDirectory =
				(Test-Path -LiteralPath $directoryMarker -PathType Leaf) -and
				(Get-Content -LiteralPath $directoryMarker -Raw) -ceq $directoryMarkerValue
			Assert-Condition $isExpectedDirectory `
				"Unexpected child working directory: $workingDirectory (expected $expectedDirectory)."
			Assert-Condition ($stream.Position -eq $stream.Length) `
				"The alias probe result contains trailing data."
		}
		finally {
			$reader.Dispose()
		}
	}
	finally {
		$stream.Dispose()
	}
	Assert-Condition `
		(@(Compare-Object $expectedArguments $actualArguments -SyncWindow 0).Count -eq 0) `
		"The WinGet alias did not preserve argument boundaries."

	try {
		$probeProcess = [System.Diagnostics.Process]::GetProcessById($probeProcessId)
	}
	catch [System.ArgumentException] {
		$probeProcess = $null
	}
	if ($null -ne $probeProcess) {
		try {
			while (-not $probeProcess.HasExited) {
				if ([DateTime]::UtcNow -ge $deadline) {
					throw "The alias probe process did not exit before the timeout."
				}

				Start-Sleep -Milliseconds 25
			}
		}
		finally {
			$probeProcess.Dispose()
		}
	}

	return [pscustomobject]@{
		Status = if ($usedSymbolicLink) { "Passed" } else { "DirectOnly" }
		Reason = if ($usedSymbolicLink) { $null } else { "SymbolicLinkPrivilegeUnavailable" }
	}
}
finally {
	if ($null -ne $launcherProcess) {
		if (-not $launcherProcess.HasExited) {
			$launcherProcess.Kill()
			$launcherProcess.WaitForExit()
		}
		$launcherProcess.Dispose()
	}
	if (Test-Path -LiteralPath $temporaryRoot) {
		$resolvedTemporaryRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path
		if ($resolvedTemporaryRoot -ne $createdRoot -or
			-not [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith("SylphyHornPlus-AliasTest-")) {
			throw "Unexpected alias test cleanup path: $resolvedTemporaryRoot"
		}
		Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
	}
}
