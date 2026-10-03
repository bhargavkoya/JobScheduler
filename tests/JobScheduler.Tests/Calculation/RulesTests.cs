using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Calculation;
using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Runs;
using Moq;

namespace JobScheduler.Tests.Calculation;

public class RulesTests
{
    private static readonly ReportData Report = new("r1",
    [
        new ReportRow("ACC-001", 1250.50m),
        new ReportRow("ACC-002", -300.00m),
        new ReportRow("ACC-003", 98.25m)
    ]);

    private static Dictionary<string, string> P(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void SumAmount_TotalsAllRows_OrOneAccount()
    {
        var rule = new SumAmountRule();

        Assert.Equal(1048.75m, rule.Evaluate("n", Report, P()).Value);
        Assert.Equal(-300m, rule.Evaluate("n", Report, P(("account", "acc-002"))).Value);
    }

    [Theory]
    [InlineData("0", "2000", true)]
    [InlineData("2000", null, false)]
    [InlineData(null, "1000", false)]
    public void Threshold_PassesOnlyInsideBounds(string? min, string? max, bool expected)
    {
        var parameters = new Dictionary<string, string>();
        if (min is not null) parameters["min"] = min;
        if (max is not null) parameters["max"] = max;

        Assert.Equal(expected, new ThresholdRule().Evaluate("n", Report, parameters).Passed);
    }

    [Fact]
    public void Threshold_RejectsMissingOrInvertedBounds()
    {
        var rule = new ThresholdRule();
        Assert.Throws<ValidationException>(() => rule.Validate(P()));
        Assert.Throws<ValidationException>(() => rule.Validate(P(("min", "5"), ("max", "1"))));
        Assert.Throws<ValidationException>(() => rule.Validate(P(("min", "abc"))));
    }

    [Theory]
    [InlineData("1000", "5", true)]   // 4.875% off
    [InlineData("1000", "1", false)]
    public void Variance_ComparesPercentageToTolerance(string expected, string tolerance, bool passes)
    {
        var result = new VarianceRule().Evaluate("n", Report, P(("expected", expected), ("tolerancePercent", tolerance)));

        Assert.Equal(passes, result.Passed);
        Assert.Equal(4.875m, result.Value);
    }

    [Fact]
    public void Variance_Validation_RequiresNonZeroExpectedAndNonNegativeTolerance()
    {
        var rule = new VarianceRule();
        Assert.Throws<ValidationException>(() => rule.Validate(P(("tolerancePercent", "1"))));
        Assert.Throws<ValidationException>(() => rule.Validate(P(("expected", "0"), ("tolerancePercent", "1"))));
        Assert.Throws<ValidationException>(() => rule.Validate(P(("expected", "10"), ("tolerancePercent", "-1"))));
        rule.Validate(P(("expected", "10"), ("tolerancePercent", "0")));
    }

    [Fact]
    public void Registry_FindsByType_CaseInsensitively()
    {
        var registry = new RuleRegistry([new SumAmountRule(), new ThresholdRule()]);

        Assert.NotNull(registry.Find("threshold"));
        Assert.Null(registry.Find("Nope"));
        Assert.Equal(["SumAmount", "Threshold"], registry.Types);
    }
}

public class RulesCalculationEngineTests
{
    private static readonly ReportData Report = new("r1", [new ReportRow("A", 600m), new ReportRow("B", 448.75m)]);

    private readonly Mock<IRuleStore> _store = new();
    private readonly RuleRegistry _registry = new([new SumAmountRule(), new ThresholdRule(), new VarianceRule()]);

    private RulesCalculationEngine Sut() => new(_store.Object, _registry);

    private static CalculationRule Rule(string name, string type, object parameters, bool active = true) => new()
    {
        Name = name,
        Type = type,
        ParametersJson = JsonSerializer.Serialize(parameters),
        IsActive = active
    };

    private void StoreHas(params CalculationRule[] rules) =>
        _store.Setup(s => s.FindByNamesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

    private static Dictionary<string, string> Config(string rules) => new() { ["rules"] = rules };

    [Fact]
    public async Task NoRulesConfigured_ReturnsNeutralResult_WithoutTouchingTheStore()
    {
        var result = await Sut().CalculateAsync(Guid.NewGuid(), Report, new Dictionary<string, string>(), default);

        Assert.Contains("No calculation rules", result.Summary);
        Assert.Empty(result.Values);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunsEachNamedRule_AndReportsPassAndFail()
    {
        StoreHas(
            Rule("Total", "SumAmount", new { }),
            Rule("Cap", "Threshold", new { max = "500" }));

        var result = await Sut().CalculateAsync(Guid.NewGuid(), Report, Config("Total, Cap"), default);

        Assert.StartsWith("1 of 2 rules passed", result.Summary);
        Assert.Contains("PASS Total", result.Summary);
        Assert.Contains("FAIL Cap", result.Summary);
        Assert.Equal(1048.75m, result.Values["Total"]);
    }

    [Fact]
    public async Task UnknownRule_Throws()
    {
        StoreHas();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().CalculateAsync(Guid.NewGuid(), Report, Config("Ghost"), default));
        Assert.Contains("Ghost", ex.Message);
    }

    [Fact]
    public async Task InactiveRule_Throws()
    {
        StoreHas(Rule("Total", "SumAmount", new { }, active: false));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().CalculateAsync(Guid.NewGuid(), Report, Config("Total"), default));
    }

    [Fact]
    public async Task RuleWithUnknownType_Throws()
    {
        StoreHas(Rule("Weird", "Quantum", new { }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().CalculateAsync(Guid.NewGuid(), Report, Config("Weird"), default));
    }

    [Fact]
    public async Task SameInputs_GiveIdenticalResult_SoRetriesNeverDoubleCount()
    {
        StoreHas(Rule("Total", "SumAmount", new { }), Rule("Var", "Variance", new { expected = "1000", tolerancePercent = "1" }));
        var config = Config("Total,Var");

        var first = await Sut().CalculateAsync(Guid.NewGuid(), Report, config, default);
        var second = await Sut().CalculateAsync(Guid.NewGuid(), Report, config, default);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Values, second.Values);
    }

    [Fact]
    public async Task DuplicateRuleNames_AreRunOnce()
    {
        StoreHas(Rule("Total", "SumAmount", new { }));

        var result = await Sut().CalculateAsync(Guid.NewGuid(), Report, Config("Total, total"), default);

        Assert.StartsWith("1 of 1 rules passed", result.Summary);
    }
}

public class RuleServiceTests
{
    private readonly Mock<IRuleStore> _store = new();
    private readonly RuleRegistry _registry = new([new SumAmountRule(), new ThresholdRule()]);

    private RuleService Sut() => new(_store.Object, _registry);

    [Fact]
    public async Task Create_ValidRule_IsSavedWithCanonicalType()
    {
        CalculationRule? saved = null;
        _store.Setup(s => s.AddAsync(It.IsAny<CalculationRule>(), It.IsAny<CancellationToken>()))
            .Callback<CalculationRule, CancellationToken>((r, _) => saved = r).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(new SaveRuleRequest(" Cap ", null, "threshold", new() { ["max"] = "10" }), default);

        Assert.Equal("Cap", dto.Name);
        Assert.Equal("Threshold", saved!.Type);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("", "SumAmount")]
    [InlineData("a,b", "SumAmount")]
    [InlineData("ok", "NoSuchType")]
    public async Task Create_RejectsBadNameOrType(string name, string type)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(new SaveRuleRequest(name, null, type, null), default));
    }

    [Fact]
    public async Task Create_RejectsInvalidParameters()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().CreateAsync(new SaveRuleRequest("Cap", null, "Threshold", new()), default));
    }

    [Fact]
    public async Task Create_RejectsDuplicateName()
    {
        _store.Setup(s => s.NameExistsAsync("Total", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<ConflictException>(
            () => Sut().CreateAsync(new SaveRuleRequest("Total", null, "SumAmount", null), default));
    }

    [Fact]
    public async Task Update_UnknownRule_IsNotFound()
    {
        _store.Setup(s => s.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalculationRule?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Sut().UpdateAsync(Guid.NewGuid(), new SaveRuleRequest("x", null, "SumAmount", null), default));
    }
}
