[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$ExpectedRevision,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$symbols = Get-Item -LiteralPath ([IO.Path]::ChangeExtension($package.FullName, '.snupkg'))
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $manifest = $archive.GetEntry('Lokad.GZip.nuspec').Open()
    $reader = [IO.StreamReader]::new($manifest)
    try {
        [xml]$nuspec = $reader.ReadToEnd()
        $version = [string]$nuspec.package.metadata.version
    } finally {
        $reader.Dispose()
    }
} finally {
    $archive.Dispose()
}
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The candidate package has no version.'
}

$workspace = [IO.Directory]::CreateTempSubdirectory('lokad-gzip-package-')
try {
    $project = Join-Path $workspace.FullName 'PackageConsumer.csproj'
    $config = Join-Path $workspace.FullName 'NuGet.Config'
    $cache = Join-Path $workspace.FullName 'packages'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageConsumer/PackageConsumer.csproj') -Destination $workspace.FullName
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageConsumer/Program.cs') -Destination $workspace.FullName

    $feed = [Security.SecurityElement]::Escape($package.DirectoryName)
    Set-Content -LiteralPath $config -Value @"
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="Lokad.GZip" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@

    $versionProperty = "-p:CandidateVersion=$version"
    & $Dotnet restore $project --configfile $config --packages $cache $versionProperty
    if ($LASTEXITCODE -ne 0) {
        throw "Package consumer restore failed with exit code $LASTEXITCODE."
    }

    $arguments = @($package.FullName, $symbols.FullName)
    if ($ExpectedRevision) {
        $arguments += $ExpectedRevision
    }
    foreach ($framework in 'net10.0', 'net11.0') {
        & $Dotnet run --project $project -f $framework -c Release --no-restore $versionProperty -- @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Package consumer failed on $framework with exit code $LASTEXITCODE."
        }
    }
    Get-FileHash -Algorithm SHA256 -LiteralPath $package.FullName, $symbols.FullName
} finally {
    $resolved = (Resolve-Path -LiteralPath $workspace.FullName).ProviderPath
    if ($resolved -cne $workspace.FullName) {
        throw 'The package verification directory changed while the consumer was running.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
