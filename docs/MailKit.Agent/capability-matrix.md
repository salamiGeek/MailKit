# MailKit Agent foundation capability matrix

This matrix is the authoritative boundary for the experimental foundation plugin. `Supported` means the capability is implemented by the current MCP server. `Planned` rows have no foundation MCP tool and must not be treated as available.

| Domain | Capability | MCP tool | MailKit API | Protocol prerequisite | Risk | Automated test | Status |
|---|---|---|---|---|---|---|---|
| Diagnostics | Health | `diagnostics_health` | None | None | Read-only | `ToolSchemaTests.FoundationToolsReturnStructuredContentAndInvalidPutIsSanitized`; `FoundationServerTests.FoundationToolsRunOverStdioWithIsolatedAccountStorage` | Supported |
| Accounts | List non-secret profiles | `account_list` | None | None | Read-only | `ToolSchemaTests.FoundationToolsAdvertiseSafeStructuredSchemas`; `FoundationServerTests.FoundationToolsRunOverStdioWithIsolatedAccountStorage` | Supported |
| Accounts | Save non-secret profile | `account_profile_put` | None | None | Recoverable write | `ToolSchemaTests.FoundationToolsAdvertiseSafeStructuredSchemas`; `JsonAccountProfileStoreTests` | Supported |
| Connection | IMAP connection | None in foundation | None in foundation | IMAP server, TLS, and authentication | Read-only | Not implemented | Planned — follow-on plan 2, **MailKit connection manager and read-only IMAP/POP3 tools** |
| Connection | POP3 connection | None in foundation | None in foundation | POP3 server, TLS, and authentication | Read-only | Not implemented | Planned — follow-on plan 2, **MailKit connection manager and read-only IMAP/POP3 tools** |
| Connection | SMTP connection | None in foundation | None in foundation | SMTP server, TLS, and authentication | Read-only | Not implemented | Planned — follow-on plan 4, **send, permanent delete, confirmation, and idempotency** |
| Mailbox | Search | None in foundation | None in foundation | IMAP or POP3 connection; server capability varies | Read-only | Not implemented | Planned — follow-on plan 2, **MailKit connection manager and read-only IMAP/POP3 tools** |
| Message | Read | None in foundation | None in foundation | IMAP or POP3 connection | Read-only | Not implemented | Planned — follow-on plan 2, **MailKit connection manager and read-only IMAP/POP3 tools** |
| Message | Write | None in foundation | None in foundation | IMAP connection and required server capability | Recoverable write | Not implemented | Planned — follow-on plan 3, **recoverable writes and drafts** |
| Send | Send | None in foundation | None in foundation | SMTP connection | External or irreversible | Not implemented | Planned — follow-on plan 4, **send, permanent delete, confirmation, and idempotency** |
| Authentication | OAuth | None in foundation | None in foundation | Gmail or Microsoft OAuth client configuration | External or irreversible | Not implemented | Planned — follow-on plan 1, **account vault and Gmail/Microsoft OAuth** |
| Advanced IMAP | ACL, quota, metadata, and annotation | None in foundation | None in foundation | IMAP connection plus the corresponding server capability and account authorization | Varies: read-only to external or irreversible | Not implemented | Planned — follow-on plan 5, **ACL, quota, metadata, annotation, POP3 advanced operations, and diagnostics** |

The foundation does not expose an arbitrary IMAP, POP3, or SMTP command tool. Future capabilities remain subject to protocol support, safety policy, confirmation requirements, and automated contract tests before their status can change to `Supported`.
