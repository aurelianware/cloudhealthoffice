namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Thrown when a backend service is unreachable or returns a transport-level error.
/// Portal pages should catch this and display a user-friendly MudAlert.
/// </summary>
public class ServiceUnavailableException : Exception
{
    public string ServiceName { get; }

    public ServiceUnavailableException(string serviceName, Exception? innerException = null)
        : base($"Unable to connect to {serviceName}. Please try again or contact your administrator if the problem persists.", innerException)
    {
        ServiceName = serviceName;
    }
}
