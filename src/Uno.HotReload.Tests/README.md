# Hot Reload dependency regression

`Uno.HotReload` and `Uno.UI.RemoteControl.Server.Processors` deliberately keep
an explicit `System.Security.Cryptography.Xml` dependency. Their MSBuild and
Roslyn dependencies otherwise select an older transitive version. The 8.0.4
servicing update addresses the July 2026 XML crypto advisories (including
[GHSA-cvvh-rhrc-wg4q](https://github.com/dotnet/runtime/security/advisories/GHSA-cvvh-rhrc-wg4q))
without upgrading either host framework or the Roslyn/MSBuild toolset.

After restoring the actual project graph with NuGet auditing enabled, run:

```powershell
python build/test-scripts/validate-hot-reload-crypto-graph.py
dotnet test --project src/Uno.HotReload.Tests/Uno.HotReload.Tests.csproj
```

The graph check reads both owners' real `project.assets.json` files. It rejects
missing assets, an unpatched direct or selected dependency, missing compile
or runtime assets, and disabled auditing. Keep this check aligned with future
servicing updates. Restore must also retain warnings-as-errors; never suppress
an audit failure or remove Hot Reload from a runtime-test graph to make these
checks pass. Test/build results and a successful restore are separate evidence.
