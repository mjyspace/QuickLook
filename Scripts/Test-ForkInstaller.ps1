<# Verify the compiled MSI using read-only Windows Installer database queries. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$StagingRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null

function Read-MsiRows([string]$Query, [int]$Columns, [scriptblock]$ReadRow) {
    $view = $database.OpenView($Query)
    try {
        $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $row = for ($index = 1; $index -le $Columns; $index++) { $record.StringData($index) }
                & $ReadRow $row
            }
            finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
        }
    }
    finally {
        $view.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
}

try {
    $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Msi), 0)
    $properties = @{}
    Read-MsiRows 'SELECT `Property`,`Value` FROM `Property`' 2 { param($r) $properties[$r[0]] = $r[1] }
    if ($properties.ProductVersion -ne $Version -or $properties.QUICKLOOKGITCOMMIT -ne $Commit -or
        $properties.ProductName -ne 'QuickLook (mjyspace)' -or
        $properties.UpgradeCode -ne '{9B9A2879-8C4C-4934-A21B-865F3CA45C0A}') { throw 'Unexpected MSI identity or version.' }

    $directories = @{}
    Read-MsiRows 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`' 3 {
        param($r)
        # MSI encodes the target/source pair and short/long names in DefaultDir.
        $directories[$r[0]] = @{ Parent = $r[1]; Name = (($r[2] -split ':')[0] -split '\|')[-1] }
    }
    $installDir = $directories['INSTALLFOLDER']
    $programs = $directories[$installDir.Parent]
    $userPrograms = $installDir.Parent -eq 'UserProgramFilesFolder' -or
        ($programs.Name -eq 'Programs' -and $programs.Parent -eq 'LocalAppDataFolder')
    if ($installDir.Name -ne 'QuickLook-mjyspace' -or !$userPrograms) { throw 'Unexpected per-user MSI install location.' }

    $shortcuts = @{}
    Read-MsiRows 'SELECT `Directory_`,`Target` FROM `Shortcut`' 2 { param($r) $shortcuts[$r[0]] = $r[1] }
    foreach ($location in @('ProgramMenuFolder', 'DesktopFolder')) {
        if ($shortcuts[$location] -ne '[INSTALLFOLDER]QuickLook.exe') { throw "Incorrect MSI shortcut: $location" }
    }

    $staging = (Get-Item -LiteralPath $StagingRoot).FullName.TrimEnd('\')
    $expected = @{}
    foreach ($file in Get-ChildItem -LiteralPath $staging -File -Recurse -Force) {
        $path = $file.FullName.Substring($staging.Length + 1)
        if ($path -ne 'portable.lock') { $expected[$path] = $file.Length }
    }
    $installedFiles = 'SELECT `Component`.`Directory_`,`File`.`FileName`,`File`.`FileSize` FROM `Component`,`File` WHERE `Component`.`Component`=`File`.`Component_`'
    Read-MsiRows $installedFiles 3 {
        param($r)
        $path = ($r[1] -split '\|')[-1]
        $directory = $r[0]
        while ($directory -ne 'INSTALLFOLDER') {
            if (!$directory -or !$directories.ContainsKey($directory)) { throw "File outside install directory: $path" }
            $part = $directories[$directory]
            if ($part.Name -ne '.') { $path = $part.Name + '\' + $path }
            $directory = $part.Parent
        }
        if (!$expected.ContainsKey($path) -or $expected[$path] -ne [long]$r[2]) { throw "Unexpected MSI file: $path" }
        $expected.Remove($path)
    }
    if ($expected.Count) { throw "Missing MSI file: $($expected.Keys | Select-Object -First 1)" }
}
finally {
    if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
