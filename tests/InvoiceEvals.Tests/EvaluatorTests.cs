using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Tests;

public sealed class EvaluatorTests
{
    private static readonly InvoiceDto Golden = new(
        Vendor: new Party("Acme Intl. GmbH", null),
        Customer: new Party("Jane Doe", null),
        InvoiceNumber: "INV/2024-001",
        InvoiceDate: new DateOnly(2024, 2, 29),
        DueDate: null,
        Currency: "EUR",
        Subtotal: 100.00m,
        Discount: null,
        Tax: 19.00m,
        Total: 150.00m, // Does not reconcile, like FATURA.
        LineItems: null);

    // ---------- SchemaValidityEvaluator ----------

    [Fact]
    public async Task Schema_ValidResponse_Scores1()
    {
        var metric = await Numeric(new SchemaValidityEvaluator(), Json(Golden), SchemaValidityEvaluator.MetricName);

        Assert.Equal(1, metric.Value);
        Assert.False(metric.Interpretation!.Failed);
    }

    [Theory]
    [InlineData("not json", "Does not parse")]
    [InlineData("""{"invoiceDate":"29/02/2024"}""", "invoiceDate")]
    [InlineData("""{"invoiceNumber":null,"invoiceDate":null,"total":null,"subtotal":1}""", "all null")]
    [InlineData("""{"total":1,"currency":"€"}""", "3-letter")]
    [InlineData("""{"total":1,"currency":"usd"}""", "3-letter")]
    [InlineData("""{"total":1,"lineItems":[{"description":"ok","amount":1},{"description":null,"amount":2}]}""", "lineItems[1]")]
    public async Task Schema_Violation_Scores0_WithDiagnostic(string response, string diagnostic)
    {
        var metric = await Numeric(new SchemaValidityEvaluator(), response, SchemaValidityEvaluator.MetricName);

        Assert.Equal(0, metric.Value);
        Assert.True(metric.Interpretation!.Failed);
        Assert.Contains(metric.Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Error && d.Message.Contains(diagnostic, StringComparison.Ordinal));
    }

    // ---------- FieldAccuracyEvaluator ----------

    [Fact]
    public async Task Fields_ExactCopy_AllCorrect_IncludingNullGoldenNullPredicted()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(Golden));

        Assert.All(FieldAccuracyEvaluator.FieldNames, f => Assert.Equal(1, Field(result, f).Value));
        Assert.Equal(FieldAccuracyEvaluator.Outcome.CorrectNull, Outcome(result, "due_date"));
        Assert.Equal(FieldAccuracyEvaluator.Outcome.CorrectValue, Outcome(result, "total"));
    }

    [Theory]
    [InlineData("150.01", 1)] // Exactly at the 0.01 tolerance: correct.
    [InlineData("149.99", 1)]
    [InlineData("150.011", 0)] // Just outside.
    [InlineData("150.02", 0)]
    public async Task Fields_AmountTolerance_IsInclusive(string total, double expected)
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(Golden with { Total = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture) }));

        Assert.Equal(expected, Field(result, "total").Value);
    }

    [Fact]
    public async Task Fields_NullGolden_PredictedValue_IsFalsePositive()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(Golden with { DueDate = new DateOnly(2024, 3, 30), Discount = 0m }));

        Assert.Equal(0, Field(result, "due_date").Value);
        Assert.Equal(FieldAccuracyEvaluator.Outcome.FalsePositive, Outcome(result, "due_date"));
        Assert.Equal(FieldAccuracyEvaluator.Outcome.FalsePositive, Outcome(result, "discount")); // 0 is a value, not "not printed".
    }

    [Fact]
    public async Task Fields_NonNullGolden_PredictedNull_IsMiss_AndWrongValue_IsMismatch()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(Golden with { InvoiceDate = null, Tax = 18.00m }));

        Assert.Equal(FieldAccuracyEvaluator.Outcome.Miss, Outcome(result, "invoice_date"));
        Assert.Equal(FieldAccuracyEvaluator.Outcome.Mismatch, Outcome(result, "tax"));
    }

    [Fact]
    public async Task Fields_Normalization_InvoiceNumberCase_CurrencyCase_VendorSuffix()
    {
        var predicted = Golden with { InvoiceNumber = "  inv/2024-001 ", Currency = "eur", Vendor = new Party("ACME Intl.", null), Customer = new Party("jane doe.", null) };

        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(predicted));

        Assert.Equal(1, Field(result, "invoice_number").Value);
        Assert.Equal(1, Field(result, "currency").Value);
        Assert.Equal(1, Field(result, "vendor_name").Value);
        Assert.Equal(1, Field(result, "customer_name").Value);
    }

    [Fact]
    public async Task Fields_DifferentDate_IsMismatch()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), Json(Golden with { InvoiceDate = new DateOnly(2024, 3, 1) }));

        Assert.Equal(0, Field(result, "invoice_date").Value);
    }

    [Fact]
    public async Task Fields_Unparsed_AllWrong_EvenWhereGoldenIsNull()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator(), "oops");

        Assert.All(FieldAccuracyEvaluator.FieldNames, f => Assert.Equal(0, Field(result, f).Value));
        Assert.Equal(FieldAccuracyEvaluator.Outcome.Unparsed, Outcome(result, "due_date"));
    }

    [Fact]
    public async Task Fields_VendorMatcher_IsInjectable()
    {
        var result = await Evaluate(new FieldAccuracyEvaluator((_, _) => false), Json(Golden));

        Assert.Equal(0, Field(result, "vendor_name").Value);
        Assert.Equal(1, Field(result, "customer_name").Value);
    }

    [Theory]
    [InlineData("Müller Elektro GmbH & Co. KG", "MÜLLER ELEKTRO")]
    [InlineData("Atelier Dubois S.à r.l.", "atelier dubois")]
    [InlineData("O'Neill & Sons, LLC", "o neill and sons")]
    [InlineData("Yamada Shoji Co., Ltd.", "yamada shoji")]
    [InlineData("Kirk, Murphy and Daniels", "Kirk Murphy and Daniels")]
    [InlineData("Inc.", "inc")] // A name that is only a suffix keeps it.
    public void NormalizeName_StripsSuffixesPunctuationAndCase(string a, string b) =>
        Assert.Equal(FieldAccuracyEvaluator.NormalizeName(a), FieldAccuracyEvaluator.NormalizeName(b));

    [Fact]
    public void NormalizeName_KeepsAbbreviationsDistinct() => // Intl. vs International is the Phase 3 judge's job.
        Assert.NotEqual(FieldAccuracyEvaluator.NormalizeName("Acme Intl."), FieldAccuracyEvaluator.NormalizeName("Acme International"));

    // ---------- RecomputedTotalEvaluator ----------

    [Fact]
    public async Task Recomputed_ModelFixedArithmetic_Scores1()
    {
        var metric = await Numeric(new RecomputedTotalEvaluator(), Json(Golden with { Total = 119.00m }), RecomputedTotalEvaluator.MetricName);

        Assert.Equal(1, metric.Value);
        Assert.True(metric.Interpretation!.Failed);
    }

    [Theory]
    [InlineData("150.00")] // As printed.
    [InlineData("200.00")] // Wrong, but not recomputed.
    public async Task Recomputed_OtherTotals_Score0(string total)
    {
        var metric = await Numeric(new RecomputedTotalEvaluator(), Json(Golden with { Total = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture) }), RecomputedTotalEvaluator.MetricName);

        Assert.Equal(0, metric.Value);
    }

    [Fact]
    public async Task Recomputed_NotApplicable_WhenGoldenReconciles_OrSubtotalMissing()
    {
        var reconciles = Golden with { Total = 119.00m };
        var noSubtotal = Golden with { Subtotal = null };

        Assert.Null((await Numeric(new RecomputedTotalEvaluator(), Json(reconciles), RecomputedTotalEvaluator.MetricName, reconciles)).Value);
        Assert.Null((await Numeric(new RecomputedTotalEvaluator(), Json(noSubtotal), RecomputedTotalEvaluator.MetricName, noSubtotal)).Value);
    }

    // ---------- LineItemsF1Evaluator ----------

    private static readonly InvoiceDto WithLines = Golden with
    {
        LineItems =
        [
            new LineItem("Steel bracket, M8", 2, 10.00m, 20.00m),
            new LineItem("Consulting hours, premium", 1, 80.00m, 80.00m),
            new LineItem("Shipping pallet", 1, 5.00m, 5.00m),
        ],
    };

    [Fact]
    public async Task LineItems_GoldenNull_IsNotApplicable()
    {
        var metric = await Numeric(new LineItemsF1Evaluator(), Json(WithLines), LineItemsF1Evaluator.F1);

        Assert.Null(metric.Value);
        Assert.False(metric.Interpretation!.Failed);
    }

    [Fact]
    public async Task LineItems_PartialMatch_ComputesPrecisionRecallF1()
    {
        var predicted = WithLines with
        {
            LineItems =
            [
                new LineItem("steel bracket M8", null, null, 20.01m), // Within tolerance, same tokens.
                new LineItem("Consulting hours, premium", 1, 80.00m, 79.00m), // Amount off.
                new LineItem("Unrelated thing", 1, 5.00m, 5.00m), // Amount matches, description does not.
                new LineItem("Shipping pallet", 1, 5.00m, 5.00m),
            ],
        };

        var result = await Evaluate(new LineItemsF1Evaluator(), Json(predicted), WithLines);

        Assert.Equal(2.0 / 4, Value(result, LineItemsF1Evaluator.Precision));
        Assert.Equal(2.0 / 3, Value(result, LineItemsF1Evaluator.Recall));
        Assert.Equal(2 * 0.5 * (2.0 / 3) / (0.5 + (2.0 / 3)), Value(result, LineItemsF1Evaluator.F1)!.Value, 10);
    }

    [Fact]
    public async Task LineItems_AmountJustOutsideTolerance_DoesNotMatch()
    {
        var predicted = WithLines with { LineItems = [new LineItem("Shipping pallet", 1, 5.00m, 5.02m)] };

        var result = await Evaluate(new LineItemsF1Evaluator(), Json(predicted), WithLines);

        Assert.Equal(0, Value(result, LineItemsF1Evaluator.F1));
    }

    [Theory]
    [InlineData(true, false, 1.0)] // Both empty.
    [InlineData(true, true, 0.0)] // Golden empty, prediction invents lines.
    [InlineData(false, false, 0.0)] // Golden has lines, prediction has none.
    public async Task LineItems_EmptyCases(bool goldenEmpty, bool predictedHasLines, double f1)
    {
        var golden = goldenEmpty ? WithLines with { LineItems = [] } : WithLines;
        var predicted = WithLines with { LineItems = predictedHasLines ? WithLines.LineItems : [] };

        var result = await Evaluate(new LineItemsF1Evaluator(), Json(predicted), golden);

        Assert.Equal(f1, Value(result, LineItemsF1Evaluator.F1));
    }

    [Fact]
    public async Task LineItems_GreedyMatching_UsesEachPredictionOnce()
    {
        var golden = WithLines with { LineItems = [new LineItem("Widget", 1, 5m, 5m), new LineItem("Widget", 1, 5m, 5m)] };
        var predicted = golden with { LineItems = [new LineItem("Widget", 1, 5m, 5m)] };

        var result = await Evaluate(new LineItemsF1Evaluator(), Json(predicted), golden);

        Assert.Equal(1.0, Value(result, LineItemsF1Evaluator.Precision));
        Assert.Equal(0.5, Value(result, LineItemsF1Evaluator.Recall));
    }

    // ---------- CompositeScoreEvaluator ----------

    [Fact]
    public void Composite_WeightsSumToOne() =>
        Assert.Equal(1.0, CompositeScoreEvaluator.Weights.Schema + CompositeScoreEvaluator.Weights.Fields
            + CompositeScoreEvaluator.Weights.LineItems + CompositeScoreEvaluator.Weights.NotRecomputed, 10);

    [Fact]
    public async Task Composite_PerfectFatura_Is1_WithLineItemsRescaledAway()
    {
        var result = await Evaluate(new CompositeScoreEvaluator(), Json(Golden));

        Assert.Equal(1.0, Value(result, CompositeScoreEvaluator.MetricName)!.Value, 10);
        Assert.Null(Value(result, LineItemsF1Evaluator.F1));
    }

    [Fact]
    public async Task Composite_RescalesOverApplicableComponents()
    {
        // Schema valid (1), 9/10 fields correct, line items n/a, recomputed total n/a (golden reconciles).
        var golden = Golden with { Total = 119.00m };
        var predicted = golden with { Tax = 1m };

        var result = await Evaluate(new CompositeScoreEvaluator(), Json(predicted), golden);

        var w = CompositeScoreEvaluator.Weights;
        Assert.Equal(((w.Schema * 1) + (w.Fields * 0.9)) / (w.Schema + w.Fields), Value(result, CompositeScoreEvaluator.MetricName)!.Value, 10);
    }

    [Fact]
    public async Task Composite_Unparsed_Is0()
    {
        var result = await Evaluate(new CompositeScoreEvaluator(), "nope");

        Assert.Equal(0, Value(result, CompositeScoreEvaluator.MetricName));
    }

    // ---------- helpers ----------

    private static string Json(InvoiceDto dto) => JsonSerializer.Serialize(dto, GoldenSet.JsonOptions);

    private static async Task<EvaluationResult> Evaluate(IEvaluator evaluator, string response, InvoiceDto? golden = null) =>
        await evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "invoice text")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, response)),
            additionalContext: [new GoldenInvoiceContext(golden ?? Golden)],
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<NumericMetric> Numeric(IEvaluator evaluator, string response, string name, InvoiceDto? golden = null) =>
        (await Evaluate(evaluator, response, golden)).Get<NumericMetric>(name);

    private static NumericMetric Field(EvaluationResult result, string field) => result.Get<NumericMetric>(FieldAccuracyEvaluator.MetricName(field));

    private static string Outcome(EvaluationResult result, string field) => Field(result, field).Metadata![FieldAccuracyEvaluator.OutcomeKey];

    private static double? Value(EvaluationResult result, string name) => result.Get<NumericMetric>(name).Value;
}
