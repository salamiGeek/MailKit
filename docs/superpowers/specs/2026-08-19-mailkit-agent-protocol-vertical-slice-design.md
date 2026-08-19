# MailKit Agent 三协议纵向切片设计

## 1. 背景与目标

MailKit Agent 基础版已经提供本地 stdio MCP Server、非秘密账户档案、稳定错误结果、分页引用、操作策略和 Codex Plugin 包装，但尚未连接邮件服务器。

本阶段以自定义邮箱服务器的实际可用性为最高优先级，在 Windows 上优先打通：

- IMAP 邮件获取、搜索、阅读、已读状态和附件下载；
- POP3 邮件列出、阅读和附件下载；
- SMTP 邮件预览、确认发送和发送结果查询；
- 密码或应用专用密码通过 Windows Credential Manager 安全存取；
- 三个协议的独立连接测试、脱敏错误和能力报告。

本阶段不建立后台同步、本地邮件数据库或离线索引。工具按需连接服务器并实时操作。

## 2. 非目标

本阶段不实现：

- 删除、移动、复制、归档、标签和任意旗标管理；
- 草稿管理；
- IMAP ACL、配额、元数据、注解、IDLE 和其他高级写操作；
- POP3 删除；
- Gmail、Microsoft 365 或自定义 OAuth 2.0 登录流程；
- macOS Keychain 或 Linux Secret Service；
- 任意原始 IMAP、POP3 或 SMTP 命令执行；
- 忽略 TLS 证书错误或明文连接；
- 自动执行附件、加载远程图片或跟踪像素。

已读状态是邮件阅读流程的一部分，不属于本阶段延后的邮件管理功能。

## 3. 总体架构

在 `codex/mailkit-agent-foundation` 分支上增加两个项目，并扩展现有项目：

### 3.1 `MailKit.Agent.Auth`

负责秘密的保存、读取、状态检查和删除。Core 只依赖抽象，MCP 不接触秘密值。

主要边界：

- `IAccountCredentialVault`：按账户 ID 读取、写入、检查和删除认证材料；
- `WindowsCredentialVault`：使用 Windows Credential Manager 的 Generic Credential；
- 本地交互式账户命令：从隐藏输入读取密码或应用专用密码；
- 凭据目标名称固定为 `MailKit.Agent/account/{account_id}/password`。

用户名来自非秘密账户档案。密码不得出现在 MCP Schema、命令行参数、普通配置文件、标准输出、日志、异常或测试快照中。

首期只实现 `password` 认证材料，但接口允许后续加入 OAuth token 集而不改变协议网关。

### 3.2 `MailKit.Agent.Mail`

通过 ProjectReference 使用仓库内 MailKit 公共 API，包含：

- `ConnectionManager`：账户与协议级并发、超时、取消、短生命周期连接和可靠断开；
- `ImapGateway`：文件夹发现、分页列表、服务器端搜索、消息读取和已读状态；
- `Pop3Gateway`：UIDL 列表和消息读取；
- `SmtpGateway`：连接验证和 MIME 消息发送；
- `MimeContentService`：正文选择、纯文本安全表示、HTML 受限表示、MIME 树和附件元数据；
- `AttachmentService`：受限路径内的原子附件保存；
- `ProtocolExceptionMapper`：把 MailKit 与网络异常映射为稳定、脱敏的 Agent 错误。

每个工具调用按需创建协议客户端、建立 TLS、认证、执行操作并断开。首期不保持后台连接，也不启动网络监听器。

### 3.3 `MailKit.Agent.Core`

继续保持协议实现无关，新增稳定 DTO、协议网关接口、应用用例、确认令牌、幂等记录接口和能力模型。Core 不引用具体 MailKit 客户端、Windows API 或 MCP SDK。

### 3.4 `MailKit.Agent.Mcp`

保持 Handler 轻量：绑定参数、调用 Core 用例并返回结构化结果。它只暴露邮箱语义工具，不暴露 MailKit 对象、连接、流或原始协议命令。

## 4. 账户与凭据

账户档案继续保存以下非秘密字段：

- `id`：小写字母、数字、连字符或下划线组成的稳定标识；
- `display_name`：用户可读别名；
- `username`：服务器登录用户名；
- `authentication`：首期必须为 `password`；
- 可选的 `imap`、`pop3` 和 `smtp` Endpoint；
- 每个 Endpoint 的 `host`、`port` 和 `tls`。

TLS 只允许 `implicit_tls` 或 `start_tls`。不允许 `plain`，也不允许自动从安全模式降级。

本地交互式命令为：

```powershell
mailkit-agent account credential set --account <account_id>
mailkit-agent account credential status --account <account_id>
mailkit-agent account credential delete --account <account_id>
```

`set` 从账户档案解析用户名并使用隐藏输入。目标名称由程序生成，用户无需手工拼接。`status` 只返回是否存在及凭据类型。`delete` 只删除精确匹配的 MailKit Agent 凭据。

允许用户通过 Windows Credential Manager GUI 手工创建相同目标名称的普通/通用凭据，但交互式命令是首选路径。

MCP 工具 `account_credential_status` 只返回 `configured`、`kind` 和非秘密诊断。MCP 不提供写入、读取或删除密码的工具。

## 5. MCP 工具范围

### 5.1 账户与连接

- `account_credential_status(account_id)`：检查凭据是否已配置；
- `account_connection_test(account_id, protocols?)`：分别测试已配置的 IMAP、POP3、SMTP Endpoint，返回每个协议的 TLS、认证和能力结果。

连接测试不得发送邮件、改变已读状态或下载正文。

### 5.2 IMAP

- `folder_list(account_id)`：列出可选择文件夹、属性和特殊用途；
- `message_list(account_id, folder, page_size?, cursor?)`：分页返回信封、日期、大小、旗标、附件提示和稳定引用；
- `message_search(account_id, folder, query, page_size?, cursor?)`：把受支持的结构化搜索下推给服务器；
- `message_read(reference, mark_as_read = true, body_mode?)`：读取正文、头部和 MIME 摘要，默认设置 `\\Seen`；
- `message_mark_read(references, is_read)`：批量设置已读或未读，受批量上限约束。

IMAP 稳定引用包含 `account_id + folder_id + uid_validity + uid`。执行操作前必须重新验证 UIDVALIDITY。游标必须有短期有效期并绑定账户、文件夹、查询和排序参数。

`message_read(mark_as_read = false)` 使用不改变 `\\Seen` 的读取方式。默认读取在可写文件夹中确保 `\\Seen` 已设置，并在结果中返回 `read_state_updated`。服务器或权限不允许更新时，正文可以返回，但必须附带结构化警告，不能声称已成功更新状态。

### 5.3 POP3

- `pop3_message_list(account_id, page_size?, cursor?)`：按 UIDL 分页列出邮件元数据；
- `pop3_message_read(reference, body_mode?)`：按 UIDL 重新定位并读取邮件。

POP3 稳定引用包含 `account_id + uidl`。索引只用于当前会话定位，不能作为跨请求引用。

POP3 不支持文件夹、通用服务器端搜索或服务器端已读状态。工具必须明确报告这些能力差异，不得模拟不存在的协议语义。

### 5.4 附件

- `attachment_list(message_reference)`：返回附件 ID、文件名、媒体类型、大小和内联属性；
- `attachment_save(message_reference, attachment_id, destination_name?)`：显式下载一个附件。

附件目标限制在配置的下载根目录。服务必须规范化最终路径、阻止路径穿越和链接逃逸、执行单文件与总量上限，并以临时文件加原子移动完成保存。附件不会自动打开、解析为指令或执行。

### 5.5 SMTP

- `send_prepare(account_id, message, idempotency_key)`：校验、规范化并预览邮件，返回短期确认令牌；
- `send_commit(confirmation_token)`：只发送令牌绑定的规范化消息；
- `send_status(account_id, idempotency_key)`：查询已知发送结果。

消息支持纯文本、HTML、To、Cc、Bcc 和受限本地附件。From 默认使用账户档案用户名；自定义 From 必须显式提供并通过地址校验。发送附件路径必须位于配置允许的上传根目录中。

`send_prepare` 返回收件人、主题、正文摘要、附件清单、规范化内容哈希、确定性 Message-Id、影响说明和短期确认令牌。令牌绑定账户、规范化消息哈希、幂等键、调用会话和过期时间，且只能成功使用一次。

`send_commit` 不接受可改变消息内容的字段。未经 prepare 或令牌过期时不得发送。

## 6. 数据流

### 6.1 读取

```text
MCP 参数校验
  -> 账户与策略检查
  -> 获取非秘密档案
  -> 从 Credential Manager 获取认证材料
  -> 获取协议连接租约
  -> TLS 连接与认证
  -> IMAP/POP3 操作
  -> MIME 安全转换
  -> 输出上限、脱敏和不可信数据标记
  -> 可靠断开与秘密清理
```

只读、幂等的网络操作可进行有限次数退避重试。已读状态更新不盲目重放；重试前必须重新获取并验证稳定引用。

### 6.2 发送

```text
send_prepare
  -> 校验账户、地址、正文和附件
  -> 构建规范化 MimeMessage
  -> 生成内容哈希、Message-Id 和确认令牌
  -> 返回精确预览

用户明确确认

send_commit
  -> 验证令牌、会话、TTL、内容哈希和幂等键
  -> 记录发送尝试
  -> SMTP TLS 连接与认证
  -> 发送完全相同的 MimeMessage
  -> 记录 succeeded / failed / indeterminate
```

发送记录只保存账户 ID、幂等键的不可逆摘要、Message-Id、状态、时间和关联 ID，不保存正文、主题、完整地址列表或附件内容。

若 SMTP 在服务器可能已接受 DATA 后断线，状态为 `indeterminate`。相同幂等键不得自动重发；用户必须通过 `send_status` 查看结果，并自行决定是否创建一封新的发送请求。

## 7. 安全模型

- 邮件主题、地址显示名、头部、正文、日历内容、HTML 和附件名称均为不可信数据；
- 邮件内容不能改变工具策略、触发发送或调用其他工具；
- 默认返回安全文本正文；HTML 作为受限数据返回，不执行脚本或加载远程资源；
- 密码只在协议认证所需的最短生命周期内存在，不进入模型上下文；
- TLS 必须验证证书链和主机名；不提供全局忽略证书错误开关；
- 认证失败不自动连续重试，避免触发账户锁定；
- 每个请求支持取消和超时；每账户、每协议和全局并发均有限制；
- 协议日志默认关闭；显式启用时仍经过 MailKit 秘密检测器和 Agent 脱敏层；
- 输出限制正文长度、单附件大小、总下载量、页面大小和批量标记数量；
- 所有文件操作验证解析后的最终路径位于允许根目录内。

## 8. 错误与能力模型

协议和平台异常统一映射为现有 `ToolError`：

- `validation`：账户、地址、游标、引用或路径无效；
- `authentication`：凭据缺失、拒绝或已失效；
- `authorization`：文件夹、旗标或服务器操作权限不足；
- `capability`：服务器或协议不支持请求的语义；
- `conflict`：UIDVALIDITY、UIDL、消息状态或确认状态已变化；
- `transient`：连接中断、超时、限流或临时服务器错误；
- `policy`：超过大小、并发、批量、路径或确认限制；
- `internal`：未分类异常的脱敏包装。

每个错误包含稳定代码、是否可重试、公开细节和关联 ID。完整协议响应、服务器堆栈、秘密、正文和附件内容不得进入工具错误或普通日志。

连接测试按协议返回独立结果；一个 Endpoint 失败不掩盖另两个协议的状态。

## 9. 连接与兼容策略

- Endpoint 的 TLS 模式必须显式配置；
- `implicit_tls` 直接建立 TLS，`start_tls` 要求升级成功后才能认证；
- 不根据常见端口猜测或静默改写 TLS 模式；
- 连接超时、认证超时和命令超时分别配置并设硬上限；
- 短生命周期客户端在异常、取消和正常路径上都必须释放；
- IMAP 根据实际能力选择搜索、UTF-8 和分页策略，受控降级必须在结果中可见；
- POP3 只承诺 UIDL 可用时的跨请求稳定引用；缺少 UIDL 时只允许当前会话诊断，不提供可误认为稳定的引用；
- SMTP 发送能力在 prepare 阶段检查，包括消息大小、SMTPUTF8 和服务器声明的限制。

## 10. 测试策略

### 10.1 单元测试

- 账户档案与凭据目标名称校验；
- 使用内存 Fake Vault 验证秘密不会穿透 Core 或 MCP；
- Windows Credential Manager 适配器使用唯一测试前缀做真实写入、读取状态和清理测试；
- 引用、游标、确认令牌、TTL、一次性消费和内容绑定；
- 幂等发送状态机与 `indeterminate` 分支；
- MIME 正文选择、HTML 限制和附件 ID；
- 下载与上传根目录、路径穿越、链接逃逸、原子保存和大小上限；
- 异常映射与所有错误字段的脱敏。

真实 Windows Credential Manager 测试只能访问精确测试前缀，并在 finally 中清理；不得枚举或读取用户的其他凭据。

### 10.2 协议脚本测试

沿用 MailKit 仓库现有的可重复会话脚本模式，不依赖 CI 中的真实邮件服务器：

- IMAP：TLS、认证、文件夹、分页、搜索、UIDVALIDITY、正文、附件、`\\Seen` 更新和只读文件夹；
- POP3：TLS、认证、UIDL、列表、读取、断线和缺少 UIDL；
- SMTP：TLS、认证、纯文本、HTML、附件、SMTPUTF8、大小限制、服务器拒绝以及 DATA 前后断线；
- 三协议：取消、超时、能力差异、认证失败和秘密日志检测。

### 10.3 MCP 契约与进程测试

- 固定工具名称、输入 Schema、结构化输出和稳定错误码；
- 验证 MCP Schema 不包含 password、token、secret 或等价秘密输入；
- 通过 stdio 启动发布后的插件，使用 Fake Gateway 完成读取、附件、已读状态和两阶段发送；
- 验证未确认、令牌篡改、令牌过期和重复 commit 均不能发送；
- 验证邮件中的提示注入文本不会改变策略或触发外部操作。

### 10.4 可选真实服务器冒烟测试

真实服务器测试只在用户本机显式触发，使用已存在的非秘密账户档案和 Windows Credential Manager 凭据。测试配置、邮箱地址、服务器返回和凭据均不提交到仓库或 CI。

执行顺序：

1. 分别测试 IMAP、POP3 和 SMTP 的 TLS 与认证；
2. IMAP 列表、搜索、读取并验证 `mark_as_read` 的两个分支；
3. POP3 通过 UIDL 列出并读取同一邮件；
4. 下载一个用户选定的小附件到隔离测试目录；
5. SMTP 向用户明确指定的测试收件人执行 prepare；
6. 只有用户检查预览并明确确认后才 commit；
7. 使用同一幂等键重试并验证没有重复投递。

冒烟测试不删除、移动、归档或修改除已读状态以外的任何服务器数据。

## 11. 验收标准

- 自定义账户能够独立测试 IMAP、POP3 和 SMTP 连接；
- IMAP 能列文件夹、分页列邮件、搜索、读取正文和附件元数据；
- IMAP 阅读默认设置已读，显式关闭时保持未读，并能批量设置已读或未读；
- POP3 能通过 UIDL 稳定列出和读取邮件，并明确报告无服务器端已读状态；
- IMAP 和 POP3 附件可安全保存且不能越过下载根目录；
- SMTP 能预览并发送纯文本、HTML 和附件邮件；未经明确确认不能发送；
- 相同幂等键不会重复投递，模糊结果不会自动重发；
- TLS、认证、能力和网络错误均结构化且脱敏；
- 密码不进入账户档案、MCP Schema、日志、错误、测试快照或提交内容；
- Agent 解决方案的 Release 构建、单元测试、协议测试、MCP 契约测试和发布检查全部通过。

## 12. 后续阶段

首期验收后，按独立规格继续：

1. Gmail、Microsoft 365 和自定义 OAuth 2.0，以及 macOS/Linux 安全凭据实现；
2. IMAP 移动、复制、归档、删除、标签、任意旗标和草稿；
3. 高级 IMAP ACL、配额、元数据、注解、IDLE 和增强诊断；
4. POP3 受控删除及其他服务器特有能力。

这些阶段复用本设计的 Core 接口、协议网关、安全策略和错误模型，不改变首期工具的既有语义。
