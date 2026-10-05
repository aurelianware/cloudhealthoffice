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
public sealed class SftpNachaTransmitter : INachaTransmitter
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

    public async Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        var fileName = NachaFileNames.Require(request.FileName);
        var settings = await _settings.GetAsync(request.TenantId, cancellationToken)
            ?? throw new NachaTransmissionException(
                "NACHA transmission is not configured for this tenant (paymentControls.nachaTransmission).", notConfigured: true);

        // Strict host-key pinning (and every other required field) before any
        // secret is read or any connection is opened.
        if (settings.Problem(request.TenantId) is { } problem)
            throw new NachaTransmissionException(problem, notConfigured: true);

        string? privateKey = null, password = null;
        if (!string.IsNullOrEmpty(settings.PrivateKeySecretRef))
            privateKey = await ReadSecretAsync(settings.PrivateKeySecretRef, "private key", cancellationToken);
        if (!string.IsNullOrEmpty(settings.PasswordSecretRef))
            password = await ReadSecretAsync(settings.PasswordSecretRef, "password", cancellationToken);

        var bytes = NachaFileFacts.Encode(request.Content);
        var facts = NachaFileFacts.From(bytes);
        var directory = settings.RemoteDirectory!.TrimEnd('/');
        if (directory.Length == 0) directory = "/";
        var finalPath = Combine(directory, fileName);
        var tempPath = Combine(directory, NachaFileNames.Temporary(fileName));
        var destination = $"sftp://{settings.Host}:{settings.Port}{(directory.StartsWith('/') ? "" : "/")}{directory}";

        var parameters = new SftpConnectParameters
        {
            Host = settings.Host!,
            Port = settings.Port,
            Username = settings.Username!,
            HostKeyFingerprint = settings.HostKeyFingerprint!,
            PrivateKey = privateKey,
            Password = password,
        };

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
        string? presented = null;
        var trusted = false;
        client.HostKeyReceived += (_, e) =>
        {
            presented = HostKeyPin.Of(e.HostKey);
            trusted = HostKeyPin.Matches(parameters.HostKeyFingerprint, e.HostKey);
            e.CanTrust = trusted;
        };

        try
        {
            client.Connect();
        }
        catch (SshConnectionException) when (presented != null && !trusted)
        {
            client.Dispose();
            throw new NachaTransmissionException(
                $"Refusing to send: the bank's SFTP server presented host key {presented}, which is not the pinned key.");
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

        return new Session(client);
    }

    private sealed class Session : ISftpSession
    {
        private readonly SftpClient _client;

        public Session(SftpClient client) => _client = client;

        public bool Exists(string path) => _client.Exists(path);

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
