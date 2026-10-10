using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.NachaTransmission.Tests;

public class SftpNachaTransmitterTests
{
    private readonly StaticSettings _settings = new() { Value = Nacha.Settings() };
    private readonly FakeSecrets _secrets = new();
    private readonly FakeSftp _sftp = new();
    private readonly ListLogger<SftpNachaTransmitter> _log = new();
    private readonly FixedClock _clock = new();

    private SftpNachaTransmitter Transmitter() => new(_settings, _secrets, _sftp, _log, _clock);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MD5:16:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48")]
    [InlineData("SHA256:not-base64!")]
    [InlineData("SHA256:AAAA")] // not 32 bytes
    public async Task NoValidPinnedHostKey_RefusesBeforeAnySecretOrConnection(string? pin)
    {
        _settings.Value = Nacha.Settings(pin);

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        var ex = (await act.Should().ThrowAsync<NachaTransmissionException>()).Which;
        ex.Message.Should().Contain("hostKeyFingerprint");
        ex.NotConfigured.Should().BeTrue();
        _sftp.Connections.Should().BeEmpty();
        _secrets.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task SshNetFactory_RefusesToConnectWithoutAPin()
    {
        var factory = new SshNetSftpSessionFactory();

        var act = () => factory.Connect(new SftpConnectParameters
        {
            Host = "127.0.0.1", Port = 1, Username = "u", HostKeyFingerprint = "", Password = "p"
        });

        act.Should().Throw<NachaTransmissionException>().WithMessage("*no pinned SSH host key*");
    }

    [Fact]
    public void HostKeyPin_MatchesOnlyThePinnedKey()
    {
        var blob = Encoding.ASCII.GetBytes("ssh-ed25519 AAAA host key blob");
        var pin = HostKeyPin.Of(blob);

        HostKeyPin.Matches(pin, blob).Should().BeTrue();
        HostKeyPin.Matches(pin + "=", blob).Should().BeTrue("padding is optional");
        HostKeyPin.Matches(pin, Encoding.ASCII.GetBytes("another key")).Should().BeFalse();
        HostKeyPin.Matches(null, blob).Should().BeFalse();
    }

    [Fact]
    public async Task NotConfiguredOrDisabled_IsNotConfigured()
    {
        _settings.Value = null;
        (await FluentActions.Awaiting(() => Transmitter().TransmitAsync(Nacha.Request(Nacha.File())))
            .Should().ThrowAsync<NachaTransmissionException>()).Which.NotConfigured.Should().BeTrue();

        _settings.Value = Nacha.Settings();
        _settings.Value.Enabled = false;
        (await FluentActions.Awaiting(() => Transmitter().TransmitAsync(Nacha.Request(Nacha.File())))
            .Should().ThrowAsync<NachaTransmissionException>()).Which.NotConfigured.Should().BeTrue();
        _sftp.Connections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nacha--tenant-2--key")]      // another tenant's secret
    [InlineData("cosmos-primary-key")]        // a platform secret
    [InlineData("nacha--tenant-1--")]         // prefix only
    public async Task SecretRefOutsideTheTenantsPrefix_IsRefusedWithoutReadingIt(string reference)
    {
        _settings.Value!.PrivateKeySecretRef = reference;

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        (await act.Should().ThrowAsync<NachaTransmissionException>()).Which.Message.Should().Contain("nacha--tenant-1--");
        _secrets.Reads.Should().BeEmpty();
        _sftp.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_IsAtomic_TempNameThenRename_AndTheBankSeesOnlyTheFinalFile()
    {
        var content = Nacha.File();
        var request = Nacha.Request(content);

        await Transmitter().TransmitAsync(request);

        var final = $"/inbound/ach/{request.FileName}";
        var uploads = _sftp.Operations.Where(o => o.StartsWith("upload ")).ToList();
        uploads.Should().ContainSingle();
        var temp = uploads[0]["upload ".Length..];
        temp.Should().StartWith("/inbound/ach/.").And.EndWith(".part").And.NotBe(final);
        _sftp.Operations.Should().ContainInOrder($"exists {final}", $"upload {temp}", $"rename {temp} -> {final}", "close");
        _sftp.Operations.Should().NotContain($"upload {final}", "the final name is only ever created by the rename");
        _sftp.Files.Keys.Should().BeEquivalentTo(new[] { final });
        Encoding.ASCII.GetString(_sftp.Files[final]).Should().Be(content);

        // Credentials came from Key Vault, by the names tenant-service holds.
        _secrets.Reads.Should().BeEquivalentTo(new[] { "nacha--tenant-1--key", "nacha--tenant-1--passphrase" });
        var connection = _sftp.Connections.Single();
        connection.HostKeyFingerprint.Should().Be(Nacha.Pin);
        connection.PrivateKey.Should().Be(FakeSecrets.PrivateKey);
    }

    [Fact]
    public async Task ExistingFileAtTheBank_IsNeverOverwritten()
    {
        var request = Nacha.Request(Nacha.File());
        _sftp.Files[$"/inbound/ach/{request.FileName}"] = new byte[] { 1 };

        var act = () => Transmitter().TransmitAsync(request);

        await act.Should().ThrowAsync<NachaTransmissionException>().WithMessage("*already*");
        _sftp.Operations.Should().NotContain(o => o.StartsWith("upload") || o.StartsWith("rename"));
    }

    [Fact]
    public async Task FailedRename_WithTheTemporaryFileStillThere_IsUnknown_AndTheFileIsLeftAlone()
    {
        _sftp.FailRename = true;

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        // Temporary there, final absent is not proof of non-delivery (a server that
        // renames by copy-then-delete may be mid-copy): unknown, nothing deleted.
        (await act.Should().ThrowAsync<NachaTransmissionException>()).Which.DeliveryUnknown.Should().BeTrue();
        _sftp.Files.Should().ContainSingle().Which.Key.Should().EndWith(".part");
        _sftp.Operations.Should().NotContain(o => o.StartsWith("delete "));
    }

    [Fact]
    public async Task ClosingTheSessionFailsAfterTheRename_TheFileIsStillDelivered()
    {
        _sftp.FailDispose = true;
        var request = Nacha.Request(Nacha.File());

        var receipt = await Transmitter().TransmitAsync(request);

        receipt.RemoteFileName.Should().Be(request.FileName);
        _sftp.Files.Keys.Should().ContainSingle().Which.Should().EndWith("/" + request.FileName);
        _log.All.Should().Contain("closing the SFTP session failed");
    }

    [Fact]
    public async Task ClosingTheSessionFailsAfterAFailedUpload_IsStillNotDelivered()
    {
        _sftp.FailDispose = true;
        _sftp.FailUpload = true;

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        var ex = (await act.Should().ThrowAsync<NachaTransmissionException>()).Which;
        ex.DeliveryUnknown.Should().BeFalse();
        ex.Message.Should().Contain("upload");
    }

    [Fact]
    public async Task ClosingTheSessionFailsAfterADropCheck_TheAnswerStands()
    {
        var request = Nacha.Request(Nacha.File());
        var receipt = await Transmitter().TransmitAsync(request);
        _sftp.FailDispose = true;

        var check = await Transmitter().CheckAsync(Nacha.Tenant, request.FileName, receipt.ByteSize);

        check.Presence.Should().Be(NachaRemoteFilePresence.Present);
    }

    [Fact]
    public async Task RenameReplyLost_ButTheFileIsInPlace_IsDelivered()
    {
        _sftp.RenameAppliesThenFails = true;
        var request = Nacha.Request(Nacha.File());

        var receipt = await Transmitter().TransmitAsync(request);

        receipt.RemoteFileName.Should().Be(request.FileName);
        _sftp.Files.Keys.Should().ContainSingle().Which.Should().EndWith("/" + request.FileName);
        _sftp.Operations.Should().NotContain(o => o.StartsWith("delete "));
    }

    [Fact]
    public async Task RenameFailed_AndTheServerCannotBeAsked_DeliveryIsUnknown()
    {
        // The rename went through on the server, then the connection died: the
        // file is at the bank, and nothing the client can see says so.
        _sftp.DeadAfterRename = true;

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        var ex = (await act.Should().ThrowAsync<NachaTransmissionException>()).Which;
        ex.DeliveryUnknown.Should().BeTrue();
        ex.Message.Should().Contain("Verify with the bank");
        _sftp.Files.Should().ContainSingle("the file did reach the bank");
    }

    [Fact]
    public async Task FileAlreadyInTheDrop_IsNotOverwritten_AndDeliveryIsUnknown()
    {
        var request = Nacha.Request(Nacha.File());
        await Transmitter().TransmitAsync(request);

        var act = () => Transmitter().TransmitAsync(request);

        var ex = (await act.Should().ThrowAsync<NachaTransmissionException>()).Which;
        ex.DeliveryUnknown.Should().BeTrue("a file of that name is this file from an earlier attempt");
        _sftp.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task Receipt_HashSizeCountAndTotals_AreThoseOfTheBytesSent()
    {
        var content = Nacha.File();
        var request = Nacha.Request(content);

        var receipt = await Transmitter().TransmitAsync(request);

        var bytes = Encoding.ASCII.GetBytes(content);
        receipt.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        receipt.ByteSize.Should().Be(bytes.Length);
        receipt.EntryCount.Should().Be(3);
        receipt.TotalDebitAmount.Should().Be(1525.50m);   // 1500.00 + 25.50
        receipt.TotalCreditAmount.Should().Be(10.00m);
        receipt.TenantId.Should().Be(Nacha.Tenant);
        receipt.RemoteFileName.Should().Be(request.FileName);
        receipt.Destination.Should().Be("sftp://sftp.bank.example:22/inbound/ach");
        receipt.TransmittedAt.Should().Be(_clock.Now.UtcDateTime);
        receipt.TransmittedBy.Should().Be("approver-9");
        receipt.RunId.Should().Be("run-1");
        receipt.BatchId.Should().Be(request.FileReference);
        receipt.FileReference.Should().Be(request.FileReference);

        // The file on the server hashes to the receipt's hash.
        var stored = _sftp.Files.Single().Value;
        Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant().Should().Be(receipt.Sha256);
    }

    [Fact]
    public async Task SecretsAndFile_AreNeverLogged_OrPutInErrors()
    {
        await Transmitter().TransmitAsync(Nacha.Request(Nacha.File(), reference: "NACHA-OK000001"));
        _sftp.FailRename = true;
        var failure = await FluentActions.Awaiting(() => Transmitter().TransmitAsync(Nacha.Request(Nacha.File(), reference: "NACHA-FAIL0001")))
            .Should().ThrowAsync<NachaTransmissionException>();
        _sftp.FailRename = false;
        _sftp.FailConnect = new InvalidOperationException("auth failed with password " + FakeSecrets.Passphrase);
        var connectFailure = await FluentActions.Awaiting(() => Transmitter().TransmitAsync(Nacha.Request(Nacha.File(), reference: "NACHA-FAIL0002")))
            .Should().ThrowAsync<NachaTransmissionException>();

        foreach (var text in new[] { _log.All, failure.Which.Message, connectFailure.Which.Message })
        {
            text.Should().NotContain(FakeSecrets.PrivateKey).And.NotContain(FakeSecrets.Passphrase)
                .And.NotContain("SECRET-KEY-MATERIAL")
                .And.NotContain(Nacha.Account).And.NotContain(Nacha.Routing);
        }
        _log.All.Should().Contain("NACHA-OK000001");
        new SftpConnectParameters
        {
            Host = "h", Port = 22, Username = "u", HostKeyFingerprint = Nacha.Pin,
            PrivateKey = FakeSecrets.PrivateKey, Password = FakeSecrets.Passphrase
        }.ToString().Should().NotContain(FakeSecrets.Passphrase).And.NotContain("SECRET");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData(".hidden")]
    [InlineData("a/b.ach")]
    public async Task FileNameWithAPath_IsRefused(string name)
    {
        var request = new NachaTransmissionRequest
        {
            TenantId = Nacha.Tenant, FileReference = "R", FileName = name, Content = Nacha.File(), TransmittedBy = "u"
        };

        await FluentActions.Awaiting(() => Transmitter().TransmitAsync(request)).Should().ThrowAsync<NachaTransmissionException>();
        _sftp.Connections.Should().BeEmpty();
    }
}
