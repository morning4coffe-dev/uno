[CmdletBinding()]
param(
	[string]$DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source,
	[switch]$AppleIcuOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appleUIKitProject = Join-Path $root 'src\Uno.UI.Runtime.Skia.AppleUIKit\Uno.UI.Runtime.Skia.AppleUIKit.csproj'
$nonShippingVersion = '0.0.0-nonshipping-apple-icu-wiring-contract'

function Get-AppleIcuEvaluation {
	param(
		[Parameter(Mandatory)][string]$Name,
		[string]$TargetFramework,
		[string]$ExpectedPlatform,
		[Parameter(Mandatory)][bool]$ExpectedIOS,
		[Parameter(Mandatory)][bool]$ExpectedTvOS,
		[Parameter(Mandatory)][bool]$ExpectedCatalyst,
		[Parameter(Mandatory)][string]$ExpectedPackage,
		[string]$Version
	)

	$arguments = @(
		'msbuild',
		$appleUIKitProject,
		'-nologo',
		'-getProperty:TargetFramework,TargetPlatformIdentifier,IsIOS,IsTvOS,IsCatalyst,UnoICUVersion',
		'-getItem:PackageReference'
	)
	if ($TargetFramework) {
		$arguments += "-p:TargetFramework=$TargetFramework"
	}
	if ($Version) {
		$arguments += "-p:UnoICUVersion=$Version"
	}

	$output = @(& $DotNetPath @arguments)
	if ($LASTEXITCODE -ne 0) {
		throw "Apple ICU evaluation '$Name' failed."
	}
	$evaluation = ($output -join [Environment]::NewLine) | ConvertFrom-Json
	$properties = $evaluation.Properties
	$icuPackages = @($evaluation.Items.PackageReference | Where-Object Identity -Like 'Uno.icu-*')
	$expectedIOSValue = $ExpectedIOS.ToString().ToLowerInvariant()
	$expectedTvOSValue = $ExpectedTvOS.ToString().ToLowerInvariant()
	$expectedCatalystValue = $ExpectedCatalyst.ToString().ToLowerInvariant()

	if ($properties.TargetFramework -cne $TargetFramework -or
		$properties.TargetPlatformIdentifier -cne $ExpectedPlatform -or
		$properties.IsIOS -cne $expectedIOSValue -or
		$properties.IsTvOS -cne $expectedTvOSValue -or
		$properties.IsCatalyst -cne $expectedCatalystValue) {
		throw "Apple ICU evaluation '$Name' did not retain its requested framework/platform identity."
	}
	if ($icuPackages.Count -ne 1 -or $icuPackages[0].Identity -cne $ExpectedPackage) {
		$actual = $icuPackages.Identity -join ', '
		throw "Apple ICU evaluation '$Name' expected only '$ExpectedPackage', found '$actual'."
	}
	if (-not $properties.UnoICUVersion -or $icuPackages[0].Version -cne $properties.UnoICUVersion) {
		throw "Apple ICU evaluation '$Name' did not use the shared UnoICUVersion."
	}
	if ($Version -and $properties.UnoICUVersion -cne $Version) {
		throw "Apple ICU evaluation '$Name' did not forward the explicit UnoICUVersion override."
	}

	[pscustomobject]@{
		Name = $Name
		TargetFramework = $properties.TargetFramework
		TargetPlatformIdentifier = $properties.TargetPlatformIdentifier
		IsIOS = $properties.IsIOS
		IsTvOS = $properties.IsTvOS
		IsCatalyst = $properties.IsCatalyst
		Package = $icuPackages[0].Identity
		Version = $icuPackages[0].Version
	}
}

$cases = @(
	@{ Name = 'neutral'; TargetFramework = ''; ExpectedPlatform = ''; ExpectedIOS = $false; ExpectedTvOS = $false; ExpectedCatalyst = $false; ExpectedPackage = 'Uno.icu-ios' },
	@{ Name = 'net9-ios'; TargetFramework = 'net9.0-ios18.0'; ExpectedPlatform = 'ios'; ExpectedIOS = $true; ExpectedTvOS = $false; ExpectedCatalyst = $false; ExpectedPackage = 'Uno.icu-ios' },
	@{ Name = 'net9-tvos'; TargetFramework = 'net9.0-tvos18.0'; ExpectedPlatform = 'tvos'; ExpectedIOS = $false; ExpectedTvOS = $true; ExpectedCatalyst = $false; ExpectedPackage = 'Uno.icu-tvos' },
	@{ Name = 'net10-ios'; TargetFramework = 'net10.0-ios26.0'; ExpectedPlatform = 'ios'; ExpectedIOS = $true; ExpectedTvOS = $false; ExpectedCatalyst = $false; ExpectedPackage = 'Uno.icu-ios' },
	@{ Name = 'net10-tvos'; TargetFramework = 'net10.0-tvos26.0'; ExpectedPlatform = 'tvos'; ExpectedIOS = $false; ExpectedTvOS = $true; ExpectedCatalyst = $false; ExpectedPackage = 'Uno.icu-tvos' },
	@{ Name = 'net10-maccatalyst'; TargetFramework = 'net10.0-maccatalyst26.0'; ExpectedPlatform = 'maccatalyst'; ExpectedIOS = $false; ExpectedTvOS = $false; ExpectedCatalyst = $true; ExpectedPackage = 'Uno.icu-ios' }
)
$defaultEvaluations = @($cases | ForEach-Object { Get-AppleIcuEvaluation @_ })
$defaultVersion = $defaultEvaluations[0].Version
if (@($defaultEvaluations | Where-Object Version -CNE $defaultVersion).Count) {
	throw 'Default Apple ICU evaluations did not share one UnoICUVersion.'
}
$overrideEvaluations = @($cases |
	Where-Object TargetFramework -Match '^net(?:9|10)\.0-(?:ios|tvos)' |
	ForEach-Object { Get-AppleIcuEvaluation @_ -Version $nonShippingVersion })
Write-Output ($defaultEvaluations + $overrideEvaluations | ConvertTo-Json -Depth 3)
Write-Output "Apple UIKit ICU package selection and shared-version forwarding passed (metadata evaluation only; '$nonShippingVersion' was not restored)."

if ($AppleIcuOnly) {
	return
}

$project = Join-Path $PSScriptRoot 'Uno.UI.Build.csproj'
$common = @('msbuild', $project, '-nologo', '-p:CombinedConfiguration=Release|AnyCPU')
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('uno-packer-contract-' + [Guid]::NewGuid().ToString('N'))

& $DotNetPath msbuild (Join-Path $PSScriptRoot 'PackageContracts.proj') -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Package declaration contracts failed.' }
$pinned = [string](& $DotNetPath @common -getProperty:NuGetBin)
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pinned -PathType Leaf)) {
	throw 'Restore Uno.UI.Build.csproj before running executable-identity contracts.'
}
& $DotNetPath @common -target:ValidateNuGetPacker -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'The restored pinned packer was rejected.' }
try {
	New-Item -ItemType Directory -Path $temporary | Out-Null
	$copy = Join-Path $temporary 'pinned packer copy.exe'
	Copy-Item -LiteralPath $pinned -Destination $copy
	& $DotNetPath @common -target:ValidateNuGetPacker "-p:NuGetBin=$copy" -v:minimal
	if ($LASTEXITCODE -ne 0) { throw 'A byte-identical pinned packer at a path with spaces was rejected.' }

	$legacy = Join-Path $PSScriptRoot 'external\nuget\NuGet.exe'
	$output = & $DotNetPath @common -target:ValidateNuGetPacker "-p:NuGetBin=$legacy" -v:minimal 2>&1
	$code = $LASTEXITCODE
	if ($code -eq 0 -or ($output -join "`n") -notmatch 'does not match the restored pinned NuGet.CommandLine tool') {
		throw 'The concrete legacy packer was not rejected for its executable identity.'
	}

	$prepared = Join-Path $temporary 'prepared-build'
	$preparedNuspecs = Join-Path $prepared 'nuget'
	New-Item -ItemType Directory -Path $preparedNuspecs | Out-Null
	$preparedProject = Join-Path $prepared 'Uno.UI.Build.csproj'
	Copy-Item -LiteralPath $project -Destination $preparedProject
	[xml]$buildDefinition = Get-Content -LiteralPath $project -Raw
	$sourceHashes = @{}
	foreach ($item in $buildDefinition.SelectNodes("/Project/Target[@Name='PrepareNuGetPackage']/ItemGroup/_NuspecFiles")) {
		$source = Join-Path $PSScriptRoot $item.Include
		$sourceHashes[$source] = (Get-FileHash -LiteralPath $source).Hash
		Copy-Item -LiteralPath $source -Destination (Join-Path $preparedNuspecs ([IO.Path]::GetFileName($source)))
	}
	$version = '6.7.0-contract.123'
	& $DotNetPath msbuild $preparedProject -nologo -target:PrepareNuGetPackage `
		'-p:CombinedConfiguration=Release|AnyCPU' "-p:NBGV_SemVer2=$version" `
		'-p:RepositoryUrl=https://github.com/unoplatform/uno' -v:minimal
	if ($LASTEXITCODE -ne 0) { throw 'Normal isolated package dependency preparation failed.' }
	$closure = @('msbuild', (Join-Path $PSScriptRoot 'PackageContracts.proj'), '-nologo',
		'-target:ValidateCorePackageClosure', "-p:CorePackageContractsRoot=$preparedNuspecs\",
		"-p:ExpectedLoggingVersion=$version", "-p:ExpectedFoundationVersion=$version", '-v:minimal')
	& $DotNetPath @closure
	if ($LASTEXITCODE -ne 0) { throw 'Normal preparation lost standalone core package closure or sibling-version stamping.' }
	foreach ($source in $sourceHashes.Keys) {
		if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHashes[$source]) {
			throw "Package preparation contracts modified owning source: $source"
		}
	}
	Write-Output 'Standalone Foundation/WinRT closure and normal sibling-version preparation passed.'
	Write-Output 'Pinned/default, identical-copy and legacy-rejection package contracts passed.'
}
finally {
	if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse }
}
