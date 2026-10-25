# Layer: Farol.Engine — Navigation & Markup

The Navigation and Markup subsystem equips AI agents with compiler-accurate symbol resolution, cross-language reference searching, type hierarchies, call graphs, and markup indexing across C#, VB.NET, ASP.NET WebForms, and WPF XAML.

---

## 1. Subsystem Architecture

```
Farol.Engine (Navigation & Markup)
├── Navigation/
│   ├── SymbolLocator.cs          # Resolves symbols by name, doc-comment ID, or path:line
│   ├── DeclarationSearch.cs      # Parallel symbol declaration searching
│   ├── NamePattern.cs            # Fast camel-case humps, prefix, and substring matcher
│   ├── ReferenceFinder.cs        # Unified C#/VB reference search with IOperation classification
│   ├── HierarchyFinder.cs        # Base types, derived types, and interface implementations
│   ├── CallHierarchyFinder.cs    # Caller/callee trees, 1 to 3 levels, with call sites
│   ├── OutlineBuilder.cs         # Structural outlines (members & signatures, no bodies)
│   ├── SymbolFormatter.cs        # Standard documentation comment ID & signature formatting
│   └── MetadataSource.cs         # Assembly decompilation via ICSharpCode.Decompiler
└── Markup/
    ├── MarkupIndex.cs            # In-memory index of code symbols referenced by markup
    ├── MarkupReference.cs        # Markup reference model (file, line, symbol, detail)
    ├── MarkupFiles.cs            # Discovers .aspx, .ascx, .master, .asax, .ashx, .asmx, .svc, .xaml files
    ├── WebFormsMarkupParser.cs   # Regex/scanner parser for WebForms directives & server tags
    └── XamlMarkupParser.cs       # XML-based parser for WPF XAML event wiring & types
```

---

## 2. Symbol Resolution (`SymbolLocator`)

Tools like `dotnet_symbol`, `dotnet_find_references`, and `dotnet_hierarchy` accept symbols in multiple formats to accommodate how LLMs think:

1. **Short Unqualified Name**: `OrderCalculator`, `GetTotal`
2. **Dotted Name**: `Legacy.Core.Orders.OrderCalculator.GetTotal`
3. **Documentation Comment ID**: `M:Legacy.Core.Orders.OrderCalculator.GetTotal(System.Int32)`
4. **File Location (`path:line`)**: `Legacy.Core/Orders/OrderCalculator.cs:14`

### Normalization of Roslyn 5.x Return Type Suffixes
In Roslyn 5.x, method documentation comment IDs append `~ReturnType` suffixes (e.g. `M:MyClass.MyMethod~System.Int32`). Farol normalizes IDs to the standard ECMA-334 format (retaining the tilde only for user-defined conversion operators), while accepting both forms seamlessly on input.

### Resolving Referenced & Framework Symbols (`AC-43`)
Agents frequently query standard framework types or package classes (e.g. `StringBuilder`, `Newtonsoft.Json.JsonConvert`). `SymbolLocator` searches not only source declarations within the solution but also external metadata references imported by projects. `dotnet_symbol` names the assembly such a symbol comes from; `dotnet_outline` refuses it (it has no source), and the downward directions of `dotnet_hierarchy` list only the workspace's own code.

### Graceful Ambiguity Handling (`AC-9`)
If a name matches multiple symbols (for example, an overloaded method or an interface and its implementation), Farol never fails or picks one arbitrarily. It returns an organized list of all matching candidates with their documentation comment IDs, allowing the agent to refine its query.

---

## 3. High-Performance Declaration Search (`NamePattern`)

The first benchmark on Umbraco CMS (0.77M lines) measured a p95 of 556 ms for symbol queries: Roslyn's pattern search matched every declared name of the solution on each call.

Farol replaced this with a custom parallel matcher (`NamePattern`):
- Scans the pre-warmed declaration tables of each compilation in parallel.
- **Camel-Case Hump Matching**: Matches camel-case acronyms (e.g. `OrdCalc` matches `OrderCalculator`, `IPubCont` matches `IPublishedContent`).
- **Prefix and Substring Matching**: Matches exact names, prefixes, and substrings.
- **Result**: CI then measured a p95 of **43 ms or less** on both benchmark corpora (target: 100 ms).

---

## 4. Compiler-Accurate Reference Finding (`ReferenceFinder`)

Unlike text search tools that return every occurrence of a word, `ReferenceFinder` uses Roslyn's semantic models and `IOperation` trees to locate verified references across C#, VB.NET, and multi-targeted projects.

### Reference Classification
Every reference is classified into its exact semantic kind:
- `call`: Method invocation.
- `new`: Object creation expression (instantiation site).
- `read`: Field, property, or local variable read.
- `write`: Assignment to a field, property, or variable.
- `method group`: Method passed as a delegate or event handler.
- `reference`: Type usage in a declaration, type argument, or cast.
- `markup`: Reference discovered in WebForms or XAML markup.

### Locating Type Instantiations (`new`)
In Roslyn, searching for references to a type (`INamedTypeSymbol`) returns locations where the type is named (in casts, variable declarations, parameters), but leaves out instantiation sites (`new OrderCalculator()`), because Roslyn files constructor calls under the constructor method. Farol explicitly queries constructors and includes instantiation sites in the type's reference list, labeled as `new`.

### Interface Implementations and Overrides (`AC-37`)
When searching for references to a method that implements an interface or overrides a base class method (e.g. `ModuleController.GetModule`), callers frequently invoke the member through the interface (`IModuleController.Instance.GetModule()`).
Roslyn's default symbol search files those calls under the interface member. Farol checks the symbol's hierarchy:
- Discovers solution-declared interface members it implements and base members it overrides.
- Ingests calls made through those contract members.
- Marks each such hit `via` the contract member it went through, and says how many came through each.

---

## 5. Markup Indexing: WebForms & XAML (`MarkupIndex`)

In legacy enterprise code, millions of lines of C# and VB.NET code-behind logic are wired up through markup rather than compiled C# code. If an agent checks for references using the compiler alone, these methods appear unreferenced.

`MarkupIndex` parses and indexes markup files across the solution:

### WebForms Parser (`WebFormsMarkupParser`)
Scans `.aspx`, `.ascx`, `.master`, `.asmx`, `.svc`, `.ashx`, and `Global.asax`:
- **Directive Inheritance**: Extracts `<%@ Page Inherits="Namespace.ClassName" %>` and `<%@ WebService Class="Namespace.Service" %>` to bind code-behind and service classes.
- **Server Control Event Wireup**: Parses `<asp:Button runat="server" OnClick="btnSubmit_Click" />`, mapping event attributes directly to code-behind methods.
- **Model Binding Directives**: Extracts `SelectMethod`, `InsertMethod`, `UpdateMethod`, and `DeleteMethod` attributes used in WebForms data-bound controls.
- **Auto-Event Wireup**: Page and control lifecycle handlers (`Page_Load`, `Page_Init`, …) wired by name, unless the directive sets `AutoEventWireup="false"`, and `Global.asax` handlers such as `Application_Start`.

### XAML Parser (`XamlMarkupParser`)
Parses WPF XAML files using XML line-aware readers:
- **`x:Class` Bindings**: Connects XAML roots to partial code-behind classes.
- **Event Wireup**: Identifies attributes matching event handlers (`Click="OnCalculateClick"`).
- **Element Names**: Connects `x:Name` declarations to backing fields.
- **Markup Extensions**: Resolves `{x:Type local:CustomControl}` and `{x:Static local:Constants.DefaultColor}`.

---

## 6. Type and Call Hierarchies

- **Type Hierarchy (`HierarchyFinder`)**:
  - `all` (default): both directions.
  - `base`: upwards: base types and implemented interfaces, or the members a member overrides or implements.
  - `derived`: downwards, including implementations and overrides.
  - `implementations`: types implementing an interface, or members implementing an interface member.
  - Downward results list only types and members declared in the workspace's source, not the framework's.
- **Call Hierarchy (`CallHierarchyFinder`)**:
  - Traces callers (default) or callees, 1 to 3 levels (default 1).
  - Identifies the file and line of up to 5 call sites per node.

---

## 7. Structural Outlines (`OutlineBuilder`)

Agents frequently read entire files just to understand their structure, rapidly consuming context windows.
`OutlineBuilder` extracts an AST outline of a file or type:
- Lists types and members with their signatures and line numbers.
- **Leaves out method and property bodies**, giving a compact structural map at a fraction of the token cost.

---

## 8. Assembly Decompilation (`MetadataSource`)

When an agent inspects a symbol originating from a NuGet package or the .NET runtime (via `dotnet_symbol include='source'`), reference assemblies contain no IL method bodies.

Farol embeds `ICSharpCode.Decompiler`:
1. Locates the runtime implementation assembly from the package's `lib/` folder, the installed shared framework, or the .NET Framework directory.
2. Decompiles the IL byte code back into clean, readable C# syntax.
3. Formats external types as a skeleton of their member signatures.
