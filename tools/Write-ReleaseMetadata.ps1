#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$Package = [IO.Path]::GetFullPath($Package)
$licenses = Join-Path $root '.downloads\licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$sources = Get-Content -LiteralPath (Join-Path $root 'native\license-sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    $fileName = if ($source.file_name) { $source.file_name } else { $source.name + '.txt' }
    $path = Join-Path $licenses $fileName
    if (-not (Test-Path -LiteralPath $path)) {
        & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 30 --max-time 180 `
            --tls-max 1.2 --http1.1 $source.url --output ($path + '.partial')
        if ($LASTEXITCODE -ne 0) { throw "Acquiring the $($source.name) redistribution notice failed." }
        if ((Get-FileHash -LiteralPath ($path + '.partial') -Algorithm SHA256).Hash -ine $source.sha256) {
            throw "The $($source.name) notice checksum does not match its pinned source."
        }
        Move-Item -LiteralPath ($path + '.partial') -Destination $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $source.sha256) { throw "Invalid cached notice: $path" }
}
$nuget = $env:NUGET_PACKAGES
if (-not $nuget) {
    $nuget = (& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate NuGet package notices.' }
}
$components = @{}
foreach ($file in Get-ChildItem -LiteralPath $Package -Recurse -File -Filter '*.deps.json') {
    $dependencies = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($property in $dependencies.libraries.PSObject.Properties) {
        if ($property.Value.type -notin @('package', 'runtimepack')) { continue }
        $identity = $property.Name -replace '^runtimepack\.', ''
        if ($components.ContainsKey($identity)) { continue }
        $parts = $identity.Split('/')
        $name = $parts[0]; $packageVersion = $parts[1]
        $directory = Join-Path (Join-Path $nuget $name.ToLowerInvariant()) $packageVersion
        if (-not (Test-Path -LiteralPath $directory)) { throw "Missing package/license source: $identity" }
        $noticeDirectory = Join-Path $Package "third-party\$name-$packageVersion"
        New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
        Get-ChildItem -LiteralPath $directory -File | Where-Object Name -match '(?i)license|notice|copying' |
            Copy-Item -Destination $noticeDirectory -Force
        $extra = if ($name -eq 'Google.Protobuf') { 'protobuf' } elseif ($name -eq 'Microsoft.Data.Sqlite.Core') { 'dotnet-sqlite' } elseif ($name -like 'SQLitePCLRaw.*') { 'sqlitepclraw' }
        if ($extra) { Copy-Item -LiteralPath (Join-Path $licenses ($extra + '.txt')) -Destination (Join-Path $noticeDirectory 'LICENSE.txt') -Force }
        if ($name -eq 'Microsoft.Windows.SDK.NET.Ref') {
            Copy-Item -LiteralPath (Join-Path $licenses 'windows-sdk.rtf') -Destination (Join-Path $noticeDirectory 'LICENSE.rtf')
        }
        if ($name -eq 'TreeSitter.DotNet') {
            foreach ($source in $sources | Where-Object name -Like 'tree-sitter*') {
                Copy-Item -LiteralPath (Join-Path $licenses ($source.name + '.txt')) -Destination $noticeDirectory
            }
        }
        $specPath = Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec' | Select-Object -First 1
        [xml]$spec = Get-Content -LiteralPath $specPath.FullName -Raw
        $components[$identity] = @{
            name = $name; version = $packageVersion
            license = $spec.package.metadata.license.InnerText
            source = $spec.package.metadata.projectUrl
            notices = @(Get-ChildItem -LiteralPath $noticeDirectory -File | ForEach-Object { [IO.Path]::GetRelativePath($Package, $_.FullName).Replace('\', '/') })
        }
        if ($components[$identity].notices.Count -eq 0) { throw "No redistribution notice was found for $identity." }
    }
}
$native = Get-Content -LiteralPath (Join-Path $root 'native\dependencies.json') -Raw | ConvertFrom-Json
$diskann = Join-Path $root '.downloads\native\DiskANN'
$notice = Join-Path $Package 'third-party\DiskANN'
New-Item -ItemType Directory -Force -Path $notice | Out-Null
Copy-Item -LiteralPath (Join-Path $diskann 'LICENSE'), (Join-Path $diskann 'NOTICE.txt') -Destination $notice
foreach ($header in Get-ChildItem -LiteralPath (Join-Path $diskann 'include\tsl') -File -Filter '*.h') {
    $content = [IO.File]::ReadAllText($header.FullName)
    $block = [regex]::Match($content, '(?s)/\*.*?\*/')
    if (-not $block.Success -or $block.Value -notmatch 'Copyright') { throw "Vendored notice is missing: $($header.Name)" }
    [IO.File]::WriteAllText((Join-Path $notice ($header.Name + '-NOTICE.txt')), $block.Value)
}
Copy-Item -LiteralPath (Join-Path $licenses 'boost.txt') -Destination (Join-Path $notice 'BOOST-LICENSE.txt')
$components['Microsoft.DiskANN'] = @{
    name = 'Microsoft.DiskANN'; version = '0.7.0'; source = $native.diskann.repository; commit = $native.diskann.commit
    license = 'MIT'; notices = @(Get-ChildItem -LiteralPath $notice -File | ForEach-Object { [IO.Path]::GetRelativePath($Package, $_.FullName).Replace('\', '/') })
}
foreach ($packageName in @('intelmkl.static.win-x64', 'intelopenmp.redist.win')) {
    $dependency = $native.packages | Where-Object id -eq $packageName
    $target = Join-Path $Package "third-party\$packageName"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $root ".downloads\native\packages\$packageName\license.txt") -Destination $target
    $components[$packageName] = @{name=$packageName;version=$dependency.version;license='Intel Simplified Software License';notices=@("third-party/$packageName/license.txt")}
}
$crt = Get-ChildItem -LiteralPath $Package -Recurse -File -Filter 'msvcp140.dll' | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($Package,$_.FullName).Replace('\','/'); version=$_.VersionInfo.FileVersion; copyright=$_.VersionInfo.LegalCopyright }
}
$sourceCommit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the source revision.' }
$dirty = [bool](git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source provenance.' }
[IO.File]::WriteAllText((Join-Path $Package 'THIRD-PARTY-NOTICES.txt'), @'
Fuzzy Find Anything - bundled component notices

Application copyright 2026 Suman Ghosh. No open-source license has been
designated for the application's own source. Dependency licenses do not
grant a license for the application.

Exact component versions, source revision, CRT file versions and complete
file checksums are recorded in release-manifest.json. Original component
license/notice texts are retained in third-party.

The app-local Microsoft Visual C++ runtime files are redistributed from the
Visual Studio redist directories under Microsoft's redistribution terms:
https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files

EmbeddingGemma 2 model assets and their model card/terms are acquired
separately. Windows and installed PDF/Office IFilters are not bundled.
'@)
$files = @(Get-ChildItem -LiteralPath $Package -Recurse -File | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath($Package,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = @{
    schema_version=2;application='Fuzzy Find Anything';version=$Version;runtime_id=$RuntimeIdentifier
    built_at=[DateTime]::UtcNow.ToString('O');source_commit=$sourceCommit;source_dirty=$dirty
    source_repository='https://github.com/sumanthewhiz/FuzzyFindAnything'
    architectures=@{ui=$RuntimeIdentifier;inference=$RuntimeIdentifier;engine='win-x64';extractor='win-x64';tools='win-x64'}
    model=@{id='google/embeddinggemma-2';revision='914f7f89142e33e77833254d9c9b90c3cef7303b';asset_id='embeddinggemma-2-onnx-v1';bundled=$false}
    packages=@($components.Values | Sort-Object name,version);visual_cpp_runtime=@($crt);files=$files
}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Package 'release-manifest.json') -Encoding utf8
