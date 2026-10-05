using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>
/// Development stand-in for the bank: writes each file to a local folder
/// (temporary name, then rename), one sub-folder per tenant. Refused outside
/// Development and Testing, where it would leave full account numbers on a
/// pod's disk.
/// </summary>
public sealed class LocalFolderNachaTransmitter : INachaTransmitter
{
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalFolderNachaTransmitter> _logger;

    public LocalFolderNachaTransmitter(string root, IHostEnvironment environment, ILogger<LocalFolderNachaTransmitter> logger, TimeProvider? clock = null)
    {
        if (!IsAllowed(environment))
            throw new InvalidOperationException(
                "NachaTransmission:Mode=LocalFolder is for Development and Testing only. Configure the tenant's bank SFTP " +
                "(paymentControls.nachaTransmission) instead.");
        _root = root;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static bool IsAllowed(IHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        var fileName = NachaFileNames.Require(request.FileName);
        var tenantFolder = NachaFileNames.Require(request.TenantId);
        var directory = Path.Combine(_root, tenantFolder);
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, fileName);
        if (File.Exists(finalPath))
            throw new NachaTransmissionException($"A file named {fileName} is already in the local outbox; nothing was overwritten.");

        var bytes = NachaFileFacts.Encode(request.Content);
        var facts = NachaFileFacts.From(bytes);
        var tempPath = Path.Combine(directory, NachaFileNames.Temporary(fileName));
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw new NachaTransmissionException($"Writing the NACHA file to the local outbox failed ({ex.GetType().Name}).");
        }

        _logger.LogInformation("Development: NACHA file {FileReference} written to the local outbox for tenant {TenantId}",
            request.FileReference, request.TenantId);
        return Task.FromResult(NachaFileNames.Receipt(request, facts, fileName, $"file://{directory}", _clock.GetUtcNow().UtcDateTime));
    }
}
