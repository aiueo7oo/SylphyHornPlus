#requires -Version 7.2

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$topologyScript = Join-Path $PSScriptRoot "Get-WinGetManifestTopology.ps1"

function New-Manifest {
	param(
		[Parameter(Mandatory = $true)]
		[string] $RelativeFilePath,

		[Parameter(Mandatory = $true)]
		[string[]] $Architectures,

		[Parameter()]
		[switch] $IncludeCli
	)

	$nestedFiles = @([pscustomobject]@{
		RelativeFilePath = $RelativeFilePath
		PortableCommandAlias = "SylphyHornPlus"
	})
	if ($IncludeCli) {
		$nestedFiles += [pscustomobject]@{
			RelativeFilePath = "SylphyHorn/sylphyhorn-cli.WinGetLauncher.exe"
			PortableCommandAlias = "sylphyhorn-cli"
		}
	}

	return [pscustomobject]@{
		PackageIdentifier = "hwtnb.SylphyHornPlus"
		InstallerType = "zip"
		NestedInstallerType = "portable"
		UpgradeBehavior = "install"
		NestedInstallerFiles = $nestedFiles
		Installers = @($Architectures | ForEach-Object {
			[pscustomobject]@{ Architecture = $_ }
		})
	}
}

$legacy = & $topologyScript -Manifest (
	New-Manifest "SylphyHorn/SylphyHorn.exe" @("x86"))
if ($legacy.InstallerCount -ne 1 -or
	$legacy.Topology -cne "LegacySingleArchitecture") {
	throw "The legacy one-architecture fixture was not classified correctly."
}

$launcher = & $topologyScript -Manifest (
	New-Manifest `
		"SylphyHorn/SylphyHorn.WinGetLauncher.exe" `
		@("x86", "x64", "arm64"))
if ($launcher.InstallerCount -ne 3 -or
	$launcher.Topology -cne "LauncherThreeArchitecture") {
	throw "The launcher three-architecture fixture was not classified correctly."
}

$withCli = & $topologyScript -Manifest (
	New-Manifest `
		"SylphyHorn/SylphyHorn.WinGetLauncher.exe" `
		@("x86", "x64", "arm64") `
		-IncludeCli)
if ($withCli.InstallerCount -ne 3 -or
	$withCli.Topology -cne "LauncherAndCliThreeArchitecture") {
	throw "The GUI and CLI three-architecture fixture was not classified correctly."
}

$wrongAlias = New-Manifest "SylphyHorn/SylphyHorn.WinGetLauncher.exe" `
	@("x86", "x64", "arm64") -IncludeCli
$wrongAlias.NestedInstallerFiles[1].PortableCommandAlias = "shp"

foreach ($invalidManifest in @(
	(New-Manifest "SylphyHorn/SylphyHorn.WinGetLauncher.exe" @("x86")),
	(New-Manifest "SylphyHorn/SylphyHorn.exe" @("x86", "x64", "arm64")),
	(New-Manifest "SylphyHorn/SylphyHorn.exe" @("x86") -IncludeCli),
	(New-Manifest "SylphyHorn/SylphyHorn.exe" @("x86", "x64", "arm64") -IncludeCli),
	$wrongAlias
)) {
	$failed = $false
	try {
		$null = & $topologyScript -Manifest $invalidManifest
	}
	catch {
		$failed = $true
	}
	if (-not $failed) {
		throw "An invalid mixed WinGet migration topology was accepted."
	}
}

"WinGet manifest topology fixtures passed."
