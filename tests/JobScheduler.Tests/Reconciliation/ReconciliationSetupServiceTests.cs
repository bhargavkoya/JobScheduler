using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Reconciliation;

public class ReconciliationSetupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IReconciliationStore> _store = new();
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Guid _myId = Guid.NewGuid();
    private readonly List<ReconciliationRule> _addedRules = [];
    private readonly Dictionary<Guid, InputFile> _files = [];

    private readonly Job _job = new()
    {
        Name = "j", Team = Team.Business, Status = JobStatus.Scheduled,
        Template = new JobTemplate { IsFileReconciliation = true }
    };

    public ReconciliationSetupServiceTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        _job.OwnerId = _myId;

        _store.Setup(s => s.FindFilesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.Where(_files.ContainsKey).Select(i => _files[i]).ToList());
        _store.Setup(s => s.AddRule(It.IsAny<ReconciliationRule>())).Callback<ReconciliationRule>(_addedRules.Add);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
    }

    private ReconciliationSetupService Sut() => new(_store.Object, _jobs.Object, _me.Object, _clock.Object);

    private InputFile File(string name, string[] headers, Guid? owner = null, Guid? jobId = null)
    {
        var f = new InputFile
        {
            OwnerId = owner ?? _myId, FileName = name, JobId = jobId, HeadersJson = JsonSerializer.Serialize(headers), RowCount = 3
        };
        _files[f.Id] = f;
        return f;
    }

    private static ReconciliationRuleDto Rule(
        string name = "R1", string left = "a", string right = "b", string? keyA = "Id", string? keyB = "Id",
        string l = "a.Gross - a.Fee", string r = "b.Net", ComparisonOperator op = ComparisonOperator.Equal,
        decimal tol = 0, ToleranceMode mode = ToleranceMode.Absolute) =>
        new(null, name, left, right, keyA, keyB, l, op, r, tol, mode);

    private (InputFile A, InputFile B, ReconciliationSetupDto Setup) Typical(params ReconciliationRuleDto[] rules)
    {
        var a = File("a.csv", ["Id", "Gross", "Fee"]);
        var b = File("b.csv", ["Id", "Net"]);
        return (a, b, new ReconciliationSetupDto(
            [new AttachFileDto(a.Id, "a"), new AttachFileDto(b.Id, "b")], rules.Length == 0 ? [Rule()] : rules));
    }

    // ---- attach ----

    [Fact]
    public async Task Attach_StoresRulesAndAttachesFilesToTheJob_WithoutSaving()
    {
        var (a, b, setup) = Typical(Rule(tol: 0.05m), Rule("R2", l: "a.Gross", r: "b.Net", op: ComparisonOperator.GreaterThan));

        await Sut().AttachAsync(_job, setup, default);

        Assert.Equal([1, 2], _addedRules.Select(r => r.Position));
        Assert.All(_addedRules, r => Assert.Equal(_job.Id, r.JobId));
        Assert.Equal(0.05m, _addedRules[0].Tolerance);
        Assert.Equal(_job.Id, a.JobId);
        Assert.Equal(_job.Id, b.JobId);
        Assert.Equal("a", a.Alias);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Attach_NormalisesAliasAndColumnCasingToTheRealHeaders()
    {
        var (_, _, setup) = Typical(Rule(left: "A", right: "B", keyA: "id", keyB: "ID"));

        await Sut().AttachAsync(_job, setup, default);

        var rule = Assert.Single(_addedRules);
        Assert.Equal(("a", "b", "Id", "Id"), (rule.LeftAlias, rule.RightAlias, rule.LeftKeyColumn, rule.RightKeyColumn));
    }

    [Fact]
    public async Task Attach_WithAWithinFileRule_DoesNotNeedKeys()
    {
        var f = File("a.csv", ["X", "Y"]);
        var setup = new ReconciliationSetupDto([new AttachFileDto(f.Id, "a")],
            [Rule(left: "a", right: "a", keyA: null, keyB: null, l: "a.X", r: "a.Y")]);

        await Sut().AttachAsync(_job, setup, default);

        var rule = Assert.Single(_addedRules);
        Assert.True(rule.IsSingleFile);
        Assert.Null(rule.RightKeyColumn);
    }

    [Fact]
    public async Task Attach_RejectsMissingSetup() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, null, default));

    [Fact]
    public async Task Attach_RejectsNoFilesAndNoRules()
    {
        var (a, _, _) = Typical();
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().AttachAsync(_job, new ReconciliationSetupDto([], [Rule()]), default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().AttachAsync(_job, new ReconciliationSetupDto([new AttachFileDto(a.Id, "a")], []), default));
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("this_alias_is_way_too_long_to_be_accepted")]
    public async Task Attach_RejectsInvalidAliases(string alias)
    {
        var f = File("a.csv", ["Id"]);
        var setup = new ReconciliationSetupDto([new AttachFileDto(f.Id, alias)], [Rule(left: alias, right: alias)]);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, setup, default));
        Assert.Contains("alias", ex.Message);
    }

    [Fact]
    public async Task Attach_RejectsDuplicateAliasesAndDuplicateFiles()
    {
        var a = File("a.csv", ["Id"]);
        var b = File("b.csv", ["Id"]);

        var sameAlias = new ReconciliationSetupDto([new AttachFileDto(a.Id, "x"), new AttachFileDto(b.Id, "X")], [Rule()]);
        Assert.Contains("used twice", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, sameAlias, default))).Message);

        var sameFile = new ReconciliationSetupDto([new AttachFileDto(a.Id, "x"), new AttachFileDto(a.Id, "y")], [Rule()]);
        Assert.Contains("twice", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, sameFile, default))).Message);
    }

    [Fact]
    public async Task Attach_RejectsAnUnknownFileId()
    {
        var setup = new ReconciliationSetupDto([new AttachFileDto(Guid.NewGuid(), "a")], [Rule()]);
        Assert.Contains("not found", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Attach_RejectsSomeoneElsesFile_LookingLikeAMissingOne()
    {
        var theirs = File("a.csv", ["Id", "Gross", "Fee"], owner: Guid.NewGuid());
        var b = File("b.csv", ["Id", "Net"]);
        var setup = new ReconciliationSetupDto([new AttachFileDto(theirs.Id, "a"), new AttachFileDto(b.Id, "b")], [Rule()]);

        Assert.Contains("not found", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Attach_AnAdminMayUseAnotherUsersUpload()
    {
        _me.SetupGet(m => m.Role).Returns(Role.Admin);
        var a = File("a.csv", ["Id", "Gross", "Fee"], owner: Guid.NewGuid());
        var b = File("b.csv", ["Id", "Net"], owner: Guid.NewGuid());
        var setup = new ReconciliationSetupDto([new AttachFileDto(a.Id, "a"), new AttachFileDto(b.Id, "b")], [Rule()]);

        await Sut().AttachAsync(_job, setup, default);

        Assert.Equal(_job.Id, a.JobId);
    }

    [Fact]
    public async Task Attach_RejectsAFileAlreadyUsedByAJob()
    {
        var a = File("a.csv", ["Id", "Gross", "Fee"], jobId: Guid.NewGuid());
        var b = File("b.csv", ["Id", "Net"]);
        var setup = new ReconciliationSetupDto([new AttachFileDto(a.Id, "a"), new AttachFileDto(b.Id, "b")], [Rule()]);

        Assert.Contains("already used", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Attach_AFailedValidation_AttachesNothing()
    {
        var (a, _, setup) = Typical(Rule(), Rule("R2", l: "a.Missing"));

        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, setup, default));

        Assert.Null(a.JobId);
        _store.Verify(s => s.AddRule(It.Is<ReconciliationRule>(r => r.Name == "R1")), Times.Never,
            "rules are only stored once every rule has been validated");
    }

    // ---- rule validation against the real headers ----

    [Fact]
    public async Task Rule_WithAnUnknownColumn_NamesTheColumnAndListsTheRealOnes()
    {
        var (_, _, setup) = Typical(Rule(l: "a.Gros - a.Fee"));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, setup, default));

        Assert.Contains("'R1'", ex.Message);
        Assert.Contains("'Gros'", ex.Message);
        Assert.Contains("Id, Gross, Fee", ex.Message);
    }

    [Fact]
    public async Task Rule_WithAnUnknownKeyColumn_IsRejected()
    {
        var (_, _, setup) = Typical(Rule(keyB: "TradeId"));
        Assert.Contains("key column 'TradeId'", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Rule_ComparingTwoFiles_NeedsBothKeys()
    {
        var (_, _, setup) = Typical(Rule(keyB: null));
        Assert.Contains("key column in each file", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Rule_WithAnUnknownFileAlias_IsRejected()
    {
        var (_, _, setup) = Typical(Rule(right: "zzz"));
        Assert.Contains("'zzz'", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Fact]
    public async Task Rule_ExpressionMayNotReachIntoAFileThatIsNotPartOfTheRule()
    {
        var a = File("a.csv", ["Id", "X"]);
        var b = File("b.csv", ["Id", "Y"]);
        var c = File("c.csv", ["Id", "Z"]);
        var setup = new ReconciliationSetupDto(
            [new AttachFileDto(a.Id, "a"), new AttachFileDto(b.Id, "b"), new AttachFileDto(c.Id, "c")],
            [Rule(l: "a.X", r: "c.Z")]);

        Assert.Contains("compares only 'a' and 'b'", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, setup, default))).Message);
    }

    [Theory]
    [InlineData("a.Gross +")]
    [InlineData("Gross")]
    [InlineData("")]
    public async Task Rule_WithABrokenExpression_SaysWhichSide(string left)
    {
        var (_, _, setup) = Typical(Rule(l: left));
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, setup, default));
        Assert.Contains("left side", ex.Message);
    }

    [Fact]
    public async Task Rule_WithNegativeOrHugePercentTolerance_IsRejected()
    {
        var (_, _, neg) = Typical(Rule(tol: -1));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, neg, default));

        var (_, _, pct) = Typical(Rule(tol: 101, mode: ToleranceMode.Percent));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, pct, default));
    }

    [Fact]
    public async Task Rules_NeedUniqueNonBlankNames()
    {
        var (_, _, dup) = Typical(Rule("Same"), Rule("same"));
        Assert.Contains("two rules named", (await Assert.ThrowsAsync<ValidationException>(
            () => Sut().AttachAsync(_job, dup, default))).Message);

        var (_, _, blank) = Typical(Rule("  "));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, blank, default));
    }

    [Fact]
    public async Task Rule_WithAnUndefinedOperator_IsRejected()
    {
        var (_, _, setup) = Typical(Rule(op: (ComparisonOperator)99));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().AttachAsync(_job, setup, default));
    }

    // ---- replace file ----

    private (InputFile Current, ReconciliationRule Rule) JobWithOneAttachedFile()
    {
        var current = File("old.csv", ["Id", "Gross", "Fee"], jobId: _job.Id);
        current.Alias = "a";
        var other = File("b.csv", ["Id", "Net"], jobId: _job.Id);
        other.Alias = "b";
        var rule = new ReconciliationRule
        {
            JobId = _job.Id, Name = "R1", LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Id",
            LeftExpression = "a.Gross - a.Fee", RightExpression = "b.Net"
        };
        _store.Setup(s => s.ListActiveFilesAsync(_job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { current, other }.Where(f => f.SupersededAtUtc is null).ToList());
        _store.Setup(s => s.ListRulesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        return (current, rule);
    }

    [Fact]
    public async Task Replace_SupersedesTheOldFileAndTakesOverItsAlias()
    {
        var (current, _) = JobWithOneAttachedFile();
        var fresh = File("new.csv", ["Id", "Gross", "Fee", "Extra"]);

        await Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("A", fresh.Id), default);

        Assert.Equal(Now.UtcDateTime, current.SupersededAtUtc);
        Assert.Equal(_job.Id, fresh.JobId);
        Assert.Equal("a", fresh.Alias);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Replace_RejectsAFileMissingAColumnTheRulesNeed()
    {
        var (current, _) = JobWithOneAttachedFile();
        var fresh = File("new.csv", ["Id", "Gross"]);   // "Fee" is gone

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("a", fresh.Id), default));

        Assert.Contains("Fee", ex.Message);
        Assert.Null(current.SupersededAtUtc);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Replace_RejectsAnUnknownAlias()
    {
        JobWithOneAttachedFile();
        var fresh = File("new.csv", ["Id"]);
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("nope", fresh.Id), default));
        Assert.Contains("a, b", ex.Message);
    }

    [Fact]
    public async Task Replace_IsOwnerOrAdminOnly()
    {
        JobWithOneAttachedFile();
        _job.OwnerId = Guid.NewGuid();
        var fresh = File("new.csv", ["Id", "Gross", "Fee"]);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("a", fresh.Id), default));

        _me.SetupGet(m => m.Role).Returns(Role.Admin);
        await Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("a", fresh.Id), default);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Replace_IsRefusedWhileRunningOrAfterCancelling(JobStatus status)
    {
        JobWithOneAttachedFile();
        _job.Status = status;
        var fresh = File("new.csv", ["Id", "Gross", "Fee"]);

        await Assert.ThrowsAsync<ConflictException>(
            () => Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("a", fresh.Id), default));
    }

    [Fact]
    public async Task Replace_IsRefusedForJobsThatDoNotUseFiles()
    {
        _job.Template = new JobTemplate { IsFileReconciliation = false };
        var fresh = File("new.csv", ["Id"]);
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().ReplaceFileAsync(_job.Id, new ReplaceFileRequest("a", fresh.Id), default));
    }

    // ---- view ----

    [Fact]
    public async Task Get_ReturnsFilesWithColumnsAndRulesInOrder()
    {
        var (_, rule) = JobWithOneAttachedFile();
        rule.Position = 1;

        var view = await Sut().GetAsync(_job.Id, default);

        Assert.Equal(["a", "b"], view.Files.Select(f => f.Alias));
        Assert.Equal(["Id", "Gross", "Fee"], view.Files[0].Headers);
        Assert.Equal("R1", Assert.Single(view.Rules).Name);
    }

    [Fact]
    public async Task Get_HidesJobsOfOtherTeams()
    {
        _job.Team = Team.Technical;
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().GetAsync(_job.Id, default));
    }

    // ---- helpers used by the pipeline ----

    [Fact]
    public void RequiredColumns_CollectsKeysAndExpressionColumnsForOneAlias()
    {
        var rule = new ReconciliationRule
        {
            Name = "r", LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Ref",
            LeftExpression = "a.Gross - a.Fee", RightExpression = "b.Net"
        };

        Assert.Equal(["Fee", "Gross", "Id"], ReconciliationSetupService.RequiredColumns([rule], "a").Order());
        Assert.Equal(["Net", "Ref"], ReconciliationSetupService.RequiredColumns([rule], "b").Order());
    }

    [Fact]
    public void RuleText_DescribesTheRule()
    {
        var rule = new ReconciliationRule
        {
            Name = "r", LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Ref",
            LeftExpression = "a.Gross - a.Fee", RightExpression = "b.Net", Tolerance = 0.05m
        };

        Assert.Equal("a.Gross - a.Fee == b.Net, joined a.Id = b.Ref, tolerance 0.05", RuleText.Describe(rule));
        rule.ToleranceMode = ToleranceMode.Percent;
        Assert.EndsWith("tolerance 0.05%", RuleText.Describe(rule));
        rule.Operator = ComparisonOperator.GreaterThan;
        Assert.DoesNotContain("tolerance", RuleText.Describe(rule));
    }
}
