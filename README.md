# Semantic Bridge for Power BI

[中文（中国区 / 世纪互联）](README.zh-CN.md) - the Chinese guide covers the China (21Vianet) cloud. This README covers the global cloud.

Semantic Bridge for Power BI is a command-line tool (`sbridge`) and an MCP server that let you read and query the Power BI semantic models you already have. It uses a Microsoft Entra service principal that you create and control, talks to the XMLA endpoint and the Power BI REST API, and has presets for the global and China (21Vianet) clouds.

Not affiliated with or endorsed by Microsoft. Power BI is a trademark of Microsoft.

> **Status: pre-release.** Phases 1–5 of the [v1 plan](docs/superpowers/plans/2026-11-04-v1-plan.md) are implemented. Nothing has been released yet. Live verification so far covers linux-x64 against a China (21Vianet) tenant only; the global cloud has not been verified live. See [Status and platforms](#status-and-platforms).

## What it is and what it is not

It is:

- One program with two front-ends that share one configuration and one set of behaviours: the `sbridge` CLI and `sbridge mcp`, an MCP server over stdio or Streamable HTTP.
- Read access to workspaces, datasets, model metadata (tables, columns, measures with their DAX, relationships), DAX queries and refresh history.
- A small set of writes: updating descriptions of tables, columns and measures, and queuing a dataset refresh. Both are off unless `allow_write: true` is set (see [Write-risk warning](#write-risk-warning)).

It is not (design spec §3):

- A source of business logic. There is no glossary, metric definitions, prompt engineering, dataset routing or natural-language-to-DAX. The AI client writes the DAX and Semantic Bridge runs it. Only queries that start with `DEFINE` or `EVALUATE` are accepted.
- A reporting tool. There are no reports, charts, file storage, notifications or chat UI.
- A model authoring tool. Apart from descriptions, objects cannot be created, deleted or renamed, and DAX expressions, relationships, roles and partitions cannot be edited.
- A tool with per-user sign-in. v1 uses a service principal only, and the HTTP server does not identify end users.
- Able to read Power BI Desktop, PBIP or TMDL files on disk.
- Aware of row-level security or roles. v1 has no RLS or role handling.

## How it compares with Microsoft's Power BI MCP servers

As of 2026-11, per the [design spec §1](docs/superpowers/specs/2026-11-03-semantic-bridge-design.md), which cites Microsoft Learn. Microsoft's offerings change, so check their documentation before relying on this table.

| Microsoft offering | Gap this project fills |
|---|---|
| Fabric IQ MCP (consumption) | Not available in sovereign clouds (so not in China / 21Vianet); no service principal or app-only auth; default 250 rows |
| Hosted Power BI Consumption / Authoring MCP (`api.fabric.microsoft.com/...`) | Global cloud endpoint only; needs Entra OAuth per user, and clients that rely on dynamic client registration (e.g. Claude Desktop) need a manually registered app |
| Local Power BI Authoring MCP (`microsoft/powerbi-modeling-mcp`) | Aimed at model authoring, not consumption; does not support macOS; sovereign-cloud support not documented (unverified) |

The differences this project is built around are: presets for the global and China (21Vianet) clouds; service-principal (app-only) auth with your own credentials; release binaries for Windows, macOS and Linux (see [Status and platforms](#status-and-platforms)); a CLI as well as an MCP server; and self-hosting.

## Status and platforms

- Phases 1–5 are implemented. The first release is pending.
- Verified live on 2026-11-07 on linux-x64 against a China (21Vianet) Premium Per User (PPU) workspace: XMLA (token auth with an MSAL access token), REST, schema, queries, refresh, refresh status, description writes, MCP over stdio and HTTP, and the single-file linux-x64 binary.
- The global cloud has not been verified live. Pro (REST-only) workspaces have not been tested against a live tenant.
- XMLA is unverified on the other RIDs: `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64` and `osx-x64`.
- Release binaries are built for six RIDs: `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64` and `osx-x64`. CI checks that each one starts and answers the MCP stdio handshake. CI does not call the REST API or XMLA.

## Install

### Release binaries

Download the archive for your platform from the [GitHub Releases](https://github.com/NeoSunxz/semantic-bridge/releases) page. Each archive contains one file: `sbridge`, or `sbridge.exe` on Windows. The binaries are self-contained, so you do not need .NET installed. Check the archive against `SHA256SUMS` before you run it. The release workflow does not sign the binaries, so the operating system may warn when you first run them.

### .NET tool

```bash
dotnet tool install -g semantic-bridge
```

This installs the `sbridge` command. It needs the .NET 10 runtime. The release workflow publishes the package to NuGet only when the `NUGET_API_KEY` secret is set, so it is not published yet.

### Docker image

```bash
docker pull ghcr.io/neosunxz/semantic-bridge:latest
docker pull neosunxz/semantic-bridge:latest
```

The Docker Hub image is pushed only when the Docker Hub secrets are configured. Images are built for `linux/amd64` and `linux/arm64`. Version tags and `latest` are created for release tags. See [Docker and HTTP transport](#docker-and-http-transport).

## Setup

### 1. Register an app in Microsoft Entra ID

1. Create an app registration in the tenant you want to query.
2. Note the tenant ID and the application (client) ID.
3. Create a client secret. Use the secret **value**, not the secret ID. When the secret expires, create a new one.
4. Put the secret in the `SBRIDGE_CLIENT_SECRET` environment variable (see [Configuration](#configuration)).

The app registration and the tenant must be in the cloud that you set in `cloud` (`global` by default). If authentication fails, check that `cloud` matches the tenant's cloud.

Register the app in the Azure portal (portal.azure.com) or the Microsoft Entra admin center (entra.microsoft.com).

The app needs no Power BI API permissions and no admin consent in Entra. Access comes from the Power BI tenant settings (step 2) and the workspace role (step 3).

### 2. Enable the tenant settings

A Power BI or Fabric administrator enables these in the Power BI admin portal (app.powerbi.com). Give the setting to a security group that contains the app.

- **Allow service principals to use Power BI APIs** (under Developer settings). Needed for any service principal access. See [Datasets - Execute Queries In Group](https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries-in-group).
- **Allow XMLA endpoints and Analyze in Excel with on-premises semantic models**. Needed for XMLA: schema, DAX on capacity workspaces and description writes. See [Semantic model connectivity with the XMLA endpoint](https://learn.microsoft.com/en-us/fabric/enterprise/powerbi/service-premium-connect-tools).
- **Dataset Execute Queries REST API** (under Integration settings). Needed for DAX over REST, which the Pro path uses. See the Execute Queries page above.

### 3. Give the app a workspace role

Add the service principal to each workspace you want to query. Microsoft lists the workspace roles as Admin, Member, Contributor and Viewer, and service principals can be assigned to them ([Roles in workspaces](https://learn.microsoft.com/en-us/fabric/fundamentals/roles-workspaces)). The project's error hint recommends Member or higher, because a Viewer cannot read model metadata. For DAX over REST, Microsoft requires read and build permissions on the dataset (Execute Queries page above).

### 4. Capacity for XMLA

Schema reading, XMLA DAX and description writes need a workspace on Premium, Premium Per User (PPU), Embedded or Fabric capacity. On Premium capacity, the capacity admin sets the XMLA Endpoint to Read Only or Read Write. For PPU, the setting is in the Premium Per User settings (see the XMLA page above). Description writes need Read Write.

A Pro workspace has no XMLA endpoint. `query` and `refresh` work there through REST, but `schema` and `describe` do not (see [Pro workspaces and REST limits](#pro-workspaces-and-rest-limits)).

### Per-cloud notes

| | `cloud: global` | `cloud: china` (21Vianet) |
|---|---|---|
| Authority (`authority_host`) | `https://login.microsoftonline.com` | `https://login.partner.microsoftonline.cn` |
| Token scope (`token_scope`) | `https://analysis.windows.net/powerbi/api/.default` | `https://analysis.chinacloudapi.cn/powerbi/api/.default` |
| REST base (`rest_base_url`) | `https://api.powerbi.com/v1.0/myorg` | `https://api.powerbi.cn/v1.0/myorg` |
| XMLA base (`xmla_base`) | `powerbi://api.powerbi.com/v1.0/myorg/{workspace}` | `powerbi://api.powerbi.cn/v1.0/myorg/{workspace}` |

`cloud` defaults to `global`. The China preset is described in the Chinese guide, [README.zh-CN.md](README.zh-CN.md). The China preset, including its XMLA base, was verified live on linux-x64. The global preset has not been verified live.

To use another cloud, set `cloud: custom` and give all four endpoint keys. Endpoints must be `https` URLs, except a loopback `http` address for a custom cloud. `xmla_base` must start with `powerbi://` or `asazure://` and contain `{workspace}`.

## Configuration

`sbridge config init` writes a commented template. `sbridge config show` prints the resolved values and where each one came from, with the secret masked.

### Config file location

| OS | Default path |
|---|---|
| Windows | `%APPDATA%\semantic-bridge\config.yaml` |
| Linux, macOS | `~/.config/semantic-bridge/config.yaml` |

Use `--config <path>` or `SBRIDGE_CONFIG` to choose another file. If you name a file that does not exist, the command fails.

### Profiles

A file can hold several named profiles under `profiles:`. The profile is chosen in this order:

1. `--profile <name>`
2. `SBRIDGE_PROFILE`
3. `default_profile` in the file
4. the only profile in the file
5. no profile in the file, but `SBRIDGE_TENANT_ID`, `SBRIDGE_CLIENT_ID` or `SBRIDGE_CLIENT_SECRET` set (environment-only setup, used by Docker and CI). All three credentials are needed. `SBRIDGE_CLOUD`, `SBRIDGE_WORKSPACE` and `SBRIDGE_DATASET` are optional and do not start this mode on their own.

If the file has several profiles and no `default_profile`, the command fails. Profile names are matched without regard to case. Unknown keys in the file are rejected.

### Keys

Keys under each profile:

| Key | Default | Notes |
|---|---|---|
| `default_profile` | none | Top level. Profile used when none is selected. |
| `cloud` | `global` | `global`, `china` or `custom`. |
| `tenant_id` | none | Required. |
| `client_id` | none | Required. |
| `client_secret` | none | Plaintext secret. Accepted but discouraged. |
| `client_secret_env` | none | Name of an environment variable that holds the secret. |
| `authority_host` | cloud preset | Required for `custom`. |
| `token_scope` | cloud preset | Required for `custom`. |
| `rest_base_url` | cloud preset | Required for `custom`. |
| `xmla_base` | cloud preset | Required for `custom`. Must contain `{workspace}`. |
| `default_workspace` | none | Name or ID, used when `-w` or `workspace` is not given. |
| `default_dataset` | none | Name or ID, used when `-d` or `dataset` is not given. Not used by the MCP write tools (see [MCP server](#mcp-server)). |
| `allow_write` | `false` | Must be `true` for writes (see [Write-risk warning](#write-risk-warning)). |
| `max_rows` | `1000` | Default row limit per query. Must be between 1 and `max_rows_ceiling`. |
| `max_rows_ceiling` | `100000` | Hard cap, between 1 and 100000. Requests above it are capped and reported. |
| `query_timeout_seconds` | `120` | Clamped to 1–600. |
| `default_via` | `auto` | `auto`, `xmla` or `rest`. |

The secret is resolved in this order: `SBRIDGE_CLIENT_SECRET`, then the variable named by `client_secret_env`, then `client_secret` in the file.

### Environment variables

| Variable | Purpose |
|---|---|
| `SBRIDGE_CONFIG` | Path to the config file. |
| `SBRIDGE_PROFILE` | Profile to use. |
| `SBRIDGE_TENANT_ID` | Overrides `tenant_id`. |
| `SBRIDGE_CLIENT_ID` | Overrides `client_id`. |
| `SBRIDGE_CLIENT_SECRET` | Overrides the client secret. |
| `SBRIDGE_CLOUD` | Overrides `cloud`. |
| `SBRIDGE_WORKSPACE` | Overrides `default_workspace`. |
| `SBRIDGE_DATASET` | Overrides `default_dataset`. |
| `SBRIDGE_HTTP_TOKEN` | Bearer token for `sbridge mcp --http`. |
| `SBRIDGE_HTTP_NO_AUTH=1` | Same as `--no-auth`. |
| `SBRIDGE_ALLOW_HTTP_WRITE=1` | Same as `--allow-http-write`. |

Precedence, highest first: command-line flag, environment variable, config file, cloud preset or built-in default. Environment variables exist only for the settings in this table. Endpoints and the other keys are read from the file or the preset.

Keep the secret out of the file. Set `SBRIDGE_CLIENT_SECRET`, or name another variable with `client_secret_env`. `config show` prints the secret as `*** (set)` or `(not set)`.

### Example

```yaml
default_profile: prod

profiles:
  prod:
    cloud: global
    tenant_id: "00000000-0000-0000-0000-000000000000"
    client_id: "00000000-0000-0000-0000-000000000000"
    default_workspace: "My Workspace"
```

With the secret in `SBRIDGE_CLIENT_SECRET`, this is the whole setup. Run `sbridge config test` to check it.

## CLI reference

Global options, accepted by every command: `--config <path>` (env `SBRIDGE_CONFIG`) and `--profile <name>` (env `SBRIDGE_PROFILE`). `sbridge --version` prints the version and exits.

Output goes to stdout. Diagnostics, notes and errors go to stderr.

Workspaces and datasets can be given by name or by ID. A name that matches more than one item is an error that asks for the ID. `-w` can be left out when `default_workspace` is set, and `-d` when `default_dataset` is set. `describe` and `refresh` also use the default dataset and print the dataset they resolved.

| Command | Purpose | Key options |
|---|---|---|
| `sbridge config init` | Write the commented config template to the config path. | `--force` (overwrite) |
| `sbridge config show` | Print the resolved profile, with the source of each value. | none |
| `sbridge config test` | Check the token, REST and, when a workspace is given, XMLA. | `-w` |
| `sbridge workspaces` | List workspaces the service principal can see (REST). | `-f table\|json\|csv` |
| `sbridge datasets` | List datasets in a workspace (REST). | `-w`, `-f` |
| `sbridge schema` | Tables, columns, measures with their DAX, and relationships (XMLA only). | `-w`, `-d`, `--table`, `-f table\|json`, `--via` |
| `sbridge query` | Run a DAX query, given as an argument, with `--file`, or on stdin. Must start with `DEFINE` or `EVALUATE`. | `-w`, `-d`, `--file`, `-f table\|json\|csv`, `--max-rows`, `--via` |
| `sbridge describe` (write) | Update descriptions of tables, columns or measures on the live model (XMLA). | `-w`, `-d`, `--table`, `--column`, `--measure`, `--file`, `-y, --yes`, `-f table\|json` |
| `sbridge refresh` (write) | Queue a dataset refresh (REST). | `-w`, `-d`, `--wait`, `--timeout` (default `600`), `-y, --yes` |
| `sbridge refresh-status` | Refresh history, newest first (REST; works without `allow_write`). | `-w`, `-d`, `--top` (1–100, default `5`), `-f` |
| `sbridge mcp` | Run the MCP server for the profile chosen at start. | `--http`, `--host` (default `127.0.0.1`), `--port` (default `8080`), `--no-auth`, `--allow-http-write` |

Notes on behaviour that is not obvious from the table:

- **Query output.** In table or CSV format, rows go to stdout. Stderr gets a summary line (`-- N row(s), path: xmla|rest, M ms`), a `note: result is TRUNCATED.` line when the result is cut off, and a `warning:` line for each warning. JSON output puts everything on stdout: `columns`, `rows`, `row_count`, `truncated`, `truncation_reason`, `path`, `elapsed_ms` and `warnings`. Column names are returned as the engine reports them, for example `Sales[Amount]` and `[Total Sales]`. Over REST, column types are inferred from the JSON values because `executeQueries` returns no types, so date/time columns show as `String`; date/time values use the same `yyyy-MM-ddTHH:mm:ss[.fff]` format on both paths.
- **Truncation is always reported.** A result cut off by a row cap is marked `truncated` in JSON and shown as a `note:` line in table or CSV output. A `--max-rows` above `max_rows_ceiling` is capped, and a warning says so. When metadata has more than 100,000 rows, `schema` reports the schema as incomplete in a warning.
- **`--via auto`.** `auto` uses XMLA. It uses REST only when the workspace is known not to be on dedicated capacity, and then adds a warning. It never retries over REST after an XMLA error. `schema --via rest` is refused.
- **Confirmation.** `describe` and `refresh` ask before they change anything. `-y` or `--yes` skips the question. When stdin is not a terminal and `--yes` is not given, both refuse before any network call, with exit code `2`. `describe` prints `old -> new` for each change to stderr and lists unchanged targets. If you answer no, it exits `1` and changes nothing.
- **`refresh --wait`.** The command exits `0` only when the final status is `Completed`. It exits `1` when the refresh failed, when `--timeout` (1–86400 seconds, default 600) passed, or when the refresh cannot be found in the history. In the last case it prints `status: unknown`: the outcome is unknown, and the refresh may still be running.
- **Exit codes.** `0` success. `1` runtime error: a request failed, a `config test` step failed, you answered no to a confirmation, or a refresh failed, timed out or has an unknown outcome. `2` usage or configuration error: bad options or values, unknown config keys, missing credentials or profile, writes disabled, or an HTTP bind that needs a token. `130` cancelled with Ctrl-C.

## MCP server

Start the server with `sbridge mcp`. Clients usually start it for you. See the client configs below.

### Tools

Read tools are always registered. Write tools are registered only when writes are enabled, and the agent never sees them otherwise.

| Tool | Writes | Inputs |
|---|---|---|
| `list_workspaces` | no | none |
| `list_datasets` | no | `workspace` (optional) |
| `get_model_schema` | no | `dataset` (optional), `workspace` (optional), `table` (optional). XMLA only. |
| `run_dax` | no | `dax`, `dataset` (optional), `workspace` (optional), `max_rows` (optional), `via` (optional: `auto`, `xmla` or `rest`) |
| `get_refresh_status` | no | `dataset` (optional), `workspace` (optional), `top` (optional, 1–100, default 5) |
| `update_description` | yes | `dataset`, `target_type` (`table`, `column` or `measure`), `target`, `description`, `workspace` (optional) |
| `refresh_dataset` | yes | `dataset`, `workspace` (optional) |

The read tools fall back to `default_dataset` when `dataset` is omitted. The write tools (`update_description`, `refresh_dataset`) always require `dataset`, so an AI client never changes a default dataset implicitly.

Results are JSON text. A `run_dax` result has the same fields as the CLI JSON output, and gets a `note` field when it is truncated. Failures come back as tool errors with the message scrubbed of secrets, plus a hint where one applies. A call with missing, unknown or wrong-typed arguments fails with a tool error that names the argument(s) and lists the expected ones (never the values).

The tool descriptions teach the workflow: `list_workspaces`, then `list_datasets`, then `get_model_schema`, then `run_dax`. Prefer aggregations over raw rows, and check `truncated` and `warnings`.

### Client configurations

These are typical formats. Check your client's documentation if a format has changed. Each example starts `sbridge` with the argument `mcp`. `sbridge` must be on the PATH the client sees, or you can use the full path to the binary.

Keep credentials out of shared files where you can. Where a client expands `${...}` variables, the examples use them. Where it does not, the value is written into the file, so keep that file private. If you use a config file instead of environment variables, add `SBRIDGE_PROFILE` to the `env` block.

**Claude Desktop** (`claude_desktop_config.json`, in `~/Library/Application Support/Claude/` on macOS or `%APPDATA%\Claude\` on Windows). Claude Desktop does not expand variables, so the values are written here:

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "00000000-0000-0000-0000-000000000000",
        "SBRIDGE_CLIENT_ID": "00000000-0000-0000-0000-000000000000",
        "SBRIDGE_CLIENT_SECRET": "replace-with-your-secret"
      }
    }
  }
}
```

**Claude Code.** Add the server from the command line, passing the credentials with `-e`:

```bash
claude mcp add --transport stdio semantic-bridge \
  -e SBRIDGE_TENANT_ID=your-tenant-id \
  -e SBRIDGE_CLIENT_ID=your-client-id \
  -e SBRIDGE_CLIENT_SECRET=your-client-secret \
  -- sbridge mcp
```

Or add a project `.mcp.json`. Claude Code expands `${VAR}` from your environment:

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "${SBRIDGE_TENANT_ID}",
        "SBRIDGE_CLIENT_ID": "${SBRIDGE_CLIENT_ID}",
        "SBRIDGE_CLIENT_SECRET": "${SBRIDGE_CLIENT_SECRET}"
      }
    }
  }
}
```

**Cursor** (`.cursor/mcp.json` in the project, or `~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "semantic-bridge": {
      "command": "sbridge",
      "args": ["mcp"],
      "env": {
        "SBRIDGE_TENANT_ID": "${env:SBRIDGE_TENANT_ID}",
        "SBRIDGE_CLIENT_ID": "${env:SBRIDGE_CLIENT_ID}",
        "SBRIDGE_CLIENT_SECRET": "${env:SBRIDGE_CLIENT_SECRET}"
      }
    }
  }
}
```

**VS Code** (`.vscode/mcp.json`, which uses the `servers` key):

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
        "SBRIDGE_CLIENT_SECRET": "${env:SBRIDGE_CLIENT_SECRET}"
      }
    }
  }
}
```

## Docker and HTTP transport

The image runs `sbridge mcp --http --host 0.0.0.0 --port 8080` and exposes port 8080. It contains no token, no secret and no write opt-in. Credentials come from `SBRIDGE_*` environment variables or from a mounted config file.

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e SBRIDGE_HTTP_TOKEN="$SBRIDGE_HTTP_TOKEN" \
  -e SBRIDGE_TENANT_ID -e SBRIDGE_CLIENT_ID -e SBRIDGE_CLIENT_SECRET \
  ghcr.io/neosunxz/semantic-bridge:latest
```

The `-e NAME` form passes the variable from your shell. For a config file, mount it and set `SBRIDGE_CONFIG` to the path inside the container.

The endpoint is `http://<host>:<port>/mcp`. Send the token as `Authorization: Bearer <token>`.

> **Security warning: use the HTTP server only on an intranet or in a test environment.**
>
> - Anyone who can reach the endpoint, and who has the token if one is set, can use the credentials of the profile the server runs with. Without a token, the server logs this warning when it starts.
> - Authentication, TLS and network access control are the deployer's responsibility. The server speaks plain HTTP and has no TLS option. Put TLS and network restrictions in front of it.
> - A bearer token is required on any non-loopback bind. Without `SBRIDGE_HTTP_TOKEN` the server refuses to start with `--host 0.0.0.0` (exit `2`), unless you pass `--no-auth`. Tokens are compared as hashes in constant time, and a token shorter than 16 characters produces a warning.
> - On a loopback bind, a request whose `Host` or `Origin` is not a loopback name is rejected with `403`. This blocks DNS rebinding from a browser. On other binds, a request whose `Origin` host differs from its `Host` is rejected with `403`. Request bodies larger than 1,000,000 bytes are rejected.
> - Writes over HTTP need the opt-ins described in the [Write-risk warning](#write-risk-warning). The Docker image sets neither.

## Write-risk warning

> **Writes change the live semantic model.** A description update or a refresh changes what every report built on that model shows, as soon as it is applied. A refresh also uses capacity.

- Writes are off by default. `allow_write` is `false` unless a profile sets it to `true`. Enable it only in a profile that needs it.
- Over HTTP, writes also need `--allow-http-write` (or `SBRIDGE_ALLOW_HTTP_WRITE=1`), on top of `allow_write: true`. Both are needed on the HTTP server; the stdio server needs only `allow_write: true`.
- After an XMLA write, a model that was authored in Power BI Desktop can no longer be downloaded as a PBIX file.
- Use a separate profile with a separate service principal that has write rights, and enable it only when you need it.
- Description updates need the XMLA endpoint set to Read Write on the capacity.
- CLI: `describe` shows each change as `old -> new` and asks before it applies. `refresh` asks before it queues. `--yes` skips the question.
- MCP: the server does not ask for confirmation. The write tools' descriptions tell the agent to get your explicit confirmation before it calls them. Your protection is the agent following those instructions, so check what the agent is about to change.

## Pro workspaces and REST limits

Workspaces that are not on dedicated capacity have no XMLA endpoint. On these workspaces, `query` with the default `--via auto` runs over REST. The following applies:

- `schema` and `describe` need XMLA and fail on these workspaces. `schema --via rest` is refused, because INFO functions are not supported by `executeQueries`.
- `executeQueries` returns at most 100,000 rows or 1,000,000 values per query, whichever is hit first, and at most 120 query requests per minute per user (Execute Queries page above).
- `executeQueries` also caps a response at about 15 MB of data. This was observed live as the error `DaxByteCountNotSupported` ("More than 15728640 bytes"). `sbridge` reports it as `truncated: true` with the reason.
- If REST returns an error after partial data, the result is marked truncated.
- Microsoft does not support service principals on datasets with row-level security or SSO (Execute Queries page above).
- `refresh` and `refresh-status` use REST, so they work on these workspaces too.

## Licence

Semantic Bridge for Power BI is released under the [MIT License](LICENSE).

The Microsoft Analysis Services client libraries (ADOMD.NET, and AMO/TOM) are not covered by the MIT License. They are provided under the Microsoft Software License Terms, and copies are in [licenses/](licenses/). By using the binaries, the `semantic-bridge` .NET tool or the Docker image, you agree to those terms. The other third-party components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
