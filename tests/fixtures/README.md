# Test fixtures

Sample solutions Farol is tested against. They are loaded, not built by the Farol solution.

| Fixture | What it exercises |
|---|---|
| `modern/` | .NET 10: `.slnx`, central package management, a library multi-targeting `net10.0;net48`, an ASP.NET Core minimal API. |
| `legacy/` | .NET Framework 4.8 classic (non-SDK) projects: C# library, VB.NET library, WebForms + ASMX + WCF web application, WinForms and WPF apps. Needs Windows with Visual Studio or Build Tools. |

The files in this folder (`Directory.Build.*`, `Directory.Packages.props`, `.editorconfig`) isolate the fixtures from Farol's own build settings.
