using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>What an SFTP connection needs. The credential values live only for the connection.</summary>
public sealed class SftpConnectParameters
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required string HostKeyFingerprint { get; init; }
    public string? PrivateKey { get; init; }
    public string? Password { get; init; }

    /// <summary>Never prints a credential.</summary>
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

/// <summary>One connected SFTP session (the seam tests replace).</summary>
public interface ISftpSession : IDisposable
{
    bool Exists(string path);

    /// <summary>The size of the file at <paramref name="path"/> in bytes, or null when the server does not say.</summary>
    long? Size(string path);

    void Upload(Stream content, string path);
    void Rename(string from, string to);
    void Delete(string path);
}

public interface ISftpSessionFactory
{
    /// <summary>
    /// Connects only when the server presents the pinned host key; otherwise throws.
    /// </summary>
    ISftpSession Connect(SftpConnectParameters parameters);
}

/// <summary>
/// Sends NACHA files to the tenant's bank by SFTP, with the settings tenant-service
/// holds for the tenant and the credentials Key Vault holds under the names
/// those settings give. Refuses to connect without a pinned host key. Uploads
/// to a temporary hidden name and renames it into place, so the bank never
/// picks up a partial file. Never overwrites an existing file. Never logs a
/// credential or the file.
/// </summary>
public sealed class SftpNachaTransmitter : INachaTransmitter, INachaRemoteFileProbe
{
    private readonly INachaTransmissionSettingsSource _settings;
    private readonly INachaSecretReader _secrets;
    private readonly ISftpSessionFactory _sessions;
    private readonly TimeProvider _clock;
    private readonly ILogger<SftpNachaTransmitter> _logger;

    public SftpNachaTransmitter(
        INachaTransmissionSettingsSource settings,
        INachaSecretReader secrets,
        ISftpSessionFactory sessions,
        ILogger<SftpNachaTransmitter> logger,
        TimeProvider? clock = null)
    {
        _settings = settings;
        _secrets = secrets;
        _sessions = sessions;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The tenant's drop, ready to connect to. Holds the credentials only for the call.</summary>
    private sealed record Drop(SftpConnectParameters Parameters, string Directory, string Destination);

    /// <summary>
    /// Settings, then strict host-key pinning (and every other required field)
    /// before any secret is read or any connection is opened, then the secrets.
    /// </summary>
    private async Task<Drop> PrepareAsync(string tenantId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(tenantId, cancellationToken)
            ?? throw new NachaTransmissionException(
                "NACHA transmission is not configured for this tenant (paymentControls.nachaTransmission).", notConfigured: true);

        if (settings.Problem(tenantId) is { } problem)
            throw new NachaTransmissionException(problem, notConfigured: true);

        string? privateKey = null, password = null;
        if (!string.IsNullOrEmpty(settings.PrivateKeySecretRef))
            privateKey = await ReadSecretAsync(settings.PrivateKeySecretRef, "private key", cancellationToken);
        if (!string.IsNullOrEmpty(settings.PasswordSecretRef))
            password = await ReadSecretAsync(settings.PasswordSecretRef, "password", cancellationToken);

        var directory = settings.RemoteDirectory!.TrimEnd('/');
        if (directory.Length == 0) directory = "/";
        var destination = $"sftp://{settings.Host}:{settings.Port}{(directory.StartsWith('/') ? "" : "/")}{directory}";

        return new Drop(new SftpConnectParameters
        {
            Host = settings.Host!,
            Port = settings.Port,
            Username = settings.Username!,
            HostKeyFingerprint = settings.HostKeyFingerprint!,
            PrivateKey = privateKey,
            Password = password,
        }, directory, destination);
    }

    /// <summary>
    /// Read-only look for <paramref name="fileName"/> in the tenant's drop
    /// (exists, and its size). Nothing is uploaded, renamed or deleted.
    /// </summary>
    public async Task<NachaRemoteFileCheck> CheckAsync(string tenantId, string fileName, long expectedByteSize, CancellationToken cancellationToken = default)
    {
        var name = NachaFileNames.Require(fileName);
        var drop = await PrepareAsync(tenantId, cancellationToken);
        var path = Combine(drop.Directory, name);
        try
        {
            using var session = _sessions.Connect(drop.Parameters);
            if (!session.Exists(path))
                return new NachaRemoteFileCheck { Presence = NachaRemoteFilePresence.Absent, Destination = drop.Destination };
            var size = session.Size(path);
            return new NachaRemoteFileCheck
            {
                Presence = size == expectedByteSize ? NachaRemoteFilePresence.Present : NachaRemoteFilePresence.DifferentSize,
                Destination = drop.Destination,
                RemoteByteSize = size,
            };
        }
        catch (NachaTransmissionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("The bank's SFTP drop {Destination} for tenant {TenantId} could not be checked: {Error}",
                drop.Destination, tenantId, ex.GetType().Name);
            throw new NachaTransmissionException($"The bank's SFTP drop could not be checked ({ex.GetType().Name}).");
        }
    }

    public async Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        var fileName = NachaFileNames.Require(request.FileName);
        var drop = await PrepareAsync(request.TenantId, cancellationToken);

        var bytes = NachaFileFacts.Encode(request.Content);
        var facts = NachaFileFacts.From(bytes);
        var directory = drop.Directory;
        var finalPath = Combine(directory, fileName);
        var tempPath = Combine(directory, NachaFileNames.Temporary(fileName));
        var destination = drop.Destination;
        var parameters = drop.Parameters;

        try
        {
            using var session = _sessions.Connect(parameters);
            // File names carry the unique file reference: one already there is this
            // file from an earlier attempt whose outcome was not known.
            if (session.Exists(finalPath))
                throw new NachaTransmissionException(
                    $"A file named {fileName} is already in the bank's drop; nothing was overwritten. It may be this file " +
                    "from an earlier attempt: verify with the bank whether it was received before sending it again.",
                    deliveryUnknown: true);

            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                    session.Upload(stream, tempPath);
            }
            catch
            {
                TryDelete(session, tempPath);
                throw;
            }

            try
            {
                session.Rename(tempPath, finalPath);
            }
            catch (Exception renameError) when (renameError is not OperationCanceledException)
            {
                // The whole file is on the server under the temporary name. A failed
                // rename reply does not say whether the server renamed it (the reply
                // can be lost after the rename), so look before calling it a failure.
                switch (ProbeRename(session, tempPath, finalPath))
                {
                    case RenameProbe.InPlace:
                        _logger.LogWarning(
                            "NACHA file {FileReference} for tenant {TenantId}: the rename reported {Error}, but the file is in place at {Destination}",
                            request.FileReference, request.TenantId, renameError.GetType().Name, destination);
                        break;
                    case RenameProbe.NotRenamed:
                        TryDelete(session, tempPath);
                        throw;
                    default:
                        _logger.LogError(
                            "NACHA file {FileReference} for tenant {TenantId}: uploaded to {Destination} but the rename failed ({Error}) " +
                            "and its outcome could not be checked; delivery is unknown",
                            request.FileReference, request.TenantId, destination, renameError.GetType().Name);
                        throw new NachaTransmissionException(
                            $"The file was uploaded to the bank's SFTP server but renaming it into place failed ({renameError.GetType().Name}) " +
                            "and the result could not be checked: it may or may not have reached the bank. Verify with the bank " +
                            "before sending it again.",
                            deliveryUnknown: true);
                }
            }
        }
        catch (NachaTransmissionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The SSH library's messages can carry server banners; keep only the kind.
            _logger.LogWarning("NACHA file {FileReference} for tenant {TenantId} was not delivered to {Destination}: {Error}",
                request.FileReference, request.TenantId, destination, ex.GetType().Name);
            throw new NachaTransmissionException(
                $"The upload to the bank's SFTP server failed ({ex.GetType().Name}).");
        }

        var receipt = NachaFileNames.Receipt(request, facts, fileName, destination, _clock.GetUtcNow().UtcDateTime);
        _logger.LogInformation(
            "NACHA file {FileReference} for tenant {TenantId} delivered to {Destination} as {RemoteFileName}: " +
            "{EntryCount} entries, {ByteSize} bytes, sha256 {Sha256}",
            receipt.FileReference, receipt.TenantId, destination, fileName, receipt.EntryCount, receipt.ByteSize, receipt.Sha256);
        return receipt;
    }

    private async Task<string> ReadSecretAsync(string name, string what, CancellationToken cancellationToken)
    {
        try
        {
            var value = await _secrets.GetSecretAsync(name, cancellationToken);
            if (string.IsNullOrEmpty(value))
                throw new NachaTransmissionException($"The Key Vault secret for the SFTP {what} is empty.", notConfigured: true);
            return value;
        }
        catch (NachaTransmissionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("The Key Vault secret for the NACHA SFTP {What} could not be read: {Error}", what, ex.GetType().Name);
            throw new NachaTransmissionException($"The Key Vault secret for the SFTP {what} could not be read ({ex.GetType().Name}).");
        }
    }

    private enum RenameProbe { InPlace, NotRenamed, Unknown }

    /// <summary>
    /// After a failed rename: the final name there and the temporary one gone
    /// means the rename happened; the reverse means it did not; anything else
    /// (including a connection that no longer answers) is unknown.
    /// </summary>
    private static RenameProbe ProbeRename(ISftpSession session, string tempPath, string finalPath)
    {
        try
        {
            var final = session.Exists(finalPath);
            var temp = session.Exists(tempPath);
            if (final && !temp) return RenameProbe.InPlace;
            if (!final && temp) return RenameProbe.NotRenamed;
            return RenameProbe.Unknown;
        }
        catch
        {
            return RenameProbe.Unknown;
        }
    }

    private static string Combine(string directory, string name)
        => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    private void TryDelete(ISftpSession session, string path)
    {
        try
        {
            if (session.Exists(path)) session.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not remove the partial NACHA upload: {Error}", ex.GetType().Name);
        }
    }
}

/// <summary>SSH.NET sessions with strict host-key pinning.</summary>
public sealed class SshNetSftpSessionFactory : ISftpSessionFactory
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ISftpSession Connect(SftpConnectParameters parameters)
    {
        // Defence in depth: the transmitter already refused, but this factory
        // must never be the thing that trusts an unknown host.
        if (HostKeyPin.Parse(parameters.HostKeyFingerprint) == null)
            throw new NachaTransmissionException("Refusing to connect: no pinned SSH host key.", notConfigured: true);

        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? keyFile = null;
        if (!string.IsNullOrEmpty(parameters.PrivateKey))
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(parameters.PrivateKey));
            keyFile = string.IsNullOrEmpty(parameters.Password)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, parameters.Password);
            methods.Add(new PrivateKeyAuthenticationMethod(parameters.Username, keyFile));
        }
        else if (!string.IsNullOrEmpty(parameters.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(parameters.Username, parameters.Password));
        }

        var connection = new ConnectionInfo(parameters.Host, parameters.Port, parameters.Username, methods.ToArray())
        {
            Timeout = Timeout
        };
        var client = new SftpClient(connection) { OperationTimeout = Timeout };
        var hostKey = new PinnedHostKeyCheck(parameters.HostKeyFingerprint);
        client.HostKeyReceived += hostKey.OnHostKeyReceived;

        try
        {
            client.Connect();
        }
        catch (Exception) when (hostKey.Rejected)
        {
            client.Dispose();
            throw hostKey.RejectionException();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            keyFile?.Dispose();
        }

        // Never trust a session whose host key was not seen and matched (should
        // the library ever connect without raising HostKeyReceived).
        if (!hostKey.Trusted)
        {
            try { client.Disconnect(); } catch { /* closing an untrusted session */ }
            client.Dispose();
            throw hostKey.RejectionException();
        }

        return new Session(client);
    }

    private sealed class Session : ISftpSession
    {
        private readonly SftpClient _client;

        public Session(SftpClient client) => _client = client;

        public bool Exists(string path) => _client.Exists(path);

        public long? Size(string path) => _client.GetAttributes(path).Size;

        public void Upload(Stream content, string path) => _client.UploadFile(content, path, canOverride: false);

        public void Rename(string from, string to) => _client.RenameFile(from, to);

        public void Delete(string path) => _client.DeleteFile(path);

        public void Dispose()
        {
            try { if (_client.IsConnected) _client.Disconnect(); }
            finally { _client.Dispose(); }
        }
    }
}

/// <summary>
/// The host-key decision of one SSH connection: trusted only when every key
/// the server presents is the pinned one (no trust on first use, no fallback).
/// Wired to SSH.NET's <c>HostKeyReceived</c>; kept apart so the decision is
/// tested with real SSH.NET event arguments, without a server.
/// </summary>
public sealed class PinnedHostKeyCheck
{
    private readonly string? _pinned;
    private bool _mismatchSeen;

    public PinnedHostKeyCheck(string? pinnedFingerprint) => _pinned = pinnedFingerprint;

    /// <summary>The fingerprint the server presented last (SHA256:base64), or null before any.</summary>
    public string? Presented { get; private set; }

    /// <summary>At least one key was presented and every one matched the pin.</summary>
    public bool Trusted => Presented != null && !_mismatchSeen;

    /// <summary>A key was presented that did not match the pin.</summary>
    public bool Rejected => _mismatchSeen;

    public void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var matches = HostKeyPin.Matches(_pinned, e.HostKey);
        Presented = HostKeyPin.Of(e.HostKey);
        if (!matches) _mismatchSeen = true;
        e.CanTrust = matches && !_mismatchSeen;
    }

    /// <summary>Safe to show and log: host key fingerprints are public.</summary>
    public NachaTransmissionException RejectionException()
        => new(Presented == null
                ? "Refusing to send: the bank's SFTP server's host key was not verified against the pinned key."
                : $"Refusing to send: the bank's SFTP server presented host key {Presented}, which is not the pinned key. " +
                  "Nothing was sent. Confirm the bank's key out of band before changing hostKeyFingerprint.",
            hostKeyRejected: true);
}
