using System.Text;

namespace ConInfrastructure.Tray;

/// <summary>What the user typed into the password prompt. Printing it (logs, debugger, test output)
/// never shows the password.</summary>
public sealed record ScoutPasswordAnswer(string Password, bool Remember)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Password = ***, Remember = ").Append(Remember);
        return true;
    }
}

public enum ScoutOutcomeKind
{
    /// <summary>The server took the items; <see cref="ScoutOutcome.Result"/> carries the totals.</summary>
    Sent,
    /// <summary>The password prompt budget was spent and the server still said no.</summary>
    PasswordNotAccepted,
    /// <summary>The user cancelled the password prompt or declined to send over plain HTTP.</summary>
    Cancelled,
    /// <summary>Any other failure; <see cref="ScoutResult.Message"/> says what.</summary>
    Failed,
}

/// <param name="Result">The client's last answer.</param>
public sealed record ScoutOutcome(ScoutOutcomeKind Kind, ScoutResult Result);

/// <summary>
/// The tray scout's send loop, kept for the life of the tray. One send:
/// starts with the password saved for that server (if any); asks for a password at most
/// <see cref="MaxPrompts"/> times; saves a typed password only after the server accepts it, and only
/// when the user ticked Remember; and asks before sending the password over plain HTTP, remembering a
/// yes for that one server until <see cref="ForgetConsent"/> (the tray's "Forget saved password").
/// The prompts are the shell's: <paramref name="promptPassword"/> gets the server and whether the last
/// password was rejected, and returns null on cancel; <paramref name="confirmInsecure"/> returns the
/// user's yes or no.
/// </summary>
/// <remarks>
/// The delegates run on the caller's synchronization context (the tray's UI thread), because the awaits
/// in <see cref="SendAsync"/> resume there. Do NOT add ConfigureAwait(false) to them: a prompt shown from
/// a thread-pool thread would not be a legal UI call.
/// </remarks>
public sealed class ScoutSession(
    IScoutPasswordStore store,
    Func<Uri, bool, ScoutPasswordAnswer?> promptPassword,
    Func<Uri, bool> confirmInsecure)
{
    public const int MaxPrompts = 3;

    private string? _insecureAuthority;   // the one server the user said yes to for plain HTTP, this session only

    /// <summary>The user forgot the saved password: the plain-HTTP yes goes with it.</summary>
    public void ForgetConsent() => _insecureAuthority = null;

    public async Task<ScoutOutcome> SendAsync(ScoutClient client, IReadOnlyList<ScoutItem> items, CancellationToken ct)
    {
        var server = client.Server;
        var authority = server.Authority.ToLowerInvariant();
        var password = store.Load(server);
        string? toRemember = null;            // a typed password, saved only once the server accepts it
        var prompts = 0;                      // only password prompts count against the budget
        while (true)
        {
            var allowInsecure = _insecureAuthority == authority;
            var result = await client.SendAsync(items, password, allowInsecure, ct);
            switch (result.Failure)
            {
                case ScoutFailure.None:
                    if (toRemember is not null)
                        store.Save(server, toRemember);
                    return new(ScoutOutcomeKind.Sent, result);
                case ScoutFailure.PasswordRequired:
                case ScoutFailure.WrongPassword:
                    if (prompts == MaxPrompts)
                        return new(ScoutOutcomeKind.PasswordNotAccepted, result);   // budget spent
                    prompts++;
                    var answer = promptPassword(server, result.Failure == ScoutFailure.WrongPassword);
                    if (answer is null)
                        return new(ScoutOutcomeKind.Cancelled, result);
                    password = answer.Password;
                    toRemember = answer.Remember ? password : null;
                    break;
                case ScoutFailure.InsecureTransport:
                    if (!confirmInsecure(server))
                        return new(ScoutOutcomeKind.Cancelled, result);
                    _insecureAuthority = authority;
                    break;
                default:
                    return new(ScoutOutcomeKind.Failed, result);
            }
        }
    }
}
