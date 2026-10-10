using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace CloudHealthOffice.NachaTransmission.Tests;

/// <summary>
/// The read-only drop listing used for reconciliation, and the pinned host-key
/// decision taken with real SSH.NET host-key event arguments (no server).
/// </summary>
public class RemoteProbeAndHostKeyTests
{
    private readonly StaticSettings _settings = new() { Value = Nacha.Settings() };
    private readonly FakeSecrets _secrets = new();
    private readonly FakeSftp _sftp = new();
    private readonly ListLogger<SftpNachaTransmitter> _log = new();

    private SftpNachaTransmitter Transmitter() => new(_settings, _secrets, _sftp, _log, new FixedClock());

    [Fact]
    public async Task Probe_FindsTheFileOnlyWithTheExpectedSize_AndNeverWrites()
    {
        var request = Nacha.Request(Nacha.File());
        var receipt = await Transmitter().TransmitAsync(request);
        _sftp.Operations.Clear();

        var present = await Transmitter().CheckAsync(Nacha.Tenant, request.FileName, receipt.ByteSize);
        var otherSize = await Transmitter().CheckAsync(Nacha.Tenant, request.FileName, receipt.ByteSize + 1);
        var absent = await Transmitter().CheckAsync(Nacha.Tenant, "ACH-OTHER.ach", 10);

        present.Presence.Should().Be(NachaRemoteFilePresence.Present);
        present.RemoteByteSize.Should().Be(receipt.ByteSize);
        present.Destination.Should().Be("sftp://sftp.bank.example:22/inbound/ach");
        otherSize.Presence.Should().Be(NachaRemoteFilePresence.DifferentSize);
        absent.Presence.Should().Be(NachaRemoteFilePresence.Absent);
        _sftp.Operations.Should().NotContain(o => o.StartsWith("upload") || o.StartsWith("rename") || o.StartsWith("delete"));
    }

    [Fact]
    public async Task Probe_WithoutAPinnedHostKey_RefusesBeforeConnecting()
    {
        _settings.Value = Nacha.Settings(pin: null);

        var act = () => Transmitter().CheckAsync(Nacha.Tenant, "ACH-FFS-1.ach", 940);

        (await act.Should().ThrowAsync<NachaTransmissionException>()).Which.NotConfigured.Should().BeTrue();
        _sftp.Connections.Should().BeEmpty();
        _secrets.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_ConnectionFailure_IsATransmissionException_WithoutTheServersMessage()
    {
        _sftp.FailConnect = new IOException("banner: secret-host-banner " + FakeSecrets.Passphrase);

        var act = () => Transmitter().CheckAsync(Nacha.Tenant, "ACH-FFS-1.ach", 940);

        var ex = (await act.Should().ThrowAsync<NachaTransmissionException>()).Which;
        ex.Message.Should().NotContain(FakeSecrets.Passphrase).And.NotContain("banner");
        _log.All.Should().NotContain(FakeSecrets.Passphrase);
    }

    [Fact]
    public async Task Probe_RefusesAFileNameWithAPath()
    {
        var act = () => Transmitter().CheckAsync(Nacha.Tenant, "../etc/passwd", 1);
        await act.Should().ThrowAsync<NachaTransmissionException>();
        _sftp.Connections.Should().BeEmpty();
    }

    private static HostKeyEventArgs HostKey(byte seed)
    {
        var keyData = new byte[64];
        for (var i = 0; i < keyData.Length; i++) keyData[i] = (byte)(seed + i);
        return new HostKeyEventArgs(new KeyHostAlgorithm("ssh-ed25519", new ED25519Key(keyData)));
    }

    [Fact]
    public void HostKeyCheck_ThePinnedKey_IsTrusted()
    {
        var bank = HostKey(1);
        var check = new PinnedHostKeyCheck(HostKeyPin.Of(bank.HostKey));

        check.OnHostKeyReceived(null, bank);

        bank.CanTrust.Should().BeTrue();
        check.Trusted.Should().BeTrue();
        check.Rejected.Should().BeFalse();
    }

    [Fact]
    public void HostKeyCheck_AnyOtherKey_IsRefused_AndSaysWhichKeyWasPresented()
    {
        var bank = HostKey(1);
        var impostor = HostKey(77);
        var check = new PinnedHostKeyCheck(HostKeyPin.Of(bank.HostKey));

        check.OnHostKeyReceived(null, impostor);

        impostor.CanTrust.Should().BeFalse();
        check.Trusted.Should().BeFalse();
        check.Rejected.Should().BeTrue();
        var ex = check.RejectionException();
        ex.HostKeyRejected.Should().BeTrue();
        ex.DeliveryUnknown.Should().BeFalse();
        ex.Message.Should().Contain(HostKeyPin.Of(impostor.HostKey)).And.Contain("not the pinned key");
    }

    [Fact]
    public void HostKeyCheck_AMismatchIsNeverForgotten_EvenIfThePinnedKeyFollows()
    {
        var bank = HostKey(1);
        var check = new PinnedHostKeyCheck(HostKeyPin.Of(bank.HostKey));

        check.OnHostKeyReceived(null, HostKey(77));
        var later = HostKey(1);
        check.OnHostKeyReceived(null, later);

        later.CanTrust.Should().BeFalse();
        check.Trusted.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHA256:AAAA")]
    public void HostKeyCheck_NoValidPin_TrustsNothing(string? pin)
    {
        var key = HostKey(1);
        var check = new PinnedHostKeyCheck(pin);

        check.OnHostKeyReceived(null, key);

        key.CanTrust.Should().BeFalse();
        check.Trusted.Should().BeFalse();
    }

    [Fact]
    public void HostKeyCheck_NoKeySeen_IsNotTrusted()
    {
        var check = new PinnedHostKeyCheck(Nacha.Pin);
        check.Trusted.Should().BeFalse();
        check.RejectionException().HostKeyRejected.Should().BeTrue();
    }

    [Fact]
    public async Task HostKeyRejectedByTheFactory_SurfacesAsHostKeyRejected_AndNothingIsUploaded()
    {
        _sftp.FailConnect = new PinnedHostKeyCheck(Nacha.Pin).RejectionException();

        var act = () => Transmitter().TransmitAsync(Nacha.Request(Nacha.File()));

        (await act.Should().ThrowAsync<NachaTransmissionException>()).Which.HostKeyRejected.Should().BeTrue();
        _sftp.Files.Should().BeEmpty();
        _sftp.Operations.Should().NotContain(o => o.StartsWith("upload"));
    }
}
