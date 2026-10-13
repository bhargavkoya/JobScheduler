using System.Text.Json;
using JobScheduler.Application.Common;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Jobs;
using JobScheduler.Infrastructure.Persistence;
using JobScheduler.Infrastructure.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;

namespace JobScheduler.Tests.Reconciliation;

/// <summary>Runs only when JOBSCHEDULER_TEST_PG holds a Postgres connection string; otherwise reported as skipped.</summary>
public sealed class FactIfPostgresAttribute : FactAttribute
{
    public const string EnvVar = "JOBSCHEDULER_TEST_PG";

    public FactIfPostgresAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} to a Postgres connection string to run the database tests.";
    }
}

/// <summary>Creates a throwaway database on the given server, migrates it from scratch and drops it afterwards.</summary>
public sealed class ScratchDatabase : IAsyncLifetime
{
    private string? _connectionString;

    public JobSchedulerDbContext NewContext() => new(
        new DbContextOptionsBuilder<JobSchedulerDbContext>().UseNpgsql(_connectionString).Options);

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(FactIfPostgresAttribute.EnvVar);
        if (string.IsNullOrWhiteSpace(server)) return;

        _connectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = "jobscheduler_it_" + Guid.NewGuid().ToString("N")[..12],
            Pooling = false
        }.ConnectionString;

        await using var db = NewContext();
        await db.Database.MigrateAsync();   // proves every migration, including Phase 8, applies to an empty database
    }

    public async Task DisposeAsync()
    {
        if (_connectionString is null) return;
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}

public class PostgresReconciliationTests(ScratchDatabase scratch) : IClassFixture<ScratchDatabase>
{
    private readonly Guid _userId = Guid.NewGuid();

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    private async Task<(Guid JobId, Guid RunId)> SeedJobAsync(bool fileTemplate = true)
    {
        await using var db = scratch.NewContext();
        db.Users.Add(new User { Id = _userId, Email = $"{Guid.NewGuid():N}@x.test", PasswordHash = "x" });
        var template = new JobTemplate
        {
            Name = "T " + Guid.NewGuid().ToString("N"), IsApproved = true, IsFileReconciliation = fileTemplate,
            SupportedScheduleTypes = [ScheduleType.Manual]
        };
        var job = new Job { Template = template, TemplateId = template.Id, Name = "recon", OwnerId = _userId, Team = Team.Business };
        var run = new JobRun { JobId = job.Id, IdempotencyKey = Guid.NewGuid().ToString("N") };
        db.JobTemplates.Add(template);
        db.Jobs.Add(job);
        db.JobRuns.Add(run);
        await db.SaveChangesAsync();
        return (job.Id, run.Id);
    }

    private Mock<ICurrentUser> Me()
    {
        var me = new Mock<ICurrentUser>();
        me.SetupGet(m => m.UserId).Returns(_userId);
        me.SetupGet(m => m.Role).Returns(Role.Employee);
        me.SetupGet(m => m.Team).Returns(Team.Business);
        me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        return me;
    }

    [FactIfPostgres]
    public async Task TemplateSeeder_AddsTheFileReconciliationTemplate_OnAFreshMigratedDatabase()
    {
        await using var db = scratch.NewContext();
        await new TemplateSeeder(db).SeedAsync(default);

        var template = await db.JobTemplates.SingleAsync(t => t.Name == "File Reconciliation");
        Assert.True(template.IsFileReconciliation);
        Assert.True(template.IsApproved);
        Assert.Contains(ScheduleType.Manual, template.SupportedScheduleTypes);
    }

    [FactIfPostgres]
    public async Task Files_RoundTripBytesAndColumns()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x00, 0x01, 0xFF, 0x7F, (byte)'\r', (byte)'\n' };
        var file = new InputFile
        {
            OwnerId = _userId, Alias = "a", FileName = "a.csv", SizeBytes = bytes.Length, Sha256 = new string('a', 64),
            HeadersJson = JsonSerializer.Serialize(new[] { "Id", "Net Amount" }), RowCount = 7
        };

        await using (var db = scratch.NewContext())
        {
            await SeedUserOnlyAsync(db);
            new EfReconciliationStore(db).AddFile(file, bytes);
            await db.SaveChangesAsync();
        }

        await using var read = scratch.NewContext();
        var store = new EfReconciliationStore(read);
        var found = Assert.Single(await store.FindFilesAsync([file.Id], default));
        Assert.Equal(["Id", "Net Amount"], found.HeaderList());
        Assert.Equal(7, found.RowCount);
        Assert.Equal(bytes, await store.GetContentAsync(file.Id, default));
        Assert.Null(await store.GetContentAsync(Guid.NewGuid(), default));
    }

    private async Task SeedUserOnlyAsync(JobSchedulerDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Id == _userId))
        {
            db.Users.Add(new User { Id = _userId, Email = $"{Guid.NewGuid():N}@x.test", PasswordHash = "x" });
            await db.SaveChangesAsync();
        }
    }

    [FactIfPostgres]
    public async Task ReplaceResults_SwapsTheRunsResults_InsteadOfAddingToThem()
    {
        var (jobId, runId) = await SeedJobAsync();
        var rule = new ReconciliationRule
        {
            JobId = jobId, Name = "R", LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Id",
            LeftExpression = "a.X", RightExpression = "b.X"
        };
        await using (var db = scratch.NewContext())
        {
            db.ReconciliationRules.Add(rule);
            await db.SaveChangesAsync();
        }

        ReconciliationResult Row(int i) => new()
        {
            RunId = runId, RuleId = rule.Id, RuleName = "R", RecordKey = "K" + i, LeftRow = i, RightRow = i,
            LeftValue = 1234567.891m, RightValue = 1234567.89m, Difference = 0.001m,
            Status = i % 4 == 0 ? RecordStatus.Fail : RecordStatus.Pass, Reason = i % 4 == 0 ? "bad" : ""
        };

        for (var attempt = 1; attempt <= 2; attempt++)   // the second pass is a retried run
        {
            await using var db = scratch.NewContext();
            await new EfReconciliationStore(db).ReplaceResultsAsync(runId, Enumerable.Range(1, 3000).Select(Row).ToList(), default);
        }

        await using var read = scratch.NewContext();
        var store = new EfReconciliationStore(read);
        Assert.Equal(3000, await store.CountAsync(runId, null, null, default));
        Assert.Equal(750, await store.CountAsync(runId, RecordStatus.Fail, null, default));

        var count = Assert.Single(await store.CountByRuleAsync(runId, default));
        Assert.Equal(("R", 3000, 2250), (count.RuleName, count.Total, count.Passed));

        var page = await store.QueryAsync(runId, RecordStatus.Fail, rule.Id, skip: 10, take: 5, default);
        Assert.Equal(["K44", "K48", "K52", "K56", "K60"], page.Select(r => r.RecordKey));
        Assert.All(page, r => Assert.Equal(RecordStatus.Fail, r.Status));
        Assert.Equal(0.001m, page[0].Difference);
        Assert.Equal(1234567.891m, page[0].LeftValue);
    }

    [FactIfPostgres]
    public async Task ReplaceResults_ForOneRun_LeavesOtherRunsAlone()
    {
        var (jobId, runA) = await SeedJobAsync();
        Guid runB;
        var rule = new ReconciliationRule { JobId = jobId, Name = "R", LeftAlias = "a", RightAlias = "a", LeftExpression = "a.X", RightExpression = "a.X" };
        await using (var db = scratch.NewContext())
        {
            var other = new JobRun { JobId = jobId, IdempotencyKey = Guid.NewGuid().ToString("N") };
            runB = other.Id;
            db.JobRuns.Add(other);
            db.ReconciliationRules.Add(rule);
            await db.SaveChangesAsync();
        }

        ReconciliationResult Row(Guid run) => new() { RunId = run, RuleId = rule.Id, RuleName = "R", RecordKey = "k", Status = RecordStatus.Pass };
        await using (var db = scratch.NewContext())
        {
            var store = new EfReconciliationStore(db);
            await store.ReplaceResultsAsync(runA, [Row(runA), Row(runA)], default);
            await store.ReplaceResultsAsync(runB, [Row(runB)], default);
            await store.ReplaceResultsAsync(runA, [Row(runA)], default);
        }

        await using var read = scratch.NewContext();
        var reader = new EfReconciliationStore(read);
        Assert.Equal(1, await reader.CountAsync(runA, null, null, default));
        Assert.Equal(1, await reader.CountAsync(runB, null, null, default));
    }

    [FactIfPostgres]
    public async Task SampleFiles_Upload_Attach_Run_AndRerun_EndToEndAgainstPostgres()
    {
        var (jobId, runId) = await SeedJobAsync();
        var me = Me();

        // 1. Upload the three sample files, as the API would.
        IReadOnlyList<InputFileDto> uploaded;
        await using (var db = scratch.NewContext())
        {
            uploaded = await new InputFileService(new EfReconciliationStore(db), me.Object, TimeProvider.System).UploadAsync(
            [
                new FileUpload("1_internal_trades.csv", File.ReadAllBytes(Sample("1_internal_trades.csv"))),
                new FileUpload("2_custodian_settlements.csv", File.ReadAllBytes(Sample("2_custodian_settlements.csv"))),
                new FileUpload("3_gl_cash_postings.csv", File.ReadAllBytes(Sample("3_gl_cash_postings.csv")))
            ], default);
        }

        Assert.Equal(["internal_trades", "custodian_settlements", "gl_cash_postings"], uploaded.Select(u => u.Alias));
        Assert.Equal([60, 59, 60], uploaded.Select(u => u.RowCount));
        Assert.Equal(13, uploaded[0].Headers.Count);

        // 2. Attach them to the job with two rules, validated against the real headers.
        var setup = new ReconciliationSetupDto(
            [new AttachFileDto(uploaded[0].Id, "trades"), new AttachFileDto(uploaded[1].Id, "custodian"), new AttachFileDto(uploaded[2].Id, "ledger")],
            [
                new ReconciliationRuleDto(null, "Cash vs custodian", "trades", "custodian", "TradeId", "ClientTradeRef",
                    "trades.GrossCashAmount - trades.Commission - trades.Tax", ComparisonOperator.Equal, "custodian.CashAmount"),
                new ReconciliationRuleDto(null, "Custodian vs ledger", "custodian", "ledger", "ClientTradeRef", "TradeId",
                    "custodian.CashAmount", ComparisonOperator.Equal, "ledger.DebitAmount - ledger.CreditAmount", 0.05m)
            ]);
        await using (var db = scratch.NewContext())
        {
            var job = await new EfJobStore(db).FindAsync(jobId, default);
            await new ReconciliationSetupService(new EfReconciliationStore(db), new EfJobStore(db), me.Object, TimeProvider.System)
                .AttachAsync(job!, setup, default);
            await db.SaveChangesAsync();
        }

        // 3. Run the pipeline steps twice (the second is a retry of the same run).
        for (var pass = 1; pass <= 2; pass++)
        {
            await using var db = scratch.NewContext();
            var job = await new EfJobStore(db).FindAsync(jobId, default);
            var run = await db.JobRuns.FindAsync(runId);
            var runner = new ReconciliationRunner(new EfReconciliationStore(db));
            var snapshot = await runner.SnapshotInputsAsync(job!, default);
            var result = await runner.RunAsync(job!, run!, snapshot, default);
            Assert.Equal(10m, result.Values["Cash vs custodian"]);
            Assert.Equal(15m, result.Values["Custodian vs ledger"]);
        }

        // 4. The stored per-record results match the documented sample outcome and were not doubled by the rerun.
        await using var read = scratch.NewContext();
        var store = new EfReconciliationStore(read);
        var counts = (await store.CountByRuleAsync(runId, default)).ToDictionary(c => c.RuleName);
        Assert.Equal((62, 52), (counts["Cash vs custodian"].Total, counts["Cash vs custodian"].Passed));
        // 59 custodian records + 4 ledger-only records; 15 fail (see docs/samples/README.md).
        Assert.Equal((63, 48), (counts["Custodian vs ledger"].Total, counts["Custodian vs ledger"].Passed));

        var failures = await store.QueryAsync(runId, RecordStatus.Fail, null, 0, 1000, default);
        var big = failures.Single(f => f.RuleName == "Cash vs custodian" && f.RecordKey == "T-100026");
        Assert.Equal(1200.00m, big.Difference);
        Assert.Contains("exceeds tolerance", big.Reason);
        Assert.Contains(failures, f => f.RecordKey == "T-100020" && f.Reason.StartsWith("Duplicate"));

        // 5. Replace the ledger file with an updated extract: same alias, old file superseded, still one active per alias.
        IReadOnlyList<InputFileDto> fresh;
        await using (var db = scratch.NewContext())
            fresh = await new InputFileService(new EfReconciliationStore(db), me.Object, TimeProvider.System)
                .UploadAsync([new FileUpload("ledger_v2.csv", File.ReadAllBytes(Sample("3_gl_cash_postings.csv")))], default);
        await using (var db = scratch.NewContext())
        {
            var view = await new ReconciliationSetupService(new EfReconciliationStore(db), new EfJobStore(db), me.Object, TimeProvider.System)
                .ReplaceFileAsync(jobId, new ReplaceFileRequest("ledger", fresh[0].Id), default);
            Assert.Equal(["custodian", "ledger", "trades"], view.Files.Select(f => f.Alias).Order());
            Assert.Equal("ledger_v2.csv", view.Files.Single(f => f.Alias == "ledger").FileName);
        }

        await using var after = scratch.NewContext();
        Assert.Equal(1, await after.InputFiles.CountAsync(f => f.JobId == jobId && f.Alias == "ledger" && f.SupersededAtUtc == null));
        Assert.Equal(1, await after.InputFiles.CountAsync(f => f.JobId == jobId && f.Alias == "ledger" && f.SupersededAtUtc != null));
    }
}
