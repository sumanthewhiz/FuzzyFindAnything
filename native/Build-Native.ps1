#requires -Version 7.0
[CmdletBinding()]
param([ValidateRange(1, 64)][int]$Jobs = [Math]::Max(1, [Math]::Floor([Environment]::ProcessorCount / 2)))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root '.downloads\native'
$source = Join-Path $cache 'DiskANN'
$packages = Join-Path $cache 'packages'
$archives = Join-Path $cache 'archives'
$build = Join-Path $cache 'build'
$output = Join-Path $root 'artifacts\native'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Install Visual Studio 2022 or later with Desktop development with C++, CMake and a Windows SDK.'
}
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'The MSVC x64 compiler component is missing.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvarsall.bat'
$cmake = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
foreach ($tool in @($vcvars, $cmake, $ninja)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Required C++ build tool is missing: $tool" }
}
New-Item -ItemType Directory -Force -Path $cache, $packages, $archives, $output | Out-Null
if (-not (Test-Path -LiteralPath $source)) {
    git clone --quiet --depth 1 --branch $manifest.diskann.tag $manifest.diskann.repository $source
    if ($LASTEXITCODE -ne 0) { throw 'Acquiring the pinned DiskANN source failed.' }
}
$commit = git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -cne $manifest.diskann.commit) { throw 'The cached DiskANN source is not the approved commit.' }
$changes = git -C $source status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The cached DiskANN source has local changes; use a separate clean cache.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $manifest.packages) {
    $archive = Join-Path $archives "$($package.id).$($package.version).nupkg"
    if (-not (Test-Path -LiteralPath $archive)) {
        $temporary = "$archive.partial"
        & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 30 --max-time 1800 `
            --tls-max 1.2 --http1.1 "https://www.nuget.org/api/v2/package/$($package.id)/$($package.version)" --output $temporary
        if ($LASTEXITCODE -ne 0) { throw "Downloading $($package.id) failed." }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $package.sha256) {
            throw "Checksum verification failed for $($package.id); the untrusted download was not extracted."
        }
        Move-Item -LiteralPath $temporary -Destination $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $package.sha256) {
        throw "The cached package checksum is invalid: $archive"
    }
    $directory = Join-Path $packages $package.id
    $marker = Join-Path $directory '.ffa-package.sha256'
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ine $package.sha256) {
        $staging = Join-Path $packages ("unpack-" + [Guid]::NewGuid().ToString('N'))
        $retired = Join-Path $packages ("retired-" + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
            [IO.File]::WriteAllText((Join-Path $staging '.ffa-package.sha256'), $package.sha256)
            if (Test-Path -LiteralPath $directory) { [IO.Directory]::Move($directory, $retired) }
            try { [IO.Directory]::Move($staging, $directory) }
            catch {
                if (Test-Path -LiteralPath $retired) { [IO.Directory]::Move($retired, $directory) }
                throw
            }
        } finally {
            if (Test-Path -LiteralPath $staging) { [IO.Directory]::Delete($staging, $true) }
            if (Test-Path -LiteralPath $retired) { [IO.Directory]::Delete($retired, $true) }
        }
    }
}
$configure = "`"$cmake`" -S `"$PSScriptRoot`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=Release " +
    "`"-DCMAKE_MAKE_PROGRAM=$ninja`" `"-DDISKANN_SOURCE=$source`" `"-DNATIVE_PACKAGES=$packages`" `"-DNATIVE_OUTPUT=$output`""
& $env:ComSpec /d /c "set `"PATH=$installer;%PATH%`" && call `"$vcvars`" x64 >nul && $configure && `"$cmake`" --build `"$build`" --parallel $Jobs"
if ($LASTEXITCODE -ne 0) { throw 'The native DiskANN build failed.' }
$redist = Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC\Redist\MSVC') -Directory |
    Where-Object Name -match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $redist) { throw 'The Visual C++ redistributable files are missing from the build tools.' }
foreach ($architecture in @('x64', 'arm64')) {
    $architectureRoot = Join-Path $redist.FullName $architecture
    if (-not (Test-Path -LiteralPath $architectureRoot)) { continue }
    $crt = Get-ChildItem -LiteralPath $architectureRoot -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
    if (-not $crt) { continue }
    $target = Join-Path $output "crt\$architecture"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll') {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($file.FullName))
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid CRT executable: $($file.Name)" }
            $reader.BaseStream.Position = 60
            $offset = $reader.ReadInt32()
            $reader.BaseStream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid CRT PE header: $($file.Name)" }
            $machine = $reader.ReadUInt16()
        } finally { $reader.Dispose() }
        $expected = if ($architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
        $destination = Join-Path $target $file.Name
        if ($machine -ne $expected) {
            # ARM redist also contains an x64-only companion; never mix it into a native ARM64 process.
            if (Test-Path -LiteralPath $destination) { [IO.File]::Delete($destination) }
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $output 'crt\x64\msvcp140.dll'))) { throw 'The x64 app-local Visual C++ runtime is missing.' }
& (Join-Path $output 'ffa_diskann_smoke.exe')
if ($LASTEXITCODE -ne 0) { throw 'The native DiskANN ABI smoke check failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Destination $output -Force
Write-Host "Native DiskANN built from $commit"
