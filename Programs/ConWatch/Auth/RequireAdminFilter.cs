namespace ConWatch.Auth;

/// <summary>401s any request to a protected endpoint when the gate is on
/// and the caller lacks the admin cookie. Inert while no password is set.</summary>
public class RequireAdminFilter(AdminGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (gate.IsLocked(context.HttpContext.User))
            return Results.Json(new { errors = new[] { "Admin password required." } }, statusCode: 401);
        return await next(context);
    }
}
