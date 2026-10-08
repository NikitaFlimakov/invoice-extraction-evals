using System.CommandLine;
using System.Text.Json;

using InvoiceEvals.Cli.Langfuse;
using InvoiceEvals.Core;

namespace InvoiceEvals.Cli;

/// <summary>`evals langfuse sync-dataset`: mirrors the eval set into the Langfuse dataset and records the item ids.</summary>
internal static class LangfuseCommand
{
    private const int Concurrency = 4;

    public static Command Create()
    {
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var sync = new Command("sync-dataset", $"Create or update the Langfuse dataset '{LangfuseOptions.DatasetName}' with one item per eval document.") { evalsDir };
        sync.SetAction((r, ct) => SyncAsync(r.GetValue(evalsDir)!.FullName, ct));
        return new Command("langfuse", "Langfuse dataset management (needs LANGFUSE_PUBLIC_KEY, LANGFUSE_SECRET_KEY, LANGFUSE_BASE_URL).") { sync };
    }

    private static async Task<int> SyncAsync(string evalsDir, CancellationToken ct)
    {
        var options = LangfuseOptions.FromEnvironment(Console.Error);
        if (options is null)
        {
            Console.Error.WriteLine("Set LANGFUSE_PUBLIC_KEY, LANGFUSE_SECRET_KEY and LANGFUSE_BASE_URL first.");
            return 2;
        }

        var docs = await EvalSet.LoadAsync(evalsDir, EvalSet.TextMode, ct);
        var itemsPath = Path.Combine(evalsDir, LangfuseItems.FileName);
        var previous = LangfuseItems.Load(itemsPath);
        using var client = new LangfuseClient(options);
        try
        {
            var datasetId = await client.GetDatasetIdAsync(LangfuseOptions.DatasetName, ct)
                ?? await client.CreateDatasetAsync(LangfuseOptions.DatasetName,
                    "Invoice extraction eval set: FATURA golden sample plus synthetic edge cases. Expected output is the golden InvoiceDto.",
                    new { source = "https://github.com/NikitaFlimakov/invoice-extraction-evals" }, ct);
            Console.WriteLine($"Dataset {LangfuseOptions.DatasetName}: {datasetId}");

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var done = 0;
            await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (doc, token) =>
            {
                var id = await client.UpsertDatasetItemAsync(new DatasetItem(
                    LangfuseItems.ItemId(doc.Id), LangfuseOptions.DatasetName,
                    new { documentId = doc.Id, textPath = doc.Golden.TextPath, ocrTextPath = doc.Golden.OcrTextPath, documentPath = doc.Golden.DocumentPath },
                    JsonSerializer.SerializeToElement(doc.Golden.Expected, GoldenSet.JsonOptions),
                    new { source = doc.Golden.Source, layout = doc.Golden.Layout },
                    "ACTIVE"), token);
                lock (ids) ids[doc.Id] = id;
                if (Interlocked.Increment(ref done) % 30 == 0) Console.WriteLine($"  {done}/{docs.Count} items");
            });

            // Documents that left the eval set are archived, not deleted, so past experiments keep their links.
            var removed = previous?.Items.Where(kv => !ids.ContainsKey(kv.Key)).ToList() ?? [];
            foreach (var (docId, itemId) in removed)
            {
                await client.UpsertDatasetItemAsync(new DatasetItem(itemId, LangfuseOptions.DatasetName, null, null, new { documentId = docId }, "ARCHIVED"), ct);
            }

            await new LangfuseItems(LangfuseOptions.DatasetName, datasetId, ids).SaveAsync(itemsPath, ct);
            Console.WriteLine($"Upserted {ids.Count} items, archived {removed.Count}; wrote {itemsPath}");
            return 0;
        }
        catch (LangfuseException ex)
        {
            Console.Error.WriteLine($"Langfuse: {ex.Message}");
            return 1;
        }
    }
}
