# SourceLink security and source-binding regression

`global.json` and the CI replacement `build/ci/net10/_global.json` select SDK
**10.0.111** without roll-forward. SourceLink-enabled
projects reference the published `Microsoft.SourceLink.GitHub` **10.0.111**
package and its matching Git/Common dependencies. These are real NuGet package
versions, not values inferred solely from an SDK advisory.

[GHSA-23fw-v26w-5fgq](https://github.com/advisories/GHSA-23fw-v26w-5fgq)
affects `Microsoft.Build.Tasks.Git` 8.0.0 and the SDK-serviced versions listed
there. The 8.0.0 package has no patched 8.x replacement. Pinning only the
package would not qualify an older SDK's built-in providers; use the selected
patched SDK rather than replacing DLLs inside an SDK or restored package.

## Configuration

`SourceLinkEnabled` and the SDK's `EnableSourceLink` are aliases at repository
configuration entry. Set either one, or set both consistently. Contradictory
values fail as an initial target, before SourceLink generation, including
direct calls to `GenerateSourceLinkFile` and its `_GenerateSourceLinkFile`
writer in either contradiction direction. The normal build guard is retained.
Ordinary local builds remain off by default; GitHub builds retain their enabled
default.

`SourceLinkEnabled=false` also prevents implicit untracked-source embedding
and does not add PDBs to normal package output. It does not remove normal
compiler PDB output or override an explicitly requested `EmbedAllSources`.
For source-binding validation, use `EmbedAllSources=true` and portable PDBs.
The selected revision comes from the actual Git checkout; the repository URL
must match its real GitHub origin.

## Regression

Use an already installed matching SDK, an explicit audited restore
configuration, and a new output directory:

```powershell
pwsh -NoProfile -File build\Test-SourceLink.ps1 `
  -DotNetPath <dotnet-executable> `
  -RestoreConfigFile <absolute-approved-NuGet.Config> `
  -OutputDirectory <new-owned-output-directory>
```

The configuration must provide one explicit package cache and clear unwanted
fallback folders. Set CLI home, cache and temporary paths process-locally for
an isolated toolchain. Do not initialize a fresh home against a shared SDK or
disable certificate/audit checks to make the test pass.

The script restores and builds the real small `Uno.Foundation.Logging`
project for net9 and net10, with SourceLink on and off in distinct outputs.
It records effective properties and actual MSBuild task-load diagnostics,
verifies the Git/GitHub/Common task DLLs against their matching restored ZIP
entries, and retains archive/DLL hashes and file versions.
Off mode has no package override but still executes SDK Git/SourceLink tasks;
an empty package-task list is not evidence of task absence. The SDK pin does
not remediate other SDK versions separately installed by CI jobs.

The verifier checks the PE CodeView/portable-PDB identity and every document
checksum. Enabled builds require embedded bytes matching the actual source
file; tracked documents also bind to the selected Git blob. Normal Git
checkout line-ending conversion is reported explicitly, not treated as exact
byte identity with the remote LF blob. Generated files must remain within the
selected intermediate directory and match their PDB checksums too.
Every enabled non-generated document must have a usable, unambiguous
[SourceLink mapping](https://github.com/dotnet/designs/blob/main/accepted/2020/diagnostics/source-link.md).
Keys match case-insensitively, with exact/most-specific precedence and paired
wildcard validation. The resolved repository-relative URL path must match
the document's source path and selected repository/revision. Local path
lookup alone cannot qualify an enabled tracked document. Generated-source
and off-mode checks use the selected local roots without requiring a mapping.

Off controls reject SourceLink records and embedded source. Negative fixtures
alter a document checksum in an independent PDB copy and supply a wrong source
revision; both must fail for the intended reason. Independent mapping copies
preserve the PDB ID, document checksums and embedded source while testing
unmatched keys, wrong resolved paths, ambiguous/duplicate keys and invalid
wildcards. Correct overlapping case-insensitive and exact entries are positive
controls. Conflicting switches must fail for both target entry points and
normal build preparation on both frameworks, with no mapping created in
fresh output directories. Prior negative outputs are retained, not cleaned
to hide a write-before-failure. No published package or accepted artifact is
mutated.

These checks establish the tested tooling/source-binding path, not application
runtime behavior, package publication, ICU provenance, or a full IL verifier.
