using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 3 && args[0] == "corrupt-checksum")
{
	using var input = File.OpenRead(args[1]);
	using var reader = MetadataReaderProvider.FromPortablePdbStream(input);
	var metadataReader = reader.GetMetadataReader();
	var document = metadataReader.GetDocument(metadataReader.Documents.First());
	var checksum = metadataReader.GetBlobBytes(document.Hash);
	var bytes = File.ReadAllBytes(args[1]);
	var offset = bytes.AsSpan().IndexOf(checksum);
	if (checksum.Length == 0 || offset < 0)
	{
		throw new InvalidDataException("Unable to locate fixture checksum.");
	}
	bytes[offset] ^= 1;
	using var output = new FileStream(args[2], FileMode.CreateNew);
	output.Write(bytes);
	return;
}

if (args.Length != 7 || args[0] is not ("on" or "off"))
{
	throw new ArgumentException("Expected on|off DLL PDB repository-root intermediate-root revision repository-URL.");
}

var enabled = args[0] == "on";
var repositoryRoot = Path.GetFullPath(args[3]);
var intermediateRoot = Path.GetFullPath(args[4]);
var revision = args[5];
var repositoryUrl = args[6].TrimEnd('/');
if (repositoryUrl.EndsWith(".git", StringComparison.Ordinal))
{
	repositoryUrl = repositoryUrl[..^4];
}
if (revision.Length != 40 || !revision.All(Uri.IsHexDigit) ||
	!repositoryUrl.StartsWith("https://github.com/", StringComparison.Ordinal))
{
	throw new ArgumentException("Expected an exact Git revision and GitHub HTTPS repository.");
}

using var assembly = File.OpenRead(args[1]);
using var pe = new PEReader(assembly);
using var pdb = File.OpenRead(args[2]);
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
var metadata = provider.GetMetadataReader();
var id = metadata.DebugMetadataHeader!.Id.ToArray();
var entry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
if (id.Length != 20 || codeView.Guid != new Guid(id.AsSpan(0, 16)) ||
	entry.Stamp != BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(16)) || codeView.Age != 1)
{
	throw new InvalidDataException("DLL/portable-PDB identity mismatch.");
}

var embedded = new Dictionary<int, byte[]>();
var mappings = new List<SourceLinkMap>();
foreach (var handle in metadata.CustomDebugInformation)
{
	var information = metadata.GetCustomDebugInformation(handle);
	var kind = metadata.GetGuid(information.Kind);
	var bytes = metadata.GetBlobBytes(information.Value);
	if (kind == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A"))
	{
		mappings.Add(JsonSerializer.Deserialize(bytes, JsonContext.Default.SourceLinkMap)
			?? throw new InvalidDataException("Invalid SourceLink JSON."));
	}
	else if (kind == new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE"))
	{
		embedded.Add(MetadataTokens.GetRowNumber(information.Parent), bytes);
	}
}

if (enabled ? mappings.Count != 1 : mappings.Count != 0 || embedded.Count != 0)
{
	throw new InvalidDataException(enabled ? "Missing unique SourceLink record." : "SourceLink-off leaked mapping or embedded source.");
}
var expectedUrl = $"https://raw.githubusercontent.com/{repositoryUrl["https://github.com/".Length..]}/{revision}/*";
if (enabled && (mappings[0].Documents.Count == 0 || mappings[0].Documents.Any(p => p.Value != expectedUrl)))
{
	throw new InvalidDataException("SourceLink repository/revision mismatch.");
}

var documents = new List<DocumentResult>();
foreach (var handle in metadata.Documents)
{
	var document = metadata.GetDocument(handle);
	var name = metadata.GetString(document.Name);
	var matching = enabled
		? mappings[0].Documents.Keys.Where(key => key.EndsWith('*') && name.StartsWith(key[..^1], StringComparison.Ordinal)).ToArray()
		: [];
	string local;
	if (matching.Length == 1)
	{
		local = Path.GetFullPath(Path.Combine(repositoryRoot, name[(matching[0].Length - 1)..]));
	}
	else if (name.StartsWith("/_/", StringComparison.Ordinal))
	{
		local = Path.GetFullPath(Path.Combine(repositoryRoot, name[3..]));
	}
	else
	{
		local = Path.GetFullPath(name);
	}
	if (!Within(local, repositoryRoot) && !Within(local, intermediateRoot))
	{
		throw new InvalidDataException($"PDB document escapes the selected source/intermediate roots: {name}");
	}
	var sourceBytes = File.ReadAllBytes(local);
	var row = MetadataTokens.GetRowNumber(handle);
	if (enabled && !embedded.ContainsKey(row))
	{
		throw new InvalidDataException($"Source document is not embedded: {name}");
	}
	var documentBytes = embedded.TryGetValue(row, out var content) ? DecodeSource(content, sourceBytes.Length) : sourceBytes;
	var algorithm = metadata.GetGuid(document.HashAlgorithm);
	var actualHash = algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16")
		? SHA256.HashData(documentBytes)
		: algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460")
			? SHA1.HashData(documentBytes)
			: throw new InvalidDataException($"Unsupported PDB document checksum: {name}");
	if (!actualHash.AsSpan().SequenceEqual(metadata.GetBlobBytes(document.Hash)) ||
		!documentBytes.AsSpan().SequenceEqual(sourceBytes))
	{
		throw new InvalidDataException($"PDB source checksum/content mismatch: {name}");
	}
	var generated = Within(local, intermediateRoot);
	string? blobHash = null;
	string? binding = null;
	if (!generated)
	{
		var relative = Path.GetRelativePath(repositoryRoot, local).Replace('\\', '/');
		var blob = await GitBlob(repositoryRoot, revision + ":" + relative);
		blobHash = Convert.ToHexString(SHA256.HashData(blob));
		if (blob.AsSpan().SequenceEqual(sourceBytes))
		{
			binding = "exact";
		}
		else if (NormalizeLineEndings(blob).AsSpan().SequenceEqual(NormalizeLineEndings(sourceBytes)))
		{
			binding = "git-checkout-line-endings";
		}
		else
		{
			throw new InvalidDataException($"Source does not match the selected Git blob: {relative}");
		}
	}
	documents.Add(new(name, Convert.ToHexString(actualHash), embedded.ContainsKey(row), generated, blobHash, binding));
}
if (documents.Count == 0 || documents.All(d => d.Generated))
{
	throw new InvalidDataException("No tracked source documents were verified.");
}
var report = new VerificationResult(enabled, revision, codeView.Guid, documents);
Console.WriteLine(JsonSerializer.Serialize(report, JsonContext.Default.VerificationResult));

static bool Within(string file, string directory)
	=> file.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

static byte[] DecodeSource(byte[] blob, int expectedLength)
{
	if (blob.Length < 4)
	{
		throw new InvalidDataException("Truncated embedded source.");
	}
	var size = BinaryPrimitives.ReadInt32LittleEndian(blob);
	if (size == 0)
	{
		if (blob.Length - 4 != expectedLength)
		{
			throw new InvalidDataException("Embedded source length differs from the source file.");
		}
		return blob[4..];
	}
	if (size != expectedLength)
	{
		throw new InvalidDataException("Embedded source length differs from the source file.");
	}
	using var input = new MemoryStream(blob, 4, blob.Length - 4);
	using var decoder = new DeflateStream(input, CompressionMode.Decompress);
	var output = new byte[size];
	decoder.ReadExactly(output);
	if (decoder.ReadByte() != -1)
	{
		throw new InvalidDataException("Embedded source exceeds its declared size.");
	}
	return output;
}

static byte[] NormalizeLineEndings(byte[] bytes)
{
	using var output = new MemoryStream();
	for (var index = 0; index < bytes.Length; index++)
	{
		if (bytes[index] != 13 || index + 1 == bytes.Length || bytes[index + 1] != 10)
		{
			output.WriteByte(bytes[index]);
		}
	}
	return output.ToArray();
}

static async Task<byte[]> GitBlob(string root, string specification)
{
	var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
	foreach (var argument in new[] { "-C", root, "cat-file", "blob", specification })
	{
		start.ArgumentList.Add(argument);
	}
	using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git.");
	using var bytes = new MemoryStream();
	var error = process.StandardError.ReadToEndAsync();
	await process.StandardOutput.BaseStream.CopyToAsync(bytes);
	await process.WaitForExitAsync();
	var errorText = await error;
	if (process.ExitCode != 0)
	{
		throw new InvalidDataException($"Git source lookup failed: {errorText}");
	}
	return bytes.ToArray();
}

internal sealed record SourceLinkMap([property: JsonPropertyName("documents")] Dictionary<string, string> Documents);
internal sealed record DocumentResult(string Name, string DocumentChecksum, bool Embedded, bool Generated, string? GitBlobSha256, string? GitBinding);
internal sealed record VerificationResult(bool SourceLinkEnabled, string Revision, Guid PdbId, List<DocumentResult> Documents);

[JsonSerializable(typeof(SourceLinkMap))]
[JsonSerializable(typeof(VerificationResult))]
internal partial class JsonContext : JsonSerializerContext;
