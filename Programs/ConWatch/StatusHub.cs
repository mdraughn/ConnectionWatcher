using Microsoft.AspNetCore.SignalR;

namespace ConWatch;

/// <summary>Server-to-client only; clients just listen for statusChanged/transition.</summary>
public class StatusHub : Hub
{
}
