[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AccountId,
    [Parameter(Mandatory)] [string] $Username,
    [Parameter(Mandatory)] [string] $ImapHost,
    [int] $ImapPort = 993,
    [Parameter(Mandatory)] [string] $Pop3Host,
    [int] $Pop3Port = 995,
    [Parameter(Mandatory)] [string] $SmtpHost,
    [int] $SmtpPort = 465,
    [string] $Recipient,
    [switch] $ConfirmMarkRead,
    [switch] $ConfirmSend
)

# Guarded wrapper around the explicit MailKit Agent live smoke fixture
# (tests/MailKit.Agent.Mcp.Tests/Live/LiveProtocolTests.cs).
#
# Safety contract:
#   * This script never accepts a password, token, or any other secret. The account
#     password is read only by the server process from the Windows Credential
#     Manager target 'MailKit.Agent/account/<account-id>/password'.
#   * It runs only the explicit live test filter, so normal test runs are unaffected.
#   * It uses an isolated non-secret data directory under the system temp directory
#     and validates that directory before removing it.
#   * -ConfirmSend requires -Recipient and a second interactive confirmation: the
#     send preview is printed and the operator must type SEND before the test run
#     that performs send_commit is started.
#   * IMAP/POP3/SMTP default to implicit_tls (ports 993/995/465); port 465 with
#     implicit_tls is the SMTP default. Override MAILKIT_AGENT_LIVE_SMTP_TLS (or the
#     IMAP/POP3 TLS variables) with start_tls for servers that need it.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ConfirmSend -and [string]::IsNullOrWhiteSpace($Recipient)) {
    throw '-ConfirmSend requires -Recipient. Refusing to prepare any send without an explicit recipient.'
}

foreach ($boundParameter in @($PSBoundParameters.Keys)) {
    if ($boundParameter -match '(?i)password|passwd|secret|token') {
        throw "Refusing the secret-looking parameter '-$boundParameter'. This script never accepts passwords or tokens."
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $repositoryRoot 'tests/MailKit.Agent.Mcp.Tests/MailKit.Agent.Mcp.Tests.csproj'
if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "MailKit Agent MCP test project was not found beneath the repository root."
}

$dataDirectory = Join-Path ([IO.Path]::GetTempPath()) ("mailkit-agent-live-{0}" -f ([Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $dataDirectory | Out-Null

# Non-secret settings forwarded to the test process. The optional selection
# variables (stable message UID/UIDL and attachment ID) are forwarded only when the
# operator already set them, after inspecting a read-only run's printed listings.
$selectionVariables = @(
    'MAILKIT_AGENT_LIVE_IMAP_UID',
    'MAILKIT_AGENT_LIVE_POP3_UIDL',
    'MAILKIT_AGENT_LIVE_ATTACHMENT_ID'
)
$scriptVariables = @(
    'MAILKIT_AGENT_LIVE_ACCOUNT_ID',
    'MAILKIT_AGENT_LIVE_USERNAME',
    'MAILKIT_AGENT_LIVE_DATA_DIR',
    'MAILKIT_AGENT_LIVE_IMAP_HOST',
    'MAILKIT_AGENT_LIVE_IMAP_PORT',
    'MAILKIT_AGENT_LIVE_POP3_HOST',
    'MAILKIT_AGENT_LIVE_POP3_PORT',
    'MAILKIT_AGENT_LIVE_SMTP_HOST',
    'MAILKIT_AGENT_LIVE_SMTP_PORT',
    'MAILKIT_AGENT_LIVE_RECIPIENT',
    'MAILKIT_AGENT_LIVE_CONFIRM_MARK_READ',
    'MAILKIT_AGENT_LIVE_CONFIRM_SEND'
) + $selectionVariables

$savedEnvironment = @{}
foreach ($name in $scriptVariables) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

$exitCode = 0
$runTests = $true
try {
    Write-Host "MailKit Agent live smoke test"
    Write-Host "  Account: $AccountId ($Username)"
    Write-Host "  IMAP:    $ImapHost`:$ImapPort (implicit_tls)"
    Write-Host "  POP3:    $Pop3Host`:$Pop3Port (implicit_tls)"
    Write-Host "  SMTP:    $SmtpHost`:$SmtpPort (implicit_tls)"
    Write-Host "  Data:    $dataDirectory (isolated, non-secret, removed afterwards)"
    foreach ($name in $selectionVariables) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            Write-Host "  Selected ${name}: $value"
        }
    }

    if ($ConfirmMarkRead) {
        Write-Host '  Phase:   read-only checks plus message_mark_read on the selected IMAP message.'
    }
    else {
        Write-Host '  Phase:   read-only checks only (no message_mark_read, no send_commit).'
    }

    if ($ConfirmSend) {
        Write-Host ''
        Write-Host 'Send preview (exactly what the live test will deliver to one recipient):'
        Write-Host "  To:      $Recipient"
        Write-Host "  Subject: MailKit Agent live send verification"
        Write-Host "  Body:    This MailKit Agent live verification message was sent by scripts/Test-MailKitAgentLive.ps1."
        Write-Host ''
        $reply = Read-Host 'Type SEND to confirm delivery of this single message'
        if (-not [string]::Equals($reply, 'SEND', [StringComparison]::Ordinal)) {
            Write-Host 'Send confirmation declined: no test run was started and nothing was sent.'
            $runTests = $false
            $exitCode = 1
        }
    }

    if ($runTests) {
        # Clear any inherited live variables first so stale values can never leak in,
        # then set exactly the non-secret inputs for this invocation.
        foreach ($name in $scriptVariables) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }

        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_ACCOUNT_ID', $AccountId)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_USERNAME', $Username)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_DATA_DIR', $dataDirectory)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_IMAP_HOST', $ImapHost)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_IMAP_PORT', [string] $ImapPort)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_POP3_HOST', $Pop3Host)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_POP3_PORT', [string] $Pop3Port)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_SMTP_HOST', $SmtpHost)
        [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_SMTP_PORT', [string] $SmtpPort)
        if (-not [string]::IsNullOrWhiteSpace($Recipient)) {
            [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_RECIPIENT', $Recipient)
        }
        if ($ConfirmMarkRead) {
            [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_CONFIRM_MARK_READ', 'yes')
        }
        if ($ConfirmSend) {
            [Environment]::SetEnvironmentVariable('MAILKIT_AGENT_LIVE_CONFIRM_SEND', 'yes')
        }

        # The net8.0 target overrides keep the test build graph (which references the
        # upstream MailKit/MimeKit repositories with a net10.0 target) buildable on an
        # 8.0.x-only SDK without changing which tests run.
        & dotnet test $testProject `
            --configuration Release `
            --filter 'FullyQualifiedName~MailKit.Agent.Mcp.Tests.Live.LiveProtocolTests' `
            --logger 'console;verbosity=detailed' `
            -p:TargetFramework=net8.0 `
            -p:TargetFrameworks=net8.0
        if ($LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
            Write-Warning "The live test filter failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    foreach ($name in $scriptVariables) {
        $original = $savedEnvironment[$name]
        if ([string]::IsNullOrEmpty($original)) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $original)
        }
    }

    # Validate the isolated directory before cleanup: it must be a direct child of
    # the system temp directory carrying this script's well-known prefix.
    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $canonicalTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $canonicalDataDirectory = [IO.Path]::GetFullPath($dataDirectory)
    $leafName = [IO.Path]::GetFileName(
        $canonicalDataDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar))
    if (-not $canonicalDataDirectory.StartsWith($canonicalTempRoot, $comparison) -or
        -not $leafName.StartsWith('mailkit-agent-live-', [StringComparison]::Ordinal)) {
        throw "Refusing to clean an unexpected live data directory: $canonicalDataDirectory"
    }

    if (Test-Path -LiteralPath $canonicalDataDirectory) {
        Remove-Item -LiteralPath $canonicalDataDirectory -Recurse -Force
    }
}

exit $exitCode
