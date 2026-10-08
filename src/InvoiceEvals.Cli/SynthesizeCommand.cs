using System.CommandLine;
using System.Text;

using InvoiceEvals.Core;
using InvoiceEvals.Synthetic;

namespace InvoiceEvals.Cli;

/// <summary>`evals synthesize`: renders the synthetic edge-case set to evals/synthetic/.</summary>
internal static class SynthesizeCommand
{
    public static Command Create()
    {
        var seed = new Option<int>("--seed") { Description = "Generation seed.", DefaultValueFactory = _ => 42 };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { Description = "Output directory.", DefaultValueFactory = _ => new("evals") };

        var command = new Command("synthesize", "Generate the synthetic edge-case invoices.") { seed, evalsDir };
        command.SetAction((result, ct) => RunAsync(result.GetValue(seed), result.GetValue(evalsDir)!, ct));
        return command;
    }

    private static async Task<int> RunAsync(int seed, DirectoryInfo evalsDir, CancellationToken ct)
    {
        var outDir = Path.Combine(evalsDir.FullName, "synthetic");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        var docs = new List<GoldenDocument>();
        foreach (var inv in SyntheticGenerator.Generate(seed))
        {
            var name = inv.Id["synthetic/".Length..];
            var pages = InvoiceRenderer.RenderPng(inv);
            for (var i = 0; i < pages.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(outDir, $"{name}-p{i + 1}.png"), pages[i], ct);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{name}.txt"), InvoiceRenderer.Text(inv), new UTF8Encoding(false), ct);
            docs.Add(new GoldenDocument(inv.Id, "synthetic", inv.Family, $"synthetic/{name}-p1.png", $"synthetic/{name}.txt", null, inv.ToDto()));
            Console.WriteLine($"  {inv.Id}: {pages.Count} page(s), {inv.Lines.Count} lines");
        }

        var annotationsPath = Path.Combine(outDir, "annotations.jsonl");
        await GoldenSet.WriteAsync(annotationsPath, docs, ct);
        Console.WriteLine($"Wrote {docs.Count} synthetic documents to {annotationsPath}");
        return 0;
    }
}
