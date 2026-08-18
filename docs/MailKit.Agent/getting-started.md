# MailKit Agent foundation: getting started

MailKit Agent is an experimental Codex plugin in this repository. The foundation release runs a local .NET MCP server over stdio and supports health checks plus non-secret account-profile configuration. It does not yet connect to IMAP, POP3, or SMTP servers or access mailbox content. See the [capability matrix](capability-matrix.md) for the exact boundary.

This plugin is separate from the supported MailKit NuGet library. Applications that need the supported .NET mail-client API should continue to use the [`MailKit` NuGet package](https://www.nuget.org/packages/MailKit/).

## Build and publish locally

From the repository root, run:

```powershell
git submodule update --init --recursive
dotnet restore MailKit.Agent.sln
dotnet test MailKit.Agent.sln --configuration Release
./scripts/Publish-MailKitAgentPlugin.ps1 -Runtime win-x64
codex plugin marketplace add .
codex plugin marketplace list
```

The publish command above targets Windows x64. It places the local MCP server and its runtime dependencies under `plugins/mailkit-agent/server` for the plugin package.

## Install and check the plugin

After adding the repository marketplace:

1. Restart the Codex desktop app.
2. Install **MailKit Agent** from the **mailkit-agent-local** marketplace.
3. In a new task, first invoke `diagnostics_health`.

A healthy response identifies the MailKit Agent foundation server, reports stdio transport, and reports that no network listener is enabled. You can then use `account_list` to list profiles or `account_profile_put` to save a non-secret profile.

## Keep secrets out of chat

Account profiles contain connection settings only; they contain no secrets. Passwords, app passwords, access tokens, refresh tokens, and client secrets must never be pasted into chat. Secret storage, OAuth, mailbox connections, reading, writing, and sending are not implemented in this foundation release.
