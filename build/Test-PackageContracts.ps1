param([string]$DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source)
$ErrorActionPreference = 'Stop'
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
