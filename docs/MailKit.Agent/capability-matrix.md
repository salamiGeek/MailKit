# MailKit Agent 能力矩阵

此矩阵是实验性插件的权威能力边界。`已支持` 表示当前 MCP 服务器已经实现该能力，且所列自动化测试守护该行为；标记为 `计划中` 的项目在当前版本中没有对应的 MCP 工具，不得视为可用功能。

| 领域 | 能力 | MCP 工具 | MailKit API | 协议前置条件 | 风险 | 自动化测试 | 状态 |
|---|---|---|---|---|---|---|---|
| 诊断 | 健康检查 | `diagnostics_health` | 无 | 无 | 只读 | `ToolSchemaTests.FoundationToolsReturnStructuredContentAndInvalidPutIsSanitized`; `FoundationServerTests.FoundationToolsRunOverStdioWithIsolatedAccountStorage` | 已支持 |
| 账户 | 列出非秘密配置 | `account_list` | 无 | 无 | 只读 | `ToolSchemaTests.AllToolsAdvertiseSafeStructuredSchemas`; `FoundationServerTests.FoundationToolsRunOverStdioWithIsolatedAccountStorage` | 已支持 |
| 账户 | 保存非秘密配置 | `account_profile_put` | 无 | 无 | 可恢复写入 | `ToolSchemaTests.AllToolsAdvertiseSafeStructuredSchemas`; `JsonAccountProfileStoreTests.PutRejectsPlainTlsBeforeCreatingStorageArtifacts` | 已支持 |
| 账户 | 凭据状态查询 | `account_credential_status` | 无 | 已存储凭据 | 只读 | `ConnectionToolsTests.CredentialStatusReportsConfiguredKindWithoutSecretValues`; `CredentialCommandTests.StatusReportsOnlyWhetherCredentialExists` | 已支持 |
| 账户 | 本地凭据 CLI | 无（CLI：`account credential set/status/delete`） | Windows 凭据管理器 | 已保存账户档案 | 可恢复写入 | `CredentialCommandTests.SetReadsSecretLocallyAndUsesTheProfilesUsername`; `WindowsCredentialVaultTests.RoundTripsAndDeletesOnlyTheNamedCredential` | 已支持 |
| 连接 | IMAP 连接 | `account_connection_test`（protocols 含 `imap`） | `ImapClient` | IMAP 服务器、TLS、已存储凭据 | 只读 | `ConnectionToolsTests.TestForwardsAccountAndProtocolSubsetExactly`; `ProtocolConnectionTesterTests.TestReportsConnectionStateAndCapabilitiesThenCleansUp` | 已支持 |
| 连接 | POP3 连接 | `account_connection_test`（protocols 含 `pop3`） | `Pop3Client` | POP3 服务器、TLS、已存储凭据 | 只读 | `ConnectionToolsTests.TestDefaultsToEveryConfiguredProtocolWhenSubsetOmitted`; `ProtocolConnectionTesterTests.TestReportsConnectionStateAndCapabilitiesThenCleansUp` | 已支持 |
| 连接 | SMTP 连接 | `account_connection_test`（protocols 含 `smtp`） | `SmtpClient` | SMTP 服务器、TLS、已存储凭据 | 只读 | `ConnectionApplicationTests.TestReportsUnconfiguredRequestedProtocolWithoutCallingTester`; `SmtpGatewayTests.GatewayRejectsInsecureSmtpTlsModesBeforeConnecting` | 已支持 |
| 邮箱 | 文件夹浏览 | `folder_list` | `ImapFolder` | IMAP 连接 | 只读 | `MailboxToolsTests.FolderListMapsPolicyDenialToSanitizedEnvelope`; `ImapGatewayTests.ListFoldersDiscoversSelectableAndSpecialUseFolders` | 已支持 |
| 邮箱 | IMAP 分页列表 | `message_list` | `ImapFolder` | IMAP 连接 | 只读 | `MailboxToolsTests.MessageListForwardsPagingAndSurfacesOpaqueCursorAcrossPages`; `ImapGatewayTests.ListMessagesPagesNewestUidsFirstAndReturnsStableReferences` | 已支持 |
| 邮箱 | POP3 分页列表 | `pop3_message_list` | `Pop3Client` | POP3 连接与 UIDL 能力 | 只读 | `MailboxApplicationTests.Pop3ListUsesBoundCursorAndPop3Gateway`; `Pop3GatewayTests.ListUsesUidlListAndTopToReturnStablePagedEnvelope` | 已支持 |
| 邮箱 | 搜索 | `message_search` | `ImapFolder.Search` | IMAP 连接 | 只读 | `ImapGatewayTests.SearchBuildsOnlyTypedCriteriaAndPagesStableResults`; `MailboxApplicationTests.SearchCursorScopeUsesCanonicalCriteriaHash` | 已支持 |
| 邮件 | 读取（IMAP） | `message_read` | `ImapFolder` | IMAP 连接；默认标记 `\Seen` | 只读（默认写已读标记） | `MailboxToolsTests.MessageReadDefaultsToMarkAsReadAndForwardsArgumentsExactly`; `ImapGatewayTests.DefaultReadExplicitlyEnsuresSeenAfterBodyFetch` | 已支持 |
| 邮件 | 读取（POP3） | `pop3_message_read` | `Pop3Client` | POP3 连接；POP3 没有服务器端已读状态，永不标记已读 | 只读 | `MailboxToolsTests.Pop3ReadAlwaysForwardsMarkAsReadFalseAndReportsReadStateFields`; `Pop3GatewayTests.ReadReloadsUidlsAndUsesRelocatedNumericIndex` | 已支持 |
| 邮件 | 已读状态 | `message_mark_read` | `ImapFolder.Store`（`\Seen`） | IMAP 连接 | 可恢复写入 | `MailboxToolsTests.MessageMarkReadForwardsReferencesAndTargetFlag`; `ImapGatewayTests.MarkReadBatchesStableUidsAndExplicitlyRemovesSeen` | 已支持 |
| 附件 | 附件列表 | `attachment_list` | `ImapFolder` / `Pop3Client` | IMAP 或 POP3 连接 | 只读 | `MailboxToolsTests.AttachmentListForwardsReferenceAndReturnsUntrustedNames`; `AttachmentApplicationTests.ListUsesDescriptorOnlyGatewayOperation` | 已支持 |
| 附件 | 保存到下载根目录 | `attachment_save` | `ImapFolder` / `Pop3Client` + 本地文件 | IMAP 或 POP3 连接 | 可恢复写入（仅本地隔离目录） | `MailboxToolsTests.AttachmentSaveStoresPayloadInsideIsolatedDownloadRoot`; `AttachmentServiceTests.SavesAtomicallyWithoutLeavingTemporaryFiles` | 已支持 |
| 发送 | 发送（两阶段确认） | `send_prepare` + `send_commit` | `SmtpClient.Send` | SMTP 连接、显式用户确认、一次性令牌、本机人工批准弹窗（人工拒绝返回 `send.approval_declined`，无交互桌面返回 `send.approval_unavailable`，均不消耗令牌） | 外部影响或不可逆 | `SendToolsTests.PrepareBindsProcessSessionAndReturnsRedactedPreviewWithToken`; `SendToolsTests.CommitConsumesOneTimeTokenAndDeliversExactlyOnce`; `SendApplicationTests.DeclinedApprovalDoesNotConsumeTokenOrSendOrWriteLedger`; `SendApplicationTests.IndeterminateTransportOutcomeIsTerminalAndNeverResent` | 已支持 |
| 发送 | 发送状态 | `send_status` | 无（本地发送账本） | 已记录的幂等键 | 只读 | `SendToolsTests.StatusReportsTheDurableTerminalState`; `SendApplicationTests.GetStatusReturnsUnknownKeyFailureWithoutEchoingRawKey` | 已支持 |
| 邮件 | 写入（草稿） | 无对应工具 | 基础版不调用 | IMAP 连接及服务器所需能力 | 可恢复写入 | 未实现 | 计划中 — 后续计划 3：**可恢复写入和草稿** |
| 邮件 | 删除 | 无对应工具 | 不调用 | IMAP 连接 | 外部影响或不可逆 | 未实现 | 计划中 — 后续计划 4：**发送、永久删除、确认和幂等性** |
| 邮件 | 移动与归档 | 无对应工具 | 不调用 | IMAP 连接 | 可恢复写入 | 未实现 | 计划中 — 后续计划 3：**可恢复写入和草稿** |
| 身份验证 | OAuth | 无对应工具 | 不调用 | Gmail 或 Microsoft OAuth 客户端配置 | 外部影响或不可逆 | 未实现 | 计划中 — 后续计划 1：**账户保险库以及 Gmail/Microsoft OAuth** |
| 高级 IMAP | ACL、配额、元数据和注解 | 无对应工具 | 不调用 | IMAP 连接、相应的服务器能力和账户授权 | 因能力而异：从只读到外部影响或不可逆 | 未实现 | 计划中 — 后续计划 5：**ACL、配额、元数据、注解、POP3 高级操作和诊断** |

发送约束：发送的收件地址仅支持 ASCII（显示名支持 Unicode）；SMTPUTF8 服务器能力协商已实现，但当前经 MCP 不可达，属后续计划。发送提交阶段由服务器强制本机人工批准弹窗（人工拒绝返回 `send.approval_declined`；非 Windows 或无交互桌面快速返回 `send.approval_unavailable`，提示需在交互会话中重试；两种情况均不消耗确认令牌）。

当前版本不提供可执行任意 IMAP、POP3 或 SMTP 命令的工具，也不提供 POP3 的文件夹、搜索或已读状态操作（协议本身没有这些概念）。未来能力必须满足协议支持、安全策略、确认要求和自动化契约测试后，状态才能变更为 `已支持`。
