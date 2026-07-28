using System.Net;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal sealed class ProviderSeedRequestException : InvalidOperationException
{
    public ProviderSeedRequestException(string operation, HttpStatusCode statusCode, string responseBody)
        : base($"{operation}: {(int)statusCode} {responseBody}")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

internal static class ProviderSeedRetryPolicy
{
    // A transient Cosmos outage can make every provider-service pod fail
    // readiness at once. Kubernetes then removes all service endpoints until
    // the dependency recovers, so retries must span more than a few seconds.
    internal const int MaxAttempts = 10;

    internal static async Task ExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken,
        Action<int, TimeSpan, Exception>? onRetry = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception exception) when (
                attempt < MaxAttempts
                && !cancellationToken.IsCancellationRequested
                && IsTransient(exception))
            {
                var delay = RetryDelay(attempt);
                onRetry?.Invoke(attempt + 1, delay, exception);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    internal static bool IsTransient(Exception exception) =>
        exception switch
        {
            ProviderSeedRequestException requestException => IsTransient(requestException.StatusCode),
            HttpRequestException requestException when requestException.StatusCode is not null =>
                IsTransient(requestException.StatusCode.Value),
            HttpRequestException => true,
            TaskCanceledException => true,
            _ => false
        };

    internal static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    internal static TimeSpan RetryDelay(int failedAttempt) =>
        TimeSpan.FromMilliseconds(Math.Min(
            15_000,
            250 * Math.Pow(2, failedAttempt - 1)));
}
