using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Duplicate-file detection (distinct file ID modifiers for same-day files of a
/// tenant, kept on rebuild) and the EFT file write touching only its own fields.
/// </summary>
public class FfsEftFileIntegrityTests
{
    private const string Npi = "1111111111";

    private static FfsRunHarness Harness()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        return h;
    }

    private static char ModifierOf(FfsNachaBuiltFile file) => file.Content.Split('\n')[0][33];

    [Fact]
    public async Task Two_runs_completed_in_the_same_minute_get_distinct_file_id_modifiers_that_survive_rebuilds()
    {
        var h = Harness();
        var first = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));
        var second = await h.ExecuteRunAsync(FfsRunHarness.Claim("c2", Npi, 50m));
        var minute = new DateTime(2026, 5, 4, 14, 7, 0, DateTimeKind.Utc);
        h.Runs.Mutate(first.Id, r => r.ExecutionCompletedAt = minute);
        h.Runs.Mutate(second.Id, r => r.ExecutionCompletedAt = minute.AddSeconds(30));

        var a = await h.EftFiles().GenerateAsync(first.Id, "approver-2");
        var b = await h.EftFiles().GenerateAsync(second.Id, "approver-2");

        // Same destination, origin, creation date and time: only the modifier tells them apart.
        Assert.Equal(a.File!.Content.Split('\n')[0][..33], b.File!.Content.Split('\n')[0][..33]);
        Assert.Equal(('A', 'B'), (ModifierOf(a.File), ModifierOf(b.File)));
        Assert.Equal(("A", "B"), (a.Run.EftFile!.FileIdModifier, b.Run.EftFile!.FileIdModifier));

        // Rebuilding keeps each run's modifier, byte for byte.
        var again = await h.EftFiles().GenerateAsync(second.Id, "approver-3");
        Assert.True(again.Reproduced);
        Assert.Equal(b.File.Content, again.File!.Content);
        Assert.Equal('A', ModifierOf((await h.EftFiles().GenerateAsync(first.Id, "approver-3")).File!));
    }

    [Fact]
    public async Task A_run_pinned_before_modifiers_were_allocated_rebuilds_with_the_configured_one()
    {
        var h = Harness();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));
        var pinned = await h.EftFiles().GenerateAsync(run.Id, "approver-2");
        h.Runs.Mutate(run.Id, r => r.EftFile!.FileIdModifier = null); // a legacy pin, made with "A"

        var again = await h.EftFiles().GenerateAsync(run.Id, "approver-3");

        Assert.True(again.Reproduced);
        Assert.Equal(pinned.File!.Content, again.File!.Content);
    }

    [Fact]
    public async Task Generating_the_file_never_overwrites_a_concurrent_write_to_the_run()
    {
        var h = Harness();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));
        // Between the file service's read and its write, someone else writes the run.
        h.Runs.AfterGet = () =>
        {
            h.Runs.AfterGet = null;
            h.Runs.Mutate(run.Id, r => { r.Warnings.Add("concurrent finalize retry"); r.PendingFinalizeClaimIds.Add("c9"); });
            return Task.CompletedTask;
        };

        await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        var stored = (await h.Runs.GetByIdAsync(run.Id))!;
        Assert.NotNull(stored.EftFile);
        Assert.Contains("concurrent finalize retry", stored.Warnings);
        Assert.Contains("c9", stored.PendingFinalizeClaimIds);

        // A verification also writes only the file fields.
        h.Runs.Mutate(run.Id, r => r.Warnings.Add("second concurrent write"));
        await h.EftFiles().GenerateAsync(run.Id, "approver-3");
        stored = (await h.Runs.GetByIdAsync(run.Id))!;
        Assert.Contains("second concurrent write", stored.Warnings);
        Assert.Equal(2, stored.EftFile!.GenerationCount);
    }

    [Fact]
    public async Task A_different_file_pinned_meanwhile_is_a_conflict_and_nothing_is_overwritten()
    {
        var h = Harness();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));
        h.Runs.AfterGet = () =>
        {
            h.Runs.AfterGet = null;
            h.Runs.Mutate(run.Id, r => r.EftFile = new PaymentRunEftFile { Sha256 = new string('b', 64), FileReference = "FFS-X" });
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<RunConflictException>(() => h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        Assert.Equal(new string('b', 64), (await h.Runs.GetByIdAsync(run.Id))!.EftFile!.Sha256);
    }
}
