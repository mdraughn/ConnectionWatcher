using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ConWatch.Auth;

/// <summary>
/// Redirects a locked-out browser to the dashboard's admin prompt instead of
/// rendering an admin page. Applied by convention in Program.cs to every
/// page under /Config plus Admin, Data, Setup and Traceroute, so a new admin
/// page cannot forget the check. Inert while no password is set.
/// </summary>
public sealed class AdminLockPageFilter(AdminGate gate) : IAsyncPageFilter
{
    public const string RedirectTarget = "/?admin=1";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        if (gate.IsLocked(context.HttpContext.User))
        {
            context.Result = new RedirectResult(RedirectTarget);
            return;
        }
        await next();
    }
}
