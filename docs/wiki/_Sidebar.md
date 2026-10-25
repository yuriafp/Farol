# Farol Wiki Documentation

Welcome to the official technical documentation for **Farol**, an open-source [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that equips AI coding agents (Claude Code, GitHub Copilot, Google Antigravity, Cursor, Codex) with compiler-accurate understanding of C# and VB.NET solutions.

---

### Core Documentation

- [Home](Home) — Project overview, value proposition, quickstart, and problem statement
- [01. Architecture & Design Philosophy](01-Architecture-and-Design-Philosophy) — Layered architecture, snapshot isolation, BuildHost model, design rationale
- [02. Layer: Farol.Core](02-Layer-Farol.Core) — Zero-Roslyn foundation: path sandbox, process execution, diffing, atomic writes, privacy
- [03. Layer: Farol.Engine — Workspaces & Loading](03-Layer-Farol.Engine-Workspaces-and-Loading) — MSBuildWorkspaceLoader, Visual Studio toolchain probing, background warmup, file watcher
- [04. Layer: Farol.Engine — Navigation & Markup](04-Layer-Farol.Engine-Navigation-and-Markup) — Compiler-accurate navigation, symbol search, IOperation classification, WebForms & XAML indexers, decompilation
- [05. Layer: Farol.Engine — Verification & Testing](05-Layer-Farol.Engine-Verification-and-Testing) — Diagnostic checks in about a second, baseline fingerprinting, edit impact analysis, Code Actions, build & test runners
- [06. Layer: Farol.Engine — Legacy Modernization & Packages](06-Layer-Farol.Engine-Legacy-and-Packages) — Legacy inventory, config inspector, target surface portability, migration planner, NuGet API inspector
- [07. Layer: Farol.Tools & MCP Contract](07-Tools-Layer-and-MCP-Contract) — Comprehensive reference for all 18 MCP tools and 4 MCP prompts
- [08. Layer: Farol.Host & CLI](08-Host-Layer-and-CLI) — Stdio transport, stream isolation, configuration precedence, privacy-first usage telemetry
- [09. Integrations & Plugins](09-Integrations-and-Plugins) — Claude Code plugin (hook & skill), Visual Studio 2026, Copilot, Antigravity, VS Code, NuGet distribution
- [10. Benchmarks & Evals](10-Benchmarks-and-Evals) — Performance benchmarks (Umbraco & DNN Platform), 30-task evaluation suite, compiler oracle
