using System.Net;
using System.Text;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

internal sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Routes every request to a delegate; records request URIs.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : this((r, n, _) => Task.FromResult(respond(r, n)))
    {
    }

    public List<Uri> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return await _respond(request, Requests.Count, cancellationToken);
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/csv") };

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Captures formatted log messages so tests can assert on them (and on what they omit).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}

internal static class ExclusionTestData
{
    public const string LeieHeader =
        "LASTNAME,FIRSTNAME,MIDNAME,BUSNAME,GENERAL,SPECIALTY,UPIN,NPI,DOB,ADDRESS,CITY,STATE,ZIP,EXCLTYPE,EXCLDATE,REINDATE,WAIVERDATE,WVRSTATE";

    public static string LeieRow(
        string last = "", string first = "", string bus = "", string npi = "0000000000", string dob = "",
        string exclDate = "20200115", string reinDate = "00000000", string waiverDate = "00000000", string waiverState = "") =>
        $"{Q(last)},{Q(first)},,{Q(bus)},PHYSICIAN,FAMILY PRACTICE,,{npi},{dob},\"100 MAIN ST, STE 2\",AUSTIN,TX,78701,1128a1,{exclDate},{reinDate},{waiverDate},{waiverState}";

    public static string Leie(params string[] rows) => LeieHeader + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    public static ExclusionScreeningOptions Options(Action<ExclusionScreeningOptions>? configure = null)
    {
        var o = new ExclusionScreeningOptions();
        o.Leie.Enabled = true;
        o.Leie.MinimumRecordCount = 1;
        o.Sam.MinimumRecordCount = 1;
        o.Sam.ApiKey = "test-secret-key-123";
        configure?.Invoke(o);
        return o;
    }

    public static IOptions<ExclusionScreeningOptions> Wrap(ExclusionScreeningOptions o) =>
        Microsoft.Extensions.Options.Options.Create(o);

    /// <summary>Load LEIE rows into a store as if synced at <paramref name="syncedAt"/>.</summary>
    public static async Task SeedLeieAsync(IExclusionRecordStore store, DateTimeOffset syncedAt, params string[] rows)
    {
        using var reader = new StringReader(Leie(rows));
        var result = await store.ReplaceDatasetAsync(
            ExclusionScreeningSource.OigLeie,
            LeieCsvParser.Parse(reader).ToList(),
            new ExclusionDatasetLoadPolicy { SourceUrl = "test", SyncedAt = syncedAt, MinimumRecordCount = 1 });
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error);
    }

    private static string Q(string value) => value.Contains(',') ? $"\"{value}\"" : value;
}
