using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI.Evaluation;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InvoiceEvals.Cli.Langfuse;

/// <summary>What identifies one <c>evals run</c> as a Langfuse experiment.</summary>
internal sealed record ExperimentInfo(string ExecutionName, string Config, string Model, string Prompt, string InputMode, string JudgePrompt, string GitSha);

/// <summary>
/// One <c>evals run</c> as one Langfuse experiment, following Langfuse's "experiments via OpenTelemetry" spec:
/// experiment attributes travel as baggage and a <see cref="BaggageSpanProcessor"/> copies them onto every span;
/// each document is its own trace whose root span carries the item attributes; model calls are child spans
/// (<see cref="TracingChatClient"/>); every metric is posted as a score on the root observation.
/// </summary>
internal sealed class LangfuseExperiment : IDisposable
{
    private readonly TracerProvider provider;
    private readonly LangfuseOptions options;
    private readonly LangfuseItems? items;
    private readonly ConcurrentQueue<Score> scores = new();
    private int unmappedItems;

    private LangfuseExperiment(TracerProvider provider, LangfuseOptions options, LangfuseItems? items)
    {
        this.provider = provider;
        this.options = options;
        this.items = items;
    }

    /// <summary>
    /// Starts the exporter and puts the experiment baggage on the current execution context. Synchronous on purpose:
    /// an AsyncLocal (baggage) set inside an async method would be reverted when it returns.
    /// </summary>
    public static LangfuseExperiment Start(LangfuseOptions options, ExperimentInfo info, LangfuseItems? items, TextWriter log)
    {
        var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("invoice-extraction-evals"))
            .AddSource(EvalsTelemetry.SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new BaggageSpanProcessor())
            .AddOtlpExporter(o =>
            {
                o.Endpoint = options.OtlpTracesEndpoint;
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.HttpClientFactory = () =>
                {
                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    http.DefaultRequestHeaders.Authorization = new("Basic", options.BasicAuth);
                    http.DefaultRequestHeaders.Add("x-langfuse-ingestion-version", "4");
                    return http;
                };
                // Spans carry full prompts and documents; small batches keep each request well under body limits.
                o.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions { MaxQueueSize = 16_384, MaxExportBatchSize = 64, ScheduledDelayMilliseconds = 1000 };
            })
            .Build();

        if (items is null) log.WriteLine($"Langfuse: no evals/{LangfuseItems.FileName}; traces use document ids, not dataset items. Run `evals langfuse sync-dataset` to link them.");
        var baggage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["langfuse.experiment.id"] = $"{info.ExecutionName}-{info.Config}",
            ["langfuse.experiment.name"] = info.ExecutionName,
            ["langfuse.experiment.description"] = $"{info.Config}: {info.Model}, prompt {info.Prompt}, input {info.InputMode}",
            ["langfuse.experiment.metadata.config"] = info.Config,
            ["langfuse.experiment.metadata.model"] = info.Model,
            ["langfuse.experiment.metadata.prompt_version"] = info.Prompt,
            ["langfuse.experiment.metadata.input_mode"] = info.InputMode,
            ["langfuse.experiment.metadata.judge_prompt_version"] = info.JudgePrompt,
            ["langfuse.experiment.metadata.git_sha"] = info.GitSha,
            ["langfuse.environment"] = LangfuseOptions.Environment,
        };
        if (items is not null) baggage["langfuse.experiment.dataset.id"] = items.DatasetId;
        Baggage.Current = Baggage.Create(baggage);
        log.WriteLine($"Langfuse: experiment '{info.ExecutionName}' → {options.BaseUrl}");
        return new LangfuseExperiment(provider, options, items);
    }

    /// <summary>
    /// Starts the document's root span as a new trace and adds the item baggage. Synchronous for the same reason as
    /// <see cref="Start"/>: the caller's context must keep the span as current and the item baggage.
    /// </summary>
    public ItemTrace StartItem(EvalDocument doc, string inputMode)
    {
        Activity.Current = null; // Each item is its own trace.
        var activity = EvalsTelemetry.Source.StartActivity(doc.Id, ActivityKind.Internal);
        if (activity is null) return new ItemTrace(null);

        string itemId;
        if (items?.Items.TryGetValue(doc.Id, out var mapped) == true)
        {
            itemId = mapped;
        }
        else
        {
            itemId = doc.Id;
            if (items is not null) Interlocked.Increment(ref unmappedItems);
        }
        var spanId = activity.SpanId.ToHexString();
        activity.SetTag("langfuse.trace.name", doc.Id)
            .SetTag("langfuse.observation.input", JsonSerializer.Serialize(new { documentId = doc.Id, inputMode, textPath = doc.Golden.TextPath, ocrTextPath = doc.Golden.OcrTextPath }))
            .SetTag("langfuse.experiment.item.id", itemId)
            .SetTag("langfuse.experiment.item.root_observation_id", spanId)
            .SetTag("langfuse.experiment.item.expected_output", JsonSerializer.Serialize(doc.Golden.Expected, GoldenSet.JsonOptions))
            .SetTag("langfuse.experiment.item.metadata.source", doc.Golden.Source)
            .SetTag("langfuse.experiment.item.metadata.layout", doc.Golden.Layout);
        Baggage.SetBaggage("langfuse.experiment.item.id", itemId);
        Baggage.SetBaggage("langfuse.experiment.item.root_observation_id", spanId);
        return new ItemTrace(activity);
    }

    /// <summary>Sets the root output, stretches the root to the preserved model latency, and queues one score per metric.</summary>
    public void Complete(ItemTrace item, ExtractionResult result, EvaluationResult evaluation)
    {
        if (item.Activity is not { } activity) return;
        activity.SetTag("langfuse.observation.output", result.RawText);
        var minimumEnd = activity.StartTimeUtc + result.Latency;
        if (DateTime.UtcNow < minimumEnd) activity.SetEndTime(minimumEnd);

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        foreach (var metric in evaluation.Metrics.Values.OfType<NumericMetric>())
        {
            if (metric.Value is not { } value) continue;
            var comment = metric.Reason is { Length: > 500 } r ? r[..500] + "…" : metric.Reason;
            scores.Enqueue(new Score($"{traceId}-{metric.Name}", traceId, spanId, metric.Name, value, comment, LangfuseOptions.Environment));
        }
    }

    /// <summary>Flushes spans, then posts the queued scores. Failures are reported, never thrown: tracing must not fail a run.</summary>
    public async Task FinishAsync(TextWriter log, CancellationToken ct)
    {
        if (!provider.ForceFlush(timeoutMilliseconds: 60_000))
            log.WriteLine("Langfuse: span export did not complete within 60 s; some traces may be missing.");
        if (unmappedItems > 0)
            log.WriteLine($"Langfuse: {unmappedItems} documents are not in evals/{LangfuseItems.FileName}; re-run `evals langfuse sync-dataset`.");

        var batch = scores.ToArray();
        if (batch.Length == 0) return;
        try
        {
            using var client = new LangfuseClient(options);
            var result = await client.CreateScoresAsync(batch, ct);
            log.WriteLine($"Langfuse: {result.Accepted} scores accepted, {result.Rejected} rejected.");
            foreach (var error in result.Errors.Take(5)) log.WriteLine($"  {error}");
        }
        catch (Exception ex) when (ex is LangfuseException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.WriteLine($"Langfuse: posting {batch.Length} scores failed: {ex.Message}");
        }
    }

    public void Dispose() => provider.Dispose();
}

/// <summary>A document's root span; null when tracing is off. Disposing ends the span.</summary>
internal sealed class ItemTrace(Activity? activity) : IDisposable
{
    public Activity? Activity { get; } = activity;

    public void Fail(Exception ex)
    {
        Activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Activity?.AddException(ex);
    }

    public void Dispose() => Activity?.Dispose();
}

/// <summary>Copies every baggage entry onto each span as it starts, so experiment and item attributes reach child spans.</summary>
internal sealed class BaggageSpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity data)
    {
        foreach (var (key, value) in Baggage.Current.GetBaggage()) data.SetTag(key, value);
    }
}
