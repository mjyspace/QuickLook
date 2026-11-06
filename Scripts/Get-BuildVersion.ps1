[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$commit = & git -C $repositoryRoot rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the current Git commit.' }
$commit = $commit.Trim()
$shortCommit = $commit.Substring(0, 12)

# A fork or shallow checkout may not contain release tags. A Git hash is not
# a valid AssemblyVersion; use an explicit local version in that case.
$tags = @(& git -C $repositoryRoot tag --merged HEAD --sort=-version:refname)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git release tags.' }
$assemblyVersion = [version]'0.0.0.0'
$baseVersion = '0.0.0'
foreach ($tag in $tags) {
    if ($tag -match '^v?(\d+\.\d+\.\d+(?:\.\d+)?)$') {
        $tagVersion = $Matches[1]
        $candidate = [version]$tagVersion
        $parts = @($candidate.Major, $candidate.Minor, $candidate.Build, [Math]::Max(0, $candidate.Revision))
        if (@($parts | Where-Object { $_ -gt 65534 }).Count -eq 0) {
            $assemblyVersion = [version]($parts -join '.')
            $baseVersion = $tagVersion
            break
        }
    }
}

$status = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine whether the worktree is clean.' }
$isDirty = $status.Count -gt 0
$suffix = if ($isDirty) { '-dirty' } else { '' }

[pscustomobject]@{
    AssemblyVersion = $assemblyVersion.ToString(4)
    InformationalVersion = "$baseVersion+g$shortCommit$suffix"
    PackageVersion = "$baseVersion-local.$shortCommit$suffix"
    Commit = $commit
    IsDirty = $isDirty
}
