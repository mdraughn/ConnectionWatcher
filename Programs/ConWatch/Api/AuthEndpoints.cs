using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ConWatch.Auth;

namespace ConWatch.Api;

/// <summary>Login/logout and password management for the admin gate.</summary>
public static class AuthEndpoints
{
    public record LoginRequest(string? Password);
    public record SetPasswordRequest(string? Current, string? Password);
    public record RemovePasswordRequest(string? Current);

    private static Task SignInAdminAsync(HttpContext http) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin")],
                CookieAuthenticationDefaults.AuthenticationScheme)));

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/status", (AdminGate gate, HttpContext http) => Results.Ok(new
        {
            Enabled = gate.IsEnabled,
            Admin = http.User.Identity?.IsAuthenticated == true,
            ResetPath = gate.FilePath,
        }));

        auth.MapPost("/login", async (LoginRequest request, AdminGate gate, HttpContext http) =>
        {
            if (!gate.IsEnabled)
                return Results.BadRequest(new { errors = new[] { "No admin password is set." } });
            if (!await gate.VerifyOrDelayAsync(request.Password))
                return Results.Unauthorized();
            await SignInAdminAsync(http);
            return Results.NoContent();
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapPost("/set-password", async (SetPasswordRequest request, AdminGate gate, HttpContext http) =>
        {
            if (request.Password is not { Length: >= 4 })
                return Results.BadRequest(new { errors = new[] { "Password must be at least 4 characters." } });
            if (!await gate.VerifyOrDelayAsync(request.Current))
                return Results.Unauthorized();
            gate.Set(request.Password);
            await SignInAdminAsync(http); // never lock the admin out of the page they're on
            return Results.NoContent();
        });

        auth.MapPost("/remove-password", async (RemovePasswordRequest request, AdminGate gate) =>
        {
            if (!gate.IsEnabled)
                return Results.BadRequest(new { errors = new[] { "No admin password is set." } });
            if (!await gate.VerifyOrDelayAsync(request.Current))
                return Results.Unauthorized();
            gate.Remove();
            return Results.NoContent();
        });
    }
}
