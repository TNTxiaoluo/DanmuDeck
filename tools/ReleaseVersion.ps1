# Shared numeric package-version rules. Display versions are independent of install versions.
function ConvertTo-PackageVersion {
    param([Parameter(Mandatory)][string]$Value)
    $parsed = $null
    if ($Value -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        -not [Version]::TryParse($Value, [ref]$parsed) -or
        $parsed.Major -lt 1 -or $parsed.Major -gt 65535 -or
        $parsed.Minor -gt 65535 -or $parsed.Build -gt 65535 -or $parsed.Revision -gt 65535) {
        throw 'Package version must contain four numbers in 0..65535 (major must be positive); use b only in the display version.'
    }
    return $parsed
}

function Get-DevelopmentPackageVersion {
    param(
        [Parameter(Mandatory)][string]$ReleasePackageVersion,
        [string[]]$PreviousVersions = @(),
        [string]$RequestedVersion
    )
    $release = ConvertTo-PackageVersion $ReleasePackageVersion
    $highest = $null
    foreach ($value in $PreviousVersions) {
        $previous = ConvertTo-PackageVersion $value
        if ($null -eq $highest -or $previous -gt $highest) { $highest = $previous }
    }
    if ($RequestedVersion) {
        $requested = ConvertTo-PackageVersion $RequestedVersion
        if ($requested -lt $release -or ($null -ne $highest -and $requested -le $highest)) {
            throw 'The requested development package version must not precede the release base and must exceed previous development packages.'
        }
        return $requested
    }
    if ($null -eq $highest -or $release -gt $highest) { return $release }
    if ($highest.Build -lt 65535) { return [Version]::new($highest.Major, $highest.Minor, $highest.Build + 1, 0) }
    if ($highest.Minor -lt 65535) { return [Version]::new($highest.Major, $highest.Minor + 1, 0, 0) }
    if ($highest.Major -lt 65535) { return [Version]::new($highest.Major + 1, 0, 0, 0) }
    throw 'The development package version range is exhausted.'
}
