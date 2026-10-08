using System.CommandLine;
using System.IO.Compression;
using InvoiceEvals.Core;
using InvoiceEvals.Core.Fatura;

namespace InvoiceEvals.Cli;

/// <summary>`evals download`: fetches FATURA, samples a stratified golden set, writes images and annotations.jsonl.</summary>
internal static class DownloadCommand
{
    private const string AnnotationPrefix = "invoices_dataset_final/Annotations/Original_Format/";
    private const string ImagePrefix = "invoices_dataset_final/images/";

    public static Command Create()
    {
        var count = new Option<int>("--count") { Description = "Documents to sample, split evenly across the 50 layouts.", DefaultValueFactory = _ => 150 };
        var seed = new Option<int>("--seed") { Description = "Sampling seed.", DefaultValueFactory = _ => 42 };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { Description = "Output directory.", DefaultValueFactory = _ => new("evals") };
        var cacheDir = new Option<DirectoryInfo>("--cache-dir") { Description = "Where the archive is cached.", DefaultValueFactory = _ => new(".cache/fatura") };

        var command = new Command("download", "Download FATURA and build the golden set.") { count, seed, evalsDir, cacheDir };
        command.SetAction((result, ct) => RunAsync(
            result.GetValue(count), result.GetValue(seed), result.GetValue(evalsDir)!, result.GetValue(cacheDir)!, ct));
        return command;
    }

    private static async Task<int> RunAsync(int count, int seed, DirectoryInfo evalsDir, DirectoryInfo cacheDir, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var archivePath = await new FaturaArchive(http, Console.Out).EnsureAsync(cacheDir.FullName, ct);
        using var zip = await ZipFile.OpenReadAsync(archivePath, ct);

        var converted = new List<(string Name, string Layout, InvoiceDto Invoice)>();
        var rejected = new List<string>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(AnnotationPrefix, StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            await using var stream = await entry.OpenAsync(ct);
            using var reader = new StreamReader(stream);
            try
            {
                converted.Add((name, name.Split('_')[0], FaturaConverter.Convert(await reader.ReadToEndAsync(ct))));
            }
            catch (FormatException ex)
            {
                rejected.Add($"{name}: {ex.Message}");
            }
        }
        Console.WriteLine($"Converted {converted.Count} annotations, rejected {rejected.Count}:");
        rejected.ForEach(r => Console.WriteLine($"  {r}"));

        var sample = StratifiedSampler.Sample(converted, c => c.Name, c => c.Layout, count, seed);

        var imageDir = Path.Combine(evalsDir.FullName, "golden", "fatura");
        if (Directory.Exists(imageDir)) Directory.Delete(imageDir, recursive: true);
        Directory.CreateDirectory(imageDir);

        var golden = new List<GoldenDocument>();
        foreach (var (name, layout, invoice) in sample)
        {
            var entry = zip.GetEntry($"{ImagePrefix}{name}.jpg") ?? throw new InvalidDataException($"Missing image for {name}.");
            await entry.ExtractToFileAsync(Path.Combine(imageDir, $"{name}.jpg"), overwrite: true, ct);
            golden.Add(new GoldenDocument($"fatura/{name}", "fatura", layout, $"golden/fatura/{name}.jpg", invoice));
        }

        var annotationsPath = Path.Combine(evalsDir.FullName, "annotations.jsonl");
        await GoldenSet.WriteAsync(annotationsPath, golden, ct);
        Console.WriteLine($"Wrote {golden.Count} documents across {golden.Select(g => g.Layout).Distinct().Count()} layouts to {annotationsPath}");
        return 0;
    }
}
