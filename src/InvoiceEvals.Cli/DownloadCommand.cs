using System.CommandLine;
using System.IO.Compression;
using System.Text;

using InvoiceEvals.Core;
using InvoiceEvals.Core.Fatura;

namespace InvoiceEvals.Cli;

/// <summary>
/// `evals download`: fetches FATURA, holds out a few layouts as the dev pool, samples a stratified golden set from the rest,
/// and writes images, text layers and annotations.jsonl for both.
/// </summary>
internal static class DownloadCommand
{
    private const string AnnotationPrefix = "invoices_dataset_final/Annotations/Original_Format/";
    private const string ImagePrefix = "invoices_dataset_final/images/";

    public static Command Create()
    {
        var count = new Option<int>("--count") { Description = "Golden documents to sample, split evenly across the non-dev layouts.", DefaultValueFactory = _ => 150 };
        var devLayouts = new Option<int>("--dev-layouts") { Description = "Layouts held out of the golden set for prompt development.", DefaultValueFactory = _ => 5 };
        var devCount = new Option<int>("--dev-count") { Description = "Documents to sample from the held-out layouts.", DefaultValueFactory = _ => 20 };
        var seed = new Option<int>("--seed") { Description = "Sampling seed.", DefaultValueFactory = _ => 42 };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { Description = "Output directory.", DefaultValueFactory = _ => new("evals") };
        var cacheDir = new Option<DirectoryInfo>("--cache-dir") { Description = "Where the archive is cached.", DefaultValueFactory = _ => new(".cache/fatura") };

        var command = new Command("download", "Download FATURA and build the golden set and dev pool.") { count, devLayouts, devCount, seed, evalsDir, cacheDir };
        command.SetAction((result, ct) => RunAsync(
            result.GetValue(count), result.GetValue(devLayouts), result.GetValue(devCount), result.GetValue(seed),
            result.GetValue(evalsDir)!, result.GetValue(cacheDir)!, ct));
        return command;
    }

    private static async Task<int> RunAsync(int count, int devLayouts, int devCount, int seed, DirectoryInfo evalsDir, DirectoryInfo cacheDir, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var archivePath = await new FaturaArchive(http, Console.Out).EnsureAsync(cacheDir.FullName, ct);
        using var zip = await ZipFile.OpenReadAsync(archivePath, ct);

        var converted = new List<Converted>();
        var rejected = new List<string>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(AnnotationPrefix, StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            await using var stream = await entry.OpenAsync(ct);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(ct);
            try
            {
                converted.Add(new Converted(name, name.Split('_')[0], FaturaConverter.Convert(json), json));
            }
            catch (FormatException ex)
            {
                rejected.Add($"{name}: {ex.Message}");
            }
        }
        Console.WriteLine($"Converted {converted.Count} annotations, rejected {rejected.Count}:");
        rejected.ForEach(r => Console.WriteLine($"  {r}"));

        var (dev, rest) = StratifiedSampler.HoldOutStrata(converted, c => c.Layout, devLayouts, seed);
        await WritePoolAsync(zip, StratifiedSampler.Sample(rest, c => c.Name, c => c.Layout, count, seed),
            evalsDir.FullName, "golden", "annotations.jsonl", ct);
        await WritePoolAsync(zip, StratifiedSampler.Sample(dev, c => c.Name, c => c.Layout, devCount, seed),
            evalsDir.FullName, "dev", "dev/annotations.jsonl", ct);
        return 0;
    }

    private static async Task WritePoolAsync(ZipArchive zip, IReadOnlyList<Converted> sample, string evalsDir, string poolDir, string annotationsFile, CancellationToken ct)
    {
        var docDir = Path.Combine(evalsDir, poolDir, "fatura");
        if (Directory.Exists(docDir)) Directory.Delete(docDir, recursive: true);
        Directory.CreateDirectory(docDir);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var docs = new List<GoldenDocument>();
        foreach (var c in sample)
        {
            var entry = zip.GetEntry($"{ImagePrefix}{c.Name}.jpg") ?? throw new InvalidDataException($"Missing image for {c.Name}.");
            await entry.ExtractToFileAsync(Path.Combine(docDir, $"{c.Name}.jpg"), overwrite: true, ct);
            await File.WriteAllTextAsync(Path.Combine(docDir, $"{c.Name}.txt"), FaturaText.Spans(c.Json), utf8, ct);
            await File.WriteAllTextAsync(Path.Combine(docDir, $"{c.Name}.ocr.txt"), FaturaText.Ocr(c.Json), utf8, ct);
            var prefix = $"{poolDir}/fatura/{c.Name}";
            docs.Add(new GoldenDocument($"fatura/{c.Name}", "fatura", c.Layout, $"{prefix}.jpg", $"{prefix}.txt", $"{prefix}.ocr.txt", c.Invoice));
        }

        var annotationsPath = Path.Combine(evalsDir, annotationsFile);
        await GoldenSet.WriteAsync(annotationsPath, docs, ct);
        var layouts = docs.Select(d => d.Layout).Distinct().Order(StringComparer.Ordinal).ToList();
        Console.WriteLine($"Wrote {docs.Count} documents across {layouts.Count} layouts to {annotationsPath}");
        if (layouts.Count <= 10) Console.WriteLine($"  layouts: {string.Join(", ", layouts)}");
    }

    private sealed record Converted(string Name, string Layout, InvoiceDto Invoice, string Json);
}
