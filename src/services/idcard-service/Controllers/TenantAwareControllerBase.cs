using Microsoft.AspNetCore.Mvc;

namespace IdCardService.Controllers;

[ApiController]
public abstract class TenantAwareControllerBase : ControllerBase
{
    public string TenantId { get; set; } = string.Empty;
}
