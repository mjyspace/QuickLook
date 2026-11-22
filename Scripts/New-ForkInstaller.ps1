<# Builds the per-user MSI from the release script's verified staging directory. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingRoot,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$WixBin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $repoRoot 'Build'
$installerSource = Join-Path $repoRoot 'QuickLook.Installer'

function Get-BuildPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (!$full.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer input and output must be inside $buildRoot : $full"
    }
    for ($parent = $full; $parent -ne $repoRoot; $parent = Split-Path -Parent $parent) {
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point in installer path: $parent"
        }
    }
    return $full
}

function Invoke-Wix([string]$Tool, [string[]]$Arguments) {
    & (Join-Path $WixBin "$Tool.exe") @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WiX $Tool failed with exit code $LASTEXITCODE." }
}

function Get-ComponentGuid([string]$RelativePath) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $key = 'mjyspace/QuickLook/msi/files/' + $RelativePath.ToLowerInvariant()
        $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
        $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x80
        $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
        return [Guid]::ParseExact([BitConverter]::ToString($bytes, 0, 16).Replace('-', ''), 'N').ToString()
    }
    finally { $hash.Dispose() }
}

$StagingRoot = Get-BuildPath $StagingRoot
$WorkDirectory = Get-BuildPath $WorkDirectory
$OutputPath = Get-BuildPath $OutputPath
foreach ($destination in @($WorkDirectory, $OutputPath)) {
    if ((Test-Path -LiteralPath $destination) -or $destination.StartsWith($StagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer output requires a fresh path outside staging: $destination"
    }
}
$numericVersion = [version]$Version
if ($numericVersion.Major -gt 255 -or $numericVersion.Minor -gt 255 -or $numericVersion.Build -gt 65535) {
    throw "Product version exceeds Windows Installer's three-field limits: $Version"
}
# The GUID prefix is private to this fork. Rebuilding a release keeps its product
# identity; advancing any MSI version field creates a distinct major-upgrade product.
$productCode = '3CA0EE90-C679-47EF-B26D-{0:X2}{1:X2}{2:X8}' -f $numericVersion.Major, $numericVersion.Minor, $numericVersion.Build

$null = New-Item -ItemType Directory -Path $WorkDirectory
$harvest = Join-Path $WorkDirectory 'Files.wxs'
Invoke-Wix 'heat' @('dir', $StagingRoot, '-nologo', '-ag', '-scom', '-sreg', '-sfrag', '-srd',
    '-dr', 'INSTALLFOLDER', '-cg', 'ForkFiles', '-var', 'var.PayloadRoot', '-out', $harvest)

$wxs = New-Object Xml.XmlDocument
$wxs.Load($harvest)
$wixNamespace = $wxs.DocumentElement.NamespaceURI
$ns = New-Object Xml.XmlNamespaceManager($wxs.NameTable)
$ns.AddNamespace('w', $wixNamespace)
$components = @($wxs.SelectNodes('//w:Component', $ns))
$markerRemoved = $false
foreach ($component in $components) {
    $file = $component.SelectSingleNode('w:File', $ns)
    $prefix = '$(var.PayloadRoot)\'
    if (!$file -or !$file.GetAttribute('Source').StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Unexpected harvested component: $($component.GetAttribute('Id'))"
    }
    $relative = $file.GetAttribute('Source').Substring($prefix.Length)
    if ($relative -eq 'portable.lock') {
        $ref = $wxs.SelectSingleNode("//w:ComponentRef[@Id='$($component.GetAttribute('Id'))']", $ns)
        $null = $ref.ParentNode.RemoveChild($ref)
        $null = $component.ParentNode.RemoveChild($component)
        $markerRemoved = $true
        continue
    }
    if ($relative -match '(^|\\)UserData(\\|$)') { throw "User data found in release staging: $relative" }
    # MSI user-profile components must use an HKCU key path (ICE38). WiX needs
    # an explicit GUID for a component that owns both a registry key path and a file.
    $component.SetAttribute('Guid', (Get-ComponentGuid $relative))
    $file.RemoveAttribute('KeyPath')
    $key = $wxs.CreateElement('RegistryValue', $wixNamespace)
    $key.SetAttribute('Root', 'HKCU')
    $key.SetAttribute('Key', 'Software\mjyspace\QuickLook\Installer\Files')
    $key.SetAttribute('Name', $relative.Replace('\', '/'))
    $key.SetAttribute('Value', '1')
    $key.SetAttribute('Type', 'integer')
    $key.SetAttribute('KeyPath', 'yes')
    $null = $component.AppendChild($key)
}
if (!$markerRemoved) { throw 'Expected portable.lock in verified portable staging.' }

# Windows Installer can remove only empty directories via RemoveFolder. Include
# nested directories with no direct files (e.g. the parent of the plugin folders).
$owner = $wxs.SelectSingleNode('//w:DirectoryRef/w:Component', $ns)
foreach ($directory in $wxs.SelectNodes('//w:Directory', $ns)) {
    $remove = $wxs.CreateElement('RemoveFolder', $wixNamespace)
    $remove.SetAttribute('Id', 'rm' + $directory.GetAttribute('Id'))
    $remove.SetAttribute('Directory', $directory.GetAttribute('Id'))
    $remove.SetAttribute('On', 'uninstall')
    $null = $owner.AppendChild($remove)
}
$wxs.Save($harvest)

$WixBin = [IO.Path]::GetFullPath($WixBin)
Push-Location -LiteralPath $WorkDirectory
try {
    Invoke-Wix 'candle' @('-nologo', '-arch', 'x64', "-dProductCode=$productCode", "-dProductVersion=$Version",
        "-dSourceCommit=$Commit", "-dPayloadRoot=$StagingRoot", "-dInstallerSource=$installerSource",
        (Join-Path $installerSource 'Fork.wxs'), $harvest)
}
finally { Pop-Location }
# ICE64 also requests removal of the shared LocalAppData\Programs parent. Only
# fork-owned folders have RemoveFolder entries. ICE91 is for dual-scope packages.
Invoke-Wix 'light' @('-nologo', '-ext', (Join-Path $WixBin 'WixUIExtension.dll'), '-cultures:zh-cn', '-sice:ICE64', '-sice:ICE91', '-spdb',
    '-out', $OutputPath, (Join-Path $WorkDirectory 'Fork.wixobj'), (Join-Path $WorkDirectory 'Files.wixobj'))
