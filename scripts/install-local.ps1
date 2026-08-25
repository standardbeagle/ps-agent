<#
.SYNOPSIS
    Put the locally built PsAgent on PSModulePath so `Import-Module PsAgent` works by name.

.DESCRIPTION
    Links the build output into the user module directory as `PsAgent`. A junction rather than a
    copy, so a rebuild is picked up without reinstalling — which also means you do not get a stale
    module silently shadowing the one you are working on.

    The link points at the target framework matching the PowerShell host you run this from:
    PowerShell 7.4 is .NET 8, 7.6 is .NET 10. Linking the wrong one loads but fails on first use
    with a type-load error that does not mention the framework, so it is chosen rather than guessed.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER AddToProfile
    Also add an Import-Module line to your PowerShell profile, so the module is loaded in every
    new session.

.PARAMETER Uninstall
    Remove the link (and the profile line, with -AddToProfile).

.EXAMPLE
    ./scripts/install-local.ps1
    Import-Module PsAgent

.EXAMPLE
    ./scripts/install-local.ps1 -Configuration Release -AddToProfile
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $AddToProfile,

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$moduleName = 'PsAgent'

# The user module directory is the first PSModulePath entry under the profile folder.
$userModules = Join-Path (Split-Path -Parent $PROFILE) 'Modules'
$link = Join-Path $userModules $moduleName

if ($Uninstall) {
    if (Test-Path $link) {
        # Remove the junction itself, never its contents: Remove-Item -Recurse on a junction has
        # historically followed it and deleted the build output on the other side.
        [System.IO.Directory]::Delete($link, $false)
        Write-Host "Removed $link"
    }
    else {
        Write-Host "Nothing installed at $link"
    }

    if ($AddToProfile -and (Test-Path $PROFILE)) {
        $kept = Get-Content $PROFILE | Where-Object { $_ -notmatch 'Import-Module\s+PsAgent' }
        Set-Content -Path $PROFILE -Value $kept -Encoding utf8
        Write-Host "Removed the Import-Module line from $PROFILE"
    }

    return
}

# PowerShell 7.4 runs on .NET 8, 7.6 on .NET 10. Match the host rather than assume.
$tfm = "net$($PSVersionTable.PSVersion.Major).0"
if ($PSVersionTable.PSVersion.Major -eq 7) {
    $tfm = if ($PSVersionTable.PSVersion.Minor -ge 5) { 'net10.0' } else { 'net8.0' }
}

$source = Join-Path $repo "src\PsAgent.Cmdlets\bin\$Configuration\$tfm"
$manifest = Join-Path $source 'PsAgent.psd1'

if (-not (Test-Path $manifest)) {
    $available = Get-ChildItem (Join-Path $repo 'src\PsAgent.Cmdlets\bin') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object { "$($_.Parent.Name)/$($_.Name)" }

    throw @"
No build at $source

This host is PowerShell $($PSVersionTable.PSVersion), which needs $tfm.
Built so far: $(if ($available) { $available -join ', ' } else { '(nothing)' })

Build it first:
    dotnet build -c $Configuration -m:1
"@
}

New-Item -ItemType Directory -Force -Path $userModules | Out-Null

if (Test-Path $link) {
    [System.IO.Directory]::Delete($link, $false)
}

New-Item -ItemType Junction -Path $link -Target $source | Out-Null
Write-Host "Linked $link -> $source"

if ($AddToProfile) {
    $line = 'Import-Module PsAgent'
    New-Item -ItemType File -Force -Path $PROFILE -ErrorAction SilentlyContinue | Out-Null
    if ((Get-Content $PROFILE -ErrorAction SilentlyContinue) -notmatch 'Import-Module\s+PsAgent') {
        Add-Content -Path $PROFILE -Value $line -Encoding utf8
        Write-Host "Added '$line' to $PROFILE"
    }
    else {
        Write-Host "$PROFILE already imports PsAgent"
    }
}

# Prove it resolves by name in a clean session, rather than reporting success on having made a link.
$check = pwsh -NoProfile -Command "Import-Module PsAgent -ErrorAction Stop; (Get-Command -Module PsAgent).Name -join ', '" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Linked, but 'Import-Module PsAgent' failed in a clean session:`n$check"
}

Write-Host ""
Write-Host "Import-Module PsAgent  ->  $check" -ForegroundColor Green
