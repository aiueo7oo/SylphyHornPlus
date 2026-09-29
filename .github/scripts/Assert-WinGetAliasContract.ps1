#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $ManifestRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$installerManifest = Get-Content `
	-LiteralPath (Join-Path $ManifestRoot 'hwtnb.SylphyHornPlus.installer.yaml') `
	-Raw
$aliasCount = @([regex]::Matches($installerManifest, '(?m)^  PortableCommandAlias:')).Count
$approvedEntries = @(
	'RelativeFilePath: SylphyHorn/SylphyHorn.WinGetLauncher.exe',
	'PortableCommandAlias: SylphyHornPlus',
	'RelativeFilePath: SylphyHorn/sylphyhorn-cli.WinGetLauncher.exe',
	'PortableCommandAlias: sylphyhorn-cli'
)
$missingEntries = @($approvedEntries | Where-Object { -not $installerManifest.Contains($_) })
if ($aliasCount -ne 2 -or $missingEntries.Count -ne 0) {
	throw 'Generated WinGet manifest does not contain the approved GUI and CLI aliases.'
}
