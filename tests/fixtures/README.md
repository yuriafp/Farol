# Test fixtures

Sample solutions Farol is tested against. They are loaded, not built by the Farol solution.

| Fixture | What it exercises |
|---|---|
| `modern/` | .NET 10: `.slnx`, central package management, a library multi-targeting `net10.0;net48`, an ASP.NET Core minimal API, an xUnit v3 test project run through Microsoft.Testing.Platform (its `global.json` opts in). |
| `legacy/` | .NET Framework 4.8 classic (non-SDK) projects: C# library, VB.NET library, WebForms + ASMX + WCF web application, WinForms and WPF apps, an MSTest project restored through `packages.config`. Needs Windows with Visual Studio or Build Tools. |

The files in this folder (`Directory.Build.*`, `Directory.Packages.props`, `.editorconfig`) isolate the fixtures from Farol's own build settings.

**Vulnerable and deprecated packages on purpose.** `dotnet_packages` is tested against real nuget.org data, so the fixtures pin packages with published advisories and deprecations. Nothing here is built into Farol or shipped.

| Package | Where | Why |
|---|---|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.0 | `modern/src/Modern.Api` | Brings in `Microsoft.IdentityModel.*` 7.0.3: deprecated, two of them vulnerable (GHSA-59j7-ghrg-fj52), all transitive. |
| `System.Text.Json` 8.0.4 | `modern/src/Modern.Core`, net48 only | A direct vulnerable package in one target framework (GHSA-8g4q-xg66-9fp4). |
| `Newtonsoft.Json` 12.0.3 | `legacy/Legacy.Tests` (packages.config) | Vulnerable (GHSA-5crp-9r3c-p9vr); its 12.0.3 API also differs from 13.0 for `dotnet_package_api`. |
| `MSTest.TestAdapter`, `MSTest.TestFramework` 2.2.10 | `legacy/Legacy.Tests` (packages.config) | Deprecated (legacy). |

GitHub's dependency graph may raise Dependabot alerts for these manifests; they can be dismissed as "used in tests".
