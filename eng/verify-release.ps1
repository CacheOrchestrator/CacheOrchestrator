#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Inventory', 'Build', 'Tests', 'Pack', 'Consumers', 'All')]
    [string] $Phase = 'All',
    [string] $Artifacts = '.artifacts/release',
    [ValidateSet('All', '8', '10')]
    [string] $ConsumerSdk = 'All'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathRooted($Artifacts)) { $Artifacts = Join-Path $repo $Artifacts }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
New-Item -ItemType Directory -Force $Artifacts | Out-Null
[xml] $inventory = Get-Content "$PSScriptRoot/ReleaseProjects.props" -Raw
$packages = @($inventory.Project.PropertyGroup.ReleasePackageNames.Split(';', [StringSplitOptions]::RemoveEmptyEntries))
$discovered = @(Get-ChildItem "$repo/src" -Filter *.csproj -Recurse | Where-Object {
    [xml] $projectXml = Get-Content $_.FullName -Raw
    -not $projectXml.SelectSingleNode('/Project/PropertyGroup/IsPackable[text()="false"]')
} | ForEach-Object BaseName)
if (Compare-Object ($packages | Sort-Object -Unique) ($discovered | Sort-Object -Unique)) {
    throw 'Packable source projects differ from eng/ReleaseProjects.props. Update the inventory and public API baseline together.'
}
if (($packages | Sort-Object -Unique).Count -ne $packages.Count) { throw 'Duplicate release package.' }
foreach ($name in $packages) {
    foreach ($baseline in 'Shipped', 'Unshipped') {
        if (-not (Test-Path "$PSScriptRoot/PublicApi/$name/PublicAPI.$baseline.txt")) { throw "Missing API baseline: $name/$baseline" }
    }
}
$testProjects = @(Get-ChildItem "$repo/tests" -Filter *.csproj -Recurse | Where-Object {
    [xml] $projectXml = Get-Content $_.FullName -Raw
    $projectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.NET.Test.Sdk"]')
} | Sort-Object Name)
Write-Output "Inventory: $($packages.Count) packages, $($testProjects.Count) test projects."

function Invoke-DotNet([string] $Log, [string[]] $Arguments) {
    Write-Output "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $Log -Tail 60 | Write-Output
        throw "dotnet failed ($LASTEXITCODE); log: $Log"
    }
}

Push-Location $repo
try {
    if ($Phase -in 'Build', 'All') {
        Invoke-DotNet "$Artifacts/build.log" @('build', 'CacheOrchestrator.slnx', '-c', 'Release')
    }
    if ($Phase -in 'Tests', 'All') {
        foreach ($project in $testProjects) {
            [xml] $definition = Get-Content $project.FullName -Raw
            $frameworkNode = $definition.SelectSingleNode('/Project/PropertyGroup/TargetFrameworks | /Project/PropertyGroup/TargetFramework')
            if (-not $frameworkNode) { throw "Test target frameworks must be explicit: $project" }
            foreach ($framework in $frameworkNode.InnerText.Split(';')) {
                $results = "$Artifacts/tests/$($project.BaseName)/$framework"
                New-Item -ItemType Directory -Force $results | Out-Null
                Invoke-DotNet "$results/run.log" @('test', $project.FullName, '-c', 'Release', '--no-build', '-f', $framework,
                    '--logger', 'trx;LogFileName=results.trx', '--results-directory', $results,
                    '--collect', 'XPlat Code Coverage', '--settings', "$PSScriptRoot/coverage.runsettings")
                [xml] $trx = Get-Content "$results/results.trx" -Raw
                $counters = $trx.TestRun.ResultSummary.Counters
                if ([int] $counters.total -eq 0 -or [int] $counters.executed -ne [int] $counters.total -or [int] $counters.failed -ne 0) {
                    throw "Tests were empty, skipped or failed: $($project.Name) $framework"
                }
                if (-not (Get-ChildItem $results -Filter coverage.cobertura.xml -Recurse)) { throw "Missing coverage: $results" }
                Write-Output "$($project.BaseName) $framework : $($counters.passed) passed"
            }
        }
    }
    $packageDirectory = "$Artifacts/packages"
    if ($Phase -in 'Pack', 'All') {
        New-Item -ItemType Directory -Force $packageDirectory | Out-Null
        # Never silently mix artifacts from different commits/versions.
        if (Get-ChildItem $packageDirectory -File) { throw "Package directory must be empty: $packageDirectory" }
        foreach ($name in $packages) {
            Invoke-DotNet "$Artifacts/pack-$name.log" @('pack', "src/$name/$name.csproj", '-c', 'Release', '--no-build', '-o', $packageDirectory)
        }
    }
    if ($Phase -in 'Pack', 'Consumers', 'All') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archives = @(Get-ChildItem $packageDirectory -Filter *.nupkg)
        if ($archives.Count -ne $packages.Count -or @(Get-ChildItem $packageDirectory -Filter *.snupkg).Count -ne $packages.Count) {
            throw 'Incorrect package or symbol count.'
        }
        $version = $null
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($archive in $archives) {
            $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
            try {
                $reader = [IO.StreamReader]::new(@($zip.Entries | Where-Object FullName -Like '*.nuspec')[0].Open())
                try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $id = $nuspec.package.metadata.id
                $currentVersion = $nuspec.package.metadata.version
                if ($id -notin $packages -or -not $seen.Add($id)) { throw "Unexpected or duplicate package: $id" }
                if ($version -and $version -ne $currentVersion) { throw 'Packages have inconsistent versions.' }
                $version = $currentVersion
                foreach ($asset in @('README.md', 'logo-small.png', "lib/net8.0/$id.dll", "lib/net10.0/$id.dll", "lib/net8.0/$id.xml", "lib/net10.0/$id.xml")) {
                    if (-not $zip.GetEntry($asset)) { throw "$id missing $asset" }
                }
                $analyzers = @($zip.Entries | Where-Object FullName -Like 'analyzers/*/*.dll')
                $expected = if ($id -eq 'CacheOrchestrator.AspNetCore') { 1 } else { 0 }
                if ($analyzers.Count -ne $expected -or ($expected -eq 1 -and -not $zip.GetEntry('analyzers/dotnet/cs/CacheOrchestrator.Analyzers.dll'))) {
                    throw "Incorrect analyzer placement: $id"
                }
            }
            finally { $zip.Dispose() }
        }
        Write-Output "Validated $($packages.Count) packages and symbol packages at $version."
        $version | Set-Content "$Artifacts/package-version.txt"
    }
    if ($Phase -in 'Consumers', 'All') {
        $sdks = if ($ConsumerSdk -eq 'All') { @('8', '10') } else { @($ConsumerSdk) }
        foreach ($sdk in $sdks) {
            # A fresh directory/cache plus source mapping proves that every product assembly
            # came from these packages, even when another build has the same MinVer version.
            $consumer = "$Artifacts/consumer-$sdk-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
            New-Item -ItemType Directory $consumer | Out-Null
            Copy-Item "$PSScriptRoot/PackageConsumer/*" $consumer -Force
            Copy-Item "$PSScriptRoot/PackageConsumer/.editorconfig" $consumer -Force
            Copy-Item "$PSScriptRoot/ReleaseProjects.props" $consumer
            # Compile the actual extension contract examples, not separately maintained copies.
            $docs = Get-Content "$repo/docs/reference/extensibility.md" -Raw
            foreach ($type in 'MyDataCacheProvider', 'MyEngineDomainSettings') {
                $block = @([regex]::Matches($docs, '(?ms)^```csharp\s*\r?\n(.*?)^```') | Where-Object { $_.Groups[1].Value.Contains("public sealed class $type") })
                if ($block.Count -ne 1) { throw "Expected one documented example for $type" }
                $code = $block[0].Groups[1].Value
                $code = $code.Substring($code.IndexOf("public sealed class $type"))
                ("using CacheOrchestrator.Admin;`nusing CacheOrchestrator.Configuration;`nusing CacheOrchestrator.Orchestration;`nusing System.Text.Json;`n" + $code) |
                    Set-Content "$consumer/$type.cs"
            }
            $localFeed = [Security.SecurityElement]::Escape($packageDirectory)
            @"
<configuration>
  <packageSources><clear /><add key="local" value="$localFeed" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="CacheOrchestrator*" /></packageSource><packageSource key="nuget"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content "$consumer/NuGet.Config"
            $sdkPolicy = if ($sdk -eq '8') { "$consumer/global.net8.json" } else { "$repo/global.json" }
            Copy-Item $sdkPolicy "$consumer/global.json"
            $frameworks = if ($sdk -eq '8') { @('net8.0') } else { @('net8.0', 'net10.0') }
            $frameworkProperty = "-p:SmokeNet8Only=$($sdk -eq '8')"
            Push-Location $consumer
            try {
                Invoke-DotNet "$consumer/sdk.log" @('--version')
                $selectedSdk = (Get-Content "$consumer/sdk.log" -Raw).Trim()
                if (-not $selectedSdk.StartsWith("$sdk.", [StringComparison]::Ordinal)) {
                    throw "Expected SDK $sdk for package consumer, got $selectedSdk"
                }
                $project = "$consumer/Consumer.csproj"
                $property = "-p:SmokePackageVersion=$version"
                Invoke-DotNet "$consumer/restore.log" @('restore', $project, $property, $frameworkProperty, '--configfile', "$consumer/NuGet.Config", '--packages', "$consumer/cache")
                Invoke-DotNet "$consumer/build.log" @('build', $project, '-c', 'Release', '--no-restore', $property, $frameworkProperty)
                foreach ($framework in $frameworks) {
                    Invoke-DotNet "$consumer/run-$framework.log" @("$consumer/bin/Release/$framework/Consumer.dll")
                }
                @'
public sealed class DuplicateIdentity
{
    [CacheOrchestrator.Identity.CacheIdentity(new[] { "GET" }, "first")]
    [CacheOrchestrator.Identity.CacheIdentity(new[] { "GET" }, "second")]
    public void Execute() { }
}
'@ | Set-Content "$consumer/DuplicateIdentity.cs"
                foreach ($framework in $frameworks) {
                    $log = "$consumer/analyzer-$framework.log"
                    & dotnet build $project -c Release --no-restore -f $framework $property $frameworkProperty *> $log
                    if ($LASTEXITCODE -eq 0 -or -not (Select-String -Path $log -Pattern 'error COIDENTITY001')) {
                        throw "Packaged analyzer did not reject duplicate identity on $framework; see $log"
                    }
                }
                Write-Output "Package consumers and analyzer positive/negative cases passed: SDK $selectedSdk, $($frameworks -join ', ')."
            }
            finally { Pop-Location }
        }
    }
}
finally { Pop-Location }
# Negative analyzer builds are expected failures. Do not leak their native exit
# status into the CI PowerShell wrapper after all assertions have succeeded.
exit 0
