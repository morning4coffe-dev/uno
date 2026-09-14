using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;

internal static class SourceLinkFixtures
{
	internal static void Remap(string kind, string inputPath, string outputPath)
	{
		var original = File.ReadAllBytes(inputPath);
		using var input = new MemoryStream(original);
		using var provider = MetadataReaderProvider.FromPortablePdbStream(input);
		var metadata = provider.GetMetadataReader();
		var handle = metadata.CustomDebugInformation.Single(h =>
			metadata.GetGuid(metadata.GetCustomDebugInformation(h).Kind) == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A"));
		var information = metadata.GetCustomDebugInformation(handle);
		var map = JsonSerializer.Deserialize(metadata.GetBlobBytes(information.Value), JsonContext.Default.SourceLinkMap)!;
		var url = map.Documents["/_/*"];
		switch (kind)
		{
			case "wrong-prefix":
				map.Documents.Remove("/_/*");
				map.Documents.Add("/x/*", url);
				break;
			case "wrong-specific-path":
				map.Documents.Add("/_/src/*", url);
				break;
			case "ambiguous":
				map.Documents.Add("/_/src/*", url.Replace("*", "src/*"));
				map.Documents.Add("/_/SRC/*", url.Replace("*", "src/*"));
				break;
			case "specific":
				map.Documents.Add("/_/SRC/*", url.Replace("*", "src/*"));
				break;
			case "exact":
				var document = metadata.Documents.Select(h => metadata.GetString(metadata.GetDocument(h).Name))
					.First(name => name.StartsWith("/_/src/", StringComparison.Ordinal));
				map.Documents.Add(document, url.Replace("*", document[3..]));
				break;
			case "invalid-wildcard":
				map.Documents.Add("/_/src/*/invalid", url);
				break;
			case "duplicate-key":
				break;
			default:
				throw new ArgumentException($"Unknown mapping fixture: {kind}");
		}
		var json = JsonSerializer.SerializeToUtf8Bytes(map, JsonContext.Default.SourceLinkMap);
		if (kind == "duplicate-key")
		{
			var text = Encoding.UTF8.GetString(json);
			var start = text.IndexOf('{', 1) + 1;
			var end = text.IndexOf('}', start);
			json = Encoding.UTF8.GetBytes(text.Insert(start, text[start..end] + ","));
		}
		var blob = new BlobBuilder();
		blob.WriteCompressedInteger(json.Length);
		blob.WriteBytes(json);
		var appended = blob.ToArray();
		var blobOffset = metadata.GetHeapMetadataOffset(HeapIndex.Blob);
		var blobSize = metadata.GetHeapSize(HeapIndex.Blob);
		var indexSize = blobSize > ushort.MaxValue ? 4 : 2;
		if (indexSize == 2 && blobSize + appended.Length > ushort.MaxValue)
		{
			throw new InvalidDataException("Fixture would change the blob-index width.");
		}
		var streamHeader = FindBlobStreamHeader(original, blobOffset, blobSize);
		var rowSize = metadata.GetTableRowSize(TableIndex.CustomDebugInformation);
		var valueOffset = metadata.GetTableMetadataOffset(TableIndex.CustomDebugInformation) +
			(MetadataTokens.GetRowNumber(handle) - 1) * rowSize + rowSize - indexSize;
		var oldIndex = indexSize == 2
			? BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(valueOffset))
			: BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(valueOffset));
		if (oldIndex != MetadataTokens.GetHeapOffset(information.Value))
		{
			throw new InvalidDataException("Unexpected fixture metadata layout.");
		}
		// Append only a new SourceLink blob and redirect its existing row. Other heaps,
		// document checksums, embedded sources and the original PDB ID stay unchanged.
		var result = new byte[blobOffset + blobSize + appended.Length];
		original.CopyTo(result, 0);
		appended.CopyTo(result, blobOffset + blobSize);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(streamHeader + 4), blobSize + appended.Length);
		if (indexSize == 2)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(valueOffset), checked((ushort)blobSize));
		}
		else
		{
			BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(valueOffset), blobSize);
		}
		using var verificationInput = new MemoryStream(result);
		using var verification = MetadataReaderProvider.FromPortablePdbStream(verificationInput);
		var changed = verification.GetMetadataReader();
		if (!changed.DebugMetadataHeader!.Id.AsSpan().SequenceEqual(metadata.DebugMetadataHeader!.Id.AsSpan()))
		{
			throw new InvalidDataException("Fixture changed the PDB identity.");
		}
		foreach (var documentHandle in metadata.Documents)
		{
			if (!metadata.GetBlobBytes(metadata.GetDocument(documentHandle).Hash).AsSpan()
				.SequenceEqual(changed.GetBlobBytes(changed.GetDocument(documentHandle).Hash)))
			{
				throw new InvalidDataException("Fixture changed a document checksum.");
			}
		}
		foreach (var debugHandle in metadata.CustomDebugInformation.Where(h => h != handle))
		{
			if (!metadata.GetBlobBytes(metadata.GetCustomDebugInformation(debugHandle).Value).AsSpan()
				.SequenceEqual(changed.GetBlobBytes(changed.GetCustomDebugInformation(debugHandle).Value)))
			{
				throw new InvalidDataException("Fixture changed non-SourceLink debug information.");
			}
		}
		using var output = new FileStream(outputPath, FileMode.CreateNew);
		output.Write(result);
	}

	private static int FindBlobStreamHeader(byte[] bytes, int blobOffset, int blobSize)
	{
		var cursor = 16 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
		var streams = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 2));
		cursor += 4;
		var result = -1;
		for (var index = 0; index < streams; index++)
		{
			var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
			var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor + 4));
			var start = cursor + 8;
			var end = Array.IndexOf(bytes, (byte)0, start);
			var name = Encoding.UTF8.GetString(bytes, start, end - start);
			if (name == "#Blob")
			{
				if (offset != blobOffset || size != blobSize)
				{
					throw new InvalidDataException("Unexpected fixture blob stream.");
				}
				result = cursor;
			}
			else if (offset + size > blobOffset + blobSize)
			{
				throw new InvalidDataException("Fixture blob stream must be last.");
			}
			cursor = (end + 4) & ~3;
		}
		return result >= 0 ? result : throw new InvalidDataException("Missing fixture blob stream.");
	}
}
