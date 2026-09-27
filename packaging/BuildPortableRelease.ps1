#requires -Version 7.2

[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $Version,

	[Parameter(Mandatory = $true)]
	[ValidateSet("win-x86", "win-x64", "win-arm64")]
	[string] $RuntimeIdentifier,

	[Parameter(Mandatory = $true)]
	[ValidateSet("Scd")]
	[string] $DeploymentMode,

	[Parameter(Mandatory = $true)]
	[ValidateNotNullOrEmpty()]
	[string] $OutputRoot,

	[Parameter()]
	[ValidateSet("Review", "Release")]
	[string] $SourceTreeMode = "Review"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RequiredSdkVersion = "10.0.400"
$TargetFramework = "net10.0-windows10.0.26100.0"
$Configuration = "Release"
$ApprovedRuntimeIdentifiersArgument =
	'-p:RuntimeIdentifiers="win-x86;win-x64;win-arm64"'
$ExpectedMachines = @{
	"win-x86"   = "0x014C"
	"win-x64"   = "0x8664"
	"win-arm64" = "0xAA64"
}
$ArchitectureNames = @{
	"win-x86"   = "x86"
	"win-x64"   = "x64"
	"win-arm64" = "arm64"
}
$MetroSourceCommits = [ordered]@{
	MetroTrilithon = "0ba728cd535214844c0d676ef2b1ba3eedc9fd03"
	MetroRadiance  = "f4505e7f5c025f9468aba86641bac0ba79a15618"
}
$ExpectedMetroHashes = [ordered]@{
	"MetroRadiance.Chrome.dll"    = "765FAA1523907BCDB8BB783119C59BD6AB839B9C0C1FB03828877C5E92629D9A"
	"MetroRadiance.Core.dll"      = "72B8E5D6807BCB0C2C14CC0A4AB4F9EF46968EE11311EA4187378D0B7A3E25AA"
	"MetroRadiance.dll"           = "5C74ED8952E6A2114DCB4DCD311D7DDEDF45AACD18C5BDB4CA2A428476ED90E3"
	"MetroTrilithon.Desktop.dll"  = "1A94E19E1F51847704D1BDE170D4EB0EC0D7DF88518BC5174A101E7EF5AE9FA3"
	"MetroTrilithon.dll"          = "773D7ED98902977CA043C53FA7B493AD1B52AD137544052DDD3A3FA550EB0846"
}

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

function Get-NormalizedPath {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	return [System.IO.Path]::TrimEndingDirectorySeparator(
		[System.IO.Path]::GetFullPath($Path))
}

function Test-IsSameOrChildPath {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Candidate,

		[Parameter(Mandatory = $true)]
		[string] $Parent
	)

	$normalizedCandidate = Get-NormalizedPath $Candidate
	$normalizedParent = Get-NormalizedPath $Parent
	if ($normalizedCandidate.Equals(
			$normalizedParent,
			[System.StringComparison]::OrdinalIgnoreCase)) {
		return $true
	}

	$prefix = $normalizedParent + [System.IO.Path]::DirectorySeparatorChar
	return $normalizedCandidate.StartsWith(
		$prefix,
		[System.StringComparison]::OrdinalIgnoreCase)
}

function Get-RelativeChildPath {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Root,

		[Parameter(Mandatory = $true)]
		[string] $Child
	)

	$normalizedRoot = Get-NormalizedPath $Root
	$normalizedChild = Get-NormalizedPath $Child
	Assert-Condition `
		(Test-IsSameOrChildPath $normalizedChild $normalizedRoot) `
		"Path is outside the expected root: $normalizedChild"

	if ($normalizedRoot.Equals(
		$normalizedChild,
		[System.StringComparison]::OrdinalIgnoreCase)) {
		return ""
	}

	return $normalizedChild.Substring($normalizedRoot.Length + 1)
}

function Get-Sha256 {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TextSha256 {
	param(
		[Parameter(Mandatory = $true)]
		[AllowEmptyString()]
		[string] $Text
	)

	$bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-PeInformation {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	$stream = [System.IO.File]::OpenRead($Path)
	try {
		$reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
		try {
			$headers = $reader.PEHeaders
			$machine = "0x{0:X4}" -f ([int] $headers.CoffHeader.Machine)
			$isManaged = $null -ne $headers.CorHeader
			$isIlOnly = $false
			$corFlags = $null
			if ($isManaged) {
				$corFlags = "0x{0:X8}" -f ([int] $headers.CorHeader.Flags)
				$isIlOnly = 0 -ne (
					$headers.CorHeader.Flags -band
					[System.Reflection.PortableExecutable.CorFlags]::ILOnly)
			}

			return [pscustomobject]@{
				Path      = $Path
				Machine   = $machine
				IsManaged = $isManaged
				IsIlOnly  = $isIlOnly
				CorFlags  = $corFlags
			}
		}
		finally {
			$reader.Dispose()
		}
	}
	finally {
		$stream.Dispose()
	}
}

function Get-AndAssertPeAssets {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PayloadRoot,

		[Parameter(Mandatory = $true)]
		[string] $RuntimeIdentifier
	)

	$expectedMachine = $ExpectedMachines[$RuntimeIdentifier]
	return @(
		foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File |
			Sort-Object FullName) {
			if ($file.Extension -notin @(".exe", ".dll")) {
				continue
			}

			$peInfo = Get-PeInformation $file.FullName
			$classification = if (-not $peInfo.IsManaged) {
				"Unmanaged"
			}
			elseif ($peInfo.IsIlOnly) {
				"ManagedILOnly"
			}
			else {
				"ManagedArchitectureSpecific"
			}

			if ($classification -ne "ManagedILOnly") {
				Assert-Condition `
					($peInfo.Machine -ceq $expectedMachine) `
					("PE architecture mismatch: {0} is {1} ({2}), expected {3}." -f
						$file.FullName,
						$peInfo.Machine,
						$classification,
						$expectedMachine)
			}

			[pscustomobject]@{
				Path           = (Get-RelativeChildPath $PayloadRoot $file.FullName).
					Replace("\", "/")
				Classification = $classification
				Machine        = $peInfo.Machine
				IsManaged      = $peInfo.IsManaged
				IsIlOnly       = $peInfo.IsIlOnly
				CorFlags       = $peInfo.CorFlags
				Length         = $file.Length
				Sha256         = Get-Sha256 $file.FullName
			}
		}
	)
}

function Invoke-LoggedCommand {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Executable,

		[Parameter(Mandatory = $true)]
		[string[]] $Arguments,

		[Parameter(Mandatory = $true)]
		[string] $LogPath
	)

	$displayArguments = $Arguments | ForEach-Object {
		if ($_ -match "\s") {
			'"{0}"' -f $_.Replace('"', '\"')
		}
		else {
			$_
		}
	}
	$displayCommand = "$Executable $($displayArguments -join ' ')"
	"COMMAND: $displayCommand" | Set-Content -LiteralPath $LogPath -Encoding utf8
	& $Executable @Arguments 2>&1 |
		Tee-Object -FilePath $LogPath -Append |
		ForEach-Object { Write-Host $_ }
	$exitCode = $LASTEXITCODE
	if ($exitCode -ne 0) {
		throw "Command failed with exit code ${exitCode}: $displayCommand"
	}

	return $displayCommand
}

function Get-MSBuildPath {
	param(
		[Parameter(Mandatory = $true)]
		[string] $RuntimeIdentifier
	)

	$vswherePath = Join-Path `
		${env:ProgramFiles(x86)} `
		"Microsoft Visual Studio\Installer\vswhere.exe"
	Assert-Condition (Test-Path -LiteralPath $vswherePath -PathType Leaf) `
		"Visual Studio Installer vswhere.exe is required to build the WinGet launcher."
	$requiredComponents = @("Microsoft.VisualStudio.Component.VC.Tools.x86.x64")
	if ($RuntimeIdentifier -ceq "win-arm64") {
		$requiredComponents += "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
	}
	$arguments = @("-latest", "-products", "*", "-requires") +
		$requiredComponents + @("-find", "MSBuild\**\Bin\MSBuild.exe")
	$matches = @(& $vswherePath @arguments)
	Assert-Condition ($LASTEXITCODE -eq 0 -and $matches.Count -gt 0) `
		"MSBuild with the Visual C++ toolchain could not be resolved."
	return $matches[0].Trim()
}

function Find-ProjectAssetsFile {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ArtifactsRoot,

		[Parameter(Mandatory = $true)]
		[string] $ProjectPath
	)

	$expectedProjectPath = Get-NormalizedPath $ProjectPath
	$matches = @()
	foreach ($candidate in Get-ChildItem `
		-LiteralPath $ArtifactsRoot `
		-Recurse `
		-Filter "project.assets.json" `
		-File) {
		$assets = Get-Content -LiteralPath $candidate.FullName -Raw |
			ConvertFrom-Json
		$actualProjectPath = $assets.project.restore.projectPath
		if ($actualProjectPath -and
			(Get-NormalizedPath $actualProjectPath).Equals(
				$expectedProjectPath,
				[System.StringComparison]::OrdinalIgnoreCase)) {
			$matches += $candidate.FullName
		}
	}

	Assert-Condition `
		($matches.Count -eq 1) `
		"Expected exactly one project.assets.json for $ProjectPath; found $($matches.Count)."
	return $matches[0]
}

function Assert-AssetsClosure {
	param(
		[Parameter(Mandatory = $true)]
		[string] $AssetsPath,

		[Parameter(Mandatory = $true)]
		[string] $Rid
	)

	$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
	$targetNames = @($assets.targets.PSObject.Properties.Name)
	$ridTargets = @($targetNames | Where-Object {
		$_ -eq "$TargetFramework/$Rid"
	})
	Assert-Condition `
		($ridTargets.Count -eq 1) `
		"Assets file does not contain the required target $TargetFramework/$Rid`: $AssetsPath"
	Assert-Condition `
		(@($assets.libraries.PSObject.Properties).Count -gt 0) `
		"Assets dependency closure is empty: $AssetsPath"
	return $assets
}

function Add-StagingFile {
	param(
		[Parameter(Mandatory = $true)]
		[string] $SourcePath,

		[Parameter(Mandatory = $true)]
		[string] $RelativePath,

		[Parameter(Mandatory = $true)]
		[string] $Origin,

		[Parameter(Mandatory = $true)]
		[string] $StagingRoot,

		[Parameter(Mandatory = $true)]
		[hashtable] $Provenance
	)

	Assert-Condition (Test-Path -LiteralPath $SourcePath -PathType Leaf) `
		"Staging source file is missing: $SourcePath"
	$destination = Join-Path $StagingRoot $RelativePath
	$destinationDirectory = Split-Path -Parent $destination
	if (-not (Test-Path -LiteralPath $destinationDirectory)) {
		New-Item -ItemType Directory -Path $destinationDirectory |
			Out-Null
	}

	$sourceHash = Get-Sha256 $SourcePath
	if (Test-Path -LiteralPath $destination -PathType Leaf) {
		$destinationHash = Get-Sha256 $destination
		Assert-Condition `
			($sourceHash -eq $destinationHash) `
			"Staging collision has different content: $RelativePath ($Origin)"
	}
	else {
		Copy-Item -LiteralPath $SourcePath -Destination $destination
	}

	if (-not $Provenance.ContainsKey($RelativePath)) {
		$Provenance[$RelativePath] = @()
	}
	$Provenance[$RelativePath] += $Origin
}

function Get-LockDependencyInventory {
	param(
		[Parameter(Mandatory = $true)]
		[string[]] $LockPaths
	)

	$rows = @{}
	foreach ($lockPath in $LockPaths) {
		$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
		foreach ($target in $lock.dependencies.PSObject.Properties) {
			foreach ($dependency in $target.Value.PSObject.Properties) {
				if ($dependency.Value.type -eq "Project") {
					continue
				}
				$key = "$($dependency.Name.ToLowerInvariant())/$($dependency.Value.resolved)"
				if (-not $rows.ContainsKey($key)) {
					$rows[$key] = [ordered]@{
						Package  = $dependency.Name
						Version  = $dependency.Value.resolved
						Type     = $dependency.Value.type
						Targets  = @()
					}
				}
				$rows[$key].Targets += $target.Name
			}
		}
	}

	return @($rows.Values | Sort-Object Package, Version | ForEach-Object {
		[pscustomobject]@{
			Package = $_.Package
			Version = $_.Version
			Type    = $_.Type
			Targets = @($_.Targets | Sort-Object -Unique)
		}
	})
}

function Add-LicensePayload {
	param(
		[Parameter(Mandatory = $true)]
		[string] $RepositoryRoot,

		[Parameter(Mandatory = $true)]
		[string] $PackageRoot,

		[Parameter(Mandatory = $true)]
		[object[]] $Dependencies,

		[Parameter(Mandatory = $true)]
		[string] $StagingRoot,

		[Parameter(Mandatory = $true)]
		[hashtable] $Provenance
	)

	$licenseDirectory = Join-Path $StagingRoot "licenses"
	New-Item -ItemType Directory -Path $licenseDirectory | Out-Null

	Add-StagingFile `
		-SourcePath (Join-Path $RepositoryRoot "LICENSE.txt") `
		-RelativePath "licenses/SylphyHornPlus.txt" `
		-Origin "repository:LICENSE.txt" `
		-StagingRoot $StagingRoot `
		-Provenance $Provenance

	foreach ($license in Get-ChildItem `
		-LiteralPath (Join-Path $RepositoryRoot "source/SylphyHorn/.licenses") `
		-File) {
		Add-StagingFile `
			-SourcePath $license.FullName `
			-RelativePath ("licenses/" + $license.Name) `
			-Origin ("repository:source/SylphyHorn/.licenses/" + $license.Name) `
			-StagingRoot $StagingRoot `
			-Provenance $Provenance
	}

	$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
	foreach ($dotnetLicense in @(
		@("LICENSE.txt", "licenses/dotnet-runtime-license.txt"),
		@("ThirdPartyNotices.txt", "licenses/dotnet-runtime-third-party-notices.txt")
	)) {
		$source = Join-Path $dotnetRoot $dotnetLicense[0]
		Assert-Condition (Test-Path -LiteralPath $source -PathType Leaf) `
			"Required .NET redistribution notice is missing: $source"
		Add-StagingFile `
			-SourcePath $source `
			-RelativePath $dotnetLicense[1] `
			-Origin ("dotnet:" + $dotnetLicense[0]) `
			-StagingRoot $StagingRoot `
			-Provenance $Provenance
	}

	$fixedLicenseDirectory = Join-Path $RepositoryRoot "packaging/licenses"
	foreach ($fixedLicense in Get-ChildItem `
		-LiteralPath $fixedLicenseDirectory `
		-Filter "*.txt" `
		-File) {
		Add-StagingFile `
			-SourcePath $fixedLicense.FullName `
			-RelativePath ("licenses/" + $fixedLicense.Name) `
			-Origin ("repository:packaging/licenses/" + $fixedLicense.Name) `
			-StagingRoot $StagingRoot `
			-Provenance $Provenance
	}

	$legacyLicenseMapPath = Join-Path `
		$fixedLicenseDirectory `
		"legacy-license-map.json"
	Assert-Condition (Test-Path -LiteralPath $legacyLicenseMapPath -PathType Leaf) `
		"Reviewed legacy license mapping is missing: $legacyLicenseMapPath"
	$legacyLicenseMap = Get-Content -LiteralPath $legacyLicenseMapPath -Raw |
		ConvertFrom-Json -AsHashtable
	$expressionPayloads = @{
		"MIT"        = "licenses/MIT.txt"
		"Apache-2.0" = "licenses/Apache-2.0.txt"
	}

	$noticeLines = @(
		"SylphyHornPlus NuGet dependency license inventory",
		"",
		"This inventory is generated from the reviewed packages.lock.json files.",
		"Every package below is mapped to a license text included in this ZIP.",
		"Legacy license URLs are accepted only when a tracked, reviewed offline",
		"mapping exists in packaging/licenses/legacy-license-map.json.",
		""
	)

	$roslynNoticeCopied = $false
	foreach ($dependency in $Dependencies) {
		$packageDirectory = Join-Path `
			(Join-Path $PackageRoot $dependency.Package.ToLowerInvariant()) `
			$dependency.Version
		Assert-Condition (Test-Path -LiteralPath $packageDirectory -PathType Container) `
			"Resolved NuGet package directory is missing: $packageDirectory"
		$nuspec = Get-ChildItem `
			-LiteralPath $packageDirectory `
			-Filter "*.nuspec" `
			-File |
			Select-Object -First 1
		Assert-Condition ($null -ne $nuspec) `
			"NuGet metadata is missing for $($dependency.Package) $($dependency.Version)."

		[xml] $metadataXml = Get-Content -LiteralPath $nuspec.FullName -Raw
		$metadata = $metadataXml.SelectSingleNode(
			"//*[local-name()='metadata']")
		Assert-Condition ($null -ne $metadata) `
			"NuGet metadata node is missing for $($dependency.Package) $($dependency.Version)."
		$licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
		$licenseUrlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
		$authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
		$copyrightNode = $metadata.SelectSingleNode("*[local-name()='copyright']")

		$licenseType = ""
		$licenseValue = ""
		if ($null -ne $licenseNode) {
			$licenseType = $licenseNode.GetAttribute("type")
			$licenseValue = $licenseNode.InnerText.Trim()
		}
		$licenseUrl = if ($null -ne $licenseUrlNode) {
			$licenseUrlNode.InnerText.Trim()
		}
		else {
			""
		}
		Assert-Condition `
			(-not [string]::IsNullOrWhiteSpace($licenseValue) -or
				-not [string]::IsNullOrWhiteSpace($licenseUrl)) `
			"License metadata cannot be determined for $($dependency.Package) $($dependency.Version)."

		$noticeLines += "Package: $($dependency.Package)"
		$noticeLines += "Version: $($dependency.Version)"
		$authors = if ($null -ne $authorsNode) {
			$authorsNode.InnerText.Trim()
		}
		else {
			""
		}
		Assert-Condition (-not [string]::IsNullOrWhiteSpace($authors)) `
			"Package attribution is missing for $($dependency.Package) $($dependency.Version)."
		$noticeLines += "Authors: $authors"
		if ($null -ne $copyrightNode -and
			-not [string]::IsNullOrWhiteSpace($copyrightNode.InnerText)) {
			$noticeLines += "Copyright: $($copyrightNode.InnerText)"
		}
		else {
			$noticeLines += "Copyright metadata: not supplied by package"
			$noticeLines += "Attribution from package authors: $authors"
		}
		if (-not [string]::IsNullOrWhiteSpace($licenseValue)) {
			$noticeLines += "License: $licenseValue ($licenseType)"
		}
		if (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
			$noticeLines += "License URL: $licenseUrl"
		}

		$licensePayloads = @()
		if ($licenseType -eq "file") {
			$packageLicense = Join-Path $packageDirectory $licenseValue
			Assert-Condition (Test-Path -LiteralPath $packageLicense -PathType Leaf) `
				"Package license file is missing: $packageLicense"
			$safeName = $dependency.Package -replace "[^A-Za-z0-9_.-]", "_"
			$extension = [System.IO.Path]::GetExtension($packageLicense)
			if ([string]::IsNullOrWhiteSpace($extension)) {
				$extension = ".txt"
			}
			$packageLicenseRelativePath = "licenses/NuGet/{0}-{1}{2}" -f
				$safeName,
				$dependency.Version,
				$extension
			Add-StagingFile `
				-SourcePath $packageLicense `
				-RelativePath $packageLicenseRelativePath `
				-Origin ("nuget:{0}/{1}:{2}" -f
					$dependency.Package,
					$dependency.Version,
					$licenseValue) `
				-StagingRoot $StagingRoot `
				-Provenance $Provenance
			$licensePayloads += $packageLicenseRelativePath
		}
		elseif ($licenseType -eq "expression") {
			Assert-Condition ($expressionPayloads.ContainsKey($licenseValue)) `
				("Unreviewed NuGet license expression for {0} {1}: {2}" -f
					$dependency.Package,
					$dependency.Version,
					$licenseValue)
			$licensePayloads += $expressionPayloads[$licenseValue]
		}
		else {
			$legacyKey = "{0}/{1}" -f $dependency.Package, $dependency.Version
			Assert-Condition ($legacyLicenseMap.ContainsKey($legacyKey)) `
				("Legacy NuGet license URL has no reviewed offline mapping: {0} ({1})" -f
					$legacyKey,
					$licenseUrl)
			$legacyMapping = $legacyLicenseMap[$legacyKey]
			Assert-Condition `
				($legacyMapping.licenseFiles.Count -gt 0) `
				"Legacy NuGet license mapping has no payload: $legacyKey"
			foreach ($licenseFile in $legacyMapping.licenseFiles) {
				$trackedLicense = Join-Path $fixedLicenseDirectory $licenseFile
				Assert-Condition (Test-Path -LiteralPath $trackedLicense -PathType Leaf) `
					"Mapped offline license file is missing: $trackedLicense"
				$licensePayloads += "licenses/$licenseFile"
			}
			$noticeLines += "Reviewed legacy expression: $($legacyMapping.licenseExpression)"
		}
		Assert-Condition ($licensePayloads.Count -gt 0) `
			"No license payload was resolved for $($dependency.Package) $($dependency.Version)."
		foreach ($licensePayload in $licensePayloads) {
			Assert-Condition `
				(Test-Path -LiteralPath (Join-Path $StagingRoot $licensePayload) -PathType Leaf) `
				"Resolved license payload is absent from staging: $licensePayload"
			$noticeLines += "License payload: $licensePayload"
		}
		$noticeLines += ""

		if (-not $roslynNoticeCopied -and
			$dependency.Package -eq "Microsoft.CodeAnalysis.Common") {
			$roslynNotice = Join-Path $packageDirectory "ThirdPartyNotices.rtf"
			Assert-Condition (Test-Path -LiteralPath $roslynNotice -PathType Leaf) `
				"Roslyn third-party notice is missing: $roslynNotice"
			Add-StagingFile `
				-SourcePath $roslynNotice `
				-RelativePath "licenses/Roslyn-ThirdPartyNotices.rtf" `
				-Origin "nuget:Microsoft.CodeAnalysis.Common/ThirdPartyNotices.rtf" `
				-StagingRoot $StagingRoot `
				-Provenance $Provenance
			$roslynNoticeCopied = $true
		}
	}

	$inventoryPath = Join-Path $licenseDirectory "NuGet-packages.txt"
	$noticeLines | Set-Content -LiteralPath $inventoryPath -Encoding utf8
	$Provenance["licenses/NuGet-packages.txt"] = @(
		"generated:reviewed packages.lock.json dependency license inventory")
}

function Assert-DenyList {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PayloadRoot
	)

	$deniedExtensions = @(
		".pdb", ".xml", ".binlog", ".pfx", ".snk", ".key", ".cer",
		".crt", ".token", ".user", ".cache", ".log")
	$deniedNames = @(
		"AGENTS.md", "CLAUDE.md", "README.md", "README.txt",
		"settings.xml", "packages.lock.json")
	$deniedFragments = @(
		"TestHost", "Tests.dll", "TestResults", "coverage",
		"ErrorReports", "assemblies/VirtualDesktop.", ".git/",
		"Livet", "WindowsAPICodePack")

	$violations = @()
	foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File) {
		$relativePath = (Get-RelativeChildPath $PayloadRoot $file.FullName).
			Replace("\", "/")
		if ($deniedExtensions -contains $file.Extension.ToLowerInvariant()) {
			$violations += $relativePath
			continue
		}
		if ($deniedNames -contains $file.Name) {
			$violations += $relativePath
			continue
		}
		foreach ($fragment in $deniedFragments) {
			if ($relativePath.IndexOf(
				$fragment,
				[System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
				$violations += $relativePath
				break
			}
		}
	}

	Assert-Condition `
		($violations.Count -eq 0) `
		("Deny-list violation(s): " + (($violations | Sort-Object -Unique) -join ", "))
}

function Assert-AllowList {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PayloadRoot
	)

	$approvedJson = @(
		"SylphyHorn.deps.json",
		"SylphyHorn.runtimeconfig.json",
		"SchedulerManager.deps.json",
		"SchedulerManager.runtimeconfig.json",
		"sylphyhorn-cli.deps.json",
		"sylphyhorn-cli.runtimeconfig.json")
	$approvedConfig = @("VirtualDesktop.dll.config")

	$violations = @()
	foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File) {
		$relativePath = (Get-RelativeChildPath $PayloadRoot $file.FullName).
			Replace("\", "/")
		$extension = $file.Extension.ToLowerInvariant()
		$allowed = switch ($extension) {
			".dll" { $true; break }
			".exe" { $true; break }
			".json" { $approvedJson -ccontains $relativePath; break }
			".config" { $approvedConfig -ccontains $relativePath; break }
			".md" { $relativePath -ceq "skills/sylphyhorn/SKILL.md"; break }
			".png" { $relativePath.StartsWith(
				".assets/",
				[System.StringComparison]::Ordinal); break }
			".txt" { $relativePath.StartsWith(
				"licenses/",
				[System.StringComparison]::Ordinal); break }
			".rtf" { $relativePath.StartsWith(
				"licenses/",
				[System.StringComparison]::Ordinal); break }
			default { $false }
		}
		if (-not $allowed) {
			$violations += $relativePath
		}
	}

	Assert-Condition `
		($violations.Count -eq 0) `
		("Allow-list violation(s): " + (($violations | Sort-Object -Unique) -join ", "))
}

function Get-PayloadManifest {
	param(
		[Parameter(Mandatory = $true)]
		[string] $WrapperRoot,

		[Parameter(Mandatory = $true)]
		[hashtable] $Provenance
	)

	return @(
		Get-ChildItem -LiteralPath $WrapperRoot -Recurse -File |
			Sort-Object FullName |
			ForEach-Object {
				$relativeWithinWrapper = (
					Get-RelativeChildPath $WrapperRoot $_.FullName).
					Replace("\", "/")
				$archivePath = "SylphyHorn/$relativeWithinWrapper"
				Assert-Condition ($Provenance.ContainsKey($relativeWithinWrapper)) `
					"Staging file has no approved provenance: $relativeWithinWrapper"
				[pscustomobject]@{
					Path    = $archivePath
					Length  = $_.Length
					Sha256  = Get-Sha256 $_.FullName
					Origins = @($Provenance[$relativeWithinWrapper])
				}
			}
	)
}

if ($Version.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
	throw "-Version must not include a v prefix: $Version"
}
Assert-Condition `
	($Version -match "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$") `
	"Invalid release version: $Version"
Assert-Condition `
	($DeploymentMode -ceq "Scd") `
	"Only the PM-approved Scd deployment mode is supported."

$scriptPath = Get-NormalizedPath $PSCommandPath
$repositoryRoot = Get-NormalizedPath (
	Join-Path (Split-Path -Parent $scriptPath) "..")
$outputRootPath = Get-NormalizedPath $OutputRoot
$repositoryTemporaryRoot = Get-NormalizedPath (Join-Path $repositoryRoot ".tmp")
$outputIsInsideRepository = Test-IsSameOrChildPath $outputRootPath $repositoryRoot
if ($outputIsInsideRepository) {
	Assert-Condition `
		((Test-IsSameOrChildPath $outputRootPath $repositoryTemporaryRoot) -and
			-not $outputRootPath.Equals(
				$repositoryTemporaryRoot,
				[System.StringComparison]::OrdinalIgnoreCase)) `
		"Repository-internal OutputRoot must be a unique child of .tmp: $outputRootPath"
	$outputRootRelativePath = (Get-RelativeChildPath $repositoryRoot $outputRootPath).
		Replace("\", "/")
	& git -C $repositoryRoot check-ignore --quiet -- "$outputRootRelativePath/.phase7-ignore-probe"
	Assert-Condition ($LASTEXITCODE -eq 0) `
		"Repository .tmp OutputRoot is not covered by a Git ignore rule: $outputRootPath"
}

$applicationProject = Join-Path $repositoryRoot "source/SylphyHorn/SylphyHorn.csproj"
$cliProject = Join-Path $repositoryRoot "source/SylphyHorn.Cli/SylphyHorn.Cli.csproj"
$schedulerProject = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.SchedulerManager/SylphyHorn.SchedulerManager.csproj"
$cliWinGetLauncherProject = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WinGetLauncher/sylphyhorn-cli.WinGetLauncher.vcxproj"
$winGetLauncherProject = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WinGetLauncher/SylphyHorn.WinGetLauncher.vcxproj"
$winGetLauncherSource = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WinGetLauncher/SylphyHorn.WinGetLauncher.cpp"
$winGetAliasProbeProject = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WinGetLauncher/AliasTestProbe.vcxproj"
$winGetAliasProbeSource = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.WinGetLauncher/AliasTestProbe.cpp"
$cliWinGetAliasTest = Join-Path `
	$repositoryRoot `
	"packaging/Test-CliWinGetPortableAlias.ps1"
$winGetAliasTest = Join-Path `
	$repositoryRoot `
	"packaging/Test-WinGetPortableAlias.ps1"
$cliLock = Join-Path $repositoryRoot "source/SylphyHorn.Cli/packages.lock.json"
$applicationLock = Join-Path $repositoryRoot "source/SylphyHorn/packages.lock.json"
$schedulerLock = Join-Path `
	$repositoryRoot `
	"source/SylphyHorn.SchedulerManager/packages.lock.json"
$readmePath = Join-Path $repositoryRoot "README.md"
$agentSkillPath = Join-Path $repositoryRoot "skills/sylphyhorn/SKILL.md"

Assert-Condition (Test-Path -LiteralPath $agentSkillPath -PathType Leaf) `
	"Agent skill is missing: $agentSkillPath"

Assert-Condition (Test-Path -LiteralPath $applicationLock -PathType Leaf) `
	"Application lock file is missing: $applicationLock"
Assert-Condition (Test-Path -LiteralPath $schedulerLock -PathType Leaf) `
	"SchedulerManager lock file is missing: $schedulerLock"
Assert-Condition (Test-Path -LiteralPath $cliLock -PathType Leaf) `
	"CLI lock file is missing: $cliLock"

$workingTreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
Assert-Condition ($LASTEXITCODE -eq 0) "Cannot read repository working-tree status."
if ($SourceTreeMode -ceq "Release") {
	if ($workingTreeStatus.Count -eq 0) {
		$sourceTreeState = "CleanReleaseBuild"
		$artifactEligibility = "ReleaseCandidate"
		$publicationDecision = "ReleaseGateEligible"
	}
	else {
		$sourceTreeState = "DirtyLocalReleaseBuild"
		$artifactEligibility = "RequiresUserApproval"
		$publicationDecision = "UserOrCiDecisionRequired"
		Write-Warning "============================================================"
		Write-Warning "DIRTY WORKTREE: generating a local Release candidate."
		Write-Warning "Publication requires an explicit user or CI release decision."
		Write-Warning "Working-tree status and source-input hashes will be recorded."
		Write-Warning "============================================================"
	}
}
else {
	$sourceTreeState = if ($workingTreeStatus.Count -eq 0) {
		"CleanReviewBuild"
	}
	else {
		"DirtyReviewBuild"
	}
	$artifactEligibility = "ReviewOnlyNotForPhase7B"
	$publicationDecision = "NotApplicableReviewOnly"
}

$trackedDiff = (@(& git -C $repositoryRoot diff --binary HEAD -- .) -join "`n")
Assert-Condition ($LASTEXITCODE -eq 0) "Cannot read tracked source diff."
$buildInputPaths = @(
	$scriptPath,
	(Join-Path $repositoryRoot "global.json"),
	$readmePath,
	$agentSkillPath,
	$applicationProject,
	$schedulerProject,
	$cliProject,
	$cliWinGetLauncherProject,
	$winGetLauncherProject,
	$winGetLauncherSource,
	$winGetAliasProbeProject,
	$winGetAliasProbeSource,
	$winGetAliasTest,
	$cliWinGetAliasTest,
	$applicationLock,
	$schedulerLock,
	$cliLock,
	(Join-Path $repositoryRoot "LICENSE.txt"),
	(Join-Path $repositoryRoot "packaging/.gitignore")
)
$buildInputPaths += @(
	Get-ChildItem `
		-LiteralPath (Join-Path $repositoryRoot "source/SylphyHorn/.licenses") `
		-File |
		ForEach-Object { $_.FullName }
)
$buildInputPaths += @(
	Get-ChildItem `
		-LiteralPath (Join-Path $repositoryRoot "packaging/licenses") `
		-File |
		ForEach-Object { $_.FullName }
)
$buildInputs = @(
	$buildInputPaths |
		Sort-Object -Unique |
		ForEach-Object {
			Assert-Condition (Test-Path -LiteralPath $_ -PathType Leaf) `
				"Required build/provenance input is missing: $_"
			[pscustomobject]@{
				Path = (Get-RelativeChildPath $repositoryRoot $_).Replace("\", "/")
				Length = (Get-Item -LiteralPath $_).Length
				Sha256 = Get-Sha256 $_
			}
		}
)

[xml] $applicationProjectXml = Get-Content -LiteralPath $applicationProject -Raw
$projectVersionNode = $applicationProjectXml.SelectSingleNode(
	"/Project/PropertyGroup/Version")
Assert-Condition ($null -ne $projectVersionNode) `
	"Version is missing from $applicationProject."
$projectVersion = $projectVersionNode.InnerText.Trim()
$extraVersionNode = $applicationProjectXml.SelectSingleNode(
	"//AssemblyMetadata[@Include='ExtraVersion']")
$extraVersion = if ($null -ne $extraVersionNode) {
	$extraVersionNode.Value
}
else {
	""
}
$expectedVersion = if ([string]::IsNullOrWhiteSpace($extraVersion)) {
	$projectVersion
}
else {
	"$projectVersion-$extraVersion"
}
Assert-Condition `
	($Version -ceq $expectedVersion) `
	"-Version '$Version' does not match Version/ExtraVersion '$expectedVersion'."

$readmeText = Get-Content -LiteralPath $readmePath -Raw
Assert-Condition `
	($readmeText.Contains("SylphyHornPlus-v{version}-x86.zip") -and
		$readmeText.Contains("SylphyHornPlus-v{version}-x64.zip") -and
		$readmeText.Contains("SylphyHornPlus-v{version}-arm64.zip")) `
	"README does not contain the approved architecture-specific filename contract."

$resolvedSdkVersion = (& dotnet --version).Trim()
Assert-Condition `
	($LASTEXITCODE -eq 0 -and $resolvedSdkVersion -ceq $RequiredSdkVersion) `
	"dotnet SDK must resolve exactly to $RequiredSdkVersion; resolved '$resolvedSdkVersion'."

$globalJsonPath = Join-Path $repositoryRoot "global.json"
$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
Assert-Condition `
	($globalJson.sdk.version -ceq $RequiredSdkVersion -and
		$globalJson.sdk.rollForward -ceq "disable" -and
		$globalJson.sdk.allowPrerelease -eq $false) `
	"global.json does not contain the approved exact SDK policy."

$metroDirectory = Join-Path `
	$repositoryRoot `
	"source/libraries/MetroTrilithon.Desktop/net10.0-windows"
$metroManifest = @()
foreach ($metroEntry in $ExpectedMetroHashes.GetEnumerator()) {
	$metroPath = Join-Path $metroDirectory $metroEntry.Key
	Assert-Condition (Test-Path -LiteralPath $metroPath -PathType Leaf) `
		"Required Metro binary is missing: $metroPath"
	$actualHash = Get-Sha256 $metroPath
	Assert-Condition ($actualHash -ceq $metroEntry.Value) `
		"Metro binary hash mismatch: $($metroEntry.Key)"
	$peInfo = Get-PeInformation $metroPath
	Assert-Condition ($peInfo.IsManaged -and $peInfo.IsIlOnly) `
		"Metro binary must be managed IL-only: $($metroEntry.Key)"
	$metroManifest += [pscustomobject]@{
		Name      = $metroEntry.Key
		Length    = (Get-Item -LiteralPath $metroPath).Length
		Sha256    = $actualHash
		Machine   = $peInfo.Machine
		IsManaged = $peInfo.IsManaged
		IsIlOnly  = $peInfo.IsIlOnly
	}
}

if (-not (Test-Path -LiteralPath $outputRootPath)) {
	New-Item -ItemType Directory -Path $outputRootPath | Out-Null
}
Assert-Condition (Test-Path -LiteralPath $outputRootPath -PathType Container) `
	"OutputRoot is not a directory: $outputRootPath"

$architecture = $ArchitectureNames[$RuntimeIdentifier]
$releaseTag = "v$Version"
$zipName = "SylphyHornPlus-v$Version-$architecture.zip"
$finalZipPath = Join-Path $outputRootPath $zipName
Assert-Condition (-not (Test-Path -LiteralPath $finalZipPath)) `
	"Final ZIP already exists and will not be overwritten: $finalZipPath"

$workName = "phase7-{0}-{1}-{2}" -f
	$RuntimeIdentifier,
	([DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")),
	([guid]::NewGuid().ToString("N"))
$workRoot = Join-Path $outputRootPath $workName
Assert-Condition (-not (Test-Path -LiteralPath $workRoot)) `
	"Unique work root already exists: $workRoot"
New-Item -ItemType Directory -Path $workRoot | Out-Null

$logsRoot = Join-Path $workRoot "logs"
$temporaryRoot = Get-NormalizedPath ([System.IO.Path]::GetTempPath())
$buildRoot = Join-Path `
	$temporaryRoot `
	("SylphyHornPlus-Build-{0}" -f ([guid]::NewGuid().ToString("N")))
Assert-Condition `
	((Test-IsSameOrChildPath $buildRoot $temporaryRoot) -and
		-not $buildRoot.Equals(
			$temporaryRoot,
			[System.StringComparison]::OrdinalIgnoreCase)) `
	"Build root must be a unique child of the temporary directory."
Assert-Condition (-not (Test-Path -LiteralPath $buildRoot)) `
	"Unique build root already exists: $buildRoot"
$applicationArtifacts = Join-Path $buildRoot "app/a"
$applicationPublish = Join-Path $buildRoot "app/p"
# Share intermediate outputs so both publishes use identical SylphyHorn.Core binaries.
$cliArtifacts = $applicationArtifacts
$cliPublish = Join-Path $buildRoot "cli/p"
$schedulerArtifacts = Join-Path $buildRoot "scheduler/a"
$schedulerPublish = Join-Path $buildRoot "scheduler/p"
$schedulerOutput = Join-Path $buildRoot "scheduler/b"
$cliWinGetLauncherOutput = Join-Path $buildRoot "cli-winget-launcher"
$winGetLauncherOutput = Join-Path $buildRoot "winget-launcher"
$winGetAliasProbeOutput = Join-Path $buildRoot "winget-alias-probe"
$stagingRoot = Join-Path $workRoot "staging"
$wrapperRoot = Join-Path $stagingRoot "SylphyHorn"
$archiveRoot = Join-Path $workRoot "archive"
$extractRoot = Join-Path $workRoot "extracted"
$evidenceRoot = Join-Path $workRoot "evidence"
$mainError = $null
$cleanupError = $null
try {
foreach ($directory in @(
	$logsRoot,
	$applicationArtifacts,
	$applicationPublish,
	$cliPublish,
	$schedulerArtifacts,
	$schedulerPublish,
	$schedulerOutput,
	$cliWinGetLauncherOutput,
	$winGetLauncherOutput,
	$winGetAliasProbeOutput,
	$wrapperRoot,
	$archiveRoot,
	$extractRoot,
	$evidenceRoot
)) {
	New-Item -ItemType Directory -Path $directory | Out-Null
}

$commands = @()
$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"restore",
		$applicationProject,
		"-p:Configuration=$Configuration",
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-p:RuntimeIdentifier=$RuntimeIdentifier",
		"-p:SelfContained=true",
		"--locked-mode",
		"--artifacts-path=$applicationArtifacts",
		"-p:RunSylphyHornPostBuild=false",
		"-bl:$logsRoot/SylphyHorn-restore.binlog"
	) `
	-LogPath (Join-Path $logsRoot "SylphyHorn-restore.log")

$applicationAssetsPath = Find-ProjectAssetsFile `
	-ArtifactsRoot $applicationArtifacts `
	-ProjectPath $applicationProject
$applicationAssets = Assert-AssetsClosure `
	-AssetsPath $applicationAssetsPath `
	-Rid $RuntimeIdentifier

$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"restore",
		$cliProject,
		"-p:Configuration=$Configuration",
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-p:RuntimeIdentifier=$RuntimeIdentifier",
		"-p:SelfContained=true",
		"--locked-mode",
		"--artifacts-path=$cliArtifacts",
		"-p:RunSylphyHornPostBuild=false",
		"-bl:$logsRoot/sylphyhorn-cli-restore.binlog"
	) `
	-LogPath (Join-Path $logsRoot "sylphyhorn-cli-restore.log")

$cliAssetsPath = Find-ProjectAssetsFile `
	-ArtifactsRoot $cliArtifacts `
	-ProjectPath $cliProject
$null = Assert-AssetsClosure `
	-AssetsPath $cliAssetsPath `
	-Rid $RuntimeIdentifier

$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"restore",
		$schedulerProject,
		"-p:Configuration=$Configuration",
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-p:RuntimeIdentifier=$RuntimeIdentifier",
		"-p:SelfContained=true",
		"--locked-mode",
		"--artifacts-path=$schedulerArtifacts",
		"-p:OutputPath=$schedulerOutput",
		"-bl:$logsRoot/SchedulerManager-restore.binlog"
	) `
	-LogPath (Join-Path $logsRoot "SchedulerManager-restore.log")

$schedulerAssetsPath = Find-ProjectAssetsFile `
	-ArtifactsRoot $schedulerArtifacts `
	-ProjectPath $schedulerProject
$null = Assert-AssetsClosure `
	-AssetsPath $schedulerAssetsPath `
	-Rid $RuntimeIdentifier

$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"publish",
		$applicationProject,
		"-c",
		$Configuration,
		"-f",
		$TargetFramework,
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-r",
		$RuntimeIdentifier,
		"--self-contained",
		"true",
		"--no-restore",
		"--artifacts-path=$applicationArtifacts",
		"-p:RunSylphyHornPostBuild=false",
		"-p:PublishDir=$applicationPublish",
		"-bl:$logsRoot/SylphyHorn-publish.binlog"
	) `
	-LogPath (Join-Path $logsRoot "SylphyHorn-publish.log")

$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"publish",
		$cliProject,
		"-c",
		$Configuration,
		"-f",
		$TargetFramework,
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-r",
		$RuntimeIdentifier,
		"--self-contained",
		"true",
		"--no-restore",
		"--artifacts-path=$cliArtifacts",
		"-p:RunSylphyHornPostBuild=false",
		"-p:PublishDir=$cliPublish",
		"-bl:$logsRoot/sylphyhorn-cli-publish.binlog"
	) `
	-LogPath (Join-Path $logsRoot "sylphyhorn-cli-publish.log")

$commands += Invoke-LoggedCommand `
	-Executable "dotnet" `
	-Arguments @(
		"publish",
		$schedulerProject,
		"-c",
		$Configuration,
		"-f",
		$TargetFramework,
		"-p:Platform=AnyCPU",
		$ApprovedRuntimeIdentifiersArgument,
		"-r",
		$RuntimeIdentifier,
		"--self-contained",
		"true",
		"--no-restore",
		"--artifacts-path=$schedulerArtifacts",
		"-p:OutputPath=$schedulerOutput",
		"-p:PublishDir=$schedulerPublish",
		"-bl:$logsRoot/SchedulerManager-publish.binlog"
	) `
	-LogPath (Join-Path $logsRoot "SchedulerManager-publish.log")

$launcherPlatform = switch ($RuntimeIdentifier) {
	"win-x86" { "Win32" }
	"win-x64" { "x64" }
	"win-arm64" { "ARM64" }
}
$msbuildPath = Get-MSBuildPath -RuntimeIdentifier $RuntimeIdentifier
$commands += Invoke-LoggedCommand `
	-Executable $msbuildPath `
	-Arguments @(
		$winGetLauncherProject,
		"/t:Build",
		"/p:Configuration=Release",
		"/p:Platform=$launcherPlatform",
		"/p:OutDir=$winGetLauncherOutput\",
		"/p:IntDir=$winGetLauncherOutput\obj\",
		"/m:1",
		"/v:minimal",
		"/bl:$logsRoot/SylphyHorn.WinGetLauncher-build.binlog"
	) `
	-LogPath (Join-Path $logsRoot "SylphyHorn.WinGetLauncher-build.log")
$commands += Invoke-LoggedCommand `
	-Executable $msbuildPath `
	-Arguments @(
		$cliWinGetLauncherProject,
		"/t:Build",
		"/p:Configuration=Release",
		"/p:Platform=$launcherPlatform",
		"/p:OutDir=$cliWinGetLauncherOutput\",
		"/p:IntDir=$cliWinGetLauncherOutput\obj\",
		"/m:1",
		"/v:minimal",
		"/bl:$logsRoot/sylphyhorn-cli.WinGetLauncher-build.binlog"
	) `
	-LogPath (Join-Path $logsRoot "sylphyhorn-cli.WinGetLauncher-build.log")
$commands += Invoke-LoggedCommand `
	-Executable $msbuildPath `
	-Arguments @(
		$winGetAliasProbeProject,
		"/t:Build",
		"/p:Configuration=Release",
		"/p:Platform=$launcherPlatform",
		"/p:OutDir=$winGetAliasProbeOutput\",
		"/p:IntDir=$winGetAliasProbeOutput\obj\",
		"/m:1",
		"/v:minimal",
		"/bl:$logsRoot/AliasTestProbe-build.binlog"
	) `
	-LogPath (Join-Path $logsRoot "AliasTestProbe-build.log")

$provenance = @{}
foreach ($publishSource in @(
	@($applicationPublish, "publish:SylphyHorn"),
	@($schedulerPublish, "publish:SchedulerManager"),
	@($cliPublish, "publish:sylphyhorn-cli")
)) {
	$sourceRoot = Get-NormalizedPath $publishSource[0]
	foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
		$relativePath = (Get-RelativeChildPath $sourceRoot $file.FullName).
			Replace("\", "/")
		if ($file.Extension -in @(".pdb", ".xml")) {
			continue
		}
		if ($relativePath -in @(
			"SylphyHorn.dll.config",
			"SchedulerManager.dll.config")) {
			continue
		}
		Add-StagingFile `
			-SourcePath $file.FullName `
			-RelativePath $relativePath `
			-Origin $publishSource[1] `
			-StagingRoot $wrapperRoot `
			-Provenance $provenance
	}
}

$winGetLauncherPath = Join-Path `
	$winGetLauncherOutput `
	"SylphyHorn.WinGetLauncher.exe"
Assert-Condition (Test-Path -LiteralPath $winGetLauncherPath -PathType Leaf) `
	"WinGet launcher build output is missing: $winGetLauncherPath"
Add-StagingFile `
	-SourcePath $winGetLauncherPath `
	-RelativePath "SylphyHorn.WinGetLauncher.exe" `
	-Origin "build:SylphyHorn.WinGetLauncher" `
	-StagingRoot $wrapperRoot `
	-Provenance $provenance

$cliWinGetLauncherPath = Join-Path `
	$cliWinGetLauncherOutput `
	"sylphyhorn-cli.WinGetLauncher.exe"
Assert-Condition (Test-Path -LiteralPath $cliWinGetLauncherPath -PathType Leaf) `
	"CLI WinGet launcher build output is missing: $cliWinGetLauncherPath"
Add-StagingFile `
	-SourcePath $cliWinGetLauncherPath `
	-RelativePath "sylphyhorn-cli.WinGetLauncher.exe" `
	-Origin "build:sylphyhorn-cli.WinGetLauncher" `
	-StagingRoot $wrapperRoot `
	-Provenance $provenance

Add-StagingFile `
	-SourcePath $agentSkillPath `
	-RelativePath "skills/sylphyhorn/SKILL.md" `
	-Origin "source:skills/sylphyhorn/SKILL.md" `
	-StagingRoot $wrapperRoot `
	-Provenance $provenance

$dependencyInventory = Get-LockDependencyInventory `
	-LockPaths @($applicationLock, $schedulerLock, $cliLock)
$releaseDependencyInventory = @(
	$dependencyInventory |
		Where-Object {
			@($_.Targets | Where-Object {
				$_ -match '^net10\.0-windows10\.0\.26100(?:/|$)'
			}).Count -gt 0
		}
)
Assert-Condition ($releaseDependencyInventory.Count -gt 0) `
	"No net10 release dependencies were found in the approved lock files."
$packageRoot = @($applicationAssets.packageFolders.PSObject.Properties.Name)[0]
Assert-Condition (-not [string]::IsNullOrWhiteSpace($packageRoot)) `
	"NuGet global package root cannot be resolved from project.assets.json."
Add-LicensePayload `
	-RepositoryRoot $repositoryRoot `
	-PackageRoot $packageRoot `
	-Dependencies $releaseDependencyInventory `
	-StagingRoot $wrapperRoot `
	-Provenance $provenance

foreach ($requiredFile in @(
	"SylphyHorn.exe",
	"SylphyHorn.dll",
	"SylphyHorn.deps.json",
	"SylphyHorn.runtimeconfig.json",
	"SylphyHorn.WinGetLauncher.exe",
	"sylphyhorn-cli.WinGetLauncher.exe",
	"SchedulerManager.exe",
	"SchedulerManager.dll",
	"SchedulerManager.deps.json",
	"SchedulerManager.runtimeconfig.json",
	"sylphyhorn-cli.exe",
	"sylphyhorn-cli.dll",
	"sylphyhorn-cli.deps.json",
	"sylphyhorn-cli.runtimeconfig.json",
	"skills/sylphyhorn/SKILL.md"
)) {
	Assert-Condition `
		(Test-Path -LiteralPath (Join-Path $wrapperRoot $requiredFile) -PathType Leaf) `
		"Required release file is missing: $requiredFile"
}

Assert-DenyList $wrapperRoot
Assert-AllowList $wrapperRoot
Assert-Condition `
	(@(Get-ChildItem -LiteralPath $wrapperRoot -Recurse -Filter "*.pdb" -File).Count -eq 0) `
	"Staging contains PDB files."
Assert-Condition `
	(@(Get-ChildItem -LiteralPath $wrapperRoot -Recurse -Filter "*.xml" -File).Count -eq 0) `
	"Staging contains XML documentation files."

$peAssets = Get-AndAssertPeAssets `
	-PayloadRoot $wrapperRoot `
	-RuntimeIdentifier $RuntimeIdentifier
$nativeAssets = @($peAssets | Where-Object { -not $_.IsManaged })

foreach ($nativeEntryPoint in @(
	"SylphyHorn.exe",
	"SchedulerManager.exe",
	"sylphyhorn-cli.exe",
	"sylphyhorn-cli.WinGetLauncher.exe",
	"SylphyHorn.WinGetLauncher.exe")) {
	$peInfo = Get-PeInformation (Join-Path $wrapperRoot $nativeEntryPoint)
	Assert-Condition (-not $peInfo.IsManaged) `
		"$nativeEntryPoint must be a native executable."
	Assert-Condition `
		($peInfo.Machine -ceq $ExpectedMachines[$RuntimeIdentifier]) `
		"$nativeEntryPoint PE machine mismatch."
}

$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$canExecuteTarget = $RuntimeIdentifier -ne "win-arm64" -or
	$hostArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64
if ($canExecuteTarget) {
	$commands += Invoke-LoggedCommand `
		-Executable (Join-Path $wrapperRoot "sylphyhorn-cli.exe") `
		-Arguments @("--help") `
		-LogPath (Join-Path $logsRoot "sylphyhorn-cli-help.log")

	$cliWinGetAliasTestResult = & $cliWinGetAliasTest `
		-PackageRoot $wrapperRoot `
		-RequireSymbolicLink:($env:GITHUB_ACTIONS -ceq "true")
	Assert-Condition ($LASTEXITCODE -eq 0) `
		"CLI WinGet portable alias integration test failed."

	$cliWorkingDirectoryTestResult = & $winGetAliasTest `
		-LauncherPath (Join-Path $wrapperRoot "sylphyhorn-cli.WinGetLauncher.exe") `
		-ProbePath (Join-Path $winGetAliasProbeOutput "AliasTestProbe.exe") `
		-Cli -RequireSymbolicLink:($env:GITHUB_ACTIONS -ceq "true")
	Assert-Condition ($LASTEXITCODE -eq 0) `
		"CLI launcher working-directory test failed."

	$winGetAliasTestResult = & $winGetAliasTest `
		-LauncherPath (Join-Path $wrapperRoot "SylphyHorn.WinGetLauncher.exe") `
		-ProbePath (Join-Path $winGetAliasProbeOutput "AliasTestProbe.exe") `
		-RequireSymbolicLink:($env:GITHUB_ACTIONS -ceq "true")
	Assert-Condition ($LASTEXITCODE -eq 0) `
		"WinGet portable alias integration test failed."
}
else {
	$cliWorkingDirectoryTestResult = [pscustomobject]@{
		Status = "NotRun"
		Reason = "HostArchitectureCannotExecuteArm64"
	}
	$cliWinGetAliasTestResult = [pscustomobject]@{
		Status = "NotRun"
		Reason = "HostArchitectureCannotExecuteArm64"
	}
	$winGetAliasTestResult = [pscustomobject]@{
		Status = "NotRun"
		Reason = "HostArchitectureCannotExecuteArm64"
	}
}

foreach ($metroEntry in $ExpectedMetroHashes.GetEnumerator()) {
	$stagedMetro = Join-Path $wrapperRoot $metroEntry.Key
	Assert-Condition (Test-Path -LiteralPath $stagedMetro -PathType Leaf) `
		"Metro binary was not included in staging: $($metroEntry.Key)"
	Assert-Condition ((Get-Sha256 $stagedMetro) -ceq $metroEntry.Value) `
		"Staged Metro binary hash mismatch: $($metroEntry.Key)"
}

$payloadManifest = Get-PayloadManifest `
	-WrapperRoot $wrapperRoot `
	-Provenance $provenance
$expandedLength = (
	$payloadManifest |
	Measure-Object -Property Length -Sum).Sum

$depsJson = Get-Content `
	-LiteralPath (Join-Path $wrapperRoot "SylphyHorn.deps.json") `
	-Raw |
	ConvertFrom-Json
$runtimePackVersions = @(
	$depsJson.libraries.PSObject.Properties.Name |
	Where-Object {
		$_ -match "^runtimepack\.Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\."
	} |
	Sort-Object
)
Assert-Condition ($runtimePackVersions.Count -ge 2) `
	"SCD runtime pack closure is incomplete."

$cliDepsJson = Get-Content `
	-LiteralPath (Join-Path $wrapperRoot "sylphyhorn-cli.deps.json") `
	-Raw |
	ConvertFrom-Json
$cliRuntimePacks = @(
	$cliDepsJson.libraries.PSObject.Properties.Name |
	Where-Object { $_ -match "^runtimepack\." }
)
Assert-Condition ($cliRuntimePacks.Count -gt 0) `
	"CLI SCD runtime pack closure is incomplete."
foreach ($runtimePack in $cliRuntimePacks) {
	Assert-Condition ($runtimePack -cin $depsJson.libraries.PSObject.Properties.Name) `
		"CLI runtime pack does not match the shared GUI runtime: $runtimePack"
}

$parentCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
Assert-Condition ($LASTEXITCODE -eq 0) "Cannot read parent commit."
$parentBranchOutput = @(& git -C $repositoryRoot branch --show-current)
Assert-Condition ($LASTEXITCODE -eq 0) "Cannot read parent branch."
$parentBranch = if ($parentBranchOutput.Count -eq 0) {
	$null
}
else {
	$parentBranchOutput[0].Trim()
}
$virtualDesktopGitlink = (
	& git -C $repositoryRoot ls-tree HEAD source/VirtualDesktop).
	Split("`t")[0].
	Split(" ")[2]
Assert-Condition ($LASTEXITCODE -eq 0) "Cannot read VirtualDesktop gitlink."

$manifestDocument = [ordered]@{
	SchemaVersion = 3
	Release = [ordered]@{
		Version             = $Version
		Tag                 = $releaseTag
		ZipFilename         = $zipName
		RuntimeIdentifier   = $RuntimeIdentifier
		Architecture        = $architecture
		DeploymentMode      = $DeploymentMode
		SelfContained       = $true
		TargetFramework     = $TargetFramework
		Configuration       = $Configuration
		SdkVersion          = $resolvedSdkVersion
		RuntimePacks        = $runtimePackVersions
	}
	Source = [ordered]@{
		Branch                  = $parentBranch
		ParentCommit            = $parentCommit
		SourceTreeMode          = $SourceTreeMode
		SourceTreeState         = $sourceTreeState
		ArtifactEligibility     = $artifactEligibility
		PublicationDecision     = $publicationDecision
		WorkingTreeStatus       = $workingTreeStatus
		TrackedDiffSha256       = Get-TextSha256 $trackedDiff
		BuildInputs             = $buildInputs
		VirtualDesktopGitlink   = $virtualDesktopGitlink
		MetroSourceCommits      = $MetroSourceCommits
		MetroBinaries           = $metroManifest
	}
	LockFiles = @(
		[ordered]@{
			Path   = "source/SylphyHorn/packages.lock.json"
			Sha256 = Get-Sha256 $applicationLock
		},
		[ordered]@{
			Path   = "source/SylphyHorn.SchedulerManager/packages.lock.json"
			Sha256 = Get-Sha256 $schedulerLock
		},
		[ordered]@{
			Path   = "source/SylphyHorn.Cli/packages.lock.json"
			Sha256 = Get-Sha256 $cliLock
		}
	)
	Dependencies = $dependencyInventory
	ReleaseDependencies = $releaseDependencyInventory
	Commands = $commands
	PeAssets = $peAssets
	NativeAssets = $nativeAssets
	Payload = [ordered]@{
		FileCount = $payloadManifest.Count
		TotalLength = $expandedLength
		Files = $payloadManifest
	}
}

$manifestPath = Join-Path `
	$evidenceRoot `
	("SylphyHornPlus-v{0}-{1}.manifest.json" -f $Version, $architecture)
$manifestDocument |
	ConvertTo-Json -Depth 100 |
	Set-Content -LiteralPath $manifestPath -Encoding utf8

$workZipPath = Join-Path $archiveRoot $zipName
Assert-Condition (-not (Test-Path -LiteralPath $workZipPath)) `
	"Work ZIP already exists: $workZipPath"
[System.IO.Compression.ZipFile]::CreateFromDirectory(
	$stagingRoot,
	$workZipPath,
	[System.IO.Compression.CompressionLevel]::Optimal,
	$false)

Expand-Archive `
	-LiteralPath $workZipPath `
	-DestinationPath $extractRoot
$extractedWrapper = Join-Path $extractRoot "SylphyHorn"
Assert-Condition (Test-Path -LiteralPath $extractedWrapper -PathType Container) `
	"ZIP does not contain the SylphyHorn wrapper."
$zipRootEntries = @(Get-ChildItem -LiteralPath $extractRoot -Force)
Assert-Condition `
	($zipRootEntries.Count -eq 1 -and
		$zipRootEntries[0].Name -ceq "SylphyHorn" -and
		$zipRootEntries[0].PSIsContainer) `
	"ZIP root must contain only the SylphyHorn directory."

Assert-DenyList $extractedWrapper
Assert-AllowList $extractedWrapper
Assert-Condition `
	(@(Get-ChildItem -LiteralPath $extractedWrapper -Recurse -Filter "*.pdb" -File).Count -eq 0) `
	"Extracted ZIP contains PDB files."
Assert-Condition `
	(@(Get-ChildItem -LiteralPath $extractedWrapper -Recurse -Filter "*.xml" -File).Count -eq 0) `
	"Extracted ZIP contains XML documentation files."

$extractedPeAssets = Get-AndAssertPeAssets `
	-PayloadRoot $extractedWrapper `
	-RuntimeIdentifier $RuntimeIdentifier
Assert-Condition ($extractedPeAssets.Count -eq $peAssets.Count) `
	"Extracted ZIP PE inventory count does not match staging."
for ($peIndex = 0; $peIndex -lt $peAssets.Count; $peIndex++) {
	Assert-Condition `
		($extractedPeAssets[$peIndex].Path -ceq $peAssets[$peIndex].Path -and
			$extractedPeAssets[$peIndex].Classification -ceq $peAssets[$peIndex].Classification -and
			$extractedPeAssets[$peIndex].Machine -ceq $peAssets[$peIndex].Machine -and
			$extractedPeAssets[$peIndex].IsManaged -eq $peAssets[$peIndex].IsManaged -and
			$extractedPeAssets[$peIndex].IsIlOnly -eq $peAssets[$peIndex].IsIlOnly -and
			$extractedPeAssets[$peIndex].CorFlags -ceq $peAssets[$peIndex].CorFlags -and
			$extractedPeAssets[$peIndex].Length -eq $peAssets[$peIndex].Length -and
			$extractedPeAssets[$peIndex].Sha256 -ceq $peAssets[$peIndex].Sha256) `
		"Extracted ZIP PE inventory mismatch at index $peIndex."
}

$extractedFiles = @(
	Get-ChildItem -LiteralPath $extractedWrapper -Recurse -File |
		Sort-Object FullName |
		ForEach-Object {
			[pscustomobject]@{
				Path = "SylphyHorn/" + (
					Get-RelativeChildPath $extractedWrapper $_.FullName).
					Replace("\", "/")
				Length = $_.Length
				Sha256 = Get-Sha256 $_.FullName
			}
		}
)
Assert-Condition `
	($extractedFiles.Count -eq $payloadManifest.Count) `
	"Extracted ZIP file count does not match the payload manifest."
for ($index = 0; $index -lt $payloadManifest.Count; $index++) {
	Assert-Condition `
		($extractedFiles[$index].Path -ceq $payloadManifest[$index].Path -and
			$extractedFiles[$index].Length -eq $payloadManifest[$index].Length -and
			$extractedFiles[$index].Sha256 -ceq $payloadManifest[$index].Sha256) `
		"Extracted ZIP manifest mismatch at index $index."
}

Copy-Item -LiteralPath $workZipPath -Destination $finalZipPath
Assert-Condition `
	((Get-Sha256 $workZipPath) -ceq (Get-Sha256 $finalZipPath)) `
	"Final ZIP hash does not match the validated work ZIP."
}
catch {
	$mainError = $_
}
finally {
	try {
		if (Test-Path -LiteralPath $buildRoot) {
			$resolvedBuildRoot = Get-NormalizedPath $buildRoot
			Assert-Condition `
				((Test-IsSameOrChildPath $resolvedBuildRoot $temporaryRoot) -and
					-not $resolvedBuildRoot.Equals(
						$temporaryRoot,
						[System.StringComparison]::OrdinalIgnoreCase)) `
				"Refusing to remove a build root outside the temporary directory."
			Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force
		}
	}
	catch {
		$cleanupError = $_
	}
}

if ($null -ne $mainError) {
	if ($null -ne $cleanupError) {
		throw [AggregateException]::new(
			"Portable release build failed and temporary build-root cleanup also failed. The first inner exception is the build failure.",
			[Exception[]] @($mainError.Exception, $cleanupError.Exception))
	}
	throw $mainError
}
if ($null -ne $cleanupError) {
	throw [InvalidOperationException]::new(
		"Portable release build completed, but temporary build-root cleanup failed. No success result was published.",
		$cleanupError.Exception)
}

$zipItem = Get-Item -LiteralPath $finalZipPath
$result = [ordered]@{
	Status              = "Completed"
	TechnicalResult     = "Pass"
	PublicRelease       = "Hold"
	SourceTreeMode      = $SourceTreeMode
	SourceTreeState     = $sourceTreeState
	ArtifactEligibility = $artifactEligibility
	PublicationDecision = $publicationDecision
	Version             = $Version
	Tag                 = $releaseTag
	RuntimeIdentifier   = $RuntimeIdentifier
	DeploymentMode      = $DeploymentMode
	SelfContained       = $true
	SdkVersion          = $resolvedSdkVersion
	TargetFramework     = $TargetFramework
	Configuration       = $Configuration
	WorkRoot            = $workRoot
	ZipPath             = $finalZipPath
	ZipLength           = $zipItem.Length
	ZipSha256           = Get-Sha256 $finalZipPath
	ManifestPath        = $manifestPath
	ManifestSha256      = Get-Sha256 $manifestPath
	FileCount           = $payloadManifest.Count
	ExpandedLength      = $expandedLength
	SylphyHornMachine   = (
		Get-PeInformation (Join-Path $wrapperRoot "SylphyHorn.exe")).Machine
	SchedulerMachine    = (
		Get-PeInformation (Join-Path $wrapperRoot "SchedulerManager.exe")).Machine
	WinGetLauncherMachine = (
		Get-PeInformation (
			Join-Path $wrapperRoot "SylphyHorn.WinGetLauncher.exe")).Machine
	CliWinGetLauncherMachine = (
		Get-PeInformation (
			Join-Path $wrapperRoot "sylphyhorn-cli.WinGetLauncher.exe")).Machine
	CliWinGetAliasTestStatus = $cliWinGetAliasTestResult.Status
	CliWinGetAliasTestReason = $cliWinGetAliasTestResult.Reason
	CliWorkingDirectoryTestStatus = $cliWorkingDirectoryTestResult.Status
	CliWorkingDirectoryTestReason = $cliWorkingDirectoryTestResult.Reason
	WinGetAliasTestStatus = $winGetAliasTestResult.Status
	WinGetAliasTestReason = $winGetAliasTestResult.Reason
	PdbCount            = 0
	XmlDocumentationCount = 0
}
$resultPath = Join-Path `
	$evidenceRoot `
	("SylphyHornPlus-v{0}-{1}.result.json" -f $Version, $architecture)
$result |
	ConvertTo-Json -Depth 10 |
	Set-Content -LiteralPath $resultPath -Encoding utf8

$result
