# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Project: Semantic Bridge for Power BI. Keep this file short.

> `AGENTS.md` is a symlink to this file for other agent tools.

## What this is

An open-source CLI (`sbridge`) and MCP server that let people and AI tools use their existing Power BI semantic models over **XMLA** and the **Power BI REST API**, in the global and China (21Vianet) clouds, with the user's own service-principal credentials.

Status: Phases 1–5 of [the v1 plan](docs/superpowers/plans/2026-11-04-v1-plan.md) done; live-verified on linux-x64 against a China (21Vianet) PPU workspace; the first release is pending.

Design (source of truth for scope): [docs/superpowers/specs/2026-11-03-semantic-bridge-design.md](docs/superpowers/specs/2026-11-03-semantic-bridge-design.md)

Not affiliated with or endorsed by Microsoft. Power BI is a trademark of Microsoft.

## Stack

- .NET 10 (LTS), C#. One core library + two thin front-ends (CLI, MCP server).
- XMLA via ADOMD.NET; description writes via TOM; REST via HttpClient + MSAL.
- Distribution: self-contained single-file binaries, `dotnet tool`, Docker image (GHCR + Docker Hub).
- Names: CLI command `sbridge`, NuGet tool ID `semantic-bridge`, env var prefix `SBRIDGE_`.

## Commands

```bash
dotnet build
dotnet test                                        # Microsoft.Testing.Platform runner (global.json)
dotnet test --project tests/SemanticBridge.Tests -- --filter-method "*<TestName>*"   # single test
dotnet format --verify-no-changes
dotnet run --project src/SemanticBridge -- <args>  # run the CLI (sbridge)
```

- Layout: `src/SemanticBridge.Core` (all logic; `BridgeService` is the one facade both front-ends call), `src/SemanticBridge` (the `sbridge` exe: CLI commands and `sbridge mcp`, under `Mcp/`), `tests/SemanticBridge.Tests` (no network), `tests/SemanticBridge.IntegrationTests` (skipped unless `SBRIDGE_IT_*` or `SBRIDGE_*` credentials are set).
- Package versions are central in `Directory.Packages.props`; `spike/` is a throwaway live-connectivity probe outside the solution.

## Red lines

- Never log, print, or return secrets, tokens, or full connection strings (CLI output, MCP results, errors, `config show`).
- Never truncate results silently: any cap that is hit must be reported.
- Writes are off unless `allow_write: true`; over HTTP they also need `--allow-http-write`. Never weaken this gating.
- No company- or customer-specific code, names, endpoints, or sample data (this project was extracted from a private system).
- Don't rename engine column names in results (`Table[Column]`, `[Measure]`).
- Code, identifiers, comments, docs, commit messages and CLI/tool names are in English, except `README.zh-CN.md` (the Chinese guide for the China cloud).

## Coding guidelines

1. Think before writing: state assumptions; if unclear, ask.
2. Simplest code that solves the problem; no unrequested features, abstractions, or options.
3. Surgical changes: touch only what the task needs; match the surrounding style; clean up only the orphans you created.
4. Goal-driven: turn tasks into verifiable checks (tests first where practical).

## Git

- Conventional commits: `<type>(<scope>): <subject>`, English.
- Never `--no-verify`. Split large AI-generated changes into reviewable commits.
- Don't commit credentials or local config; `CLAUDE.local.md` is git-ignored.

## Code → docs mapping

| Code change | Docs to check/update |
|---|---|
| Config keys, env vars, cloud presets | README.md and README.zh-CN.md (configuration section), config template |
| CLI commands/options | README.md and README.zh-CN.md (CLI section) |
| MCP tools (names, inputs, behaviour) | README.md and README.zh-CN.md (MCP section and client configs) |
| Write capabilities or gating | README.md and README.zh-CN.md (write-risk warning), config template |
| Dockerfile / HTTP transport | README.md and README.zh-CN.md (Docker section and security warning) |
| Supported platforms / verified status | README.md and README.zh-CN.md (status and platforms), release notes |
| `.claude/agents/*.md` | this file (Subagents section) |

## Subagents

Defined in `.claude/agents/*.md`, started on demand; each runs in a forked context.

| Agent | Model | Role |
|---|---|---|
| `explorer` | Sonnet | Read-only exploration and architecture design |
| `implementer` | Sonnet | Writes code; states Scope / Forbidden / Validation first, self-checks after |
| `auditor` | Sonnet | Security audit and merge-risk assessment (read-only) |
| `reviewer` | Sonnet | Code-quality review (read-only) |
| `verifier` | Haiku | Runs build/test/format, Docker build, scripted live probes; reports counts honestly |
| `scribe` | Haiku | Mechanical doc sync per the mapping table; every statement cites code |
| `locator` | Haiku | Fast read-only lookup: definitions, callers, what a commit changed, with file:line |

### Pipeline

`explorer → implementer → verifier ↺ (max 3 retries back to implementer) → scribe → reviewer → auditor → owner merges`

The main session clarifies requirements, splits tasks, decides, and reviews diffs and reports; it does not write feature code itself — implementation goes to `implementer`. Pause for the owner at: design confirmation, reviewer CHANGES_REQUESTED, auditor "fix before merge"/"do not merge", and the final merge.

### Cost conventions

- Mechanical work (builds, tests, lookups, doc sync) goes to Haiku agents so large output stays out of the main context.
- Batch related changes into one implementer task instead of many small ones.
- Prefer deterministic commands over agent analysis; only involve an agent when they fail.
- Read narrowly: grep first, then read the target lines.
