using InvoiceEvals.Extraction;

namespace InvoiceEvals.Tests;

public sealed class PromptTests
{
    private static readonly string PromptsDir = Path.Combine(RepoRoot(), "prompts");

    public static TheoryData<string> Variants => ["plain", "fewshot"];

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Variant_HasVersion_AndRequiredRules_AndNoDatePlausibilityHints(string name)
    {
        var prompt = await PromptTemplate.LoadAsync(PromptsDir, name, TestContext.Current.CancellationToken);

        Assert.Matches(@"^\d+\.\d+$", prompt.Version);
        Assert.NotEmpty(prompt.Description);
        Assert.Contains("Extract values exactly as printed. Do not recompute, reconcile or round totals, subtotals, tax or discount.", prompt.Body, StringComparison.Ordinal);
        Assert.Contains("Tax is the sum of all printed tax lines.", prompt.Body, StringComparison.Ordinal);
        Assert.Contains("If a field is not printed, return null.", prompt.Body, StringComparison.Ordinal);
        Assert.Contains("Customer is the bill-to party; ignore ship-to.", prompt.Body, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)due date (must|should|is usually|comes) (be )?(after|later)", prompt.Body);
    }

    [Fact]
    public async Task FewShot_IsPlainPlusExamples()
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = await PromptTemplate.LoadAsync(PromptsDir, "plain", ct);
        var fewshot = await PromptTemplate.LoadAsync(PromptsDir, "fewshot", ct);

        Assert.StartsWith(plain.Body.TrimEnd(), fewshot.Body, StringComparison.Ordinal);
        Assert.Equal(2, fewshot.Body.Split("Invoice text:").Length - 1);
    }

    [Fact]
    public async Task Configs_ReferenceExistingPrompts()
    {
        var configs = await RunConfig.LoadAllAsync(Path.Combine(RepoRoot(), "evals", "configs.json"), TestContext.Current.CancellationToken);

        Assert.Equal(["mini-plain", "mini-fewshot", "mini-plain-ocr", "strong-plain"], configs.Select(c => c.Name));
        Assert.All(configs, c => Assert.True(File.Exists(Path.Combine(PromptsDir, $"{c.Prompt}.md"))));
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "InvoiceEvals.slnx"))) dir = dir.Parent;
        return dir.FullName;
    }
}
