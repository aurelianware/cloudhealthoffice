using System.Collections.Concurrent;
using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NachaTransmission.Tests;

internal static class Nacha
{
    public const string Tenant = "tenant-1";
    public const string Account = "000123456789";
    public const string Routing = "021000021";
    public const string Pin = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU"; // sha256("")

    /// <summary>An entry detail record (94 chars).</summary>
    public static string Entry(string transactionCode, string routing, string account, long cents, string name, int seq)
        => "6" + transactionCode + routing[..8] + routing[8] + account.PadRight(17) + cents.ToString("0000000000")
           + "ID".PadRight(15) + name.PadRight(22)[..22] + "  " + "0" + "09100001" + seq.ToString("0000000");

    /// <summary>A small file: two debits (27, 37) and one credit (22).</summary>
    public static string File()
        => string.Join("\n",
            "101 091000019 1234567890260216", // header (content irrelevant to the facts)
            "5225CHO PLAN",
            Entry("27", Routing, Account, 150000, "ACME CO", 1),
            Entry("37", "091000019", "5555444433", 2550, "BETA LLC", 2),
            Entry("22", "091000019", "7777", 1000, "GAMMA", 3),
            "8225",
            "9000001") + "\n";

    public static NachaTransmissionRequest Request(string content, string by = "approver-9", string reference = "NACHA-ABC12345")
        => new()
        {
            TenantId = Tenant,
            FileReference = reference,
            FileName = $"ACH-1234567890-{reference}.ach",
            Content = content,
            RunId = "run-1",
            BatchId = reference,
            TransmittedBy = by,
        };

    public static NachaTransmissionSettings Settings(string? pin = Pin) => new()
    {
        Enabled = true,
        Host = "sftp.bank.example",
        Port = 22,
        Username = "cho-plan",
        PrivateKeySecretRef = $"nacha--{Tenant}--key",
        PasswordSecretRef = $"nacha--{Tenant}--passphrase",
        HostKeyFingerprint = pin,
        RemoteDirectory = "/inbound/ach",
    };

    public static IFieldProtector Protector()
        => new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "test-service");
}

internal sealed class StaticSettings : INachaTransmissionSettingsSource
{
    public NachaTransmissionSettings? Value { get; set; }
    public Task<NachaTransmissionSettings?> GetAsync(string tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
}

internal sealed class FakeSecrets : INachaSecretReader
{
    public const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----SECRET-KEY-MATERIAL-----END OPENSSH PRIVATE KEY-----";
    public const string Passphrase = "s3cr3t-passphrase-value";
    public List<string> Reads { get; } = new();

    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        Reads.Add(name);
        return Task.FromResult(name.EndsWith("key", StringComparison.Ordinal) ? PrivateKey : Passphrase);
    }
}

/// <summary>An SFTP server in memory: records every operation.</summary>
internal sealed class FakeSftp : ISftpSessionFactory
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> Operations { get; } = new();
    public List<SftpConnectParameters> Connections { get; } = new();
    public bool FailRename { get; set; }
    public Exception? FailConnect { get; set; }

    public ISftpSession Connect(SftpConnectParameters parameters)
    {
        Connections.Add(parameters);
        if (FailConnect != null) throw FailConnect;
        return new Session(this);
    }

    private sealed class Session : ISftpSession
    {
        private readonly FakeSftp _server;
        public Session(FakeSftp server) => _server = server;

        public bool Exists(string path) { _server.Operations.Add($"exists {path}"); return _server.Files.ContainsKey(path); }

        public void Upload(Stream content, string path)
        {
            _server.Operations.Add($"upload {path}");
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _server.Files[path] = ms.ToArray();
        }

        public void Rename(string from, string to)
        {
            _server.Operations.Add($"rename {from} -> {to}");
            if (_server.FailRename) throw new IOException("rename failed for user cho-plan with key " + FakeSecrets.Passphrase);
            _server.Files[to] = _server.Files[from];
            _server.Files.Remove(from);
        }

        public void Delete(string path) { _server.Operations.Add($"delete {path}"); _server.Files.Remove(path); }

        public void Dispose() => _server.Operations.Add("close");
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, EventId Event, string Message)> Entries { get; } = new();

    public string All => string.Join("\n", Entries.Select(e => e.Message));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, eventId, formatter(state, exception) + (exception == null ? "" : " " + exception)));
}

internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
