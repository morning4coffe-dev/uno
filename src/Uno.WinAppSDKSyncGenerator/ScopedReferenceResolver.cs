#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Uno.WinAppSDKSyncGenerator;

internal static class ScopedReferenceResolver
{
	internal static string GetPlatformName(string projectFile, string? targetFramework)
	{
		if (targetFramework?.Contains("-android", StringComparison.OrdinalIgnoreCase) == true) { return "android"; }
		if (targetFramework?.Contains("-ios", StringComparison.OrdinalIgnoreCase) == true) { return "ios"; }
		if (targetFramework?.Contains("-tvos", StringComparison.OrdinalIgnoreCase) == true) { return "tvos"; }
		if (projectFile.EndsWith(".Skia.csproj", StringComparison.OrdinalIgnoreCase)) { return "skia"; }
		if (projectFile.EndsWith(".Wasm.csproj", StringComparison.OrdinalIgnoreCase)) { return "wasm"; }
		if (projectFile.EndsWith(".Reference.csproj", StringComparison.OrdinalIgnoreCase)) { return "reference"; }
		throw new ArgumentException($"Unknown scoped generation platform: {projectFile} ({targetFramework}).");
	}

	internal static Compilation Resolve(Compilation compilation, IReadOnlyList<PortableExecutableReference> references, Action<string>? log = null)
	{
		var metadata = CSharpCompilation.Create("ScopedReferenceInspection", references: references);
		var replacements = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
		foreach (var reference in references)
		{
			if (metadata.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
			{
				throw new ArgumentException($"Not an assembly reference: {reference.FilePath}", nameof(references));
			}
			if (!replacements.TryAdd(assembly.Identity.Name, reference))
			{
				throw new ArgumentException($"Multiple explicit references supplied for '{assembly.Identity.Name}'.", nameof(references));
			}
			log?.Invoke($"Scoped metadata: {assembly.Identity} from {reference.FilePath}");
		}
		if (replacements.ContainsKey(compilation.AssemblyName!))
		{
			throw new ArgumentException("The implementation assembly being inspected cannot be replaced by metadata.", nameof(references));
		}

		var cache = new Dictionary<Compilation, Compilation>();
		return Visit(compilation);

		Compilation Visit(Compilation source)
		{
			if (cache.TryGetValue(source, out var resolved))
			{
				return resolved;
			}
			var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var updated = new List<MetadataReference>();
			foreach (var reference in source.References)
			{
				var name = (source.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol)?.Identity.Name;
				if (name is not null && replacements.TryGetValue(name, out var replacement))
				{
					// Preserve extern aliases and interop properties from the actual project edge.
					updated.Add(replacement.WithProperties(reference.Properties));
					used.Add(name);
					var aliases = reference.Properties.Aliases.IsDefaultOrEmpty ? "<global>" : string.Join(",", reference.Properties.Aliases);
					log?.Invoke($"Scoped {source.AssemblyName}: {reference.Display} -> {replacement.FilePath}; aliases={aliases}");
				}
				else if (reference is CompilationReference projectReference)
				{
					var child = Visit(projectReference.Compilation);
					updated.Add(child.ToMetadataReference(reference.Properties.Aliases, reference.Properties.EmbedInteropTypes));
				}
				else
				{
					updated.Add(reference);
				}
			}
			foreach (var (name, reference) in replacements)
			{
				if (!used.Contains(name))
				{
					updated.Add(reference);
				}
			}
			resolved = source.WithReferences(updated);
			cache.Add(source, resolved);
			return resolved;
		}
	}
}
