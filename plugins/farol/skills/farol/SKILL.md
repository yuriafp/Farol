---
name: farol
description: >
  Playbook for modernizing .NET Framework code with Farol: what is legacy, what breaks on modern .NET, the web.config
  mapping and a migration plan, in that order.
---

# Modernizing .NET Framework code with Farol

1. `dotnet_legacy_inventory`: what is legacy and where (classic projects, packages.config, WebForms, ASMX, WCF,
   WinForms, WPF, BinaryFormatter, System.Configuration…), with counts and the files where it concentrates.
2. `dotnet_portability` (default target `net10.0`): the .NET Framework APIs that are missing or obsolete on the target,
   by technology, with locations and replacements. WinForms and WPF are checked against `-windows`, where they run.
3. `dotnet_config_inspect` on web.config/app.config: settings and connection strings with secrets masked, and the
   appsettings.json mapping. Never copy masked values back as real ones.
4. `dotnet_migration_plan`: steps bottom-up by project dependency, each with an approach, a size and file:line tasks.
   `write=true` saves `docs/modernization/assessment.md`, `plan.md` and `tasks.md` (ticked tasks survive regeneration).

While changing code:

- Before deleting a member that looks unused, check `dotnet_find_references`: WebForms markup (`OnClick="..."`),
  .asmx/.svc files and XAML event handlers reference code the compiler never sees.
- Before calling a package API you are not certain about, read it with `dotnet_package_api` (the exact version's
  signatures and docs); `dotnet_packages` shows resolved versions and vulnerable or deprecated packages.
- The plugin's hook checks each .cs/.vb edit and puts new compiler errors in front of you. `dotnet_build` picks Visual
  Studio's MSBuild for classic projects, where `dotnet build` fails; `dotnet_test` with `affectedBy='changes'` runs only
  the tests that reach your edits.

Classic .NET Framework projects need Windows with Visual Studio 2022+ or Build Tools for full fidelity;
`dotnet_workspace` says when they load with less.
