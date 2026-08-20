# MailKit Agent：上手指南

MailKit Agent 是本仓库中的实验性 Codex 插件。0.2.0 版本通过 stdio 运行本地 .NET MCP 服务器，提供 17 个 MCP 工具：非秘密账户配置、本地凭据 CLI、IMAP/POP3/SMTP 连接测试、IMAP 文件夹浏览、分页列表与服务器端搜索、邮件读取与已读标记、POP3 列表与读取、附件列表与保存，以及两阶段确认的 SMTP 发送。确切的能力边界请参阅[能力矩阵](capability-matrix.md)。

此插件与受支持的 MailKit NuGet 库相互独立。需要受支持的 .NET 邮件客户端 API 的应用程序应继续使用 [`MailKit` NuGet 包](https://www.nuget.org/packages/MailKit/)。

## 在本地构建和发布

在仓库根目录中运行：

```powershell
git submodule update --init --recursive
dotnet restore MailKit.Agent.sln
dotnet test MailKit.Agent.sln --configuration Release
./scripts/Publish-MailKitAgentPlugin.ps1 -Runtime win-x64
codex plugin marketplace add .
codex plugin marketplace list
```

上述发布命令以 Windows x64 为目标平台，并将本地 MCP 服务器及其运行时依赖项放入插件包的 `plugins/mailkit-agent/server` 目录。

## 安装并检查插件

添加仓库 marketplace 后：

1. 重新启动 Codex 桌面应用。
2. 从 **mailkit-agent-local** marketplace 安装 **MailKit Agent**。
3. 新建一个任务，并首先调用 `diagnostics_health`。

健康响应会标识 MailKit Agent 服务器，报告其使用 stdio 传输，并确认未启用网络监听器。随后可以使用 `account_list` 列出账户，用 `account_profile_put` 保存配置，再用 `account_connection_test` 验证 IMAP、POP3 和 SMTP 连接。

## 配置账户（非秘密 JSON）

使用 `account_profile_put` 保存非秘密配置。配置只包含端点和身份验证类型，绝不包含任何秘密字段：

```json
{
  "id": "personal",
  "display_name": "个人邮箱",
  "username": "user@example.com",
  "authentication": "password",
  "imap": { "host": "imap.example.com", "port": 993, "tls": "implicit_tls" },
  "pop3": null,
  "smtp": { "host": "smtp.example.com", "port": 587, "tls": "start_tls" },
  "send_mode": "confirm_dialog"
}
```

- `id` 由小写字母、数字、`_`、`-` 组成，长度 1 到 64，并且以小写字母或数字开头。
- `authentication` 当前只支持 `password`；`o_auth2` 仅为将来的 OAuth 支持保留，目前没有 OAuth 登录流程。
- 每个端点的 `tls` 只接受 `"implicit_tls"` 或 `"start_tls"`；`plain` 会被拒绝，MailKit Agent 不允许明文连接。
- `imap`、`pop3`、`smtp` 至少配置一项；不使用的协议保持 `null`。
- 可选字段 `"send_mode"` 决定确认后的发送提交方式，取值为 `confirm_dialog`（默认，弹本机批准对话框后经 SMTP 投递）或 `drafts`（把邮件保存到 IMAP 草稿箱，绝不投递）。省略该字段时按 `confirm_dialog` 处理；`drafts` 模式要求配置 `imap` 端点，否则 `send_prepare` 返回稳定的 `imap.not_configured` 错误。

## 配置凭据（本地 CLI，不要在聊天中输入秘密）

密码只能通过插件自带的本地 CLI 写入操作系统凭据存储。三个命令都要求 `--account <id>`：

```powershell
mailkit-agent account credential set --account <account-id>
mailkit-agent account credential status --account <account-id>
mailkit-agent account credential delete --account <account-id>
```

- `set` 以不回显的方式提示输入密码；`status` 只报告凭据是否已配置；`delete` 只删除该账户的凭据。
- 在 Windows 上，密码保存在 Windows 凭据管理器中，目标名称为 `MailKit.Agent/account/<account-id>/password`。MCP 工具从不接受、返回或回显密码值。
- 在没有受支持凭据存储的平台上，这些命令返回稳定的错误，而不泄露环境细节。
- 切勿在聊天中粘贴密码、应用专用密码、访问令牌、刷新令牌或客户端秘密。

## 工具总览

| 类别 | 工具 | 行为要点 |
|---|---|---|
| 诊断 | `diagnostics_health` | 报告服务器身份与 stdio 传输健康状态，不访问邮箱。 |
| 账户 | `account_list` | 列出已配置的非秘密账户档案。 |
| 账户 | `account_profile_put` | 新建或替换非秘密账户档案，绝不接受密码或令牌。 |
| 账户 | `account_credential_status` | 只报告某个账户是否已配置凭据。 |
| 账户 | `account_connection_test` | 用已存储的凭据测试 IMAP、POP3、SMTP 连接与身份验证。 |
| 邮箱 | `folder_list` | 列出 IMAP 文件夹；文件夹名称是不可信数据。 |
| 邮箱 | `message_list` | 按页列出文件夹中的 IMAP 邮件摘要，返回不透明 `next_cursor`。 |
| 邮箱 | `message_search` | 在一个文件夹内执行服务器端 IMAP 搜索，同样分页。 |
| 邮件 | `message_read` | 读取一封 IMAP 邮件，默认标记已读；`mark_as_read:false` 提供不变更状态的预览。 |
| 邮件 | `message_mark_read` | 显式设置 IMAP 邮件的已读或未读状态。 |
| 邮件 | `pop3_message_list` | 按页列出 POP3 邮件摘要，引用基于 UIDL。 |
| 邮件 | `pop3_message_read` | 读取一封 POP3 邮件；`body_mode` 可选 `safe_text`（默认）或 `html`。 |
| 附件 | `attachment_list` | 列出一封邮件的附件；附件文件名是不可信数据。 |
| 附件 | `attachment_save` | 把一个附件保存到下载根目录并返回存储路径，绝不打开或执行该文件。 |
| 发送 | `send_prepare` | 校验草稿并返回脱敏预览（含 `send_mode` 执行方式）和一次性确认令牌，此时不建立传输连接。 |
| 发送 | `send_commit` | 仅凭一次性 `confirmation_token` 完成用户确认过的发送；`confirm_dialog` 模式投递前强制本机人工批准弹窗，`drafts` 模式保存到草稿箱且绝不投递。 |
| 发送 | `send_status` | 按账户和幂等键报告持久发送状态（含 `drafted`）。 |

`message_search` 的 `criteria` 支持服务器端条件：`text`、`from`、`to`、`subject`、`since`、`before`、`unread`。列表与搜索的 `page_size` 上限为 100，翻页时把上一页返回的 `next_cursor` 原样传回。

## POP3 与 IMAP 的差异

- POP3 没有服务器端已读状态：`pop3_message_read` 永远不会标记已读，也不存在未读筛选或 `message_mark_read` 的等价工具。
- POP3 没有文件夹和搜索：没有对应的 `folder_list` 或 `message_search` 工具。
- POP3 引用基于 UIDL：`pop3_message_list` 返回的引用使用 UIDL 标识邮件，服务器必须支持 UIDL 能力。
- 翻页期间若服务器上的邮件被删除，基于序号的分页可能跳过一封现存邮件；需要精确遍历时建议一次取回或以 UIDL 为准自行比对。

## 附件与本地目录

- `attachment_save` 只把附件写入隔离的下载根目录并返回存储路径；保存结果绝不由代理打开、执行或渲染。
- 下载根目录默认是 `%LOCALAPPDATA%\MailKit.Agent\downloads`，可用环境变量 `MAILKIT_AGENT_DOWNLOAD_ROOT` 覆盖。
- 发送附件只能来自 `MAILKIT_AGENT_UPLOAD_ROOTS` 配置的上传根目录（多个目录用路径分隔符分隔）；未配置时不允许附加本地文件，根目录之外的路径会被拒绝。

## 发送与两阶段确认

发送始终分两步完成，执行方式由账户的 `send_mode` 决定（默认 `confirm_dialog`）：

1. `send_prepare`：校验草稿（收件人、主题、正文、附件路径），返回脱敏预览（收件人、主题、正文前 200 个字符、附件文件名、`send_mode` 执行方式）和一次性 `confirmation_token`。此阶段不获取凭据，也不建立传输连接。
2. 用户查看完整预览并明确确认后，调用 `send_commit`，只提交 `confirmation_token`。确认令牌在 10 分钟后过期，且只能使用一次。令牌的签名密钥与会话标识按数据目录持久保存（Windows 上以 DPAPI 保护，密钥从不明文落盘），因此 `send_prepare` 与 `send_commit` 之间服务器进程重启（例如宿主对每次调用拉起新的 stdio 进程）不影响提交；非 Windows 平台密钥保持每进程随机，重启会使未提交的令牌失效。
3. `send_commit` 按提交时账户的当前 `send_mode` 执行：
   - `confirm_dialog`（默认，即投递模式）：投递前会弹出本机确认对话框，Windows 桌面上必须由人工在对话框中批准后才会真正投递（对话框完整展示收件人、密送收件人、主题、正文预览、附件与过期时间，且仅在本机显示）。这是服务器内部强制执行的硬性门槛，不是调用方约定。
   - `drafts`（草稿模式）：提交不会投递邮件，也不会弹出本机批准对话框——服务器把编写完成的邮件（带 `\Draft` 标记）IMAP APPEND 到该账户的草稿箱；代理在此模式下永远无法投递。之后的流程是：预览→批准→保存到草稿箱→用户在邮件客户端审阅/修改/自行发送。若用户要求修改，就再次 `send_prepare`/`send_commit` 生成新草稿，旧草稿由用户自行管理。保存成功后 `send_status` 报告终态 `drafted`；找不到草稿文件夹时返回稳定的 `drafts.folder_not_found` 错误（ Capability 类别）。

`confirm_dialog` 模式的批准细节：

- 非 Windows 主机或没有交互桌面的会话（如无头服务会话）无法完成本机人工批准：`send_commit` 会快速返回稳定的 `send.approval_unavailable` 错误（Capability 类别），表示需要在前台交互会话中重试，而不是被人工拒绝。
- 在交互桌面上，本机确认对话框会一直等待，直到人工作出选择或调用方取消；人工拒绝返回稳定的 `send.approval_declined` 错误。
- 拒绝与批准不可用均不消耗确认令牌：不改写发送账本、不连接 SMTP；在令牌过期前，获得本机批准的再次提交仍可使用同一个 `confirmation_token` 完成发送。
- `drafts` 模式要求 `imap` 端点已配置：缺失时 `send_prepare` 直接返回稳定的 `imap.not_configured` 错误（Capability 类别），不会开始编写。草稿邮件只包含编写时的 To/Cc（密送收件人不出现在草稿正文中）。
- 每次发送绑定调用方选择的 `idempotency_key`（字符集 `A-Za-z0-9._-`，最长 128）：同一密钥不会投递第二封邮件，也不会保存第二份草稿。
- `send_status` 报告持久状态：`prepared`、`attempting`、`succeeded`、`drafted`、`failed`、`indeterminate`。
- 结果未知的发送（`indeterminate`，例如投递或保存中途连接断开）不会自动重试：先查询 `send_status`，再由用户决定如何处理。
- 收件地址仅支持 ASCII（显示名支持 Unicode）；SMTPUTF8 服务器能力协商已实现，但当前经 MCP 不可达，属后续计划。

## 尚未支持的能力

当前版本不支持删除、移动、归档邮件，不支持草稿管理（查看、编辑或删除既有草稿——发送的 `drafts` 模式只会新增草稿，旧草稿由用户自行处理），也不支持 OAuth 登录（Gmail/Microsoft OAuth 仍为计划中）。MailKit Agent 也不提供执行任意 IMAP、POP3 或 SMTP 命令的工具。完整边界请参阅[能力矩阵](capability-matrix.md)。
