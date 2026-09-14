[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$DotNetPath,
	[Parameter(Mandatory)][string]$RestoreConfigFile,
	[Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$RestoreConfigFile = [IO.Path]::GetFullPath($RestoreConfigFile)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Preserve previous SourceLink test outputs; select a new directory." }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$expectedSdk = (Get-Content (Join-Path $root "global.json") -Raw | ConvertFrom-Json).sdk.version
$ciPin = Get-Content (Join-Path $PSScriptRoot "ci\net10\_global.json") -Raw | ConvertFrom-Json
if ($ciPin.sdk.version -cne $expectedSdk -or $ciPin.tools.dotnet -cne $expectedSdk -or
	$ciPin.sdk.rollForward -cne "disable" -or $ciPin.sdk.allowPrerelease) {
	throw "The CI SDK replacement must retain the same patched, non-rolling SDK pin."
}
$sdkVersion = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $expectedSdk) { throw "Expected the exact pinned SDK $expectedSdk." }
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot identify source revision." }
$remote = (& git -C $root remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^https://github\.com/[^/]+/[^/]+(?:\.git)?$') {
	throw "The regression requires the actual GitHub HTTPS origin."
}
$repository = $remote -replace '\.git$', ''
$logging = Join-Path $root "src\Uno.Foundation.Logging\Uno.Foundation.Logging.csproj"
$verifierProject = Join-Path $PSScriptRoot "SourceLinkVerification\SourceLinkVerification.csproj"
$framework = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)).Project.PropertyGroup.NetCurrent |
	Where-Object { $_ } | Select-Object -First 1
$verifier = Join-Path $PSScriptRoot "SourceLinkVerification\bin\Release\$framework\SourceLinkVerification.dll"
$commands = [Collections.Generic.List[object]]::new()
$cases = [Collections.Generic.List[object]]::new()
$defaults = [Collections.Generic.List[object]]::new()
$mappingCases = [Collections.Generic.List[object]]::new()
$contradictions = [Collections.Generic.List[object]]::new()
$configHash = (Get-FileHash -LiteralPath $RestoreConfigFile).Hash

function Invoke-SourceLinkCommand {
	param([string]$Name, [string[]]$Arguments, [switch]$ExpectFailure)
	$output = @(& $DotNetPath @Arguments 2>&1)
	$exit = $LASTEXITCODE
	$text = $output -join [Environment]::NewLine
	$log = Join-Path $OutputDirectory "$Name.log"
	[IO.File]::WriteAllText($log, $text)
	$commands.Add(@{ Name = $Name; Arguments = $Arguments; ExitCode = $exit; Log = $log })
	if (($exit -eq 0) -eq [bool]$ExpectFailure) { throw "Unexpected exit $exit for $Name; see $log" }
	return $text
}

function Get-PatchedTaskLoads {
	param([string]$BuildOutput, [string]$PackagesRoot)
	$loads = [Collections.Generic.List[object]]::new()
	$seen = @{}
	foreach ($match in [regex]::Matches($BuildOutput, 'Using "([^"]+)" task from assembly "([^"]*(?:Microsoft\.Build\.Tasks\.Git|Microsoft\.SourceLink\.(?:Common|GitHub))\.dll)"')) {
		$path = [IO.Path]::GetFullPath($match.Groups[2].Value)
		if ($seen.ContainsKey($path)) { continue }
		$seen[$path] = $true
		$id = [IO.Path]::GetFileNameWithoutExtension($path).ToLowerInvariant()
		$packageRoot = Join-Path $PackagesRoot "$id\$expectedSdk"
		$prefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
		if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
			throw "SourceLink task loaded outside the pinned package: $path"
		}
		$archive = Join-Path $packageRoot "$id.$expectedSdk.nupkg"
		$entryName = [IO.Path]::GetRelativePath($packageRoot, $path).Replace('\', '/')
		$zip = [IO.Compression.ZipFile]::OpenRead($archive)
		try {
			$entry = $zip.GetEntry($entryName)
			if ($null -eq $entry) { throw "Loaded task is absent from its restored archive: $path" }
			$stream = $entry.Open()
			try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
			finally { $stream.Dispose() }
		}
		finally { $zip.Dispose() }
		$hash = (Get-FileHash -LiteralPath $path).Hash
		if ($hash -cne $entryHash) { throw "Loaded task differs from its archive entry: $path" }
		$loads.Add(@{ Path = $path; Sha256 = $hash; FileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion;
			Package = $archive; PackageSha256 = (Get-FileHash -LiteralPath $archive).Hash; Entry = $entryName })
	}
	if ($loads.Count -ne 3) { throw "Expected actual Git, GitHub and Common task loads; found $($loads.Count)." }
	return @($loads)
}

try {
	$savedProvider = [Environment]::GetEnvironmentVariable("BUILD_REPOSITORY_PROVIDER", "Process")
	try {
		foreach ($provider in @("", "GitHub")) {
			[Environment]::SetEnvironmentVariable("BUILD_REPOSITORY_PROVIDER", $provider, "Process")
			$name = if ($provider) { "github-default" } else { "local-default" }
			$json = Invoke-SourceLinkCommand $name @("msbuild", $logging, "-nologo",
				"-p:Configuration=Release", "-p:TargetFramework=net9.0",
				"-getProperty:SourceLinkEnabled,EnableSourceLink,EmbedUntrackedSources,AllowedOutputExtensionsInPackageBuildOutputFolder")
			$effective = ($json | ConvertFrom-Json).Properties
			$expected = if ($provider) { "true" } else { "false" }
			if ($effective.SourceLinkEnabled -ne $expected -or $effective.EnableSourceLink -ne $expected) {
				throw "Unexpected SourceLink default for $name."
			}
			if (-not $provider -and ($effective.EmbedUntrackedSources -ne "false" -or
				$effective.AllowedOutputExtensionsInPackageBuildOutputFolder -match '\.pdb')) {
				throw "Ordinary local default leaks source/PDB packaging."
			}
			$defaults.Add(@{ Name = $name; Effective = $effective })
		}
	}
	finally {
		[Environment]::SetEnvironmentVariable("BUILD_REPOSITORY_PROVIDER", $savedProvider, "Process")
	}
	if (-not (Test-Path (Join-Path $PSScriptRoot "SourceLinkVerification\obj\project.assets.json"))) {
		Invoke-SourceLinkCommand "verifier-restore" @("restore", $verifierProject, "--configfile", $RestoreConfigFile,
			"-p:RestoreFallbackFolders=", "-p:NuGetAudit=true", "-p:NuGetAuditMode=all") | Out-Null
	}
	Invoke-SourceLinkCommand "verifier-build" @("build", $verifierProject, "-c", "Release", "--no-restore") | Out-Null
	foreach ($tfm in @("net9.0", "net10.0")) {
		foreach ($enabled in @($true, $false)) {
			$mode = if ($enabled) { "on" } else { "off" }
			$name = "$mode-$tfm"
			$directory = Join-Path $OutputDirectory $name
			$intermediate = Join-Path $directory "obj"
			$binary = Join-Path $directory "bin"
			$switch = if ($tfm -eq "net9.0") { "SourceLinkEnabled" } else { "EnableSourceLink" }
			$properties = @("-p:Configuration=Release", "-p:TargetFrameworks=$tfm", "-p:ContinuousIntegrationBuild=true",
				"-p:DebugType=portable", "-p:$switch=$($enabled.ToString().ToLowerInvariant())",
				"-p:RepositoryUrl=$repository", "-p:RepositoryCommit=$revision", "-p:GitRepositoryRemoteName=origin",
				"-p:GeneratePackageOnBuild=false", "-p:UnoNugetOverrideVersion=", "-p:NuGetAudit=true",
				"-p:NuGetAuditMode=all", "-p:RestoreFallbackFolders=",
				"-p:BaseIntermediateOutputPath=$intermediate\", "-p:OutputPath=$binary\")
			if ($enabled) { $properties += "-p:EmbedAllSources=true" }
			Invoke-SourceLinkCommand "$name-restore" (@("restore", $logging, "--configfile", $RestoreConfigFile) + $properties) | Out-Null
			$buildOutput = Invoke-SourceLinkCommand "$name-build" (@("build", $logging, "-c", "Release", "--no-restore", "-v:diagnostic") + $properties)
			$assetsPath = Join-Path $intermediate "project.assets.json"
			$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json -AsHashtable
			if ($assets.project.restore.restoreAuditProperties.enableAudit -ne "true" -or
				$assets.project.restore.restoreAuditProperties.auditMode -ne "all" -or
				-not $assets.project.restore.warningProperties.allWarningsAsErrors) {
				throw "Auditing/warnings-as-errors were weakened."
			}
			$packagesRoot = @($assets.packageFolders.Keys)
			if ($packagesRoot.Count -ne 1) { throw "The regression requires a single explicit package-cache root." }
			$sourceLinkPackages = @($assets.libraries.Keys | Where-Object { $_ -match '^Microsoft\.(SourceLink\.|Build\.Tasks\.Git/)' })
			if ($enabled) {
				foreach ($id in @("Microsoft.SourceLink.GitHub", "Microsoft.SourceLink.Common", "Microsoft.Build.Tasks.Git")) {
					if ("$id/$expectedSdk" -notin $sourceLinkPackages) { throw "Missing patched package $id/$expectedSdk." }
				}
				if (@($sourceLinkPackages | Where-Object { -not $_.EndsWith("/$expectedSdk") }).Count) { throw "Mixed SourceLink package versions." }
				$loads = @(Get-PatchedTaskLoads $buildOutput $packagesRoot[0])
			}
			else {
				if ($sourceLinkPackages.Count) { throw "SourceLink-off still has a SourceLink package override." }
				$loads = @()
			}
			$propertiesJson = Invoke-SourceLinkCommand "$name-properties" (@("msbuild", $logging, "-nologo", "-p:TargetFramework=$tfm",
				"-getProperty:SourceLinkEnabled,EnableSourceLink,EmbedAllSources,EmbedUntrackedSources,DebugType,AllowedOutputExtensionsInPackageBuildOutputFolder") + $properties)
			$effective = ($propertiesJson | ConvertFrom-Json).Properties
			if ($effective.SourceLinkEnabled -ne $enabled.ToString().ToLowerInvariant() -or
				$effective.EnableSourceLink -ne $enabled.ToString().ToLowerInvariant()) { throw "SourceLink switches disagree." }
			if (-not $enabled -and $effective.AllowedOutputExtensionsInPackageBuildOutputFolder -match '\.pdb') {
				throw "SourceLink-off unexpectedly includes PDB in normal package output."
			}
			$dll = Join-Path $binary "Uno.Foundation.Logging.dll"
			$pdb = Join-Path $binary "Uno.Foundation.Logging.pdb"
			$verification = Invoke-SourceLinkCommand "$name-pdb" @($verifier, $mode, $dll, $pdb, $root, $intermediate, $revision, $repository)
			$cases.Add(@{ Name = $name; Effective = $effective; Assets = $assetsPath; AssetsSha256 = (Get-FileHash $assetsPath).Hash;
				TaskLoads = $loads; TaskLoadScope = "Pinned package overrides only; off mode still uses SDK tasks.";
				Pdb = ($verification | ConvertFrom-Json) })
			if ($enabled) {
				$pdbHash = (Get-FileHash $pdb).Hash
				foreach ($kind in @("wrong-prefix", "wrong-specific-path", "ambiguous", "invalid-wildcard", "duplicate-key", "specific", "exact")) {
					$fixture = Join-Path $directory "$kind.pdb"
					Invoke-SourceLinkCommand "$name-$kind-fixture" @($verifier, "remap", $kind, $pdb, $fixture) | Out-Null
					$reject = $kind -notin @("specific", "exact")
					$result = Invoke-SourceLinkCommand "$name-$kind" @($verifier, "on", $dll, $fixture, $root, $intermediate, $revision, $repository) -ExpectFailure:$reject
					$expectedError = switch ($kind) {
						"wrong-prefix" { 'Unusable SourceLink mapping: no matching key' }
						"wrong-specific-path" { 'Unusable SourceLink mapping: resolved path' }
						"ambiguous" { 'Unusable SourceLink mapping: ambiguous case-insensitive key' }
						"invalid-wildcard" { 'Unusable SourceLink mapping: invalid wildcard pattern' }
						"duplicate-key" { 'Duplicate properties not allowed' }
					}
					if ($reject -and $result -notmatch $expectedError) { throw "Mapping fixture $kind failed for the wrong reason." }
					$mappingCases.Add(@{ Framework = $tfm; Kind = $kind; Rejected = $reject;
						OriginalPdbSha256 = $pdbHash; Fixture = $fixture; FixtureSha256 = (Get-FileHash $fixture).Hash;
						Verification = if ($reject) { $null } else { $result | ConvertFrom-Json } })
				}
				if ((Get-FileHash $pdb).Hash -cne $pdbHash) { throw "Mapping fixtures changed the original PDB." }
				$corrupt = Join-Path $directory "corrupt-checksum.pdb"
				Invoke-SourceLinkCommand "$name-corrupt-fixture" @($verifier, "corrupt-checksum", $pdb, $corrupt) | Out-Null
				$negative = Invoke-SourceLinkCommand "$name-corrupt-rejected" @($verifier, "on", $dll, $corrupt, $root, $intermediate, $revision, $repository) -ExpectFailure
				if ($negative -notmatch 'PDB source checksum/content mismatch') { throw "Corrupt PDB failed for the wrong reason." }
				$wrong = Invoke-SourceLinkCommand "$name-wrong-revision" @($verifier, "on", $dll, $pdb, $root, $intermediate, ("0" * 40), $repository) -ExpectFailure
				if ($wrong -notmatch 'SourceLink repository/revision mismatch') { throw "Wrong revision failed for the wrong reason." }
			}
		}
	}
	foreach ($tfm in @("net9.0", "net10.0")) {
		foreach ($target in @("GenerateSourceLinkFile", "_GenerateSourceLinkFile", "PrepareForBuild")) {
			foreach ($enabled in @($false, $true)) {
				$name = "contradiction-$tfm-$target-repo-$enabled"
				$directory = Join-Path $OutputDirectory $name
				$intermediate = Join-Path $directory "obj"
				New-Item -ItemType Directory -Path $intermediate | Out-Null
				$conflict = Invoke-SourceLinkCommand $name @(
					"msbuild", $logging, "-nologo", "-t:$target", "-p:Configuration=Release",
					"-p:TargetFramework=$tfm", "-p:SourceLinkEnabled=$($enabled.ToString().ToLowerInvariant())",
					"-p:EnableSourceLink=$((!$enabled).ToString().ToLowerInvariant())",
					"-p:IntermediateOutputPath=$intermediate\", "-p:BaseIntermediateOutputPath=$intermediate\",
					"-p:OutputPath=$directory\bin\") -ExpectFailure
				if ($conflict -notmatch 'SourceLinkEnabled and EnableSourceLink must agree') {
					throw "Contradictory switches failed for the wrong reason."
				}
				$files = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Filter "*.sourcelink.json")
				$contradictions.Add(@{ Framework = $tfm; Target = $target; RepositoryEnabled = $enabled;
					Directory = $directory; MappingFilesCreated = $files.Count })
				if ($files.Count) { throw "Contradictory switches wrote a SourceLink mapping before failure." }
			}
		}
	}
	Write-Output "PASS: real Logging net9/net10 SourceLink on/off, loaded patched tasks and complete PDB/source binding."
}
finally {
	if ((Get-FileHash -LiteralPath $RestoreConfigFile).Hash -cne $configHash) { throw "Restore configuration changed." }
	@{ Sdk = $sdkVersion; Revision = $revision; Repository = $repository; ConfigSha256 = $configHash;
		Commands = @($commands); Defaults = @($defaults); Cases = @($cases);
		MappingCases = @($mappingCases); Contradictions = @($contradictions) } |
		ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputDirectory "results.json")
}
