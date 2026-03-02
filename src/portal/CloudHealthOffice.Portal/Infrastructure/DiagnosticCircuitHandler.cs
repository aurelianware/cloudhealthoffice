using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Logs every Blazor circuit lifecycle event at Debug level so that circuit
/// startup failures are visible in pod logs even when detailed errors are off.
/// </summary>
public class DiagnosticCircuitHandler(ILogger<DiagnosticCircuitHandler> logger) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogDebug("Circuit {CircuitId} opened", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogDebug("Circuit {CircuitId} connection up", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogDebug("Circuit {CircuitId} connection down", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogDebug("Circuit {CircuitId} closed", circuit.Id);
        return Task.CompletedTask;
    }
}
