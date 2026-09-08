using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Uno.WinAppSDKSyncGenerator;

namespace Uno.UI.SourceGenerators.Tests.ScopedApiValidationTests;

[TestClass]
public class Given_ScopedApiValidation
{
	[TestMethod]
	[DataRow("public MissingType Echo(MissingType value) => value;")]
	[DataRow("public event MissingType? Changed;")]
	[DataRow("public System.Collections.Generic.IReadOnlyList<MissingType>? Items { get; }")]
	[DataRow("public MissingType[]? Items { get; }")]
	[DataRow("public System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<MissingType[]>>? Items { get; }")]
	[DataRow("public void Run<T>() where T : MissingType { }")]
	[DataRow("public class Nested { public MissingType? Value; }")]
	[DataRow("public void Run(int value = MissingValue) { }")]
	[DataRow("public void Run([MissingAttribute] int value) { }")]
	[DataRow("public MissingType this[int index] => throw new System.Exception();")]
	[DataRow("public unsafe delegate*<MissingType, void> Callback;")]
	public void Unresolved_Public_Signature_Leaves_Output_Untouched(string member)
		=> AssertRefused($"public class Target {{ {member} }}");

	[TestMethod]
	[DataRow("public class Target : MissingBase { }")]
	[DataRow("public class Target : System.Collections.Generic.List<MissingType> { }")]
	[DataRow("public interface Target : MissingInterface { }")]
	[DataRow("public delegate MissingType Target(MissingType value);")]
	[DataRow("public record Target(int Value = MissingValue);")]
	[DataRow("public delegate MissingType Broken(); public class Target { public event Broken? Changed; }")]
	[DataRow("public interface IValue { object Value { get; } } public class Target : IValue { MissingType IValue.Value => throw new System.Exception(); }")]
	[DataRow("public interface IValue { object Echo(object value); } public class Target : IValue { MissingType IValue.Echo(object value) => throw new System.Exception(); }")]
	[DataRow("public class BrokenBase { public MissingType Echo(MissingType value) => value; } public class Target : BrokenBase { }")]
	[DataRow("public class BrokenBase { public MissingType[]? Value { get; } } public class Intermediate : BrokenBase { } public class Target : Intermediate { }")]
	[DataRow("public interface Broken { MissingType Echo(MissingType value); } public interface Intermediate : Broken { } public interface Target : Intermediate { }")]
	[DataRow("public class BrokenBase { public event System.Action<MissingType>? Changed; } public class Target : BrokenBase { }")]
	[DataRow("public class BrokenBase { public void Run(int value = MissingValue) { } } public class Target : BrokenBase { }")]
	[DataRow("public class BrokenBase<T> { public System.Collections.Generic.IReadOnlyList<MissingType>? Echo(T value) => null; } public class Target : BrokenBase<int> { }")]
	public void Unresolved_Type_Declaration_Leaves_Output_Untouched(string source)
		=> AssertRefused(source);

	[TestMethod]
	public void Valid_Public_Signatures_Generate_Despite_Unrelated_Body_Diagnostics()
	{
		var compilation = Compile("""
			public class Target
			{
				public System.Collections.Generic.IReadOnlyList<string[]>? Items { get; }
				public event System.EventHandler? Changed;
				public string Echo(string value) { UnavailablePlatformImplementation(); return value; }
				public int Value { get { UnavailablePlatformImplementation(); return 0; } }
				public void Run<T>() where T : System.IComparable<T> { }
			}
			""");
		Assert.IsTrue(compilation.GetDiagnostics().Any(diagnostic => diagnostic.Id == "CS0103"));
		var path = OutputPath();
		ScopedApiValidation.ValidateAndWrite(path, new[] { (compilation, compilation.GetTypeByMetadataName("Target")) }, () => "valid output");
		Assert.AreEqual("valid output", File.ReadAllText(path));
	}

	[TestMethod]
	public void Later_Platform_Failure_Does_Not_Render_Or_Overwrite()
	{
		var valid = Compile("public class Target { public int Value { get; } }");
		var invalid = Compile("public class Target { public MissingType Echo(MissingType value) => value; }");
		var path = OutputPath();
		File.WriteAllText(path, "original output");
		var rendered = false;
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (valid, valid.GetTypeByMetadataName("Target")), (invalid, invalid.GetTypeByMetadataName("Target")) },
			() => { rendered = true; return "bad output"; }));
		Assert.IsFalse(rendered);
		Assert.AreEqual("original output", File.ReadAllText(path));
	}

	[TestMethod]
	public void Rendering_Failure_Leaves_Output_Untouched()
	{
		var compilation = Compile("public class Target { }");
		var path = OutputPath();
		File.WriteAllText(path, "original output");
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (compilation, compilation.GetTypeByMetadataName("Target")) }, () => throw new InvalidOperationException("render failure")));
		Assert.AreEqual("original output", File.ReadAllText(path));
	}

	[TestMethod]
	public void Null_Array_Attribute_Argument_Is_Valid()
	{
		var compilation = Compile("""
			public sealed class MarkerAttribute : System.Attribute { public MarkerAttribute(params string[] values) { } }
			[Marker(null)]
			public class Target { }
			""");
		var path = OutputPath();
		ScopedApiValidation.ValidateAndWrite(path, new[] { (compilation, compilation.GetTypeByMetadataName("Target")) }, () => "valid attribute");
		Assert.AreEqual("valid attribute", File.ReadAllText(path));
	}

	[TestMethod]
	public void Real_Metadata_Reference_Resolves_Missing_Base_Identity()
	{
		var source = Compile("public class Target : System.Attribute { }").RemoveAllReferences();
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		Assert.AreEqual(TypeKind.Error, source.GetSpecialType(SpecialType.System_Object).TypeKind);
		var resolved = ScopedReferenceResolver.Resolve(source, new[] { reference });
		var type = resolved.GetTypeByMetadataName("Target");
		Assert.IsNotNull(type);
		Assert.AreEqual(typeof(Attribute).FullName, type.BaseType!.ToDisplayString());
		Assert.AreEqual(typeof(Attribute).Assembly.GetName().Name, type.BaseType.ContainingAssembly.Name);
		var path = OutputPath();
		ScopedApiValidation.ValidateAndWrite(path, new[] { (resolved, (INamedTypeSymbol?)type) }, () => "real reference");
		Assert.AreEqual("real reference", File.ReadAllText(path));
	}

	[TestMethod]
	public void Real_Metadata_Reference_Preserves_Extern_Aliases()
	{
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var aliased = reference.WithProperties(new MetadataReferenceProperties(aliases: ["CoreAlias"]));
		var source = Compile("public class Target { }").WithReferences(new[] { aliased });
		var resolved = ScopedReferenceResolver.Resolve(source, new[] { reference });
		CollectionAssert.AreEqual(new[] { "CoreAlias" }, resolved.References.Single().Properties.Aliases.ToArray());
	}

	[TestMethod]
	public void Real_Metadata_References_Repair_Referenced_Compilation()
	{
		var dependency = Compile("public class Base { }").WithAssemblyName("Dependency").RemoveAllReferences();
		var source = Compile("public class Target : Base { }").WithReferences(new[] { dependency.ToMetadataReference() });
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var resolved = ScopedReferenceResolver.Resolve(source, new[] { reference });
		var type = resolved.GetTypeByMetadataName("Target");
		Assert.IsNotNull(type);
		Assert.AreEqual("Dependency", type.BaseType!.ContainingAssembly.Name);
		Assert.AreEqual(SpecialType.System_Object, type.BaseType.BaseType!.SpecialType);
		ScopedApiValidation.ValidateAndWrite(OutputPath(), new[] { (resolved, (INamedTypeSymbol?)type) }, () => "resolved graph");
	}

	[TestMethod]
	public void Metadata_Cannot_Replace_Implementation_Assembly()
	{
		var source = Compile("public class Target { }").WithAssemblyName(typeof(object).Assembly.GetName().Name!);
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		Assert.ThrowsExactly<ArgumentException>(() => ScopedReferenceResolver.Resolve(source, new[] { reference }));
	}

	[TestMethod]
	public void Duplicate_Explicit_Metadata_Identities_Are_Rejected()
	{
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		Assert.ThrowsExactly<ArgumentException>(() => ScopedReferenceResolver.Resolve(Compile("public class Target { }"), new[] { reference, reference }));
	}

	[TestMethod]
	public void Real_References_Do_Not_Mask_Unresolved_Signatures()
	{
		var source = Compile("public class Target { public MissingType Echo(MissingType value) => value; }");
		var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var resolved = ScopedReferenceResolver.Resolve(source, new[] { reference });
		var path = OutputPath();
		File.WriteAllText(path, "unchanged");
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (resolved, resolved.GetTypeByMetadataName("Target")) }, () => "invalid"));
		Assert.AreEqual("unchanged", File.ReadAllText(path));
	}

	[TestMethod]
	public void Recursive_Interface_Constraints_Terminate_And_Validate()
	{
		var source = Compile("""
			public interface IRecursive<T> where T : IRecursive<T> { T Echo(T value); }
			public class Target : IRecursive<Target> { public Target Echo(Target value) => value; }
			""");
		var path = OutputPath();
		ScopedApiValidation.ValidateAndWrite(path, new[] { (source, source.GetTypeByMetadataName("Target")) }, () => "recursive valid");
		Assert.AreEqual("recursive valid", File.ReadAllText(path));
	}

	[TestMethod]
	public void Referenced_Source_Base_Resolution_Diagnostic_Leaves_Output_Untouched()
	{
		var dependency = Compile("public class Base { public void Run(int value = MissingValue) { } }").WithAssemblyName("Dependency");
		var source = Compile("public class Target : Base { }").AddReferences(dependency.ToMetadataReference());
		var path = OutputPath();
		File.WriteAllText(path, "original");
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (source, source.GetTypeByMetadataName("Target")) }, () => "invalid"));
		Assert.AreEqual("original", File.ReadAllText(path));
	}

	[TestMethod]
	[DataRow("Uno.UI.netcoremobile.csproj", "net9.0-android", "android")]
	[DataRow("Uno.UI.netcoremobile.csproj", "net9.0-ios18.0", "ios")]
	[DataRow("Uno.UI.netcoremobile.csproj", "net9.0-tvos18.0", "tvos")]
	[DataRow("Uno.UI.Skia.csproj", "net9.0", "skia")]
	[DataRow("Uno.UI.Wasm.csproj", "net9.0", "wasm")]
	[DataRow("Uno.UI.Reference.csproj", "net9.0", "reference")]
	public void Platform_References_Select_The_Correct_Graph(string project, string framework, string expected)
		=> Assert.AreEqual(expected, ScopedReferenceResolver.GetPlatformName(project, framework));

	[TestMethod]
	public void Constructor_Initializer_Is_Implementation_Not_Public_Signature()
	{
		var source = Compile("""
			public class Base { public Base(int value) { } }
			public class Target : Base { public Target() : base(UnavailablePlatformImplementation()) { } }
			""");
		Assert.IsTrue(source.GetDiagnostics().Any(diagnostic => diagnostic.Id == "CS0103"));
		ScopedApiValidation.ValidateAndWrite(OutputPath(), new[] { (source, source.GetTypeByMetadataName("Target")) }, () => "valid signature");
	}

	[TestMethod]
	public void Matching_Nonpublic_Inherited_Candidate_Is_Validated_Before_Write()
	{
		var source = Compile("""
			public class Base { private MissingType Echo(MissingType value) => value; }
			public class Target : Base { }
			""");
		var path = OutputPath();
		File.WriteAllText(path, "original");
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (source, source.GetTypeByMetadataName("Target")) }, () => "invalid", new HashSet<string> { "Echo" }));
		Assert.AreEqual("original", File.ReadAllText(path));
	}

	private static void AssertRefused(string source)
	{
		var compilation = Compile(source);
		Assert.IsTrue(compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		var path = OutputPath();
		File.WriteAllText(path, "original output");
		var before = File.ReadAllBytes(path);
		var rendered = false;
		Assert.ThrowsExactly<InvalidOperationException>(() => ScopedApiValidation.ValidateAndWrite(path,
			new[] { (compilation, compilation.GetTypeByMetadataName("Target")) },
			() => { rendered = true; return "bad output"; }));
		Assert.IsFalse(rendered);
		CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
	}

	private static Compilation Compile(string source) => CSharpCompilation.Create("ScopedProbe",
		new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
		new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

	private static string OutputPath()
	{
		var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scoped-api-validation", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return Path.Combine(directory, "Target.cs");
	}
}
