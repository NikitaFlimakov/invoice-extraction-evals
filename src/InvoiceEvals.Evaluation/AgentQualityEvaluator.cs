using InvoiceEvals.Agent;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

// ToolCallAccuracyEvaluator and TaskAdherenceEvaluator ship as experimental (AIEVAL001) in Quality 10.10. They are
// used as specified, behind this one class, and their scores are secondary to the deterministic metrics.
#pragma warning disable AIEVAL001

namespace InvoiceEvals.Evaluation;

/// <summary>
/// LLM-judged agent quality from Microsoft.Extensions.AI.Evaluation.Quality: <see cref="ToolCallAccuracyEvaluator"/>
/// and <see cref="TaskAdherenceEvaluator"/>, given the agent's tool definitions. Secondary to the deterministic
/// metrics. Calls go through the scenario's chat client (the response cache) with the model pinned to
/// <paramref name="model"/>, and are traced like the name judge. Results are re-emitted as numeric metrics so the
/// report aggregates them: <c>tool_call_accuracy</c> (0/1) and <c>task_adherence</c> (1–5).
/// </summary>
public sealed class AgentQualityEvaluator(string model = NameJudge.DefaultModel) : IEvaluator
{
    public const string ToolCallAccuracy = "tool_call_accuracy";
    public const string TaskAdherence = "task_adherence";

    private readonly ToolCallAccuracyEvaluator toolCalls = new();
    private readonly TaskAdherenceEvaluator adherence = new();

    public string Model => model;

    public IReadOnlyCollection<string> EvaluationMetricNames => [ToolCallAccuracy, TaskAdherence];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatConfiguration);
        var judge = new ChatConfiguration(new TracingChatClient(chatConfiguration.ChatClient)
            .AsBuilder().ConfigureOptions(o => o.ModelId = model).Build());
        var request = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        var calls = await toolCalls.EvaluateAsync(request, modelResponse, judge, [new ToolCallAccuracyEvaluatorContext(InvoiceTools.All)], cancellationToken);
        var task = await adherence.EvaluateAsync(request, modelResponse, judge, [new TaskAdherenceEvaluatorContext(InvoiceTools.All)], cancellationToken);

        var accuracy = calls.Get<BooleanMetric>(ToolCallAccuracyEvaluator.ToolCallAccuracyMetricName);
        var score = task.Get<NumericMetric>(TaskAdherenceEvaluator.TaskAdherenceMetricName);
        return new EvaluationResult(
            Copy(accuracy, new NumericMetric(ToolCallAccuracy, accuracy.Value is { } v ? (v ? 1 : 0) : null, accuracy.Reason)),
            Copy(score, new NumericMetric(TaskAdherence, score.Value, score.Reason)));
    }

    private static NumericMetric Copy(EvaluationMetric from, NumericMetric to)
    {
        to.Interpretation = from.Interpretation;
        to.AddDiagnostics(from.Diagnostics ?? []);
        foreach (var (key, value) in from.Metadata ?? new Dictionary<string, string>()) to.AddOrUpdateMetadata(key, value);
        return to;
    }
}
