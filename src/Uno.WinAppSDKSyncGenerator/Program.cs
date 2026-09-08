using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Uno.WinAppSDKSyncGenerator
{
	class Program
	{
		const string SyncMode = "sync";
		const string DocMode = "doc";
		const string AllMode = "all";

		static async Task Main(string[] args)
		{
			if (args.Length == 0)
			{
				Console.WriteLine("Supported modes: doc, sync [fully-qualified type [--reference assembly-path | --reference-for platform assembly-path]...], all.");
				return;
			}

			var mode = args[0].ToLowerInvariant();
			if ((mode != SyncMode && mode != DocMode && mode != AllMode) ||
				(args.Length >= 2 && (mode != SyncMode || string.IsNullOrWhiteSpace(args[1]))))
			{
				throw new ArgumentException("Use doc, sync [fully-qualified type [--reference assembly-path]...], or all.");
			}
			var typeFilter = args.Length >= 2 && mode == SyncMode ? args[1] : null;
			var references = new List<PortableExecutableReference>();
			var platformReferences = new Dictionary<string, List<PortableExecutableReference>>(StringComparer.Ordinal);
			for (var index = 2; index < args.Length;)
			{
				if (args[index] == "--reference" && index + 1 < args.Length)
				{
					references.Add(MetadataReference.CreateFromFile(Path.GetFullPath(args[index + 1])));
					index += 2;
				}
				else if (args[index] == "--reference-for" && index + 2 < args.Length &&
					args[index + 1] is "android" or "ios" or "tvos" or "skia" or "wasm" or "reference")
				{
					var platform = args[index + 1];
					if (!platformReferences.TryGetValue(platform, out var values))
					{
						platformReferences.Add(platform, values = new List<PortableExecutableReference>());
					}
					values.Add(MetadataReference.CreateFromFile(Path.GetFullPath(args[index + 2])));
					index += 3;
				}
				else
				{
					throw new ArgumentException("Use --reference assembly-path or --reference-for platform assembly-path.");
				}
			}
			Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
			if (typeFilter is null)
			{
				DeleteDirectoryIfExists(@"..\..\..\Uno.UI\Generated\");
				DeleteDirectoryIfExists(@"..\..\..\Uno.UWP\Generated\");
				DeleteDirectoryIfExists(@"..\..\..\Uno.Foundation\Generated\");
				DeleteDirectoryIfExists(@"..\..\..\Uno.UI.Composition\Generated\");
				DeleteDirectoryIfExists(@"..\..\..\Uno.UI.Dispatching\Generated\");
			}

			if (mode == SyncMode || mode == AllMode)
			{
				await new SyncGenerator(typeFilter, references, platformReferences).Build();
			}

			if (mode == DocMode || mode == AllMode)
			{
				await new DocGenerator().Build();
			}
		}

		private static void DeleteDirectoryIfExists(string path)
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
	}
}
