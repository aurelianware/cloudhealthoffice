namespace CloudHealthOffice.Portal.Services;

// Operating Mode
public interface IOperatingModeService
{
    Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId);
}
