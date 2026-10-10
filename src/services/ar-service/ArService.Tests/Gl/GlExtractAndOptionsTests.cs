using System.Security.Cryptography;
using System.Text;
using ArService.Gl;

namespace ArService.Tests.Gl;

/// <summary>The ERP extract (deterministic, control totals) and the startup validation of account mappings.</summary>
public class GlExtractAndOptionsTests
{
    private static async Task<List<GlJournalEntry>> PostedJournalAsync()
    {
        var gl = new InMemoryGl();
        GlFixtures.SeedChart(gl);
        var service = GlFixtures.Service(gl, GlFixtures.Options(), new GlClock());
        await service.IngestAsync(GlFixtures.RunExecuted("run-1"), GlFixtures.Tenant);
        await service.IngestAsync(GlFixtures.RunExecuted("run-2", executedAt: new DateTime(2026, 5, 3, 8, 0, 0, DateTimeKind.Utc)), GlFixtures.Tenant);
        await service.IngestAsync(GlFixtures.FileTransmitted("run-1"), GlFixtures.Tenant);
        await service.IngestAsync(GlFixtures.RunExecuted("run-3", executedAt: new DateTime(2026, 4, 30, 8, 0, 0, DateTimeKind.Utc)), GlFixtures.Tenant);
        return gl.Entries.ToList();
    }

    [Fact]
    public async Task The_extract_is_deterministic_whatever_order_the_entries_come_in()
    {
        var entries = await PostedJournalAsync();

        var a = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", entries);
        var b = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", Enumerable.Reverse(entries).ToList());

        a.Csv.Should().Be(b.Csv);
        a.Control.Sha256.Should().Be(b.Control.Sha256);
        // Ordered by entry date: run-2 (May 3) before run-1 (May 20) and its transmission.
        a.Rows.Select(r => r.EntryId).Distinct().Should().Equal("tenant-1:accrual:run-2", "tenant-1:accrual:run-1", "tenant-1:ach:run-1");
    }

    [Fact]
    public async Task Control_totals_count_the_period_only_and_hash_the_rows()
    {
        var entries = await PostedJournalAsync();

        var extract = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", entries);

        extract.Control.EntryCount.Should().Be(3);         // run-3 is April
        extract.Control.LineCount.Should().Be(3 + 3 + 2);
        extract.Control.TotalDebit.Should().Be(170m + 170m + 100m);
        extract.Control.TotalCredit.Should().Be(extract.Control.TotalDebit);
        var lines = extract.Csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be(GlExtractBuilder.Header);
        lines.Should().HaveCount(1 + 8 + 1);
        lines[^1].Should().Be($"CONTROL,2026-05,3,8,440.00,440.00,{extract.Control.Sha256}");
        var body = string.Join("", lines[..^1].Select(l => l + "\n"));
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant().Should().Be(extract.Control.Sha256);
        lines[1].Should().Contain(",5100,170.00,0.00,");
    }

    [Fact]
    public void An_empty_period_still_has_a_control_row()
    {
        var extract = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-01", Array.Empty<GlJournalEntry>());

        extract.Control.EntryCount.Should().Be(0);
        extract.Csv.Should().EndWith($"CONTROL,2026-01,0,0,0.00,0.00,{extract.Control.Sha256}\n");
    }

    [Fact]
    public void Fields_are_quoted_and_formula_characters_neutralised()
    {
        var entry = new GlJournalEntry
        {
            Id = "e1", TenantId = GlFixtures.Tenant, Period = "2026-05", EntryDate = new DateTime(2026, 5, 1), SourceReference = "=HYPERLINK(\"x\")",
            Lines =
            [
                new GlJournalLine { LineNumber = 1, AccountId = "a", AccountNumber = "5100", Debit = 1m, Memo = "a, \"b\"" },
                new GlJournalLine { LineNumber = 2, AccountId = "b", AccountNumber = "2100", Credit = 1m, Memo = "+1" },
            ],
            TotalDebit = 1m, TotalCredit = 1m,
        };

        var csv = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", [entry]).Csv;

        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"").And.Contain("\"a, \"\"b\"\"\"").And.Contain(",'+1\n");
    }

    [Fact]
    public void Another_tenants_entries_never_appear()
    {
        var foreign = new GlJournalEntry { Id = "x", TenantId = "tenant-2", Period = "2026-05", Lines = [] };

        GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", [foreign]).Control.EntryCount.Should().Be(0);
    }

    private static ValidateOutcome Validate(Dictionary<string, string> accounts)
    {
        var result = new GlPostingOptionsValidator().Validate(null, new GlPostingOptions
        {
            Enabled = true,
            Tenants = { ["tenant-1"] = new GlTenantPostingOptions { Accounts = accounts } },
        });
        return new ValidateOutcome(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    private sealed record ValidateOutcome(bool Ok, string Failures);

    [Fact]
    public void A_valid_mapping_passes_startup_validation()
        => Validate(new() { ["ClaimsExpense"] = "5100", ["ClaimsPayable"] = "2100", ["AchInTransit"] = "1010", ["ProviderReceivable"] = "1250", ["Cash"] = "1010" })
            .Ok.Should().BeTrue();

    [Theory]
    [InlineData("ClaimsExpenses", "5100", "not a posting role")]
    [InlineData("ClaimsExpense", "", "must be a GL account number")]
    [InlineData("ClaimsExpense", "51 00", "must be a GL account number")]
    [InlineData("ClaimsExpense", "2100", "opposite sides")]   // same as ClaimsPayable
    public void A_bad_mapping_fails_startup(string role, string account, string message)
    {
        var accounts = new Dictionary<string, string> { ["ClaimsPayable"] = "2100" };
        accounts[role] = account;

        var outcome = Validate(accounts);

        outcome.Ok.Should().BeFalse();
        outcome.Failures.Should().Contain(message);
    }
}
