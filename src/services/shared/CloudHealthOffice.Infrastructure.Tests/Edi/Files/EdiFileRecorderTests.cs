using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.Files;

/// <summary>
/// The EDI file record (edi-operations.md §3) written for every received
/// file: accepted, envelope-rejected, duplicate and unreadable files, with
/// their control numbers, and the outcome of processing.
/// </summary>
public class EdiFileRecorderTests
{
    private readonly InMemoryX12InterchangeStore _interchanges = new();
    private readonly InMemoryEdiFileStore _files = new();

    private X12InterchangeIntake Intake() =>
        new(_interchanges, Options.Create(new X12InterchangeOptions()), NullLogger<X12InterchangeIntake>.Instance);

    private EdiFileRecorder Recorder() => new(_files, NullLogger<EdiFileRecorder>.Instance);

    private async Task<EdiFile> ReceiveAsync(string content, string source = "Upload", string? path = null)
    {
        var envelope = await Intake().ReceiveAsync(new InterchangeIntakeRequest { TenantId = "t1", Content = content, TransactionType = "837", FileName = "f.edi" });
        return (await Recorder().RecordReceivedAsync(new EdiFileIntake
        {
            TenantId = "t1", Content = content, TransactionType = "837", FileName = "f.edi", Source = source, SourcePath = path, ReceivedBy = "user-1",
        }, envelope))!;
    }

    [Fact]
    public async Task AcceptedFile_RecordsEnvelopeIdentifiersAndControlNumbers()
    {
        var content = Interchange(f => f[13] = "1");
        var file = await ReceiveAsync(content, "sftp", "/inbound/claims/f.edi");

        file.TenantId.Should().Be("t1");
        file.Direction.Should().Be(EdiFileDirection.Inbound);
        file.Source.Should().Be(EdiFileSource.Sftp);
        file.SourcePath.Should().Be("/inbound/claims/f.edi");
        file.ReceivedBy.Should().Be("user-1");
        file.SenderId.Should().Be("SUBMITTER01");
        file.ReceiverId.Should().Be("CHO");
        file.Version.Should().Be("00501");
        file.UsageIndicator.Should().Be("T");
        file.InterchangeControlNumbers.Should().Equal("000000101");
        file.GroupControlNumbers.Should().Equal("101");
        file.TransactionSetControlNumbers.Should().Equal("0001");
        file.TransactionSetIds.Should().Equal("837");
        file.SizeBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(content));
        file.Sha256.Should().HaveLength(64);
        file.RawDocumentId.Should().BeNull();
        file.Ta1AckCode.Should().Be(Ta1AckCodes.Accepted, "ISA14=1 asked for a TA1");
        file.Ta1ControlNumber.Should().NotBeNull();
        file.Status.Should().Be(EdiFileStatus.Acknowledged);
        file.History.Select(h => h.Status).Should().Equal(EdiFileStatus.Received, EdiFileStatus.EnvelopeChecked, EdiFileStatus.Acknowledged);
        (await _files.GetAsync("t1", file.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task RejectedEnvelope_IsRecordedAsRejectedWithTheReason()
    {
        var file = await ReceiveAsync(Interchange(iea: "IEA*1*000000999"));

        file.Status.Should().Be(EdiFileStatus.Rejected);
        file.Ta1AckCode.Should().Be(Ta1AckCodes.Rejected);
        file.Errors.Should().ContainSingle().Which.Should().Contain("000000101");
    }

    [Fact]
    public async Task DuplicateIsa13_IsRecordedAsDuplicate()
    {
        await ReceiveAsync(Interchange());
        var second = await ReceiveAsync(Interchange());

        second.Status.Should().Be(EdiFileStatus.Duplicate);
        second.Errors.Should().ContainSingle().Which.Should().Contain("025");
        (await _files.ListAsync("t1", new EdiFileQuery())).Should().HaveCount(2, "the duplicate is a file of its own");
    }

    [Fact]
    public async Task UnreadableFile_IsRecordedAsRejected()
    {
        var file = await ReceiveAsync("this is not x12");
        file.Status.Should().Be(EdiFileStatus.Rejected);
        file.Errors.Should().ContainSingle();
        file.InterchangeControlNumbers.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheEnvelopeGate_TheFileIsStillRecordedWithItsIdentifiers()
    {
        var file = (await Recorder().RecordReceivedAsync(new EdiFileIntake { TenantId = "t1", Content = Interchange(), TransactionType = "837" }, envelope: null))!;
        file.Status.Should().Be(EdiFileStatus.Received);
        file.InterchangeControlNumbers.Should().Equal("000000101");
        file.Ta1AckCode.Should().BeNull();
    }

    [Theory]
    [InlineData(3, 1, EdiFileStatus.Applied)]
    [InlineData(0, 2, EdiFileStatus.Rejected)]
    [InlineData(0, 0, EdiFileStatus.Rejected)]
    public async Task Completion_SetsCountsAndTheFinalStatus(int accepted, int rejected, string status)
    {
        var file = await ReceiveAsync(Interchange());
        await Recorder().CompleteAsync(file, new EdiFileCompletion
        {
            AcceptedRecords = accepted, RejectedRecords = rejected, Ack999Code = "P", Ack999ControlNumber = "000000777",
            Errors = accepted + rejected == 0 ? ["No CLM segments"] : [],
        });

        var stored = (await _files.GetAsync("t1", file.Id))!;
        stored.Status.Should().Be(status);
        stored.AcceptedRecords.Should().Be(accepted);
        stored.RejectedRecords.Should().Be(rejected);
        stored.Ack999Code.Should().Be("P");
        stored.Ack999ControlNumber.Should().Be("000000777");
        if (accepted + rejected > 0) stored.History.Select(h => h.Status).Should().Contain(EdiFileStatus.Parsed);
        else stored.Errors.Should().Contain("No CLM segments");
    }

    [Fact]
    public async Task UnknownSource_IsRecordedAsUpload()
    {
        (await ReceiveAsync(Interchange(), "carrier-pigeon")).Source.Should().Be(EdiFileSource.Upload);
    }

    [Fact]
    public async Task StoreFailure_NeverFailsTheIntake()
    {
        var recorder = new EdiFileRecorder(new ThrowingStore(), NullLogger<EdiFileRecorder>.Instance);
        var file = await recorder.RecordReceivedAsync(new EdiFileIntake { TenantId = "t1", Content = Interchange() }, null);
        file.Should().BeNull();
        await recorder.CompleteAsync(new EdiFile { TenantId = "t1" }, new EdiFileCompletion { AcceptedRecords = 1 });
    }

    private sealed class ThrowingStore : IEdiFileStore
    {
        public Task CreateAsync(EdiFile file, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task SaveAsync(EdiFile file, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task<EdiFile?> GetAsync(string tenantId, string id, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task<IReadOnlyList<EdiFile>> ListAsync(string tenantId, EdiFileQuery query, CancellationToken ct = default) => throw new InvalidOperationException("down");
    }
}
