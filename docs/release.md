# Releasing Farol

A release publishes the `Farol.Mcp` package to nuget.org and moves the Claude Code plugin to that version.

## 1. Set the version

The version appears in these files, and the test `Every_distribution_file_names_the_same_version` fails until they all agree:

- `Directory.Build.props` (`<Version>`): the package and the version the server reports
- `src/Farol.Host/.mcp/server.json`: `version` and `packages[0].version`
- `plugins/farol/.claude-plugin/plugin.json`: `version`
- `plugins/farol/.mcp.json`: the `Farol.Mcp@<version>` the plugin runs
- `README.md` and `src/Farol.Host/README.md`: the `Farol.Mcp@<version>` in the configuration examples

Breaking changes to tool or prompt names, parameters or annotations need a new major version; the `mcp-surface.json` snapshot test points them out.

Add the version's section to `CHANGELOG.md`, with the release date.

## 2. Verify

```bash
dotnet test --solution Farol.slnx
dotnet pack src/Farol.Host -c Release -o artifacts/packages
```

Run the package the way clients do, from the local folder and with a throwaway NuGet cache, so the locally built version never shadows the published one in your real cache:

```powershell
$env:NUGET_PACKAGES = Join-Path $env:TEMP "farol-release-check-$(Get-Random)"
dotnet dnx Farol.Mcp@<version> --yes --add-source artifacts/packages
```

The server waits for an MCP client on stdin; any client configured with the same command must list 18 tools and 4 prompts. Then run the [manual smoke tests](smoke-tests.md).

## 3. Publish

```bash
dotnet nuget push artifacts/packages/Farol.Mcp.<version>.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json
```

Use an API key scoped to pushing `Farol.Mcp` (or set up [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) for a GitHub Actions release). nuget.org validates and indexes the package in a few minutes; after that, `dotnet dnx Farol.Mcp@<version> --yes` must start it from a clean cache.

On the first release, ask nuget.org to reserve the `Farol.` ID prefix (the [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation) page explains how), so no one else can publish `Farol.*` packages.

## 4. Tag and announce

Tag the commit the package was packed from, which its `.nuspec` records (`<repository commit="...">`), so the tag
matches the source nuget.org links to even when commits landed after the pack:

```bash
git tag v<version> <commit>
git push origin v<version>
```

Create the GitHub release from the tag, with the version's `CHANGELOG.md` section as its notes. Publishing it starts the
Published package workflow, which installs the version the plugin runs from nuget.org and checks it on the fixtures;
it also runs every week. Claude Code users receive the new plugin version when they update the marketplace (`/plugin marketplace update farol`); the plugin then runs the new server version.

The MCP Registry listing (`server.json` is ready for it) comes in Phase 2.
