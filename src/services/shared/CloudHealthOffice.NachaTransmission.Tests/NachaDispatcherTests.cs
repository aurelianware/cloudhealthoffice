using CloudHealthOffice.FieldProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.NachaTransmission.Tests;

public class NachaDispatcherTests
{
    private readonly Mock<INachaTransmitter> _transmitter = new();
    private readonly InMemoryNachaHeldFileStore _store = new();
    private readonly IFieldProtector _protector = Nacha.Protector();
    private readonly ListLogger<NachaDispatcher> _log = new();
    private readonly FixedClock _clock = new();
    private const string Releaser = "approver-9";

    private NachaDispatcher Dispatcher(IFieldProtector? protector = null, INachaHeldFileStore? store = null)
        => new(_transmitter.Object, store ?? _store, protector ?? _protector,
            new NachaTransmissionOptions { ServiceName = "premium-billing-service" }, _log, _clock);

    private void BankDown(string reason = "NACHA transmission is not configured for this tenant.")
        => _transmitter.Setup(t => t.TransmitAsync(It.IsAny<NachaTransmissionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NachaTransmissionException(reason, notConfigured: true));

    private void BankUp()
        => _transmitter.Setup(t => t.TransmitAsync(It.IsAny<NachaTransmissionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NachaTransmissionRequest r, CancellationToken _) => new NachaTransmissionReceipt
            {
                TenantId = r.TenantId, FileReference = r.FileReference, RemoteFileName = r.FileName,
                Sha256 = NachaFileFacts.From(r.Content).Sha256, TransmittedBy = r.TransmittedBy,
            });

    private async Task<NachaHeldFile> HeldFile()
    {
        BankDown();
        var outcome = await Dispatcher().DispatchAsync(Nacha.Request(Nacha.File(), Releaser));
        outcome.Status.Should().Be(NachaTransmissionStatus.AwaitingRetrieval);
        return _store.All.Single();
    }

    private void BankDeliveryUnknown()
        => _transmitter.Setup(t => t.TransmitAsync(It.IsAny<NachaTransmissionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NachaTransmissionException("Uploaded, rename outcome unknown. Verify with the bank.", deliveryUnknown: true));

    private async Task<NachaHeldFile> DeliveryUnknownFile()
    {
        BankDeliveryUnknown();
        var outcome = await Dispatcher().DispatchAsync(Nacha.Request(Nacha.File(), Releaser));
        outcome.Status.Should().Be(NachaTransmissionStatus.DeliveryUnknown);
        return _store.All.Single();
    }

    [Fact]
    public async Task DeliveryUnknown_IsHeld_ButNeitherRetriedNorRetrieved()
    {
        var held = await DeliveryUnknownFile();
        held.Status.Should().Be(NachaHeldFileStatus.DeliveryUnknown);
        _transmitter.Invocations.Clear();
        BankUp();

        var retry = () => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false));
        var retrieve = () => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("platform-admin-1", false), "deliver by hand");

        (await retry.Should().ThrowAsync<NachaHeldFileStateException>()).Which.Message.Should().Contain("Verify with the bank");
        (await retrieve.Should().ThrowAsync<NachaHeldFileStateException>()).Which.Message.Should().Contain("Verify with the bank");
        _transmitter.Invocations.Should().BeEmpty("nothing is sent again while it may be at the bank");
        held.Status.Should().Be(NachaHeldFileStatus.DeliveryUnknown);
        held.Retrievals.Should().BeEmpty();
        (await Dispatcher().ListHeldAsync(Nacha.Tenant)).Should().ContainSingle("it stays visible until resolved");
    }

    [Fact]
    public async Task DeliveryUnknown_AndNothingCanBeHeld_IsStillNotNotSent()
    {
        // NotSent puts the payments back to Pending, and the next release would send them again.
        BankDeliveryUnknown();

        var outcome = await Dispatcher(protector: new UnconfiguredFieldProtector()).DispatchAsync(Nacha.Request(Nacha.File(), Releaser));

        outcome.Status.Should().Be(NachaTransmissionStatus.DeliveryUnknown);
    }

    [Fact]
    public async Task Retry_WhoseOutcomeIsUnknown_LeavesTheFileDeliveryUnknown()
    {
        var held = await HeldFile();
        BankDeliveryUnknown();

        var outcome = await Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false));

        outcome.Status.Should().Be(NachaTransmissionStatus.DeliveryUnknown);
        held.Status.Should().Be(NachaHeldFileStatus.DeliveryUnknown);
    }

    [Fact]
    public async Task ResolveDeliveryUnknown_NeedsAnotherUser_AndAReason()
    {
        var held = await DeliveryUnknownFile();

        await FluentActions.Awaiting(() => Dispatcher().ResolveDeliveryUnknownAsync(Nacha.Tenant, held.FileReference,
            new NachaActor(Releaser, false), false, "bank says no")).Should().ThrowAsync<NachaSeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Dispatcher().ResolveDeliveryUnknownAsync(Nacha.Tenant, held.FileReference,
            new NachaActor("svc", true), false, "bank says no")).Should().ThrowAsync<NachaSeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Dispatcher().ResolveDeliveryUnknownAsync(Nacha.Tenant, held.FileReference,
            new NachaActor("approver-2", false), false, " ")).Should().ThrowAsync<ArgumentException>();
        held.Status.Should().Be(NachaHeldFileStatus.DeliveryUnknown);
    }

    [Fact]
    public async Task ResolveDeliveryUnknown_NotReceived_MakesItRetryable()
    {
        var held = await DeliveryUnknownFile();
        BankUp();

        await Dispatcher().ResolveDeliveryUnknownAsync(Nacha.Tenant, held.FileReference,
            new NachaActor("approver-2", false), bankReceived: false, "Bank ops (J. Doe) confirmed no file NACHA-ABC12345 today");
        var outcome = await Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-3", false));

        held.DeliveryResolution!.BankReceived.Should().BeFalse();
        held.DeliveryResolution.By.Should().Be("approver-2");
        outcome.Status.Should().Be(NachaTransmissionStatus.Transmitted);
        _log.Entries.Should().Contain(e => e.Event.Id == 4908);
    }

    [Fact]
    public async Task ResolveDeliveryUnknown_Received_DropsTheFile()
    {
        var held = await DeliveryUnknownFile();

        await Dispatcher().ResolveDeliveryUnknownAsync(Nacha.Tenant, held.FileReference,
            new NachaActor("approver-2", false), bankReceived: true, "Bank confirmed receipt, batch 0042");

        held.Status.Should().Be(NachaHeldFileStatus.Transmitted);
        held.ProtectedContent.Should().BeNull();
        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference,
            new NachaActor("platform-admin-1", false), "deliver")).Should().ThrowAsync<NachaHeldFileStateException>();
    }

    [Fact]
    public async Task Delivered_ReturnsTheReceipt_AndHoldsNothing()
    {
        BankUp();

        var outcome = await Dispatcher().DispatchAsync(Nacha.Request(Nacha.File(), Releaser));

        outcome.Status.Should().Be(NachaTransmissionStatus.Transmitted);
        outcome.Receipt.Should().NotBeNull();
        _store.All.Should().BeEmpty();
        _log.Entries.Should().Contain(e => e.Event.Id == 4901);
    }

    [Fact]
    public async Task NotDelivered_HoldsTheFileEncrypted_ForSevenDays()
    {
        var held = await HeldFile();

        held.Status.Should().Be(NachaHeldFileStatus.AwaitingRetrieval);
        held.ProtectedContent.Should().StartWith("enc:v1:");
        held.ProtectedContent.Should().NotContain(Nacha.Account).And.NotContain(Nacha.Routing[..8]);
        _protector.Unprotect(held.ProtectedContent).Should().Be(Nacha.File());
        held.ExpiresAt.Should().Be(_clock.Now.UtcDateTime.AddDays(7));
        held.ReleasedBy.Should().Be(Releaser);
        held.EntryCount.Should().Be(3);
        held.Sha256.Should().Be(NachaFileFacts.From(Nacha.File()).Sha256);
        held.Reason.Should().Contain("not configured");
        _log.Entries.Should().Contain(e => e.Event.Id == 4902);
        _log.All.Should().NotContain(Nacha.Account);
    }

    [Fact]
    public async Task NotDelivered_AndNoKeyRing_NothingIsHeld_NotSent()
    {
        BankDown();

        var outcome = await Dispatcher(protector: new UnconfiguredFieldProtector())
            .DispatchAsync(Nacha.Request(Nacha.File(), Releaser));

        outcome.Status.Should().Be(NachaTransmissionStatus.NotSent);
        outcome.Reason.Should().Contain("stay Pending");
        _store.All.Should().BeEmpty();
    }

    [Fact]
    public async Task NotDelivered_AndNoStore_NotSent()
    {
        BankDown();

        var outcome = await Dispatcher(store: new UnavailableNachaHeldFileStore()).DispatchAsync(Nacha.Request(Nacha.File(), Releaser));

        outcome.Status.Should().Be(NachaTransmissionStatus.NotSent);
    }

    [Fact]
    public async Task Retry_ByASecondApprover_SendsTheSameFile_AndDropsTheHeldCopy()
    {
        var held = await HeldFile();
        BankUp();

        var outcome = await Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false));

        outcome.Status.Should().Be(NachaTransmissionStatus.Transmitted);
        _transmitter.Verify(t => t.TransmitAsync(It.Is<NachaTransmissionRequest>(r =>
            r.Content == Nacha.File() && r.TransmittedBy == "approver-2" && r.FileName == held.FileName), It.IsAny<CancellationToken>()));
        held.Status.Should().Be(NachaHeldFileStatus.Transmitted);
        held.ProtectedContent.Should().BeNull();
        _log.Entries.Should().Contain(e => e.Event.Id == 4904 && e.Message.Contains("approver-2"));
    }

    [Fact]
    public async Task Retry_ByTheReleaser_IsRefused()
    {
        var held = await HeldFile();
        BankUp();

        var act = () => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor(Releaser, false));

        await act.Should().ThrowAsync<NachaSeparationOfDutiesException>();
        held.Status.Should().Be(NachaHeldFileStatus.AwaitingRetrieval);
        _transmitter.Verify(t => t.TransmitAsync(It.IsAny<NachaTransmissionRequest>(), It.IsAny<CancellationToken>()), Times.Once,
            "only the original attempt");
        _log.Entries.Should().Contain(e => e.Event.Id == 4906);
    }

    [Fact]
    public async Task Retry_ByAServiceToken_IsRefused()
    {
        var held = await HeldFile();

        await FluentActions.Awaiting(() => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("premium-billing-service", true)))
            .Should().ThrowAsync<NachaSeparationOfDutiesException>();
    }

    [Fact]
    public async Task Retry_StillFailing_StaysAwaitingRetrieval()
    {
        var held = await HeldFile();

        var outcome = await Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false));

        outcome.Status.Should().Be(NachaTransmissionStatus.AwaitingRetrieval);
        held.Status.Should().Be(NachaHeldFileStatus.AwaitingRetrieval);
        held.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retrieve_ByAPlatformAdmin_ReturnsTheFile_RecordsAndLogsTheReason()
    {
        var held = await HeldFile();

        var file = await Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("ops-1", false), "bank SFTP down, uploading via portal");

        file.Content.Should().Be(Nacha.File());
        file.FirstRetrieval.Should().BeTrue();
        held.Status.Should().Be(NachaHeldFileStatus.Retrieved);
        held.Retrievals.Should().ContainSingle(r => r.By == "ops-1" && r.Reason.Contains("bank SFTP down"));
        _log.Entries.Should().Contain(e => e.Event.Id == 4905 && e.Message.Contains("ops-1") && e.Message.Contains("bank SFTP down"));
        _log.All.Should().NotContain(Nacha.Account);

        // Each fetch is one request and is logged.
        var again = await Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("ops-2", false), "lost the first copy");
        again.FirstRetrieval.Should().BeFalse();
        held.Retrievals.Should().HaveCount(2);
        _log.Entries.Count(e => e.Event.Id == 4905).Should().Be(2);

        // Retrieved files are the admin's to deliver: no retry would double-send.
        await FluentActions.Awaiting(() => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false)))
            .Should().ThrowAsync<NachaHeldFileStateException>();
    }

    [Fact]
    public async Task Retrieve_ByTheReleaser_OrAService_OrWithoutAReason_IsRefused()
    {
        var held = await HeldFile();

        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor(Releaser, false), "why"))
            .Should().ThrowAsync<NachaSeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("svc", true), "why"))
            .Should().ThrowAsync<NachaSeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("ops-1", false), "  "))
            .Should().ThrowAsync<ArgumentException>();
        held.Retrievals.Should().BeEmpty();
        held.Status.Should().Be(NachaHeldFileStatus.AwaitingRetrieval);
    }

    [Fact]
    public async Task OtherTenant_CannotSeeTheFile()
    {
        var held = await HeldFile();

        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync("tenant-2", held.FileReference, new NachaActor("ops-1", false), "why"))
            .Should().ThrowAsync<NachaHeldFileNotFoundException>();
        (await Dispatcher().ListHeldAsync("tenant-2")).Should().BeEmpty();
        (await Dispatcher().ListHeldAsync(Nacha.Tenant)).Should().ContainSingle();
    }

    [Fact]
    public async Task Expired_IsGone()
    {
        var held = await HeldFile();
        _clock.Now = _clock.Now.AddDays(7).AddSeconds(1);

        await FluentActions.Awaiting(() => Dispatcher().RetrieveAsync(Nacha.Tenant, held.FileReference, new NachaActor("ops-1", false), "why"))
            .Should().ThrowAsync<NachaHeldFileExpiredException>();
        await FluentActions.Awaiting(() => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false)))
            .Should().ThrowAsync<NachaHeldFileExpiredException>();
    }

    [Fact]
    public async Task TamperedHeldFile_IsRefused()
    {
        var held = await HeldFile();
        held.ProtectedContent = _protector.Protect(Nacha.File().Replace("ACME CO", "EVIL CO"));

        await FluentActions.Awaiting(() => Dispatcher().RetryAsync(Nacha.Tenant, held.FileReference, new NachaActor("approver-2", false)))
            .Should().ThrowAsync<NachaHeldFileStateException>().WithMessage("*SHA-256*");
    }

    [Fact]
    public void HeldFileView_NeverCarriesTheFile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(NachaHeldFileView.From(new NachaHeldFile
        {
            FileReference = "R", ProtectedContent = "enc:v1:secret", Retrievals = { new NachaRetrieval { By = "x" } }
        }));
        json.Should().NotContain("enc:v1").And.NotContain("protectedContent").And.NotContain("content");
        System.Text.Json.JsonSerializer.Serialize(new NachaHeldFile { ProtectedContent = "enc:v1:x" }).Should().NotContain("enc:v1");
    }
}

public class LocalFolderAndRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nacha-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Env : IHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void LocalFolder_IsRefusedOutsideDevelopmentAndTesting(string environment)
    {
        FluentActions.Invoking(() => new LocalFolderNachaTransmitter(_root, new Env(environment), new ListLogger<LocalFolderNachaTransmitter>()))
            .Should().Throw<InvalidOperationException>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NachaTransmission:Mode"] = "LocalFolder"
        }).Build();
        FluentActions.Invoking(() => new ServiceCollection().AddChoNachaTransmission(config, new Env(environment), "svc", null))
            .Should().Throw<InvalidOperationException>().WithMessage("*Development and Testing only*");
    }

    [Fact]
    public async Task LocalFolder_InDevelopment_WritesAtomically_PerTenant()
    {
        var transmitter = new LocalFolderNachaTransmitter(_root, new Env("Development"), new ListLogger<LocalFolderNachaTransmitter>());
        var request = Nacha.Request(Nacha.File());

        var receipt = await transmitter.TransmitAsync(request);

        var path = Path.Combine(_root, Nacha.Tenant, request.FileName);
        File.ReadAllText(path).Should().Be(Nacha.File());
        Directory.GetFiles(Path.Combine(_root, Nacha.Tenant)).Should().ContainSingle("no temporary file is left");
        receipt.Sha256.Should().Be(NachaFileFacts.From(Nacha.File()).Sha256);
        await FluentActions.Awaiting(() => transmitter.TransmitAsync(request)).Should().ThrowAsync<NachaTransmissionException>();
    }

    [Fact]
    public void Production_WithoutADatabase_HoldsNothing_AndUsesSftp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFieldProtector, UnconfiguredFieldProtector>();
        services.AddChoNachaTransmission(new ConfigurationBuilder().Build(), new Env("Production"), "svc", null);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<INachaHeldFileStore>().Should().BeOfType<UnavailableNachaHeldFileStore>();
        sp.GetRequiredService<INachaTransmitter>().Should().BeOfType<SftpNachaTransmitter>();
        sp.GetRequiredService<INachaSecretReader>().Should().BeOfType<UnconfiguredNachaSecretReader>();
    }

    [Fact]
    public void TenantSettingsJson_IsParsedFromPaymentControls()
    {
        var settings = HttpNachaTransmissionSettingsSource.Parse("""
            {"configuration":{"paymentControls":{"enforceSeparationOfDuties":true,"nachaTransmission":{
              "enabled":true,"host":"sftp.bank.example","port":2222,"username":"u",
              "privateKeySecretRef":"nacha--tenant-1--key","hostKeyFingerprint":"SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU",
              "remoteDirectory":"/in","privateKeyConfigured":true}}}}
            """)!;

        settings.Port.Should().Be(2222);
        settings.Problem(Nacha.Tenant).Should().BeNull();
        HttpNachaTransmissionSettingsSource.Parse("""{"configuration":{"paymentControls":{}}}""").Should().BeNull();
    }
}
