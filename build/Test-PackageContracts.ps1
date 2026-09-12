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
	Write-Output 'Pinned/default, identical-copy and legacy-rejection package contracts passed.'
}
finally {
	if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse }
}
