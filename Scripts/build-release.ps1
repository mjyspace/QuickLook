<#
.SYNOPSIS
Builds and verifies a complete local Windows x64 portable Release ZIP.
.DESCRIPTION
Builds the host, every managed project in QuickLook.slnx, and the Win32/x64
native helpers. MSI, APPX, ARM64, publishing and installation are not involved.
Only Build/Release and this script's staging directory are cleared. Previous
archives remain in Build/Releases. Run -PreflightOnly to check prerequisites
without building or deleting files.
#>
[CmdletBinding()]
param(
    [string]$MSBuildPath,
    [string]$WindowsSdkVersion,
    [ValidateRange(1, 64)][int]$MaxCpuCount = 4,
    [switch]$SkipRestore,
    [switch]$PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$buildRoot = Join-Path $repoRoot 'Build'
$releaseRoot = Join-Path $buildRoot 'Release'
$workRoot = Join-Path $buildRoot 'PortableRelease'
$stagingRoot = Join-Path $workRoot 'Staging'
$archivesRoot = Join-Path $buildRoot 'Releases'
$utf8 = New-Object Text.UTF8Encoding($false)

function Assert-WorkspacePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (!$fullPath.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be inside the repository: $fullPath"
    }

    # Check existing ancestors as well as the target: a junction in Build or
    # another parent must not redirect any cleanup, output or copy elsewhere.
    $cursor = $fullPath
    while ($cursor.Length -ge $repoRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in build paths: $cursor"
            }
        }
        if ($cursor -eq $repoRoot) { break }
        $cursor = Split-Path -Parent $cursor
    }
    return $fullPath
}

function Assert-NoReparseTree([string]$Path) {
    $null = Assert-WorkspacePath $Path
    if (!(Test-Path -LiteralPath $Path)) { return }
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in build contents: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Reset-OwnedDirectory([string]$Path) {
    $fullPath = Assert-WorkspacePath $Path
    if ($fullPath -ne $releaseRoot -and $fullPath -ne $stagingRoot) {
        throw "Refusing to clear a directory not owned by this build: $fullPath"
    }
    Assert-NoReparseTree $fullPath
    if (Test-Path -LiteralPath $fullPath) {
        if ($fullPath -eq $releaseRoot) {
            $userData = @(Get-ChildItem -LiteralPath $fullPath -Directory -Recurse -Force |
                Where-Object { $_.Name -eq 'UserData' })
            if ($userData.Count -gt 0) {
                throw "Refusing to clear Build/Release because it contains user settings: $($userData[0].FullName). Move your user data out of the build output before rebuilding."
            }
        }
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $fullPath -Force
}

function Find-BuildTools {
    $problems = New-Object 'Collections.Generic.List[string]'
    $candidate = $MSBuildPath
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) {
            throw 'Visual Studio Installer/vswhere was not found. Install Visual Studio 2026 with C++ build tools, or pass -MSBuildPath.'
        }
        $vsRoot = & $vswhere -latest -products '*' -version '[18.0,19.0)' -property installationPath
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($vsRoot -join ''))) {
            throw 'Visual Studio 2026 was not found. The native projects require the v145 toolset.'
        }
        $candidate = Join-Path ($vsRoot | Select-Object -First 1) 'MSBuild\Current\Bin\MSBuild.exe'
    }
    if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "MSBuild executable was not found: $candidate"
    }
    $candidate = [IO.Path]::GetFullPath($candidate)
    $msbuildDirectory = Split-Path -Parent $candidate
    while ($msbuildDirectory -and (Split-Path -Leaf $msbuildDirectory) -ne 'MSBuild') {
        $msbuildDirectory = Split-Path -Parent $msbuildDirectory
    }
    if (!$msbuildDirectory) { throw 'MSBuild must come from a Visual Studio installation.' }
    $vsRoot = Split-Path -Parent $msbuildDirectory

    $versionFile = Join-Path $vsRoot 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
    if (!(Test-Path -LiteralPath $versionFile)) {
        $problems.Add('Install the MSVC v145 x86/x64 build tools for Visual Studio 2026.')
    }
    else {
        $toolsetVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        $toolsetRoot = Join-Path $vsRoot "VC\Tools\MSVC\$toolsetVersion"
        foreach ($required in @('bin\Hostx64\x86\cl.exe', 'bin\Hostx64\x64\cl.exe')) {
            if (!(Test-Path -LiteralPath (Join-Path $toolsetRoot $required))) {
                $problems.Add("Missing MSVC x86/x64 compiler: $required")
            }
        }
        foreach ($required in @('atlmfc\include\atlcomcli.h', 'atlmfc\lib\x86\atls.lib', 'atlmfc\lib\x64\atls.lib')) {
            if (!(Test-Path -LiteralPath (Join-Path $toolsetRoot $required))) {
                $problems.Add("Install C++ ATL for the v145 x86/x64 toolset (missing $required).")
            }
        }
        $vcTargets = @(Get-ChildItem -LiteralPath (Join-Path $vsRoot 'MSBuild\Microsoft\VC') -Directory -ErrorAction SilentlyContinue)
        foreach ($platform in @('Win32', 'x64')) {
            $platformTargets = @($vcTargets | Where-Object {
                Test-Path -LiteralPath (Join-Path $_.FullName "Platforms\$platform\PlatformToolsets\v145\Toolset.props")
            })
            if ($platformTargets.Count -eq 0) { $problems.Add("MSBuild v145 platform targets are missing for $platform.") }
        }
    }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
    if ($kits -and $kits.PSObject.Properties['KitsRoot10']) { $sdkRoot = $kits.KitsRoot10 }
    $sdkRoot = [IO.Path]::GetFullPath($sdkRoot).TrimEnd('\', '/')
    $sdkCandidates = @(Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending)
    if ($WindowsSdkVersion) { $sdkCandidates = @($sdkCandidates | Where-Object { $_.Name -eq $WindowsSdkVersion }) }
    $selectedSdk = $null
    foreach ($sdk in $sdkCandidates) {
        $sdkVersion = $sdk.Name
        $requiredSdkFiles = @("Include\$sdkVersion\um\Windows.h", "Lib\$sdkVersion\um\x86\kernel32.lib",
            "Lib\$sdkVersion\um\x64\kernel32.lib", "UnionMetadata\$sdkVersion\Facade\windows.winmd")
        if (@($requiredSdkFiles | Where-Object { !(Test-Path -LiteralPath (Join-Path $sdkRoot $_)) }).Count -gt 0) { continue }
        $foundation = @(Get-ChildItem -LiteralPath (Join-Path $sdkRoot "References\$sdkVersion\Windows.Foundation.FoundationContract") -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Windows.Foundation.FoundationContract.winmd') } |
            Sort-Object { [version]$_.Name } -Descending)
        $universal = @(Get-ChildItem -LiteralPath (Join-Path $sdkRoot "References\$sdkVersion\Windows.Foundation.UniversalApiContract") -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Windows.Foundation.UniversalApiContract.winmd') } |
            Sort-Object { [version]$_.Name } -Descending)
        if ($foundation.Count -gt 0 -and $universal.Count -gt 0) {
            $selectedSdk = [pscustomobject]@{ Version = $sdkVersion; Foundation = $foundation[0].Name; Universal = $universal[0].Name }
            break
        }
    }
    if (!$selectedSdk) { $problems.Add('Install a Windows 10/11 SDK with headers, x86/x64 libraries and WinRT contract metadata.') }
    if ($problems.Count -gt 0) { throw "Portable Release prerequisites are missing:`n - $($problems -join "`n - ")" }
    return [pscustomobject]@{ MSBuild = $candidate; SdkRoot = $sdkRoot; Sdk = $selectedSdk; ToolsetVersion = $toolsetVersion }
}

function New-PortableSolution {
    [xml]$solution = Get-Content -LiteralPath (Join-Path $repoRoot 'QuickLook.slnx') -Raw
    $plugins = New-Object 'Collections.Generic.List[object]'
    foreach ($project in @($solution.SelectNodes('//Project'))) {
        $relativePath = $project.GetAttribute('Path')
        if ($relativePath.EndsWith('.wixproj') -or $relativePath -match 'QuickLook\.NativeArm64') {
            $null = $project.ParentNode.RemoveChild($project)
            continue
        }
        $projectPath = Assert-WorkspacePath (Join-Path $repoRoot $relativePath)
        $project.SetAttribute('Path', $projectPath)
        if ($relativePath -match '^QuickLook\.Plugin[/\\].+\.csproj$') {
            [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
            $assembly = $projectXml.SelectSingleNode('//AssemblyName').InnerText
            $output = $projectXml.SelectSingleNode('//PropertyGroup[contains(@Condition,"Release|AnyCPU")]/OutputPath')
            if (!$output) { throw "No Release|AnyCPU output path is defined for $relativePath" }
            $outputPath = Assert-WorkspacePath (Join-Path (Split-Path -Parent $projectPath) $output.InnerText)
            if (!$outputPath.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Plugin output must be inside Build/Release: $outputPath"
            }
            $pluginDll = $outputPath.Substring($releaseRoot.Length + 1) + '\' + $assembly + '.dll'
            $plugins.Add([pscustomobject]@{ Name = $assembly; Path = $pluginDll.Replace('\', '/') })
        }
    }
    foreach ($dependency in @($solution.SelectNodes('//BuildDependency'))) {
        $dependencyPath = $dependency.GetAttribute('Project')
        if ($dependencyPath.EndsWith('.wixproj') -or $dependencyPath -match 'QuickLook\.NativeArm64') {
            $null = $dependency.ParentNode.RemoveChild($dependency)
        }
        else { $dependency.SetAttribute('Project', (Assert-WorkspacePath (Join-Path $repoRoot $dependencyPath))) }
    }
    foreach ($file in @($solution.SelectNodes('//File'))) {
        $file.SetAttribute('Path', (Assert-WorkspacePath (Join-Path $repoRoot $file.GetAttribute('Path'))))
    }
    if ($plugins.Count -eq 0) { throw 'No plugins were discovered in QuickLook.slnx.' }
    return [pscustomobject]@{ Document = $solution; Plugins = @($plugins.ToArray()) }
}

function Test-PackageFile([string]$RelativePath) {
    if ($RelativePath -match '(^|[/\\])(UserData|UserPlugins|Validation|Debug|obj|logs?)([/\\]|$)') { return $false }
    if ($RelativePath -match '\.(pdb|obj|ipdb|iobj|exp|lib|ilk|pch|tlog|lastbuildstate|cache|log|tmp)$') { return $false }
    if ($RelativePath -match '\.vshost\.') { return $false }
    return $true
}

function Invoke-ReleaseMSBuild([string]$Target, [string]$LogName) {
    $arguments = @($solutionPath, '/nologo', "/m:$MaxCpuCount", '/verbosity:minimal', "/t:$Target",
        '/p:Configuration=Release', '/p:Platform=Any CPU', '/p:FodyGenerateXsd=false',
        "/p:SolutionDir=$($repoRoot.Replace('\', '/'))/",
        "/p:DirectoryBuildTargetsPath=$targetsPath", "/p:QuickLookSdkRoot=$($tools.SdkRoot)",
        "/p:QuickLookSdkVersion=$($tools.Sdk.Version)", "/p:WindowsTargetPlatformVersion=$($tools.Sdk.Version)",
        "/p:QuickLookFoundationContractVersion=$($tools.Sdk.Foundation)",
        "/p:QuickLookUniversalApiContractVersion=$($tools.Sdk.Universal)",
        "/p:QuickLookPortableCommonPath=$commonSnapshotPath",
        "/flp:logfile=$(Join-Path $workRoot $LogName);verbosity=normal")
    & $tools.MSBuild @arguments
    if ($LASTEXITCODE -ne 0) { throw "MSBuild $Target failed with exit code $LASTEXITCODE. See $(Join-Path $workRoot $LogName)." }
}

Push-Location -LiteralPath $repoRoot
try {
    $null = Assert-WorkspacePath $releaseRoot
    $null = Assert-WorkspacePath $workRoot
    $null = Assert-WorkspacePath $archivesRoot
    $tools = Find-BuildTools
    $portable = New-PortableSolution
    $targetsPath = Join-Path $PSScriptRoot 'PortableRelease.targets'
    $versionScript = Join-Path $PSScriptRoot 'Get-BuildVersion.ps1'
    foreach ($inputFile in @($targetsPath, $versionScript)) {
        if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Required build support file is missing: $inputFile" }
    }
    $version = & $versionScript
    if (!$version -or !$version.PackageVersion -or !$version.Commit) { throw 'Get-BuildVersion.ps1 returned incomplete version information.' }
    if ($version.PackageVersion -match '[^A-Za-z0-9._+-]') { throw 'PackageVersion contains unsafe filename characters.' }
    Write-Host "Portable Release: $($version.PackageVersion), win-x64, $($portable.Plugins.Count) plugins"
    Write-Host "MSBuild: $($tools.MSBuild)"
    Write-Host "Windows SDK: $($tools.Sdk.Version); MSVC: $($tools.ToolsetVersion)"
    if ($PreflightOnly) {
        Write-Host 'Preflight passed. No files were built, cleaned or packaged.'
        return
    }

    Assert-NoReparseTree $workRoot
    $null = New-Item -ItemType Directory -Path $workRoot -Force
    $solutionPath = Join-Path $workRoot 'portable.slnx'
    $commonSnapshotPath = Join-Path $workRoot ([Guid]::NewGuid().ToString('N') + '-QuickLook.Common.dll')
    $portable.Document.Save($solutionPath)
    if (!$SkipRestore) { Invoke-ReleaseMSBuild 'Restore' 'restore.log' }
    Reset-OwnedDirectory $releaseRoot
    Invoke-ReleaseMSBuild 'Build' 'build.log'

    # Costura can embed Common into the host and remove the root copy. The
    # build target captures this run's Common immediately after its own build,
    # before that cleanup; never reuse an old obj/bin assembly as a substitute.
    if (!(Test-Path -LiteralPath $commonSnapshotPath -PathType Leaf)) {
        throw 'The build did not capture QuickLook.Common.dll for this release.'
    }
    $commonAssembly = [Reflection.AssemblyName]::GetAssemblyName($commonSnapshotPath)
    if ($commonAssembly.Name -ne 'QuickLook.Common' -or $commonAssembly.Version -ne [version]$version.AssemblyVersion) {
        throw 'The captured QuickLook.Common.dll does not match this release version.'
    }

    Reset-OwnedDirectory $stagingRoot
    Assert-NoReparseTree $releaseRoot
    foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File -Force) {
        $relative = $file.FullName.Substring($releaseRoot.Length + 1)
        if (!(Test-PackageFile $relative)) { continue }
        $destination = Join-Path $stagingRoot $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath $commonSnapshotPath -Destination (Join-Path $stagingRoot 'QuickLook.Common.dll') -Force
    foreach ($documentation in @('LICENSE-GPL.txt', 'README.md', 'OPTIONS.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $documentation) -Destination (Join-Path $stagingRoot $documentation)
    }
    [IO.File]::WriteAllText((Join-Path $stagingRoot 'portable.lock'), "This file makes QuickLook portable.`r`n", $utf8)
    $buildInfo = [ordered]@{
        commit = $version.Commit
        version = $version.InformationalVersion
        assemblyVersion = $version.AssemblyVersion
        packageVersion = $version.PackageVersion
        isDirty = [bool]$version.IsDirty
        configuration = 'Release'
        architecture = 'win-x64'
        managedPlatform = 'AnyCPU (Prefer32Bit=false)'
        nativeArchitectures = @('Win32', 'x64')
        windowsSdk = $tools.Sdk.Version
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        plugins = @($portable.Plugins)
    }
    [IO.File]::WriteAllText((Join-Path $stagingRoot 'build-info.json'), ($buildInfo | ConvertTo-Json -Depth 5), $utf8)

    $requiredFiles = @('QuickLook.exe', 'QuickLook.exe.config', 'QuickLook.Common.dll', 'QuickLook.Native32.dll', 'QuickLook.Native64.dll',
        'Translations.config', 'portable.lock', 'LICENSE-GPL.txt', 'README.md', 'OPTIONS.md', 'build-info.json') +
        @($portable.Plugins | ForEach-Object { $_.Path })
    foreach ($required in $requiredFiles) {
        $path = Join-Path $stagingRoot $required
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "The portable package is incomplete; required file is missing or empty: $required"
        }
    }

    $null = New-Item -ItemType Directory -Path $archivesRoot -Force
    $baseName = "QuickLook-$($version.PackageVersion)-win-x64"
    $archivePath = Join-Path $archivesRoot "$baseName.zip"
    if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath "$archivePath.sha256")) {
        $baseName += '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
        $archivePath = Join-Path $archivesRoot "$baseName.zip"
    }
    $temporaryArchive = Join-Path $workRoot ([Guid]::NewGuid().ToString('N') + '.zip.partial')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.EndsWith('/')) { continue }
            if (!(Test-PackageFile $name)) { throw "Excluded file appeared in the archive: $name" }
            $entries[$name] = $entry.Length
        }
        $stagedFiles = @(Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Force)
        if ($entries.Count -ne $stagedFiles.Count) { throw 'ZIP file count does not match the staging directory.' }
        foreach ($file in $stagedFiles) {
            $name = $file.FullName.Substring($stagingRoot.Length + 1).Replace('\', '/')
            if (!$entries.ContainsKey($name) -or $entries[$name] -ne $file.Length) { throw "ZIP content verification failed: $name" }
        }
        foreach ($required in $requiredFiles) {
            if (!$entries.ContainsKey($required.Replace('\', '/'))) { throw "Required ZIP entry is missing: $required" }
        }
    }
    finally { $archive.Dispose() }

    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$archivePath.sha256", "$checksum  $([IO.Path]::GetFileName($archivePath))`r`n", $utf8)
    Remove-Item -LiteralPath $commonSnapshotPath
    Write-Host "Verified portable release: $archivePath"
    Write-Host "SHA256: $checksum"
    [pscustomobject]@{ Archive = $archivePath; ChecksumFile = "$archivePath.sha256"; SHA256 = $checksum; PluginCount = $portable.Plugins.Count }
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
finally {
    Pop-Location
}
