<#
.SYNOPSIS
    Builds Data Copier, runs its tests and packs the XrmToolBox Tool Library NuGet package into dist\.

.DESCRIPTION
    1. dotnet build MyscotekDataCopier.sln -c Release, then dotnet test (both must succeed).
    2. Checks that the built MyscotekDataCopier.dll has an AssemblyVersion and a FileVersion equal to
       the <version> of Myscotek.DataCopier.nuspec. The Tool Library compares the package version with
       the assembly version: a mismatch shows an update that never goes away.
    3. Uses tools\nuget.exe (or -NuGetExe); when there is none, downloads the latest nuget.exe from
       dist.nuget.org into tools\ (gitignored).
    4. Packs Myscotek.DataCopier.nuspec into dist\ (gitignored).
    5. Unzips the package into a temporary folder and fails unless it holds exactly the plugin
       (lib/net48/Plugins/MyscotekDataCopier.dll, identical to the build output), and NuGet's
       own metadata files: XrmToolBox provides every other assembly the tool uses.

    Publishing (nuget push, the Tool Library, the GitHub release) is done by hand afterwards.

.PARAMETER NuGetExe
    A nuget.exe to use instead of tools\nuget.exe; nothing is downloaded then.

.PARAMETER SkipTests
    Packs without running the tests. Not for a release.

.EXAMPLE
    .\build-package.ps1
#>
[CmdletBinding()]
param(
    [string]$NuGetExe,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution  = Join-Path $root 'MyscotekDataCopier.sln'
$nuspec    = Join-Path $root 'Myscotek.DataCopier.nuspec'
$dll       = Join-Path $root 'src\MyscotekDataCopier\bin\Release\MyscotekDataCopier.dll'
$distDir   = Join-Path $root 'dist'
$toolsDir  = Join-Path $root 'tools'
$packageId = 'Myscotek.DataCopier'

# The package content besides NuGet's own parts: the plugin only (the icon is served from GitHub via iconUrl).
$pluginEntry = 'lib/net48/Plugins/MyscotekDataCopier.dll'
$payload     = @($pluginEntry)

function Test-NuGetPart([string]$entry) {
    # What nuget pack adds to every package (and a signature, should the package ever be signed).
    return $entry -eq '[Content_Types].xml' -or $entry -eq '_rels/.rels' -or $entry -eq "$packageId.nuspec" -or
           $entry -like 'package/services/metadata/core-properties/*.psmdcp' -or $entry -eq '.signature.p7s'
}

# --- 1. Build and test ---------------------------------------------------------------------------
Write-Host '==> Building (Release)...' -ForegroundColor Cyan
& dotnet build $solution -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }

if ($SkipTests) {
    Write-Warning 'Tests skipped (-SkipTests): not for a release.'
}
else {
    Write-Host '==> Testing...' -ForegroundColor Cyan
    & dotnet test $solution -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit code $LASTEXITCODE)." }
}

# --- 2. One version everywhere -------------------------------------------------------------------
[xml]$spec = Get-Content -Path $nuspec -Raw
$version = [string]$spec.package.metadata.version
if ($version -notmatch '^\d+\.\d+\.\d+\.[1-9]\d*$') {
    # NuGet drops a trailing .0, which would no longer match the four-part assembly version.
    throw "The nuspec version '$version' must have four parts and a last part of 1 or more (1.YYYY.M.N)."
}
if (-not (Test-Path $dll)) { throw "Build output not found: $dll" }
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
$fileVersion = (Get-Item $dll).VersionInfo.FileVersion
Write-Host "==> Versions: nuspec $version, AssemblyVersion $assemblyVersion, FileVersion $fileVersion" -ForegroundColor Cyan
if ($assemblyVersion -ne $version -or $fileVersion -ne $version) {
    throw "Version mismatch: the nuspec says $version, the DLL has AssemblyVersion $assemblyVersion and FileVersion $fileVersion. " +
          'Change src\MyscotekDataCopier\Properties\AssemblyInfo.cs and Myscotek.DataCopier.nuspec together.'
}

# --- 3. nuget.exe --------------------------------------------------------------------------------
if ($NuGetExe) {
    if (-not (Test-Path $NuGetExe)) { throw "nuget.exe not found: $NuGetExe" }
    $nuget = (Resolve-Path $NuGetExe).Path
}
else {
    $nuget = Join-Path $toolsDir 'nuget.exe'
    if (-not (Test-Path $nuget)) {
        Write-Host '==> No tools\nuget.exe: downloading the latest from dist.nuget.org...' -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget -UseBasicParsing
    }
}
Write-Host "    nuget: $nuget"

# --- 4. Pack -------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$package = Join-Path $distDir "$packageId.$version.nupkg"
if (Test-Path $package) { Remove-Item -Force $package }
Write-Host "==> Packing $packageId $version..." -ForegroundColor Cyan
& $nuget pack $nuspec -OutputDirectory $distDir -BasePath $root -NonInteractive
if ($LASTEXITCODE -ne 0) { throw "nuget pack failed (exit code $LASTEXITCODE)." }
if (-not (Test-Path $package)) { throw "Expected package not found: $package" }

# --- 5. Check what is inside ---------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inspect = Join-Path $distDir ('.verify-' + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($package, $inspect)
    $entries = @(Get-ChildItem -LiteralPath $inspect -Recurse -File |
        ForEach-Object { $_.FullName.Substring($inspect.Length + 1).Replace('\', '/') } |
        Sort-Object)

    Write-Host '==> Package contents:' -ForegroundColor Cyan
    foreach ($entry in $entries) {
        $length = (Get-Item -LiteralPath (Join-Path $inspect $entry)).Length
        Write-Host ('    {0,-70} {1,10:N0} bytes' -f $entry, $length)
    }

    $problems = @()
    foreach ($expected in $payload) {
        if ($entries -notcontains $expected) { $problems += "missing: $expected" }
    }
    foreach ($entry in $entries) {
        if ($payload -notcontains $entry -and -not (Test-NuGetPart $entry)) { $problems += "not allowed in the package: $entry" }
    }
    $packed = Join-Path $inspect $pluginEntry
    if ((Test-Path -LiteralPath $packed) -and (Get-FileHash -LiteralPath $packed).Hash -ne (Get-FileHash -LiteralPath $dll).Hash) {
        $problems += "$pluginEntry differs from the build output $dll"
    }
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
        throw 'Package check failed: the package must hold only the plugin DLL, and NuGet metadata.'
    }
}
finally {
    if (Test-Path -LiteralPath $inspect) { Remove-Item -LiteralPath $inspect -Recurse -Force }
}

Write-Host ''
Write-Host "==> OK: $package ($('{0:N0}' -f (Get-Item $package).Length) bytes)" -ForegroundColor Green
Write-Host '    Next, by hand: push the package to nuget.org, submit it to the XrmToolBox Tool Library and tag the release.'
