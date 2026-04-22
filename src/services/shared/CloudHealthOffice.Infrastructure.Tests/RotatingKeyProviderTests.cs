using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.Infrastructure.Tests;

public class RotatingKeyProviderTests
{
    private static RotatingKeyProvider New(Mock<ISecretProvider> secrets) =>
        new(secrets.Object, NullLogger<RotatingKeyProvider>.Instance);

    [Fact]
    public async Task GetKey_UnknownSecret_ThrowsDescriptive()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var sut = New(secrets);
        var act = async () => await sut.GetKeyAsync("pref", "v1");
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("pref-v1").And.Contain("AcceptedKeyVersions");
    }

    [Fact]
    public async Task GetKey_UsesDevConfigFallback_WhenSecretMissing()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var sut = New(secrets);
        var key = await sut.GetKeyAsync("pref", "v1", devConfigFallback: "dev-key-bytes-here");
        key.Should().Equal(Encoding.UTF8.GetBytes("dev-key-bytes-here"));
    }

    [Fact]
    public async Task GetKey_CachesResult_SingleSecretProviderCall()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToBase64String(new byte[32]));

        var sut = New(secrets);
        var first = await sut.GetKeyAsync("pref", "v1");
        var second = await sut.GetKeyAsync("pref", "v1");

        first.Should().BeSameAs(second);
        secrets.Verify(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetKey_PerVersionCache_NotSharedAcrossVersions()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray()));
        secrets.Setup(s => s.GetSecretAsync("pref-v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray()));

        var sut = New(secrets);
        var v1 = await sut.GetKeyAsync("pref", "v1");
        var v2 = await sut.GetKeyAsync("pref", "v2");

        v1[0].Should().Be(0x11);
        v2[0].Should().Be(0x22);
    }

    [Fact]
    public async Task InvalidateCache_ForcesReResolution()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.SetupSequence(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToBase64String(Enumerable.Repeat((byte)0xAA, 32).ToArray()))
            .ReturnsAsync(Convert.ToBase64String(Enumerable.Repeat((byte)0xBB, 32).ToArray()));

        var sut = New(secrets);
        var before = await sut.GetKeyAsync("pref", "v1");
        before[0].Should().Be(0xAA);

        sut.InvalidateCache();

        var after = await sut.GetKeyAsync("pref", "v1");
        after[0].Should().Be(0xBB);
        secrets.Verify(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetKey_DecodesBase64First_ThenFallsBackToUtf8()
    {
        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("not-base64!!plain-utf8-literal");

        var sut = New(secrets);
        var key = await sut.GetKeyAsync("pref", "v1");
        key.Should().Equal(Encoding.UTF8.GetBytes("not-base64!!plain-utf8-literal"));
    }

    /// <summary>
    /// Regression guard for the race the reviewer flagged: an in-flight
    /// resolve that completes AFTER an InvalidateCache must not persist
    /// its (now-stale) result back into the cache. The generation counter
    /// makes the resolve's write-back conditional on the generation being
    /// unchanged since the fetch started.
    /// </summary>
    [Fact]
    public async Task GetKey_InvalidationDuringInFlightResolve_SkipsWriteBack()
    {
        var fetchReleased = new TaskCompletionSource<string?>();
        var fetchStarted = new TaskCompletionSource();

        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                fetchStarted.TrySetResult();
                return await fetchReleased.Task;
            });

        var sut = New(secrets);

        // Start the resolve and wait until it's inside the secret-provider call.
        var resolveTask = sut.GetKeyAsync("pref", "v1");
        await fetchStarted.Task;

        // Invalidate before the fetch completes.
        sut.InvalidateCache();

        // Let the fetch return. The result should NOT land in the cache.
        fetchReleased.SetResult(Convert.ToBase64String(Enumerable.Repeat((byte)0xAA, 32).ToArray()));
        var firstBytes = await resolveTask;
        firstBytes[0].Should().Be(0xAA);

        // A subsequent call must re-fetch (cache was cleared and the in-flight
        // write was suppressed), so the mock returns the NEW stub.
        secrets.Setup(s => s.GetSecretAsync("pref-v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToBase64String(Enumerable.Repeat((byte)0xBB, 32).ToArray()));
        var secondBytes = await sut.GetKeyAsync("pref", "v1");
        secondBytes[0].Should().Be(0xBB);
    }
}
