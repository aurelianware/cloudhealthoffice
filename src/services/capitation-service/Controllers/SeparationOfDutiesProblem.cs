using CapitationService.Services;
using Microsoft.AspNetCore.Mvc;

namespace CapitationService.Controllers;

/// <summary>The 403 answer when a payment's maker tries to approve or release it.</summary>
internal static class SeparationOfDutiesProblem
{
    public const string Title = "Separation of duties";
    public const string Type = "https://cloudhealthoffice.com/problems/payment-separation-of-duties";

    public static ObjectResult For(ControllerBase controller, SeparationOfDutiesException ex)
        => controller.Problem(
            type: Type,
            title: Title,
            detail: ex.Message,
            statusCode: StatusCodes.Status403Forbidden);
}
