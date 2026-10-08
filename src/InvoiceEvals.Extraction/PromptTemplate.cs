namespace InvoiceEvals.Extraction;

/// <summary>A prompt variant from prompts/NAME.md: a front-matter header (version, description) and the system prompt body.</summary>
public sealed record PromptTemplate(string Name, string Version, string Description, string Body)
{
    public static async Task<PromptTemplate> LoadAsync(string promptsDir, string name, CancellationToken ct)
    {
        var path = Path.Combine(promptsDir, $"{name}.md");
        var text = (await File.ReadAllTextAsync(path, ct)).ReplaceLineEndings("\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException($"{path}: missing front-matter header.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{path}: unterminated front-matter header.");

        var header = text[4..end].Split('\n')
            .Select(l => l.Split(':', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.Ordinal);
        return new PromptTemplate(
            name,
            header.GetValueOrDefault("version") ?? throw new InvalidDataException($"{path}: header has no version."),
            header.GetValueOrDefault("description") ?? "",
            text[(end + 5)..].Trim() + "\n");
    }
}
