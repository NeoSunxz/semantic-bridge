# Semantic Bridge for Power BI（中国区 / 世纪互联）

[English README (global cloud)](README.md) - 本文是中国区（世纪互联）指南；全球云请看英文版 `README.md`。

Semantic Bridge for Power BI 是一个命令行工具（`sbridge`）加一个 MCP 服务器，让你读取和查询已有的 Power BI 语义模型。它使用你自己创建并掌控的 Microsoft Entra 服务主体，通过 XMLA 端点和 Power BI REST API 访问数据，并内置全球云和中国区（世纪互联）两套预设。

本项目与微软无关联，也未获微软认可。Power BI 是微软的商标。

> **状态：v0.1.0，首个正式版本。** [v1 计划](docs/superpowers/plans/2026-11-04-v1-plan.md)的第 1–5 阶段已实现。目前的实机验证只覆盖 linux-x64 加中国区（世纪互联）租户；全球云尚未实机验证。见[状态与平台](#状态与平台)。

## 它是什么，不是什么

它是：

- 一个程序，两个前端，共用同一份配置和同一套行为：命令行 `sbridge`，以及 `sbridge mcp`（通过 stdio 或 Streamable HTTP 提供的 MCP 服务器）。
- 对工作区、数据集、模型元数据（表、列、带 DAX 的度量值、关系）、DAX 查询和刷新历史的读取能力。
- 少量写操作：更新表、列、度量值的描述，以及发起数据集刷新。两者默认关闭，需要设置 `allow_write: true`（见[写入风险警告](#写入风险警告)）。

它不是（设计文档 §3）：

- 业务逻辑的来源。没有术语表、指标定义、提示词工程、数据集路由，也不做自然语言转 DAX。DAX 由 AI 客户端编写，Semantic Bridge 只负责执行。只接受以 `DEFINE` 或 `EVALUATE` 开头的查询。
- 报表工具。没有报表、图表、文件存储、通知或聊天界面。
- 模型编辑工具。除描述外，不能创建、删除或重命名对象，也不能编辑 DAX 表达式、关系、角色和分区。
- 按用户登录的工具。v1 只使用服务主体，HTTP 服务器不识别最终用户。
- 读取磁盘上 Power BI Desktop、PBIP 或 TMDL 文件的工具。
- 感知行级安全或角色的工具。v1 不处理 RLS 或角色。

## 与微软 Power BI MCP 服务器的对比

截至 2026-11，依据[设计文档 §1](docs/superpowers/specs/2026-11-03-semantic-bridge-design.md)（其中引用了 Microsoft Learn）。微软的产品会变化，使用本表之前请先查看他们的文档。

| 微软产品 | 本项目补上的空缺 |
|---|---|
| Fabric IQ MCP（consumption） | 主权云不可用（因此中国区 / 世纪互联不可用）；不支持服务主体或仅应用身份认证；默认 250 行 |
| 托管的 Power BI Consumption / Authoring MCP（`api.fabric.microsoft.com/...`） | 只有全球云端点；每个用户都要做 Entra OAuth，依赖动态客户端注册的客户端（例如 Claude Desktop）需要手动注册应用 |
| 本地 Power BI Authoring MCP（`microsoft/powerbi-modeling-mcp`） | 面向模型编辑而非查询；不支持 macOS；对主权云的支持没有文档说明（未验证） |

本项目围绕这些差异而建：全球云和中国区（世纪互联）预设；使用你自己凭据的服务主体（仅应用身份）认证；Windows、macOS、Linux 的发布二进制（见[状态与平台](#状态与平台)）；除 MCP 服务器外还有命令行；以及可自行托管。

## 状态与平台

- 第 1–5 阶段已实现，并已发布为 v0.1.0（GitHub Releases 和 GHCR 镜像）。
- 2026-11-07 在 linux-x64 上，针对中国区（世纪互联）的 Premium Per User（PPU）工作区做了实机验证：XMLA（使用 MSAL 访问令牌认证）、REST、schema、查询、刷新、刷新状态、描述写入、stdio 和 HTTP 两种 MCP，以及 linux-x64 单文件二进制。
- 全球云尚未实机验证。Pro（仅 REST）工作区尚未在真实租户上测试。
- 其他 RID 上的 XMLA 未验证：`linux-arm64`、`win-x64`、`win-arm64`、`osx-arm64`、`osx-x64`。
- 发布二进制针对六个 RID 构建：`linux-x64`、`linux-arm64`、`win-x64`、`win-arm64`、`osx-arm64`、`osx-x64`。CI 会检查每一个都能启动并完成 MCP stdio 握手。CI 不会调用 REST API 或 XMLA。

## 安装

### 发布二进制

从 [GitHub Releases](https://github.com/NeoSunxz/semantic-bridge/releases) 页面下载对应平台的压缩包。每个压缩包里有单文件程序 `sbridge`（Windows 上是 `sbridge.exe`），以及许可文件（`LICENSE`、`licenses/` 和 `THIRD-PARTY-NOTICES.md`）。二进制是自包含的，不需要安装 .NET。运行前请对照 `SHA256SUMS` 校验压缩包。发布流程不对二进制签名，所以首次运行时操作系统可能会给出警告。

### .NET 工具

```bash
dotnet tool install -g semantic-bridge
```

这会安装 `sbridge` 命令，需要 .NET 10 运行时。只有设置了 `NUGET_API_KEY` 机密时，发布流程才会把包推送到 NuGet，所以目前尚未发布。

### Docker 镜像

```bash
docker pull ghcr.io/neosunxz/semantic-bridge:latest
docker pull neosunxz/semantic-bridge:latest
```

只有配置了 Docker Hub 机密时才会推送 Docker Hub 镜像，所以目前只发布了 GHCR 镜像。镜像为 `linux/amd64` 和 `linux/arm64` 构建。版本标签和 `latest` 在发布标签时创建。见 [Docker 与 HTTP 传输](#docker-与-http-传输)。

## 设置

### 1. 在 Microsoft Entra ID 中注册应用

1. 在要查询的租户里创建应用注册。
2. 记下租户 ID 和应用（客户端）ID。
3. 创建客户端密码。使用密码的**值**，不是密码 ID。密码过期后需要重新创建。
4. 把密码放进环境变量 `SBRIDGE_CLIENT_SECRET`（见[配置](#配置)）。

应用注册和租户必须属于 `cloud` 中设置的云。如果认证失败，请检查 `cloud` 是否与租户所在的云一致。中国区请设置 `cloud: china`（或 `SBRIDGE_CLOUD=china`）。

中国区请在由世纪互联运营的 Entra 租户中注册应用，使用 Azure 中国区门户（portal.azure.cn）。

应用不需要任何 Power BI API 权限，也不需要在 Entra 中做管理员同意。访问权限来自 Power BI 租户设置（第 2 步）和工作区角色（第 3 步）。

### 2. 启用租户设置

由 Power BI 或 Fabric 管理员在 Power BI 管理门户（app.powerbi.cn）中启用这些设置。把设置授予包含该应用的安全组。设置名称保持微软的英文原名，因为命令行的错误提示也用英文名。

- **Allow service principals to use Power BI APIs**（位于 Developer settings）：允许服务主体使用 Power BI API。任何服务主体访问都需要它。见 [Datasets - Execute Queries In Group](https://learn.microsoft.com/zh-cn/rest/api/power-bi/datasets/execute-queries-in-group)。
- **Allow XMLA endpoints and Analyze in Excel with on-premises semantic models**：允许 XMLA 端点。XMLA 需要它：schema、容量工作区上的 DAX 以及描述写入。见 [Semantic model connectivity with the XMLA endpoint](https://learn.microsoft.com/zh-cn/fabric/enterprise/powerbi/service-premium-connect-tools)。
- **Dataset Execute Queries REST API**（位于 Integration settings）：允许通过 REST 执行 DAX 查询。Pro 路径通过 REST 执行 DAX，需要它。见上面的 Execute Queries 页面。

### 3. 给应用分配工作区角色

把服务主体添加到每个要查询的工作区。微软列出的工作区角色有 Admin、Member、Contributor 和 Viewer，服务主体可以被分配这些角色（[Roles in workspaces](https://learn.microsoft.com/zh-cn/fabric/fundamentals/roles-workspaces)）。项目的错误提示建议 Member 或更高，因为 Viewer 无法读取模型元数据。通过 REST 执行 DAX 时，微软要求对数据集有读取和生成（build）权限（见上面的 Execute Queries 页面）。

### 4. XMLA 所需的容量

读取 schema、通过 XMLA 执行 DAX 和写入描述，都需要工作区位于 Premium、Premium Per User（PPU）、Embedded 或 Fabric 容量上。在 Premium 容量上，由容量管理员把 XMLA Endpoint 设为 Read Only 或 Read Write。PPU 的设置在 Premium Per User 设置中（见上面的 XMLA 页面）。写入描述需要 Read Write。

Pro 工作区没有 XMLA 端点。`query` 和 `refresh` 在那里通过 REST 可用，但 `schema` 和 `describe` 不可用（见 [Pro 工作区与 REST 限制](#pro-工作区与-rest-限制)）。

### 各云说明

| | `cloud: global` | `cloud: china`（世纪互联） |
|---|---|---|
| 认证地址（`authority_host`） | `https://login.microsoftonline.com` | `https://login.partner.microsoftonline.cn` |
| 令牌范围（`token_scope`） | `https://analysis.windows.net/powerbi/api/.default` | `https://analysis.chinacloudapi.cn/powerbi/api/.default` |
| REST 基址（`rest_base_url`） | `https://api.powerbi.com/v1.0/myorg` | `https://api.powerbi.cn/v1.0/myorg` |
| XMLA 基址（`xmla_base`） | `powerbi://api.powerbi.com/v1.0/myorg/{workspace}` | `powerbi://api.powerbi.cn/v1.0/myorg/{workspace}` |

`cloud` 默认是 `global`。中国区请在配置档案里设置 `cloud: china`，或设置 `SBRIDGE_CLOUD=china`。中国区预设（包括 XMLA 基址）已在 linux-x64 上实机验证；全球预设尚未实机验证。

若要使用其他云，设置 `cloud: custom` 并给出全部四个端点键。端点必须是 `https` 地址；自定义云可以使用本机回环的 `http` 地址。`xmla_base` 必须以 `powerbi://` 或 `asazure://` 开头，并且包含 `{workspace}`。

## 配置

`sbridge config init` 会写出带注释的模板。`sbridge config show` 会打印解析后的值以及每个值的来源，密码会被遮盖。

### 配置文件位置

| 操作系统 | 默认路径 |
|---|---|
| Windows | `%APPDATA%\semantic-bridge\config.yaml` |
| Linux、macOS | `~/.config/semantic-bridge/config.yaml` |

用 `--config <path>` 或 `SBRIDGE_CONFIG` 指定其他文件。如果指定的文件不存在，命令会失败。

### 配置档案（Profile）

一个文件可以在 `profiles:` 下放多个命名的配置档案。选择档案的顺序是：

1. `--profile <name>`
2. `SBRIDGE_PROFILE`
3. 文件里的 `default_profile`
4. 文件里唯一的那个档案
5. 文件里没有档案，但设置了 `SBRIDGE_TENANT_ID`、`SBRIDGE_CLIENT_ID` 或 `SBRIDGE_CLIENT_SECRET`（纯环境变量方式，供 Docker 和 CI 使用）。三项凭据都需要。`SBRIDGE_CLOUD`、`SBRIDGE_WORKSPACE` 和 `SBRIDGE_DATASET` 是可选的，单独设置它们不会进入这种模式。

如果文件里有多个档案又没有 `default_profile`，命令会失败。档案名匹配时不区分大小写。文件中的未知键会被拒绝。

### 配置键

每个档案下的键：

| 键 | 默认值 | 说明 |
|---|---|---|
| `default_profile` | 无 | 顶层键。未选择档案时使用的档案。 |
| `cloud` | `global` | `global`、`china` 或 `custom`。 |
| `tenant_id` | 无 | 必填。 |
| `client_id` | 无 | 必填。 |
| `client_secret` | 无 | 明文密码。可以用，但不建议。 |
| `client_secret_env` | 无 | 保存密码的环境变量名。 |
| `authority_host` | 云预设 | `custom` 必填。 |
| `token_scope` | 云预设 | `custom` 必填。 |
| `rest_base_url` | 云预设 | `custom` 必填。 |
| `xmla_base` | 云预设 | `custom` 必填。必须包含 `{workspace}`。 |
| `default_workspace` | 无 | 名称或 ID，未给出 `-w` 或 `workspace` 时使用。 |
| `default_dataset` | 无 | 名称或 ID，未给出 `-d` 或 `dataset` 时使用。MCP 写工具不使用它（见 MCP 服务器一节）。 |
| `allow_write` | `false` | 写操作必须设为 `true`（见[写入风险警告](#写入风险警告)）。 |
| `max_rows` | `1000` | 每次查询的默认行数上限。必须在 1 和 `max_rows_ceiling` 之间。 |
| `max_rows_ceiling` | `100000` | 硬上限，范围 1 到 100000。超过它的请求会被截到上限并报告。 |
| `query_timeout_seconds` | `120` | 限制在 1–600。 |
| `default_via` | `auto` | `auto`、`xmla` 或 `rest`。 |

密码按以下顺序取值：先是 `SBRIDGE_CLIENT_SECRET`，再是 `client_secret_env` 指定的变量，最后是文件里的 `client_secret`。

### 环境变量

| 变量 | 用途 |
|---|---|
| `SBRIDGE_CONFIG` | 配置文件路径。 |
| `SBRIDGE_PROFILE` | 要使用的档案。 |
| `SBRIDGE_TENANT_ID` | 覆盖 `tenant_id`。 |
| `SBRIDGE_CLIENT_ID` | 覆盖 `client_id`。 |
| `SBRIDGE_CLIENT_SECRET` | 覆盖客户端密码。 |
| `SBRIDGE_CLOUD` | 覆盖 `cloud`。 |
| `SBRIDGE_WORKSPACE` | 覆盖 `default_workspace`。 |
| `SBRIDGE_DATASET` | 覆盖 `default_dataset`。 |
| `SBRIDGE_HTTP_TOKEN` | `sbridge mcp --http` 的 Bearer 令牌。 |
| `SBRIDGE_HTTP_NO_AUTH=1` | 等同于 `--no-auth`。 |
| `SBRIDGE_ALLOW_HTTP_WRITE=1` | 等同于 `--allow-http-write`。 |

优先级从高到低：命令行参数、环境变量、配置文件、云预设或内置默认值。只有上表中的设置有对应的环境变量。端点和其他键只从文件或预设读取。

不要把密码写进文件。请设置 `SBRIDGE_CLIENT_SECRET`，或用 `client_secret_env` 指定另一个变量。`config show` 把密码显示为 `*** (set)` 或 `(not set)`。

### 示例

```yaml
default_profile: china

profiles:
  china:
    cloud: china
    tenant_id: "00000000-0000-0000-0000-000000000000"
    client_id: "00000000-0000-0000-0000-000000000000"
    default_workspace: "My Workspace"
```

密码放在 `SBRIDGE_CLIENT_SECRET` 里，这就是全部设置。运行 `sbridge config test` 检查。

## 命令行参考

所有命令都接受的全局选项：`--config <path>`（环境变量 `SBRIDGE_CONFIG`）和 `--profile <name>`（环境变量 `SBRIDGE_PROFILE`）。`sbridge --version` 打印版本后退出。

输出写到 stdout。诊断信息、提示和错误写到 stderr。

工作区和数据集可以用名称或 ID 指定。名称匹配到多个对象时会报错并要求使用 ID。设置了 `default_workspace` 时可以省略 `-w`，设置了 `default_dataset` 时可以省略 `-d`。`describe` 和 `refresh` 也会使用默认数据集，并打印实际解析到的数据集。

| 命令 | 用途 | 主要选项 |
|---|---|---|
| `sbridge config init` | 把带注释的配置模板写到配置路径。 | `--force`（覆盖） |
| `sbridge config show` | 打印解析后的档案及每个值的来源。 | 无 |
| `sbridge config test` | 检查令牌、REST，以及给出工作区时检查 XMLA。 | `-w` |
| `sbridge workspaces` | 列出服务主体能看到的工作区（REST）。 | `-f table\|json\|csv` |
| `sbridge datasets` | 列出工作区里的数据集（REST）。 | `-w`、`-f` |
| `sbridge schema` | 表、列、带 DAX 的度量值和关系（仅 XMLA）。 | `-w`、`-d`、`--table`、`-f table\|json`、`--via` |
| `sbridge query` | 执行 DAX 查询，可作为参数、用 `--file` 或从 stdin 给出。必须以 `DEFINE` 或 `EVALUATE` 开头。 | `-w`、`-d`、`--file`、`-f table\|json\|csv`、`--max-rows`、`--via` |
| `sbridge describe`（写） | 更新线上模型中表、列或度量值的描述（XMLA）。 | `-w`、`-d`、`--table`、`--column`、`--measure`、`--file`、`-y, --yes`、`-f table\|json` |
| `sbridge refresh`（写） | 发起数据集刷新（REST）。 | `-w`、`-d`、`--wait`、`--timeout`（默认 `600`）、`-y, --yes` |
| `sbridge refresh-status` | 刷新历史，最新的在前（REST；不需要 `allow_write`）。 | `-w`、`-d`、`--top`（1–100，默认 `5`）、`-f` |
| `sbridge mcp` | 为启动时选定的档案运行 MCP 服务器。 | `--http`、`--host`（默认 `127.0.0.1`）、`--port`（默认 `8080`）、`--no-auth`、`--allow-http-write` |

表格里看不出来的行为说明：

- **查询输出。** 表格或 CSV 格式下，数据行写到 stdout。stderr 得到一行摘要（`-- N row(s), path: xmla|rest, M ms`）、结果被截断时的 `note: result is TRUNCATED.` 行，以及每条警告对应的 `warning:` 行。JSON 输出把所有内容放在 stdout：`columns`、`rows`、`row_count`、`truncated`、`truncation_reason`、`path`、`elapsed_ms` 和 `warnings`。列名按引擎返回的原样给出，例如 `Sales[Amount]` 和 `[Total Sales]`。走 REST 时，`executeQueries` 不返回列类型，列类型是根据 JSON 值推断的，所以日期/时间列显示为 `String`；两条路径下日期/时间值都使用相同的 `yyyy-MM-ddTHH:mm:ss[.fff]` 格式。
- **截断一定会报告。** 因行数上限被截断的结果，在 JSON 中标记为 `truncated`，在表格或 CSV 输出中显示为 `note:` 行。超过 `max_rows_ceiling` 的 `--max-rows` 会被截到上限，并给出警告。元数据超过 100,000 行时，`schema` 会在警告中说明 schema 不完整。
- **每个查询只含一个 EVALUATE。** 一个查询应只包含一个 `EVALUATE`。多余的结果集（来自后续的 `EVALUATE` 语句）不会返回，结果会标记为 `truncated` 并给出原因。命令行和 MCP 的 `run_dax` 工具、XMLA 和 REST 两条路径都是如此。
- **`--via auto`。** `auto` 使用 XMLA。只有在已知工作区不在专用容量上时才使用 REST，并附带一条警告。XMLA 出错后它不会改用 REST 重试。`schema --via rest` 会被拒绝。
- **确认。** `describe` 和 `refresh` 在做出更改前会先询问。`-y` 或 `--yes` 跳过询问。当 stdin 不是终端且没有给 `--yes` 时，两者都会在发起任何网络调用之前拒绝，退出码为 `2`。`describe` 在 stderr 打印每处更改的 `old -> new`，并列出未变化的目标。如果你回答否，它以 `1` 退出且不做任何更改。
- **`refresh --wait`。** 只有最终状态为 `Completed` 时命令才以 `0` 退出。刷新失败、超过 `--timeout`（1–86400 秒，默认 600），或在历史记录中找不到该刷新时，以 `1` 退出。最后一种情况会打印 `status: unknown`：结果未知，刷新可能仍在运行。
- **退出码。** `0` 成功。`1` 运行时错误：请求失败、`config test` 某一步失败、你对确认回答否，或刷新失败、超时或结果未知。`2` 用法或配置错误：选项或值有误、未知配置键、缺少凭据或档案、写操作被禁用，或 HTTP 绑定需要令牌。`130` 被 Ctrl-C 取消。

## MCP 服务器

用 `sbridge mcp` 启动服务器。客户端通常会替你启动它。见下面的客户端配置。

### 工具

读取类工具始终注册。写入类工具只在启用写入时才注册，否则 agent 看不到它们。

| 工具 | 写入 | 输入 |
|---|---|---|
| `list_workspaces` | 否 | 无 |
| `list_datasets` | 否 | `workspace`（可选） |
| `get_model_schema` | 否 | `dataset`（可选）、`workspace`（可选）、`table`（可选）。仅 XMLA。 |
| `run_dax` | 否 | `dax`、`dataset`（可选）、`workspace`（可选）、`max_rows`（可选）、`via`（可选：`auto`、`xmla` 或 `rest`） |
| `get_refresh_status` | 否 | `dataset`（可选）、`workspace`（可选）、`top`（可选，1–100，默认 5） |
| `update_description` | 是 | `dataset`、`target_type`（`table`、`column` 或 `measure`）、`target`、`description`、`workspace`（可选） |
| `refresh_dataset` | 是 | `dataset`、`workspace`（可选） |

读取类工具在省略 `dataset` 时使用 `default_dataset`。写入类工具（`update_description`、`refresh_dataset`）始终要求给出 `dataset`，避免 AI 客户端在未明确指定时修改默认数据集。

结果是 JSON 文本。`run_dax` 的结果字段与命令行 JSON 输出相同，被截断时还会带一个 `note` 字段。失败会以工具错误的形式返回，消息中已去除机密，适用时附带提示。调用时缺少参数、参数名未知或类型错误，会返回指出参数名并列出应有参数的工具错误（不会回显参数值）。

工具描述会引导工作流程：`list_workspaces`，然后 `list_datasets`，然后 `get_model_schema`，然后 `run_dax`。优先使用聚合而不是原始行，并检查 `truncated` 和 `warnings`。

### 客户端配置

下面是常见格式。如果格式有变化，请查看你的客户端文档。每个示例都用参数 `mcp` 启动 `sbridge`。客户端能看到的 PATH 里必须有 `sbridge`，或者使用二进制的完整路径。

请尽量不要把凭据放进共享文件。客户端支持展开 `${...}` 变量的地方，示例用了变量；不支持的地方，值直接写在文件里，所以请保管好该文件。如果你用配置文件而不是环境变量，请在 `env` 块里加上 `SBRIDGE_PROFILE`。下面的示例都设置了 `SBRIDGE_CLOUD` 为 `china`。

**Claude Desktop**（`claude_desktop_config.json`，macOS 在 `~/Library/Application Support/Claude/`，Windows 在 `%APPDATA%\Claude\`）。Claude Desktop 不展开变量，所以这里直接写值：

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "00000000-0000-0000-0000-000000000000",
        "SBRIDGE_CLIENT_ID": "00000000-0000-0000-0000-000000000000",
        "SBRIDGE_CLIENT_SECRET": "replace-with-your-secret",
        "SBRIDGE_CLOUD": "china"
      }
    }
  }
}
```

**Claude Code。** 用命令行添加服务器，用 `-e` 传入凭据：

```bash
claude mcp add --transport stdio semantic-bridge \
  -e SBRIDGE_TENANT_ID=your-tenant-id \
  -e SBRIDGE_CLIENT_ID=your-client-id \
  -e SBRIDGE_CLIENT_SECRET=your-client-secret \
  -e SBRIDGE_CLOUD=china \
  -- sbridge mcp
```

或者添加项目的 `.mcp.json`。Claude Code 会从你的环境中展开 `${VAR}`：

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "${SBRIDGE_TENANT_ID}",
        "SBRIDGE_CLIENT_ID": "${SBRIDGE_CLIENT_ID}",
        "SBRIDGE_CLIENT_SECRET": "${SBRIDGE_CLIENT_SECRET}",
        "SBRIDGE_CLOUD": "china"
      }
    }
  }
}
```

**Cursor**（项目里的 `.cursor/mcp.json`，或 `~/.cursor/mcp.json`）：

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "${env:SBRIDGE_TENANT_ID}",
        "SBRIDGE_CLIENT_ID": "${env:SBRIDGE_CLIENT_ID}",
        "SBRIDGE_CLIENT_SECRET": "${env:SBRIDGE_CLIENT_SECRET}",
        "SBRIDGE_CLOUD": "china"
      }
    }
  }
}
```

**VS Code**（`.vscode/mcp.json`，使用 `servers` 键）：

```json
{
  "servers": {
    "semantic-bridge": {
      "type": "stdio",
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "${env:SBRIDGE_TENANT_ID}",
        "SBRIDGE_CLIENT_ID": "${env:SBRIDGE_CLIENT_ID}",
        "SBRIDGE_CLIENT_SECRET": "${env:SBRIDGE_CLIENT_SECRET}",
        "SBRIDGE_CLOUD": "china"
      }
    }
  }
}
```

## Docker 与 HTTP 传输

镜像运行 `sbridge mcp --http --host 0.0.0.0 --port 8080`，并暴露 8080 端口。镜像里不含令牌、密码或写入开关。凭据来自 `SBRIDGE_*` 环境变量或挂载的配置文件。

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e SBRIDGE_HTTP_TOKEN="$SBRIDGE_HTTP_TOKEN" \
  -e SBRIDGE_TENANT_ID -e SBRIDGE_CLIENT_ID -e SBRIDGE_CLIENT_SECRET \
  -e SBRIDGE_CLOUD=china \
  ghcr.io/neosunxz/semantic-bridge:latest
```

`-e NAME` 形式会把变量从你的 shell 传进去。若用配置文件，把它挂载进去，并把 `SBRIDGE_CONFIG` 设为容器内的路径。

端点是 `http://<host>:<port>/mcp`。令牌以 `Authorization: Bearer <token>` 发送。

> **安全警告：HTTP 服务器只应在内网或测试环境中使用。**
>
> - 任何能访问该端点的人（如果设置了令牌，还要持有令牌）都可以使用服务器所运行档案的凭据。没有令牌时，服务器启动时会记录这条警告。
> - 认证、TLS 和网络访问控制由部署者负责。服务器使用明文 HTTP，没有 TLS 选项。请在它前面加上 TLS 和网络限制。
> - 任何非回环绑定都需要 Bearer 令牌。没有 `SBRIDGE_HTTP_TOKEN` 时，服务器拒绝以 `--host 0.0.0.0` 启动（退出码 `2`），除非传入 `--no-auth`。令牌以哈希形式做恒定时间比较，短于 16 个字符的令牌会产生警告。
> - 在回环绑定上，`Host` 或 `Origin` 不是回环名称的请求会被以 `403` 拒绝，这可以防止浏览器发起的 DNS 重绑定。在其他绑定上，`Origin` 主机与 `Host` 不一致的请求会被以 `403` 拒绝。大于 1,000,000 字节的请求体会被拒绝。
> - 通过 HTTP 写入需要[写入风险警告](#写入风险警告)中描述的开关。Docker 镜像两个都没有设置。

## 写入风险警告

> **写操作会改变线上的语义模型。** 描述更新或刷新一旦生效，基于该模型的所有报表立刻显示新的内容。刷新还会占用容量。

- 写操作默认关闭。除非档案把 `allow_write` 设为 `true`，否则它是 `false`。只在需要的档案中启用。
- 通过 HTTP 写入时，除 `allow_write: true` 外，还需要 `--allow-http-write`（或 `SBRIDGE_ALLOW_HTTP_WRITE=1`）。HTTP 服务器两者都要；stdio 服务器只需要 `allow_write: true`。
- 经 XMLA 写入之后，在 Power BI Desktop 中制作的模型不能再下载为 PBIX 文件。
- 使用单独的档案和单独的、有写权限的服务主体，并且只在需要时启用。
- 更新描述需要容量上的 XMLA 端点设为 Read Write。
- 命令行：`describe` 把每处更改显示为 `old -> new`，并在应用前询问。`refresh` 在发起前询问。`--yes` 跳过询问。
- MCP：服务器不会要求确认。写入类工具的描述会告诉 agent 在调用前先取得你的明确确认。你的保护取决于 agent 是否遵循这些指示，所以请检查 agent 即将更改的内容。

## Pro 工作区与 REST 限制

不在专用容量上的工作区没有 XMLA 端点。在这些工作区上，使用默认 `--via auto` 的 `query` 通过 REST 运行。适用以下限制：

- `schema` 和 `describe` 需要 XMLA，在这些工作区上会失败。`schema --via rest` 会被拒绝，因为 `executeQueries` 不支持 INFO 函数。
- `executeQueries` 每次查询最多返回 100,000 行或 1,000,000 个值，先到者为准；每个用户每分钟最多 120 个查询请求（见上面的 Execute Queries 页面）。
- `executeQueries` 还会把一次响应限制在大约 15 MB 数据。实机观察到的错误是 `DaxByteCountNotSupported`（"More than 15728640 bytes"）。`sbridge` 会把它报告为 `truncated: true` 并附上原因。
- 如果 REST 在返回部分数据之后出错，结果会被标记为截断。
- 微软不支持在启用了行级安全或 SSO 的数据集上使用服务主体（见上面的 Execute Queries 页面）。
- `refresh` 和 `refresh-status` 使用 REST，所以在这些工作区上也能用。

## 许可

Semantic Bridge for Power BI 以 [MIT License](LICENSE) 发布。

Microsoft Analysis Services 客户端库（ADOMD.NET 以及 AMO/TOM）不在 MIT 许可范围内。它们按 Microsoft Software License Terms 提供，副本在 [licenses/](licenses/) 中。使用这些二进制、`semantic-bridge` .NET 工具或 Docker 镜像，即表示你同意这些条款。其他第三方组件及其许可列在 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 中。
