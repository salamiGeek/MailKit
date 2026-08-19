[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64')]
    [string] $Runtime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CanonicalPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    return (Resolve-Path -LiteralPath $LiteralPath -ErrorAction Stop).ProviderPath
}

function Assert-NoReparsePointPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $current = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($LiteralPath))
    while ($null -ne $current) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to publish: $Description contains a reparse-point path component: $($current.FullName)"
            }
        }

        $current = $current.Parent
    }
}

function Test-PathEquals {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Left,

        [Parameter(Mandatory = $true)]
        [string] $Right
    )

    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    return [string]::Equals($Left, $Right, $comparison)
}

function Test-IsChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Parent
    )

    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $parentPrefix = $Parent.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($parentPrefix, $comparison)
}

$repoRootPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-NoReparsePointPath -LiteralPath $repoRootPath -Description 'repository root'
$canonicalScriptRoot = Get-CanonicalPath -LiteralPath $PSScriptRoot
$canonicalRepoRoot = Get-CanonicalPath -LiteralPath $repoRootPath
$expectedPluginPath = [IO.Path]::GetFullPath(
    (Join-Path $canonicalRepoRoot 'plugins/mailkit-agent'))
Assert-NoReparsePointPath -LiteralPath $expectedPluginPath -Description 'plugin root'
$canonicalPluginRoot = Get-CanonicalPath -LiteralPath (
    $expectedPluginPath)
if (-not (Test-PathEquals -Left $canonicalPluginRoot -Right $expectedPluginPath) -or
    -not (Test-IsChildPath -Path $canonicalPluginRoot -Parent $canonicalRepoRoot)) {
    throw "Refusing to publish: resolved plugin root is not beneath the repository root."
}

$expectedServerPath = [IO.Path]::GetFullPath(
    (Join-Path $canonicalPluginRoot 'server'))
$outputRelativePath = 'plugins/mailkit-agent/server'
$outputPath = [IO.Path]::GetFullPath(
    (Join-Path $canonicalRepoRoot $outputRelativePath))
Assert-NoReparsePointPath -LiteralPath $outputPath -Description 'server output'

if (-not (Test-PathEquals -Left $outputPath -Right $expectedServerPath)) {
    throw "Refusing to publish: output is not the expected plugin server path."
}
if (-not (Test-IsChildPath -Path $outputPath -Parent $canonicalPluginRoot)) {
    throw "Refusing to publish: output is not a child of the plugin root."
}

$projectPath = Join-Path $canonicalRepoRoot 'src/MailKit.Agent.Mcp/MailKit.Agent.Mcp.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "MailKit Agent MCP project was not found beneath the repository root."
}

New-Item -ItemType Directory -Path $expectedServerPath -Force | Out-Null
Assert-NoReparsePointPath -LiteralPath $expectedServerPath -Description 'server output'
$canonicalServerPath = Get-CanonicalPath -LiteralPath $expectedServerPath
if (-not (Test-PathEquals -Left $canonicalServerPath -Right $expectedServerPath) -or
    -not (Test-IsChildPath -Path $canonicalServerPath -Parent $canonicalPluginRoot)) {
    throw "Refusing to publish: resolved server directory failed path validation."
}

foreach ($item in Get-ChildItem -LiteralPath $canonicalServerPath -Force) {
    $targetPath = Get-CanonicalPath -LiteralPath $item.FullName
    if (-not (Test-IsChildPath -Path $targetPath -Parent $canonicalServerPath)) {
        throw "Refusing to remove a target outside the validated server directory: $targetPath"
    }

    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Remove-Item -LiteralPath $targetPath -Force
    } else {
        Remove-Item -LiteralPath $targetPath -Recurse -Force
    }
}

Push-Location -LiteralPath $canonicalRepoRoot
try {
    # The Agent graph project-references the upstream MailKit/MimeKit repositories,
    # whose TargetFrameworks include net10.0. The plugin publishes only the Agent's
    # net8.0 target, so the net8.0 overrides below keep the publish buildable on an
    # 8.0.x-only SDK (NETSDK1045) without changing what is published.
    & dotnet publish src/MailKit.Agent.Mcp/MailKit.Agent.Mcp.csproj `
        --configuration Release `
        --framework net8.0 `
        --runtime $Runtime `
        --self-contained false `
        --output $outputRelativePath `
        -p:TargetFramework=net8.0 `
        -p:TargetFrameworks=net8.0
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}

# Post-publish verification: the framework-dependent server output must contain the
# Agent assemblies plus the upstream MailKit/MimeKit dependencies and the platform
# apphost, and the plugin root must keep declaring 'dotnet server/mailkit-agent.dll'.
$requiredServerFiles = @(
    'MailKit.Agent.Auth.dll',
    'MailKit.Agent.Mail.dll',
    'MailKit.Agent.Core.dll',
    'MailKit.dll',
    'MimeKit.dll',
    'mailkit-agent.dll',
    'mailkit-agent.runtimeconfig.json'
)
if ($Runtime -eq 'win-x64') {
    $requiredServerFiles += 'mailkit-agent.exe'
}

$missingServerFiles = @(
    $requiredServerFiles |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputPath $_) -PathType Leaf) }
)
if ($missingServerFiles.Count -gt 0) {
    throw "Published server output is missing required files: $($missingServerFiles -join ', ')"
}

$mcpDeclarationPath = Join-Path $canonicalPluginRoot '.mcp.json'
if (-not (Test-Path -LiteralPath $mcpDeclarationPath -PathType Leaf)) {
    throw "The plugin root is missing the .mcp.json server declaration."
}

$mcpDeclaration = Get-Content -LiteralPath $mcpDeclarationPath -Raw | ConvertFrom-Json
$serverDeclaration = $mcpDeclaration.'mailkit-agent'
$declaredArguments = @($serverDeclaration.args)
if ($null -eq $serverDeclaration -or
    $serverDeclaration.command -ne 'dotnet' -or
    $declaredArguments.Count -ne 1 -or
    $declaredArguments[0] -ne 'server/mailkit-agent.dll') {
    throw "The plugin .mcp.json must launch 'dotnet server/mailkit-agent.dll' from the plugin root."
}

Write-Host "Published and verified the plugin server output at $outputRelativePath."
