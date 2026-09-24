#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $Version,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $ReleaseDate,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $ReleaseNotesUrl,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $UrlX86,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $PathX86,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $UrlX64,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $PathX64,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $UrlArm64,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $PathArm64,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-YamlString {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Value
	)

	return "'{0}'" -f $Value.Replace("'", "''")
}

function Get-Sha256 {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		throw "Installer asset does not exist: $Path"
	}
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

if ($Version -notmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$") {
	throw "Invalid package version: $Version"
}
if ($ReleaseDate -notmatch "^[0-9]{4}-[0-9]{2}-[0-9]{2}$") {
	throw "Invalid release date: $ReleaseDate"
}
foreach ($url in @($ReleaseNotesUrl, $UrlX86, $UrlX64, $UrlArm64)) {
	if (-not [Uri]::IsWellFormedUriString($url, [UriKind]::Absolute) -or
		([Uri] $url).Scheme -cne "https") {
		throw "Expected an absolute HTTPS URL: $url"
	}
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
	throw "Manifest output directory already exists: $outputPath"
}
New-Item -ItemType Directory -Path $outputPath | Out-Null

$installers = @(
	[ordered]@{ Architecture = "x86"; Url = $UrlX86; Path = $PathX86 },
	[ordered]@{ Architecture = "x64"; Url = $UrlX64; Path = $PathX64 },
	[ordered]@{ Architecture = "arm64"; Url = $UrlArm64; Path = $PathArm64 }
)
$installerNodes = @($installers | ForEach-Object {
	@"
- Architecture: $($_.Architecture)
  InstallerUrl: $(ConvertTo-YamlString $_.Url)
  InstallerSha256: $(Get-Sha256 $_.Path)
"@.TrimEnd()
}) -join "`n"

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json

PackageIdentifier: hwtnb.SylphyHornPlus
PackageVersion: $(ConvertTo-YamlString $Version)
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: SylphyHorn/SylphyHorn.WinGetLauncher.exe
  PortableCommandAlias: SylphyHornPlus
- RelativeFilePath: SylphyHorn/sylphyhorn-cli.WinGetLauncher.exe
  PortableCommandAlias: sylphyhorn-cli
UpgradeBehavior: install
ReleaseDate: $ReleaseDate
Installers:
$installerNodes
ManifestType: installer
ManifestVersion: 1.12.0
"@
$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json

PackageIdentifier: hwtnb.SylphyHornPlus
PackageVersion: $(ConvertTo-YamlString $Version)
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
"@
$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json

PackageIdentifier: hwtnb.SylphyHornPlus
PackageVersion: $(ConvertTo-YamlString $Version)
PackageLocale: en-US
Publisher: hwtnb
PublisherUrl: https://github.com/hwtnb
PublisherSupportUrl: https://github.com/hwtnb/SylphyHornPlus/issues
PackageName: SylphyHornPlus
PackageUrl: https://github.com/hwtnb/SylphyHornPlus
License: MIT
LicenseUrl: https://github.com/hwtnb/SylphyHornPlus/blob/HEAD/LICENSE.txt
ShortDescription: Virtual Desktop Tools for Windows 11 and 10.
Tags:
- desktop-app
- hotkeys
- rocker-gesture
- tools
- virtual-desktop
- wheel-gesture
- window-management
- windows
- windows-11
- windows11
ReleaseNotesUrl: $(ConvertTo-YamlString $ReleaseNotesUrl)
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$documents = [ordered]@{
	"hwtnb.SylphyHornPlus.installer.yaml" = $installerManifest
	"hwtnb.SylphyHornPlus.yaml" = $versionManifest
	"hwtnb.SylphyHornPlus.locale.en-US.yaml" = $localeManifest
}
foreach ($document in $documents.GetEnumerator()) {
	[System.IO.File]::WriteAllText(
		(Join-Path $outputPath $document.Key),
		$document.Value.Replace("`r`n", "`n").TrimEnd() + "`n",
		$utf8NoBom)
}

$writtenInstallerManifest = Get-Content `
	-LiteralPath (Join-Path $outputPath "hwtnb.SylphyHornPlus.installer.yaml") `
	-Raw
if (@([regex]::Matches($writtenInstallerManifest, "(?m)^- Architecture: ")).Count -ne 3) {
	throw "Generated installer manifest does not contain exactly three installer nodes."
}
foreach ($installer in $installers) {
	$hash = Get-Sha256 $installer.Path
	if (-not $writtenInstallerManifest.Contains("- Architecture: $($installer.Architecture)") -or
		-not $writtenInstallerManifest.Contains("InstallerUrl: $(ConvertTo-YamlString $installer.Url)") -or
		-not $writtenInstallerManifest.Contains("InstallerSha256: $hash")) {
		throw "Generated installer manifest is incomplete for $($installer.Architecture)."
	}
}
foreach ($requiredText in @(
	"NestedInstallerType: portable",
	"RelativeFilePath: SylphyHorn/SylphyHorn.WinGetLauncher.exe",
	"PortableCommandAlias: SylphyHornPlus",
	"RelativeFilePath: SylphyHorn/sylphyhorn-cli.WinGetLauncher.exe",
	"PortableCommandAlias: sylphyhorn-cli",
	"UpgradeBehavior: install"
)) {
	if (-not $writtenInstallerManifest.Contains($requiredText)) {
		throw "Generated installer manifest is missing: $requiredText"
	}
}

Get-ChildItem -LiteralPath $outputPath -File | Sort-Object Name
