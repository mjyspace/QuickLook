<# .SYNOPSIS
Returns the WiX 3.14.1 bin directory, restoring the pinned tools locally when necessary.
#>
[CmdletBinding()]
param([string]$WixBin)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseUrl = 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip'
$sha256 = '6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31'
$requiredTools = @('candle.exe', 'light.exe', 'heat.exe')

function Test-WixTools([string]$Directory) {
    foreach ($name in $requiredTools) {
        $path = Join-Path $Directory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne '3.14.1.8722') { return $false }
    }
    foreach ($name in @('candle.exe.config', 'light.exe.config', 'heat.exe.config', 'darice.cub',
                         'wix.dll', 'winterop.dll', 'wconsole.dll', 'WixUIExtension.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { return $false }
    }
    return $true
}

if ($WixBin) {
    $directory = (Resolve-Path -LiteralPath $WixBin).ProviderPath
    if (!(Test-WixTools $directory)) { throw "WiX 3.14.1 tools were not found in $directory" }
    return $directory
}

$candidates = @()
if ($env:WIX) { $candidates += Join-Path $env:WIX 'bin' }
if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'WiX Toolset v3.14\bin' }
foreach ($directory in $candidates) {
    if (Test-WixTools $directory) { return (Resolve-Path -LiteralPath $directory).ProviderPath }
}

$build = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build'))
$tools = Join-Path $build 'Tools'
$bin = Join-Path $tools 'wix-3.14.1'
$zip = Join-Path $tools 'wix314-binaries.zip'
function Assert-NoLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to write the WiX cache through a reparse point: $Path"
    }
}

# Serialize the first restore so another build sees a complete toolset.
$mutex = New-Object Threading.Mutex($false, 'Local\QuickLook-mjyspace-WiX-3.14.1-restore')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(5)) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Timed out waiting for a concurrent WiX tool restore.' }
    foreach ($path in @($build, $tools, $bin, $zip)) { Assert-NoLink $path }
    if (Test-WixTools $bin) { return $bin }
    $null = [IO.Directory]::CreateDirectory($bin)

    if (!(Test-Path -LiteralPath $zip -PathType Leaf) -or (Get-FileHash -LiteralPath $zip).Hash -ne $sha256) {
        $download = Join-Path $tools ([Guid]::NewGuid().ToString('N') + '.zip.partial')
        try {
            Write-Host 'Downloading WiX 3.14.1 from the WiX Toolset GitHub release...'
            Invoke-WebRequest -UseBasicParsing -Uri $releaseUrl -OutFile $download
            if ((Get-FileHash -LiteralPath $download).Hash -ne $sha256) { throw 'WiX ZIP SHA-256 mismatch.' }
            Copy-Item -LiteralPath $download -Destination $zip -Force
        }
        finally { [IO.File]::Delete($download) }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        # MSI tools and their runtime dependencies are in the ZIP root. Nested
        # directories contain SDK samples, documentation and the Burn bootstrapper.
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cne $entry.Name) { continue }
            $path = Join-Path $bin $entry.Name
            Assert-NoLink $path
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    }
    finally { $archive.Dispose() }
    if (!(Test-WixTools $bin)) { throw "Incomplete WiX 3.14.1 toolset in $bin" }
    return $bin
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
