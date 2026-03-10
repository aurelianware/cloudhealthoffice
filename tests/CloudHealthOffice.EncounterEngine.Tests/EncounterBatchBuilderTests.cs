using CloudHealthOffice.EncounterEngine.Domain;
using CloudHealthOffice.EncounterEngine.Services;
using Xunit;

namespace CloudHealthOffice.EncounterEngine.Tests;

public class EncounterBatchBuilderTests
{
    private static EncounterBatchBuilder MakeBatchBuilder() => new();
    private static EncounterTransformer MakeTransformer() => new();

    private static BatchEnvelope DefaultEnvelope(string tenant = "TENANT1") => new()
    {
        SenderId                = "PLAN001",
        ReceiverId              = "CMS001",
        ApplicationSenderId     = "PLAN001APP",
        ApplicationReceiverId   = "CMS001APP",
        TenantId                = tenant,
        InterchangeControlNumber = "000000001",
        GroupControlNumber      = "1"
    };

    private static EncounterRecord MakeRecord(string claimId = "CLM001") =>
        MakeTransformer().Transform(new EncounterInput
        {
            ClaimId         = claimId,
            TenantId        = "TENANT1",
            FormType        = ClaimFormType.Professional,
            ServiceDate     = new DateOnly(2026, 1, 15),
            PlaceOfService  = "11",
            MemberId        = "MEM001",
            SubscriberId    = "SUB001",
            MemberFirstName = "Jane",
            MemberLastName  = "Doe",
            MemberDateOfBirth = new DateOnly(1980, 6, 15),
            MemberGender    = "F",
            BillingNpi      = "1234567890",
            BillingProviderName = "ACME MEDICAL GROUP",
            BillingTaxId    = "123456789",
            PlanSubmitterId = "PLAN001",
            ReceiverSubmitterId = "CMS001",
            PlanName        = "Test Health Plan",
            PlanPayerId     = "88888",
            DiagnosisCodes  = ["Z00.00"],
            Lines =
            [
                new EncounterLineInput
                {
                    LineNumber = 1, ProcedureCode = "99213",
                    BilledAmount = 200m, AllowedAmount = 160m,
                    PlanPaidAmount = 128m, MemberResponsibility = 32m,
                    Units = 1
                }
            ]
        });

    private static List<string> Segments(string rawX12) =>
        rawX12.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    // ── Envelope structure ────────────────────────────────────────────────

    [Fact]
    public void Build_StartsWithIsaSegment()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var segs = Segments(batch.RawX12);
        Assert.StartsWith("ISA*", segs[0]);
    }

    [Fact]
    public void Build_EndsWithIeaSegment()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var segs = Segments(batch.RawX12);
        Assert.StartsWith("IEA*", segs[^1]);
    }

    [Fact]
    public void Build_HasGsAndGeSegments()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        Assert.Contains("GS*HC*", batch.RawX12);
        var segs = Segments(batch.RawX12);
        Assert.Contains(segs, s => s.StartsWith("GE*"));
    }

    [Fact]
    public void Build_IsaLength_Is106Chars()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var isa = Segments(batch.RawX12)[0];
        // ISA is 105 characters without segment terminator (106 with ~)
        Assert.Equal(105, isa.Length);
    }

    [Fact]
    public void Build_GsContainsApplicationSenderAndReceiver()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var gs = Segments(batch.RawX12).First(s => s.StartsWith("GS*"));
        Assert.Contains("PLAN001APP", gs);
        Assert.Contains("CMS001APP", gs);
    }

    // ── IEA / GE count correctness ────────────────────────────────────────

    [Fact]
    public void Build_IeaGroupCount_IsOne()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var iea = Segments(batch.RawX12).First(s => s.StartsWith("IEA*"));
        Assert.StartsWith("IEA*1*", iea);
    }

    [Fact]
    public void Build_GeTransactionCount_MatchesEncounterCount()
    {
        var records = new[] { MakeRecord("CLM001"), MakeRecord("CLM002") };
        var batch = MakeBatchBuilder().Build(records, DefaultEnvelope());
        var ge = Segments(batch.RawX12).First(s => s.StartsWith("GE*"));
        var parts = ge.Split('*');
        Assert.Equal("2", parts[1]);
    }

    [Fact]
    public void Build_InterchangeControlNumber_PaddedTo9()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var isa = Segments(batch.RawX12)[0];
        // ISA13 is a 9-digit field
        Assert.Contains("000000001", isa);
    }

    // ── Batch metadata ────────────────────────────────────────────────────

    [Fact]
    public void Build_TransactionCount_Matches()
    {
        var records = new[] { MakeRecord("CLM001"), MakeRecord("CLM002"), MakeRecord("CLM003") };
        var batch = MakeBatchBuilder().Build(records, DefaultEnvelope());
        Assert.Equal(3, batch.TransactionCount);
    }

    [Fact]
    public void Build_EncounterControlNumbers_AllPresent()
    {
        var r1 = MakeRecord("CLM001");
        var r2 = MakeRecord("CLM002");
        var batch = MakeBatchBuilder().Build([r1, r2], DefaultEnvelope());
        Assert.Contains(r1.EncounterControlNumber, batch.EncounterControlNumbers);
        Assert.Contains(r2.EncounterControlNumber, batch.EncounterControlNumbers);
    }

    [Fact]
    public void Build_TenantId_IsFromEnvelope()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope("MCO99"));
        Assert.Equal("MCO99", batch.TenantId);
    }

    [Fact]
    public void Build_BatchId_IsNonEmpty()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        Assert.False(string.IsNullOrWhiteSpace(batch.BatchId));
    }

    [Fact]
    public void Build_TwoBatches_HaveDifferentBatchIds()
    {
        var b1 = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        var b2 = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        Assert.NotEqual(b1.BatchId, b2.BatchId);
    }

    // ── ST/SE embedded correctly ───────────────────────────────────────────

    [Fact]
    public void Build_ContainsStAndSeSegmentsFromEncounters()
    {
        var batch = MakeBatchBuilder().Build([MakeRecord()], DefaultEnvelope());
        Assert.Contains("ST*837*", batch.RawX12);
        Assert.Contains("SE*", batch.RawX12);
    }

    [Fact]
    public void Build_TwoEncounters_ContainsBothStSegments()
    {
        var records = new[] { MakeRecord("CLM001"), MakeRecord("CLM002") };
        var batch = MakeBatchBuilder().Build(records, DefaultEnvelope());
        var stCount = Segments(batch.RawX12).Count(s => s.StartsWith("ST*"));
        Assert.Equal(2, stCount);
    }

    // ── Error handling ─────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            MakeBatchBuilder().Build([], DefaultEnvelope()));
    }
}
