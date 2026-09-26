using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProviderEligibilityApi.Eligibility;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>
/// If the one startup sync fails, the retry service keeps trying (with
/// backoff) until the directory loads, instead of leaving the replica empty
/// until the next daily sync.
/// </summary>
public sealed class PayerDirectoryStartupRetryTests
{
    [Fact]
    public async Task Retries_until_the_directory_loads_then_stops()
    {
        var synchronizer = Substitute.For<IPayerDirectorySynchronizer>();
        PayerDirectorySyncStatus? status = null;
        synchronizer.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(_ => status);
        synchronizer.SynchronizeAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new HttpRequestException("directory down"),
            _ => Task.FromResult(new PayerDirectorySyncResult { Succeeded = false }),
            _ =>
            {
                status = new PayerDirectorySyncStatus { LastSucceededAt = DateTimeOffset.UtcNow, LastSucceeded = true };
                return Task.FromResult(new PayerDirectorySyncResult { Succeeded = true });
            });

        var service = Create(synchronizer, syncEnabled: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        await synchronizer.Received(3).SynchronizeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_nothing_when_the_directory_is_already_loaded()
    {
        var synchronizer = Substitute.For<IPayerDirectorySynchronizer>();
        synchronizer.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new PayerDirectorySyncStatus { LastSucceededAt = DateTimeOffset.UtcNow });

        var service = Create(synchronizer, syncEnabled: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        await synchronizer.DidNotReceiveWithAnyArgs().SynchronizeAsync(default);
    }

    [Fact]
    public async Task Does_nothing_when_sync_is_disabled()
    {
        var synchronizer = Substitute.For<IPayerDirectorySynchronizer>();

        var service = Create(synchronizer, syncEnabled: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        await synchronizer.DidNotReceiveWithAnyArgs().SynchronizeAsync(default);
        await synchronizer.DidNotReceiveWithAnyArgs().GetStatusAsync(default);
    }

    private static PayerDirectoryStartupRetryService Create(IPayerDirectorySynchronizer synchronizer, bool syncEnabled)
    {
        var options = Options.Create(new PayerReferenceOptions());
        options.Value.Sync.Enabled = syncEnabled;
        var services = new ServiceCollection().AddSingleton(synchronizer).BuildServiceProvider();
        var readiness = new PayerDirectoryReadiness(options, services);
        return new PayerDirectoryStartupRetryService(
            readiness,
            services,
            options,
            new ImmediateTimeProvider(),
            NullLogger<PayerDirectoryStartupRetryService>.Instance);
    }

    /// <summary>Fires every timer at once so backoff delays do not slow the test.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
