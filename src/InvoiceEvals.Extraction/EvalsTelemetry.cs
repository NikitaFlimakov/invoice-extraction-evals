using System.Diagnostics;

namespace InvoiceEvals.Extraction;

/// <summary>
/// The one <see cref="ActivitySource"/> for evals spans. Nothing is recorded unless a listener subscribes (the
/// Langfuse exporter in <c>evals run</c>), so instrumented code costs nothing when tracing is off.
/// </summary>
public static class EvalsTelemetry
{
    public const string SourceName = "InvoiceEvals";

    /// <summary>Tag on a model-call span: true when served from the response cache, false after a real network call.</summary>
    public const string CacheHitTag = "evals.cache_hit";

    public static ActivitySource Source { get; } = new(SourceName);
}
