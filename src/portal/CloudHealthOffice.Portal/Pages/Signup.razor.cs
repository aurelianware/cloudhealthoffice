using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudHealthOffice.Portal.Pages;

[AllowAnonymous]
public class SignupModel : PageModel
{
    public void OnGet()
    {
    }
}
