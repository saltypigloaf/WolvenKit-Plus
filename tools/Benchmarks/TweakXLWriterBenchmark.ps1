# Run from PowerShell or edit these settings for an IDE invocation.
# This only compiles a standalone measurement harness; it never changes Git refs or production code.
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$BenchmarkParent = [System.IO.Path]::GetTempPath(),
    [string]$ResultsDirectory = '',
    [string]$ExpectedCommit = '4dd33be7802e805b71ad9bd4e4150515eac9e520',
    [string]$DirectWriteCommit = '3db99b10b079ba507b08ad664b3e1b0c7a5e4606'
)
$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$head = & git -C $RepositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCommit) { throw 'Checkout is not the expected benchmark commit.' }
$branch = & git -C $RepositoryRoot branch --show-current
$helperRelative = 'WolvenKit.App/Helpers/TweakXLAtomicFileWriter.cs'
$serviceRelative = 'WolvenKit.App/Services/TweakXLBulkExportService.cs'
& git -C $RepositoryRoot diff --quiet HEAD -- $helperRelative $serviceRelative
if ($LASTEXITCODE -ne 0) { throw 'Writer sources differ from the expected commit.' }
$helperPath = Join-Path $RepositoryRoot $helperRelative
$servicePath = Join-Path $RepositoryRoot $serviceRelative
$helperHash = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()
$serviceHash = (Get-FileHash -LiteralPath $servicePath -Algorithm SHA256).Hash.ToLowerInvariant()
$oldLines = @(& git -C $RepositoryRoot show "${DirectWriteCommit}:$serviceRelative")
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the earlier direct-write implementation.' }
$oldService = $oldLines -join "`n"
$currentService = [System.IO.File]::ReadAllText($servicePath)
function Get-WriterMethod([string]$source) {
    $matches = [regex]::Matches($source, '(?s)    private static (?:async )?Task WriteRecordYamlAsync\(.*?(?=    private sealed class ExportPlan)')
    if ($matches.Count -ne 1) { throw 'Writer method extraction is ambiguous; review the harness before running.' }
    return $matches[0].Value.Replace('private static', 'internal static').Replace('WriteRecordYamlAsync(', 'WriteAsync(')
}
$directMethod = Get-WriterMethod $oldService
$atomicMethod = Get-WriterMethod $currentService
if ($directMethod -notmatch 'FileMode.Create : FileMode.CreateNew' -or $directMethod -match 'TweakXLAtomicFileWriter') { throw 'Unexpected direct-write source.' }
if ($atomicMethod -notmatch 'TweakXLAtomicFileWriter.WriteAsync' -or $atomicMethod -notmatch 'FlushAsync\(token\)') { throw 'Unexpected atomic wrapper source.' }

# Every invocation owns a fresh directory; no existing file is deleted or reused.
$parent = (Resolve-Path -LiteralPath $BenchmarkParent).Path
$runRoot = [System.IO.Path]::GetFullPath((Join-Path $parent ('wolvenkit-tweakxl-writer-benchmark-' + [guid]::NewGuid().ToString('N'))))
$parentPrefix = $parent.TrimEnd('\') + '\'
if (-not $runRoot.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $runRoot)) { throw 'Unsafe or existing benchmark directory.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
[System.IO.File]::WriteAllText((Join-Path $runRoot 'BENCHMARK-OWNED.txt'), "Synthetic files only. Created for TweakXL writer measurement. Retained for inspection.`n", $utf8)
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) { $ResultsDirectory = Join-Path $runRoot 'results' }
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'ResultsDirectory must be new to avoid overwriting prior evidence.' }
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
$buildRoot = Join-Path $runRoot 'harness'
New-Item -ItemType Directory -Path $buildRoot | Out-Null
Copy-Item -LiteralPath $helperPath -Destination (Join-Path $buildRoot 'TweakXLAtomicFileWriter.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TweakXLWriterBenchmark.cs') -Destination (Join-Path $buildRoot 'Program.cs')
$writers = @"
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WolvenKit.App.Helpers;
internal static class DirectWriter
{
$directMethod
}
internal static class AtomicWriter
{
$atomicMethod
}
"@
[System.IO.File]::WriteAllText((Join-Path $buildRoot 'ExtractedWriters.cs'), $writers, $utf8)
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Optimize>true</Optimize>
    <TieredCompilation>false</TieredCompilation>
    <UseSharedCompilation>false</UseSharedCompilation>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
  </PropertyGroup>
</Project>
'@
[System.IO.File]::WriteAllText((Join-Path $buildRoot 'WriterBenchmark.csproj'), $project, $utf8)
[System.IO.File]::WriteAllText((Join-Path $buildRoot 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read .NET SDK.' }
$environment = [ordered]@{
    CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); Repository=$RepositoryRoot
    Commit=$head; Branch=$branch; DirectWriteCommit=$DirectWriteCommit
    HelperSource=$helperPath; HelperSha256=$helperHash; ServiceSha256=$serviceHash
    HarnessSha256=(Get-FileHash -LiteralPath (Join-Path $buildRoot 'Program.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExtractedWritersSha256=(Get-FileHash -LiteralPath (Join-Path $buildRoot 'ExtractedWriters.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    Sdk=$sdk; BenchmarkRoot=$runRoot; ResultsDirectory=$ResultsDirectory
    Volume=@(Get-Volume -DriveLetter ([System.IO.Path]::GetPathRoot($runRoot).Substring(0,1)) | Select-Object DriveLetter,FileSystemType,DriveType,Size,SizeRemaining)
    Disks=@(Get-CimInstance Win32_DiskDrive | Select-Object Model,MediaType,Size)
    Cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
}
$environment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'environment.json') -Encoding utf8
Write-Output "Benchmark data: $runRoot"
Write-Output "Results: $ResultsDirectory"
& dotnet build (Join-Path $buildRoot 'WriterBenchmark.csproj') --configuration Release --disable-build-servers -p:UseSharedCompilation=false -m:1 -nr:false *> (Join-Path $ResultsDirectory 'harness-build.log')
if ($LASTEXITCODE -ne 0) { throw "Harness build failed; see $ResultsDirectory\harness-build.log" }
& dotnet (Join-Path $buildRoot 'bin\Release\net10.0\WriterBenchmark.dll') $runRoot $ResultsDirectory *> (Join-Path $ResultsDirectory 'benchmark.log')
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; see $ResultsDirectory\benchmark.log" }
if ((& git -C $RepositoryRoot rev-parse HEAD) -ne $head) { throw 'HEAD changed during measurement.' }
if ((Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $helperHash -or (Get-FileHash -LiteralPath $servicePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $serviceHash) { throw 'Writer sources changed during measurement.' }
Get-Content -LiteralPath (Join-Path $ResultsDirectory 'benchmark.log')
Write-Output 'Completed; all generated data is retained in the dedicated benchmark directory. No commits or pushes performed.'
