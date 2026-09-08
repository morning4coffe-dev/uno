#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Uno.WinAppSDKSyncGenerator;

internal static class ScopedApiValidation
{
	internal static void ValidateAndWrite(
		string outputPath,
		IEnumerable<(Compilation Compilation, INamedTypeSymbol? Type)> platforms,
		Func<string> generate,
		IReadOnlySet<string>? inheritedMetadataMemberNames = null)
	{
		foreach (var (compilation, type) in platforms)
		{
			if (type is null)
			{
				throw new InvalidOperationException("Cannot resolve the scoped API type. Restore its platform project before generating.");
			}
			new Validator(compilation, inheritedMetadataMemberNames).Validate(type);
		}

		// Rendering can also fail. Do not open the existing output until both phases succeed.
		var content = generate();
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
		File.WriteAllText(outputPath, content);
	}

	private sealed class Validator
	{
		private readonly Compilation _compilation;
		private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);
		private readonly HashSet<ISymbol> _declarations = new(SymbolEqualityComparer.Default);
		private readonly HashSet<INamedTypeSymbol> _inheritance = new(SymbolEqualityComparer.Default);
		private readonly IReadOnlySet<string>? _inheritedMetadataMemberNames;
		private readonly Dictionary<SyntaxTree, List<TextSpan>> _signatures = new();

		internal Validator(Compilation compilation, IReadOnlySet<string>? inheritedMetadataMemberNames)
		{
			_compilation = compilation;
			_inheritedMetadataMemberNames = inheritedMetadataMemberNames;
		}

		internal void Validate(INamedTypeSymbol type)
		{
			ValidateDeclaration(type);
			foreach (var (tree, spans) in _signatures)
			{
				var model = FindCompilation(tree).GetSemanticModel(tree);
				foreach (var span in spans)
				{
					foreach (var diagnostic in model.GetDiagnostics(span))
					{
						if (diagnostic.Severity == DiagnosticSeverity.Error &&
							IsResolutionDiagnostic(diagnostic.Id) &&
							diagnostic.Location.SourceTree == tree && span.IntersectsWith(diagnostic.Location.SourceSpan))
						{
							throw new InvalidOperationException($"Cannot resolve scoped API '{type}': {diagnostic}");
						}
					}
				}
			}
		}

		private static bool IsResolutionDiagnostic(string id) => id is
			"CS0012" or "CS0103" or "CS0104" or "CS0234" or "CS0246" or "CS0400" or
			"CS0518" or "CS0616" or "CS0656" or "CS1069" or "CS1070" or "CS1705" or "CS7069";

		private void ValidateDeclaration(ISymbol symbol)
		{
			if (!_declarations.Add(symbol))
			{
				return;
			}
			RecordSignature(symbol);
			ValidateAttributes(symbol.GetAttributes());
			switch (symbol)
			{
				case INamedTypeSymbol type:
					ValidateInheritance(type.BaseType);
					foreach (var @interface in type.Interfaces)
					{
						ValidateInheritance(@interface);
					}
					foreach (var parameter in type.TypeParameters)
					{
						ValidateType(parameter);
					}
					if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is { } invoke)
					{
						ValidateDeclaration(invoke);
					}
					foreach (var member in type.GetMembers())
					{
						if (!member.IsImplicitlyDeclared && (member.DeclaredAccessibility is
							Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal ||
							IsExplicitImplementation(member) || _inheritedMetadataMemberNames?.Contains(member.Name) == true))
						{
							ValidateDeclaration(member);
						}
					}
					break;
				case IMethodSymbol method:
					ValidateType(method.ReturnType);
					ValidateAttributes(method.GetReturnTypeAttributes());
					foreach (var parameter in method.Parameters)
					{
						ValidateDeclaration(parameter);
					}
					foreach (var parameter in method.TypeParameters)
					{
						ValidateType(parameter);
					}
					foreach (var implementation in method.ExplicitInterfaceImplementations)
					{
						ValidateType(implementation.ContainingType);
					}
					break;
				case IPropertySymbol property:
					ValidateType(property.Type);
					if (property.GetMethod is { } getter)
					{
						ValidateDeclaration(getter);
					}
					if (property.SetMethod is { } setter)
					{
						ValidateDeclaration(setter);
					}
					foreach (var parameter in property.Parameters)
					{
						ValidateDeclaration(parameter);
					}
					break;
				case IEventSymbol @event:
					ValidateType(@event.Type);
					break;
				case IFieldSymbol field:
					ValidateType(field.Type);
					break;
				case IParameterSymbol parameter:
					ValidateType(parameter.Type);
					break;
			}
		}

		private void ValidateInheritance(INamedTypeSymbol? type)
		{
			if (type is null || !_inheritance.Add(type))
			{
				return;
			}
			ValidateType(type);
			if (type.DeclaringSyntaxReferences.Length > 0)
			{
				ValidateDeclaration(type);
				return;
			}
			ValidateInheritance(type.BaseType);
			foreach (var @interface in type.Interfaces)
			{
				ValidateInheritance(@interface);
			}
			foreach (var member in type.GetMembers())
			{
				if (_inheritedMetadataMemberNames is null
					? member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
					: _inheritedMetadataMemberNames.Contains(member.Name))
				{
					ValidateDeclaration(member);
				}
			}
		}

		private Compilation FindCompilation(SyntaxTree tree)
		{
			var pending = new Stack<Compilation>();
			var visited = new HashSet<Compilation>();
			pending.Push(_compilation);
			while (pending.Count > 0)
			{
				var current = pending.Pop();
				if (!visited.Add(current))
				{
					continue;
				}
				if (current.ContainsSyntaxTree(tree))
				{
					return current;
				}
				foreach (var reference in current.References.OfType<CompilationReference>())
				{
					pending.Push(reference.Compilation);
				}
			}
			throw new InvalidOperationException($"Cannot validate inherited source declaration '{tree.FilePath}': its compilation is unavailable.");
		}

		private void ValidateType(ITypeSymbol? type)
		{
			if (type is null || !_types.Add(type))
			{
				return;
			}
			if (type.TypeKind == TypeKind.Error || type.HasUnsupportedMetadata)
			{
				throw new InvalidOperationException($"Cannot resolve scoped API signature type '{type}'. Restore its platform project before generating.");
			}
			switch (type)
			{
				case IArrayTypeSymbol array:
					ValidateType(array.ElementType);
					break;
				case IPointerTypeSymbol pointer:
					ValidateType(pointer.PointedAtType);
					break;
				case IFunctionPointerTypeSymbol pointer:
					ValidateDeclaration(pointer.Signature);
					break;
				case INamedTypeSymbol named:
					ValidateType(named.ContainingType);
					foreach (var argument in named.TypeArguments)
					{
						ValidateType(argument);
					}
					if (named.TypeKind == TypeKind.Delegate && named.DelegateInvokeMethod is { } invoke)
					{
						ValidateDeclaration(invoke);
					}
					break;
				case ITypeParameterSymbol parameter:
					foreach (var constraint in parameter.ConstraintTypes)
					{
						ValidateType(constraint);
					}
					break;
			}
		}

		private void ValidateAttributes(IEnumerable<AttributeData> attributes)
		{
			foreach (var attribute in attributes)
			{
				if (attribute.AttributeClass is null)
				{
					throw new InvalidOperationException("Cannot resolve a scoped API attribute.");
				}
				ValidateType(attribute.AttributeClass);
				foreach (var argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(pair => pair.Value)))
				{
					ValidateConstant(argument);
				}
			}
		}

		private void ValidateConstant(TypedConstant constant)
		{
			if (constant.Kind == TypedConstantKind.Error)
			{
				throw new InvalidOperationException("Cannot resolve a scoped API attribute argument.");
			}
			ValidateType(constant.Type);
			if (constant.Kind == TypedConstantKind.Array && !constant.IsNull)
			{
				foreach (var value in constant.Values)
				{
					ValidateConstant(value);
				}
			}
			else if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol type)
			{
				ValidateType(type);
			}
		}

		private void RecordSignature(ISymbol symbol)
		{
			foreach (var reference in symbol.DeclaringSyntaxReferences)
			{
				var node = reference.GetSyntax();
				if (node is VariableDeclaratorSyntax)
				{
					node = node.Parent?.Parent ?? node;
				}
				var end = node switch
				{
					TypeDeclarationSyntax declaration => declaration.OpenBraceToken.RawKind == 0 || declaration.OpenBraceToken.IsMissing
						? declaration.Span.End : declaration.OpenBraceToken.SpanStart,
					ConstructorDeclarationSyntax { Initializer: { } initializer } => initializer.SpanStart,
					BaseMethodDeclarationSyntax method => method.Body?.SpanStart ?? method.ExpressionBody?.SpanStart ?? method.Span.End,
					AccessorDeclarationSyntax accessor => accessor.Body?.SpanStart ?? accessor.ExpressionBody?.SpanStart ?? accessor.Span.End,
					BasePropertyDeclarationSyntax property => property.AccessorList?.SpanStart ?? property switch
					{
						PropertyDeclarationSyntax declaration => declaration.ExpressionBody?.SpanStart ?? declaration.Span.End,
						IndexerDeclarationSyntax indexer => indexer.ExpressionBody?.SpanStart ?? indexer.Span.End,
						_ => property.Span.End
					},
					_ => node.Span.End
				};
				var span = TextSpan.FromBounds(node.SpanStart, Math.Max(node.SpanStart, end));
				if (!_signatures.TryGetValue(node.SyntaxTree, out var spans))
				{
					_signatures.Add(node.SyntaxTree, spans = new List<TextSpan>());
				}
				spans.Add(span);
			}
		}

		private static bool IsExplicitImplementation(ISymbol symbol) =>
			symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is
				MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null } or
				PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null } or
				IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null } or
				EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null });
	}
}
