using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class SetupModel(AdminGate gate) : PageModel
{
    public IActionResult OnGet() =>
        gate.IsLocked(User) ? Redirect("/?admin=1") : Page();
}
