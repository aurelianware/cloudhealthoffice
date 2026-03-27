using ArService.Models;
using ArService.Controllers;

namespace ArService.Tests.Models;

public class GlSegmentCodesTests
{
    [Fact]
    public void ToQualifiedString_AllSegmentsPopulated_ReturnsHyphenSeparatedString()
    {
        var segments = new GlSegmentCodes
        {
            Company = "01",
            Fund = "GEN",
            Department = "ADMIN",
            Program = "HMO",
            Account = "4010",
            SubAccount = "00"
        };

        segments.ToQualifiedString().Should().Be("01-GEN-ADMIN-HMO-4010-00");
    }

    [Fact]
    public void ToQualifiedString_EmptySegments_ReturnsHyphensOnly()
    {
        var segments = new GlSegmentCodes();

        segments.ToQualifiedString().Should().Be("-----");
    }

    [Fact]
    public void ToQualifiedString_PartialSegments_HandlesCorrectly()
    {
        var segments = new GlSegmentCodes
        {
            Company = "02",
            Account = "5020"
        };

        segments.ToQualifiedString().Should().Be("02----5020-");
    }
}

public class AgingSummaryTests
{
    [Fact]
    public void TotalOutstanding_IsAliasForTotal()
    {
        var summary = new AgingSummary
        {
            Current = 1000m,
            Days31To60 = 500m,
            Days61To90 = 250m,
            Days91To120 = 100m,
            Over120Days = 50m,
            Total = 1900m
        };

        summary.TotalOutstanding.Should().Be(summary.Total);
    }

    [Fact]
    public void TotalOutstanding_WhenTotalChanges_ReflectsNewValue()
    {
        var summary = new AgingSummary { Total = 5000m };

        summary.TotalOutstanding.Should().Be(5000m);

        summary.Total = 7500m;
        summary.TotalOutstanding.Should().Be(7500m);
    }

    [Fact]
    public void TotalOutstanding_DefaultsToZero()
    {
        var summary = new AgingSummary();

        summary.TotalOutstanding.Should().Be(0m);
    }
}

public class ArPostingEntryTests
{
    [Fact]
    public void NewEntry_GeneratesUniqueEntryId()
    {
        var entry1 = new ArPostingEntry();
        var entry2 = new ArPostingEntry();

        entry1.EntryId.Should().NotBeNullOrEmpty();
        entry2.EntryId.Should().NotBeNullOrEmpty();
        entry1.EntryId.Should().NotBe(entry2.EntryId);
    }

    [Fact]
    public void NewEntry_DefaultsPostedAtToUtcNow()
    {
        var before = DateTime.UtcNow;
        var entry = new ArPostingEntry();

        entry.PostedAt.Should().BeOnOrAfter(before);
        entry.PostedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }
}

public class ModelDefaultsTests
{
    [Fact]
    public void GlAccount_NewInstance_HasDefaultId()
    {
        var account = new GlAccount();

        account.Id.Should().NotBeNullOrEmpty();
        Guid.TryParse(account.Id, out _).Should().BeTrue();
    }

    [Fact]
    public void GlAccount_NewInstance_DefaultStatusIsActive()
    {
        var account = new GlAccount();

        account.Status.Should().Be(GlAccountStatus.Active);
    }

    [Fact]
    public void ArBalance_NewInstance_HasEmptyPostingEntries()
    {
        var balance = new ArBalance();

        balance.PostingEntries.Should().NotBeNull();
        balance.PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public void CashPosting_NewInstance_DefaultStatusIsPending()
    {
        var posting = new CashPosting();

        posting.Status.Should().Be(CashPostingStatus.Pending);
    }

    [Fact]
    public void CashPosting_NewInstance_HasEmptyApplications()
    {
        var posting = new CashPosting();

        posting.Applications.Should().NotBeNull();
        posting.Applications.Should().BeEmpty();
    }

    [Fact]
    public void ArAdjustment_NewInstance_DefaultStatusIsPending()
    {
        var adjustment = new ArAdjustment();

        adjustment.Status.Should().Be(ArAdjustmentStatus.Pending);
    }

    [Fact]
    public void ArBatchRule_NewInstance_DefaultStatusIsActive()
    {
        var rule = new ArBatchRule();

        rule.Status.Should().Be(BatchRuleStatus.Active);
    }

    [Fact]
    public void GlAccount_NewInstance_HasEmptyCollections()
    {
        var account = new GlAccount();

        account.LineOfBusinessMapping.Should().NotBeNull().And.BeEmpty();
        account.BatchRuleIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ArBatchRule_NewInstance_HasEmptyCollections()
    {
        var rule = new ArBatchRule();

        rule.ApplicableLobs.Should().NotBeNull().And.BeEmpty();
        rule.ApplicablePlanIds.Should().NotBeNull().And.BeEmpty();
    }
}
