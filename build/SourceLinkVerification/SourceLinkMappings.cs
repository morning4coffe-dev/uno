internal sealed class SourceLinkMappings
{
	private readonly Dictionary<string, string> _documents;
	private readonly string _repositoryUrl;

	internal SourceLinkMappings(SourceLinkMap map, string repositoryUrl)
	{
		_documents = map.Documents;
		_repositoryUrl = repositoryUrl;
		if (_documents is null || _documents.Count == 0)
		{
			throw new InvalidDataException("Unusable SourceLink mapping: no documents.");
		}
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, url) in _documents)
		{
			if (!keys.Add(key))
			{
				throw new InvalidDataException($"Unusable SourceLink mapping: ambiguous case-insensitive key '{key}'.");
			}
			var wildcard = key.IndexOf('*');
			if (key.Length == 0 || url is null ||
				(wildcard >= 0 && wildcard != key.Length - 1) ||
				url.Count(c => c == '*') != (wildcard >= 0 ? 1 : 0))
			{
				throw new InvalidDataException($"Unusable SourceLink mapping: invalid wildcard pattern '{key}'.");
			}
			if (!url.StartsWith(repositoryUrl, StringComparison.Ordinal))
			{
				throw new InvalidDataException("SourceLink repository/revision mismatch.");
			}
		}
	}

	internal string Resolve(string document)
	{
		string? selectedKey = null;
		string? selectedUrl = null;
		var specificity = -1;
		foreach (var (key, url) in _documents)
		{
			var wildcard = key.EndsWith('*');
			var prefix = wildcard ? key[..^1] : key;
			if (wildcard ? !document.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) :
				!document.Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// Exact entries win ties; overlapping prefixes use the most specific entry.
			var candidateSpecificity = prefix.Length * 2 + (wildcard ? 0 : 1);
			if (candidateSpecificity > specificity)
			{
				selectedKey = key;
				selectedUrl = wildcard ? url.Replace("*", document[prefix.Length..].Replace('\\', '/')) : url;
				specificity = candidateSpecificity;
			}
		}
		if (selectedKey is null || selectedUrl is null)
		{
			throw new InvalidDataException($"Unusable SourceLink mapping: no matching key for {document}.");
		}
		if (!Uri.TryCreate(selectedUrl, UriKind.Absolute, out var uri) ||
			uri.Query.Length != 0 || uri.Fragment.Length != 0)
		{
			throw new InvalidDataException($"Unusable SourceLink mapping: invalid resolved URL for {document}.");
		}
		var relative = Uri.UnescapeDataString(selectedUrl[_repositoryUrl.Length..]);
		if (relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl) ||
			relative.Split('/').Any(segment => segment is "" or "." or ".."))
		{
			throw new InvalidDataException($"Unusable SourceLink mapping: unsafe resolved path for {document}.");
		}
		return relative;
	}
}
