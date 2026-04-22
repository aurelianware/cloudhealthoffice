using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace CloudHealthOffice.Infrastructure.Tests;

public class SecretRefreshServiceTests
{
    /// <summary>
    /// An IConfiguration source whose reload token is a mutable
    /// CancellationChangeToken — calling <see cref="FireReload"/> cancels
    /// the current token, which fires the ChangeToken.OnChange callback.
    /// </summary>
    private sealed class FakeReloadingConfiguration : IConfiguration
    {
        private CancellationTokenSource _cts = new();
        private IChangeToken _token;

        public FakeReloadingConfiguration()
        {
            _token = new CancellationChangeToken(_cts.Token);
        }

        public void FireReload()
        {
            var old = _cts;
            var newCts = new CancellationTokenSource();
            _cts = newCts;
            _token = new CancellationChangeToken(newCts.Token);
            old.Cancel();
        }

        public IChangeToken GetReloadToken() => _token;
        public string? this[string key] { get => null; set { } }
        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }

    private sealed class CountingProvider : RotatingKeyProvider
    {
        public int InvalidateCalls;
        public CountingProvider() : base(new NullSecretProvider(), NullLogger<RotatingKeyProvider>.Instance) { }
        public override void InvalidateCache()
        {
            Interlocked.Increment(ref InvalidateCalls);
            base.InvalidateCache();
        }
    }

    private sealed class FailOnceProvider : RotatingKeyProvider
    {
        public int InvalidateCalls;
        public FailOnceProvider() : base(new NullSecretProvider(), NullLogger<RotatingKeyProvider>.Instance) { }
        public override void InvalidateCache()
        {
            var n = Interlocked.Increment(ref InvalidateCalls);
            if (n == 1) throw new InvalidOperationException("first invalidation throws");
            base.InvalidateCache();
        }
    }

    [Fact]
    public async Task OnReload_InvalidatesCache()
    {
        var cfg = new FakeReloadingConfiguration();
        var provider = new CountingProvider();
        var svc = new SecretRefreshService(cfg, provider, NullLogger<SecretRefreshService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        cfg.FireReload();
        await Task.Delay(50); // ChangeToken.OnChange fires synchronously but on the firing thread; give it a tick.

        provider.InvalidateCalls.Should().Be(1);

        await svc.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Regression guard for the exact silent-failure mode called out in the
    /// addendum review: if InvalidateCache throws on one fire, a second
    /// reload must still reach InvalidateCache. Without the try/catch in
    /// SecretRefreshService.OnReload, the exception escapes the
    /// ChangeToken.OnChange callback and depending on the Microsoft.Extensions
    /// version, subsequent reload-token fires can silently stop processing —
    /// which would negate the entire rotation flow.
    /// </summary>
    [Fact]
    public async Task OnReload_SurvivesCallbackException_AndProcessesSubsequentReloads()
    {
        var cfg = new FakeReloadingConfiguration();
        var provider = new FailOnceProvider();
        var svc = new SecretRefreshService(cfg, provider, NullLogger<SecretRefreshService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        cfg.FireReload();          // attempt #1 → throws inside InvalidateCache
        await Task.Delay(50);
        cfg.FireReload();          // attempt #2 → must still reach InvalidateCache
        await Task.Delay(50);

        provider.InvalidateCalls.Should().Be(2);

        await svc.StopAsync(CancellationToken.None);
    }
}
