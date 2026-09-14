# Uno.UI.Tasks dependency security

`Microsoft.Build.Tasks.Core` 17.8.43 transitively selects
`System.Formats.Asn1` 7.0.0 through its cryptography dependencies.
[GHSA-447r-wph3-92pm](https://github.com/advisories/GHSA-447r-wph3-92pm)
identifies versions from the .NET 7 preview line through versions below 8.0.1
as vulnerable; 8.0.1 is the first patched version for that range.

`Uno.UI.Tasks` therefore pins `System.Formats.Asn1` 8.0.1 as a private
dependency. That package has a `netstandard2.0` asset, so it can service this
task project without changing its target framework or exposing the dependency
to project-reference consumers. Keep the direct pin while the project remains
on the MSBuild 17.8 compatibility baseline. Microsoft.Build.Tasks.Core 17.9.5
retains the affected cryptography graph; moving to a later MSBuild feature line
would require a coordinated compatibility update of the other MSBuild
references rather than a single-package security substitution.

Restore the actual graph with NuGet auditing enabled, then validate the
selected dependency and audit policy:

```powershell
dotnet restore src/SourceGenerators/Uno.UI.Tasks/Uno.UI.Tasks.csproj `
  -p:NuGetAudit=true -p:NuGetAuditMode=all
python build/test-scripts/validate-uno-ui-tasks-crypto-graph.py
```

The graph check rejects a missing or non-private direct pin, an affected
selected version, absent compile/runtime assets, disabled auditing, or
suppressed audit warnings. A passing restore and graph check do not establish
task execution, framework compilation, or runtime acceptance.
