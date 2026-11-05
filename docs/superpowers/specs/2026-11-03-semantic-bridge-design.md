# Semantic Bridge for Power BI — Design

Status: approved direction, owner answers folded in · 2026-11-03
Name: **Semantic Bridge for Power BI** (chosen by the owner). Repo/folder and NuGet tool ID `semantic-bridge`; CLI command `sbridge`; env var prefix `SBRIDGE_` (these three are the designer's call). The name deliberately uses the "… for Power BI" form: Power BI is a Microsoft trademark and the project must not look official; README states "Not affiliated with or endorsed by Microsoft. Power BI is a trademark of Microsoft." Avoid anything that suggests Power BI Report Server.

## 1. Intent

Let anyone with a Power BI subscription point an AI tool (Claude, Cursor, VS Code Copilot, any MCP client) — or a shell script — at the semantic models they already have, with nothing changed on the Power BI side.

It is extracted from the owner's internal project (SnowAI), which grew a lot of company-specific logic (business glossary, dataset routing, file cabinet, DingTalk, reports, Python sandbox). This project keeps only the generic foundation: connect, discover, read model metadata, run DAX, and a small, gated set of writes. It is open source.

Why it needs to exist although Microsoft ships official Power BI MCP servers (checked against Microsoft Learn, 2026-11):

| Microsoft offering | Gap this project fills |
|---|---|
| Fabric IQ MCP (consumption) | Not available in sovereign clouds (so not in China / 21Vianet); no service principal or app-only auth; default 250 rows |
| Hosted Power BI Consumption / Authoring MCP (`api.fabric.microsoft.com/...`) | Global cloud endpoint only; needs Entra OAuth per user, and clients that rely on dynamic client registration (e.g. Claude Desktop) need a manually registered app |
| Local Power BI Authoring MCP (`microsoft/powerbi-modeling-mcp`) | Aimed at model authoring, not consumption; does not support macOS; sovereign-cloud support not documented (unverified) |

So the distinguishing features are: **works in both the global and the China (21Vianet) cloud**, **service-principal (app-only) auth from the user's own config**, **runs on Windows / macOS / Linux**, **a CLI as well as an MCP server**, and **self-hostable**.

## 2. Goals

1. One program that is both a CLI and an MCP server, sharing one configuration and one set of capabilities.
2. Clouds: global and China built in as presets; every endpoint overridable in config so other clouds (e.g. US Gov) can be added without code changes.
3. Auth: the user supplies their own Entra app credentials (tenant ID, client ID, client secret) in config or environment variables. What the tool can see and do is exactly what that app has been granted in Power BI.
4. Two query paths:
   - **XMLA** (Premium / PPU / Embedded / Fabric capacity) — primary path: metadata and DAX.
   - **REST API** — workspace/dataset discovery, refresh, and DAX via `executeQueries` for Pro workspaces that have no XMLA endpoint.
5. Read capabilities (always on): list workspaces, list datasets, read model metadata (tables, columns, measures incl. DAX expression, relationships, descriptions), run a DAX query.
6. Write capabilities (off by default, see §6): update descriptions of tables/columns/measures; trigger a dataset refresh.
7. Cross-platform single-file binaries; plus a Docker image for running the MCP server over HTTP.
8. English-first: code, identifiers, CLI/tool names, docs, error messages.

## 3. Non-goals

- Any business semantics: no glossary, metric definitions, prompt engineering, dataset auto-routing, natural-language-to-DAX. The AI client writes the DAX; this tool executes it.
- Reports, charts, file storage, notifications, chat UIs.
- Model authoring beyond descriptions: no create/delete/rename of objects, no editing DAX expressions, relationships, roles, partitions.
- Its own user system, multi-tenant hosting, or per-end-user identity in the HTTP server.
- Interactive per-user sign-in (device code / browser) in v1 — service principal only. (May come later; keep the auth layer pluggable so it can be added.)
- Power BI Desktop / PBIP / TMDL files on disk.

## 4. Anti-goals (would make it a failure even if it "works")

- Silently truncating results. Any row cap or byte cap hit must be reported explicitly in CLI output and in the MCP tool result. (Lesson from SnowAI: silent truncation produced wrong answers that looked right.)
- Leaking secrets: client secrets or tokens must never appear in logs, error messages, tool results, `config show` output, or crash dumps.
- Writes happening without the user having explicitly enabled them.
- Requiring users to install .NET, Python, or Node to use the CLI.
- Company-specific leftovers from SnowAI (names, Chinese business terms, internal endpoints, sample data).

## 5. Decisions already made with the owner

| Topic | Decision |
|---|---|
| Audience | Any Power BI subscriber; generic, not SnowAI-specific |
| Clouds | Global and China both supported; domains live in config |
| Auth | Users configure their own credentials (service principal + secret) |
| Language of the project | English-first |
| Platforms | Must be multi-OS |
| Docker | Provided as one deployment idea for the MCP server, not a focus. Docs must say: recommended only for intranet / test environments, anyone who can reach it uses the configured credentials, and security (auth, TLS, network ACL) is the deployer's responsibility |
| Writes | Capability is bounded by the user's Power BI permissions; add a config switch, give recommendations, and warn about the risk of modifying models |
| Implementation language | **.NET 10 LTS (C#)** — confirmed by the owner (see §7 for why .NET). |
| Publishing | GitHub (repo/org name to be chosen by the owner when publishing); Docker image published to **both** GHCR and Docker Hub; NuGet `dotnet tool` package (check the ID is free) |
| Test environments | Available: the owner's China (21Vianet) tenant with a service principal and a PPU workspace, on linux-x64. **Not available: an ARM Mac** (and no Apple-silicon/ARM test hardware). Global-cloud tenant and a Pro-only workspace: not confirmed — treat as unavailable unless the owner provides them. |

## 6. Behaviour

### 6.1 Configuration

- File: `~/.config/semantic-bridge/config.yaml` (Windows: `%APPDATA%\semantic-bridge\config.yaml`), overridable with `--config` / `SBRIDGE_CONFIG`.
- Supports multiple named **profiles** (e.g. `cn-prod`, `global-test`), selected with `--profile` / `SBRIDGE_PROFILE`; one is the default.
- Per profile:
  - `cloud`: `global` | `china` | `custom`
  - `tenant_id`, `client_id`, `client_secret` (secret may instead come from env var `SBRIDGE_CLIENT_SECRET` or a `client_secret_env: NAME` indirection; docs recommend env vars over plaintext)
  - optional endpoint overrides: `authority_host`, `rest_base_url`, `xmla_base`, `token_scope`
  - `default_workspace` (optional)
  - `allow_write: false` (default)
  - limits: `max_rows` (default 1000, hard ceiling configurable), `query_timeout_seconds` (default 120)
- Cloud presets (verify each value against Microsoft docs during implementation):

| | global | china (21Vianet) |
|---|---|---|
| Authority | `https://login.microsoftonline.com` | `https://login.partner.microsoftonline.cn` (alias `login.chinacloudapi.cn`) |
| Token scope | `https://analysis.windows.net/powerbi/api/.default` | `https://analysis.chinacloudapi.cn/powerbi/api/.default` |
| REST base | `https://api.powerbi.com/v1.0/myorg` | `https://api.powerbi.cn/v1.0/myorg` |
| XMLA base | `powerbi://api.powerbi.com/v1.0/myorg/{workspace}` | `powerbi://api.powerbi.cn/v1.0/myorg/{workspace}` |

  The China values are the ones the owner's production system uses today.
- `sbridge config init` writes a commented template; `sbridge config show` prints the resolved config with secrets masked; `sbridge config test` checks token acquisition, REST reachability and (if the workspace is on capacity) an XMLA connection, and reports each step separately.

### 6.2 Capabilities (CLI command ↔ MCP tool, same semantics)

| Capability | CLI | MCP tool | Path |
|---|---|---|---|
| List workspaces | `sbridge workspaces` | `list_workspaces` | REST |
| List datasets in a workspace | `sbridge datasets -w <ws>` | `list_datasets` | REST |
| Model metadata | `sbridge schema -w <ws> -d <dataset> [--table T]` | `get_model_schema` | XMLA (INFO.* DAX functions / TMSCHEMA rowsets); REST fallback where possible |
| Run DAX | `sbridge query -w <ws> -d <dataset> "<DAX>" [-f table|json|csv] [--max-rows N]` (also `--file q.dax`, stdin) | `run_dax` | XMLA, or REST `executeQueries` |
| Update description *(write)* | `sbridge describe -w <ws> -d <dataset> --measure M "<text>"` (also `--table`, `--column T[C]`, batch from a file) | `update_description` | XMLA read-write (TOM) |
| Refresh dataset *(write)* | `sbridge refresh -w <ws> -d <dataset> [--wait]` | `refresh_dataset` | REST |
| Refresh status | `sbridge refresh-status ...` | `get_refresh_status` | REST |

Workspaces and datasets are addressable by name or ID.

**Path selection for DAX/metadata**: `--via auto|xmla|rest` (config default `auto`). `auto` tries XMLA and falls back to REST when the workspace has no XMLA endpoint (Pro). The result always states which path was used. REST `executeQueries` limits (100,000 rows / 1,000,000 values per query, per Microsoft docs) are documented.

**Results**:
- Columns with their types, rows, row count, `truncated: true/false`, path used, elapsed time.
- Column names exactly as the engine returns them (e.g. `Table[Column]`, `[Measure]`) — don't rename. (SnowAI lesson: renaming columns caused more downstream errors than it prevented.)
- MCP results: compact JSON suitable for an LLM; large results capped by `max_rows` with an explicit truncation note telling the agent to aggregate or filter.
- Errors: engine error text passed through (useful to the AI for self-correction) but scrubbed of secrets/tokens/connection strings.

### 6.3 Writes and safety

- With `allow_write: false`, write commands exit with a clear message and write tools are **not registered** in MCP at all (the agent never sees them).
- With `allow_write: true`:
  - CLI write commands show what will change (old → new description) and ask for confirmation; `--yes` skips it for scripts.
  - MCP write tool descriptions tell the agent to get the user's explicit confirmation before calling, and the tool result reports old and new values.
  - Description updates require the XMLA endpoint set to Read Write on the capacity and Write permission on the dataset; failures due to this produce a specific, actionable message.
- README and config template carry a plain warning: writes change the live model and affect every report built on it; recommend a separate profile/credential with write rights, used only when needed.

### 6.4 MCP server

- `sbridge mcp` — stdio transport (default; what Claude Desktop, Cursor, VS Code launch).
- `sbridge mcp --http --port 8080` — Streamable HTTP transport (used by the Docker image).
  - Writes over HTTP need a second, explicit opt-in (designer's call): with `--http`, write tools are registered only if both `allow_write: true` and `--allow-http-write` (env `SBRIDGE_ALLOW_HTTP_WRITE=1`) are set; otherwise the server logs that writes are disabled for HTTP. The Docker image never sets it by default. Reason: over HTTP the only confirmation step is the agent's own instruction-following, and anyone who reaches the server shares the credentials.
  - Static bearer token (`SBRIDGE_HTTP_TOKEN`); when set, requests without it are rejected. On the default loopback bind it is optional and its absence logs a startup warning. Bind to `127.0.0.1` by default; `--host 0.0.0.0` must be explicit (the Docker image sets it) and then **requires the token**: without it the server refuses to start (exit 2) unless `--no-auth` (env `SBRIDGE_HTTP_NO_AUTH=1`) is passed, which starts with a loud warning naming the public bind.
  - Browser-origin hardening: requests with a non-loopback `Origin` (or, on a loopback bind, a non-loopback `Host`) are rejected with 403 to block DNS rebinding; request bodies are capped at 1,000,000 bytes.
- The MCP server uses one profile for its lifetime (chosen at start).
- Tool descriptions should be concise and teach the agent the workflow: list → schema → query; prefer aggregations; respect truncation notes.
- README includes ready-to-paste client configs for Claude Desktop, Claude Code, Cursor, VS Code.

### 6.5 Distribution

- Self-contained single-file binaries per OS/arch via GitHub Releases: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 (subject to §8 risk 1).
- `dotnet tool install -g semantic-bridge` for people who already have .NET.
- Docker image (multi-arch) running `sbridge mcp --http`, credentials via env vars or a mounted config file; docs carry the intranet/test-only + "you own the security" warning.
- License: MIT (designer's call; Apache-2.0 is the alternative if patent language is wanted).
- Third-party Microsoft packages (ADOMD.NET, TOM, MSAL) ship under their own Microsoft license terms, not MIT. Precedent: Tabular Editor 2 is MIT-licensed and redistributes the Analysis Services client libraries. Builder's task: read the license terms bundled with the exact package versions used, confirm redistribution in binaries / dotnet tool / Docker image is allowed, and add a `THIRD-PARTY-NOTICES.md`. If redistribution turns out not to be allowed, stop and raise it with the owner before releasing.

## 7. How it's built (designer's recommendation)

- **.NET 10 (LTS), C#.** .NET 8 reaches end of support on 2026-11-10, so target .NET 10; it does not need to match the owner's .NET 8 production service. Confirm in the spike that the chosen ADOMD/TOM package versions support it. Reason for .NET at all: XMLA on Linux/macOS requires Microsoft's ADOMD.NET client; .NET gives single-file self-contained binaries (users install nothing); the owner's production XMLA microservice is already .NET, so the query and metadata code has a working reference.
- Libraries to evaluate: `Microsoft.AnalysisServices.AdomdClient` (queries, metadata), `Microsoft.AnalysisServices` / TOM (description writes), `Microsoft.Identity.Client` (MSAL, REST tokens), the official MCP C# SDK (`ModelContextProtocol`), `System.CommandLine` or similar for the CLI.
- ADOMD supports service-principal auth natively via the connection string `User ID=app:{clientId}@{tenantId};Password={secret}` — this is what the owner's system uses. For the China cloud, ADOMD must authenticate against the China authority; verify whether that works out of the box with the `powerbi://api.powerbi.cn/...` data source or needs an explicitly acquired token (MSAL token passed as the connection password is the fallback).
- Metadata reading: the owner's system reads columns via `INFO.COLUMNS()` / `INFO.RELATIONSHIPS()` DAX and falls back to `TMSCHEMA_*` schema rowsets because Power BI doesn't support `MDSCHEMA_COLUMNS`; carry that approach over (generic code only).
- Structure: a core library (config, auth, REST client, XMLA client, result model, safety) with two thin front-ends (CLI, MCP). Unit tests for config resolution, secret masking, truncation reporting, write gating; integration tests runnable against a real workspace when credentials are provided via env vars (skipped otherwise).
- Reference to copy ideas from (not code verbatim, and with every company-specific part removed): `/home/xiaoz/projects/powerbi-ai-query/xmla-engine/` (`Services/XmlaService.cs`, `Services/TokenService.cs`, `Services/XmlaConnectionStringBuilder.cs`, `Services/PowerBIRestClient.cs`).

## 8. Known risks — verify first

1. **ADOMD/TOM on ARM and macOS.** No ARM Mac is available to test on. Plan: verify linux-x64 (and win-x64 if a Windows machine/CI runner is available; GitHub-hosted runners can cover win-x64, osx-arm64 and linux-arm64 for at least load/startup and REST checks); for platforms where XMLA can't be verified against a real workspace, release them labelled "XMLA unverified" in the release notes and README, with REST as the guaranteed path. Original note: The owner's service uses `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` (x64 only), and Microsoft's own local MCP server does not support macOS. Before building anything else, run a spike: does current ADOMD.NET connect and query on linux-x64, linux-arm64, osx-arm64, win-x64? Where it doesn't, that platform ships REST-only and says so clearly (`config test` and docs).
2. **Global cloud and Pro (REST-only) workspaces** have no confirmed test tenant: build them from Microsoft docs, cover with unit tests and mocked HTTP, and mark them "untested against a live tenant" until someone verifies. 
3. **China cloud via XMLA with a service principal** — works in the owner's production today on linux-x64; re-verify with the chosen package version.
4. **Metadata via REST only** (Pro workspaces): whether `INFO.*` functions work through `executeQueries` and with what permissions is unverified; if not, Pro users get query-only and the docs say so.
5. **Tenant settings** the user's admin must enable (service principals can use Power BI APIs; XMLA endpoint read/read-write; Dataset Execute Queries REST API) — `config test` should detect and explain each failure.

## 9. Left to the builder

- Exact CLI framework, output formatting library, project layout, test framework.
- YAML vs TOML for config (YAML assumed above).
- Exact MCP tool input schemas and naming within the spirit of §6.2.
- Default `max_rows` and timeout values (sensible defaults given above).
- CI (GitHub Actions) for build, test, multi-platform release, Docker image.

## 10. Done looks like (v1)

- A user with only a service principal and a PPU/Premium workspace in either cloud can: download one binary, run `sbridge config init`, fill in IDs, run `sbridge config test`, then `sbridge query ...`, and add `sbridge mcp` to Claude Desktop and ask questions about their model.
- A Pro-workspace user gets the same via REST, with documented limits.
- Writes are impossible until `allow_write: true`, and every write is confirmed and reported.
- README covers: what it is / isn't, comparison with Microsoft's MCP servers, Entra app setup per cloud (permissions and tenant settings), config reference, client configs, Docker with security warning, write-risk warning.
