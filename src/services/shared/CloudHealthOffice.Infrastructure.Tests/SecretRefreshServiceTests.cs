using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace CloudHealthOffice.Infrastructure.Tests;

public class SecretRefreshServiceTests
{
    // A short-but-bounded wait used when asserting that a
    // ChangeToken.OnChange callback fired. All waits go through a signal
    // that's set inside the fake provider's InvalidateCache override, so
    // a passing test completes in microseconds; the timeout only trips
    // on a real regression.
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Test double that signals a per-call <see cref="ManualResetEventSlim"/>
    /// whenever <see cref="InvalidateCache"/> runs, so tests can wait
    /// deterministically for the Nth invalidation rather than sleeping.
    /// </summary>
    private sealed class SignallingProvider : RotatingKeyProvider
    {
        public int InvalidateCalls;
        private readonly ManualResetEventSlim _signal = new(false);
        private readonly bool _throwOnFirst;

        public SignallingProvider(bool throwOnFirst = false)
            : base(new NullSecretProvider(), NullLogger<RotatingKeyProvider>.Instance)
        {
            _throwOnFirst = throwOnFirst;
        }

        public override void InvalidateCache()
        {
            var n = Interlocked.Increment(ref InvalidateCalls);
            try
            {
                if (_throwOnFirst && n == 1)
                    throw new InvalidOperationException("first invalidation throws");
                base.InvalidateCache();
            }
            finally
            {
                _signal.Set();
            }
        }

        public void WaitFor(int expectedCount)
        {
            var deadline = DateTime.UtcNow + CallbackTimeout;
            while (Interlocked.CompareExchange(ref InvalidateCalls, 0, 0) < expectedCount)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException(
                        $"Expected InvalidateCalls >= {expectedCount}, got {InvalidateCalls} after {CallbackTimeout}");

                _signal.Reset();
                if (Interlocked.CompareExchange(ref InvalidateCalls, 0, 0) >= expectedCount) return;
                _signal.Wait(remaining);
            }
        }
    }

    [Fact]
    public async Task OnReload_InvalidatesCache()
    {
        var cfg = new FakeReloadingConfiguration();
        var provider = new SignallingProvider();
        var svc = new SecretRefreshService(cfg, provider, NullLogger<SecretRefreshService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        cfg.FireReload();
        provider.WaitFor(1);

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
        var provider = new SignallingProvider(throwOnFirst: true);
        var svc = new SecretRefreshService(cfg, provider, NullLogger<SecretRefreshService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        cfg.FireReload();               // attempt #1 → throws inside InvalidateCache
        provider.WaitFor(1);

        cfg.FireReload();               // attempt #2 → must still reach InvalidateCache
        provider.WaitFor(2);

        provider.InvalidateCalls.Should().Be(2);

        await svc.StopAsync(CancellationToken.None);
    }
}
