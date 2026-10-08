using System.Text.Json;

using InvoiceEvals.Core;

namespace InvoiceEvals.Extraction;

/// <summary>One extraction configuration from evals/configs.json.</summary>
/// <param name="Prompt">Prompt variant: file name in prompts/ without extension.</param>
/// <param name="InputMode">"text" or "ocr", see <see cref="EvalSet"/>.</param>
/// <param name="Temperature">Null leaves the provider default (required for models that reject sampling parameters).</param>
/// <param name="DisableThinking">Sends Anthropic <c>thinking: between_tools</c>, the lowest thinking setting on Claude Sonnet 5.5.</param>
public sealed record RunConfig(string Name, string Model, string Prompt, string InputMode, double? Temperature, bool DisableThinking = false)
{
    public static async Task<IReadOnlyList<RunConfig>> LoadAllAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<RunConfig>>(stream, GoldenSet.JsonOptions, ct)
            ?? throw new InvalidDataException($"{path}: no configs.");
    }

    public static async Task<RunConfig> LoadAsync(string path, string name, CancellationToken ct) =>
        (await LoadAllAsync(path, ct)).SingleOrDefault(c => c.Name == name)
            ?? throw new ArgumentException($"No config named '{name}' in {path}.", nameof(name));
}
