using System.Security.Claims;

namespace Helpdesk.Api;

public sealed record User(Guid Id, string Login, string Role, string PasswordHash, bool Active = true)
{
    public object Public() => new { Id, Login, Role };
}
public sealed class Ticket(Guid id, Guid ownerId, string title, string description, string status,
    int version, DateTimeOffset createdAt, List<Comment> comments)
{
    public Guid Id { get; set; } = id;
    public Guid OwnerId { get; set; } = ownerId;
    public string Title { get; set; } = title;
    public string Description { get; set; } = description;
    public string Status { get; set; } = status;
    public int Version { get; set; } = version;
    public DateTimeOffset CreatedAt { get; set; } = createdAt;
    public List<Comment> Comments { get; set; } = comments;
    public TicketView Public() => new(Id, OwnerId, Title, Description, Status, Version, CreatedAt);
}
public record TicketView(Guid Id, Guid OwnerId, string Title, string Description, string Status,
    int Version, DateTimeOffset CreatedAt);
public record Comment(Guid Id, Guid AuthorId, string Text, DateTimeOffset CreatedAt);
public record AuditEvent(Guid Id, Guid ActorId, Guid TicketId, string Action, int TicketVersion, DateTimeOffset At);
public sealed class State
{
    public List<User> Users { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public List<AuditEvent> Audit { get; set; } = [];
}
public sealed class ApiError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class Rules
{
    public static bool Text(string? text, int min, int max) => text is not null &&
        text.Length <= max && text.Trim().Length >= min && !text.Contains('\0');
    public static void RequireText(string? text, int min, int max)
    {
        if (!Text(text, min, max)) throw new ApiError(400, "invalid_text");
    }
    public static User Actor(State state, HttpContext http)
    {
        var id = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return state.Users.SingleOrDefault(u => u.Id == id && u.Active)
            ?? throw new ApiError(401, "authentication_required");
    }
    public static bool CanRead(User actor, Ticket ticket) => actor.Role == "operator" ||
        (actor.Role == "user" && ticket.OwnerId == actor.Id);
    public static Ticket VisibleTicket(State state, HttpContext http, Guid id)
    {
        var actor = Actor(state, http);
        var ticket = state.Tickets.SingleOrDefault(t => t.Id == id);
        if (ticket is null || !CanRead(actor, ticket)) throw new ApiError(404, "ticket_not_found");
        return ticket;
    }
    public static void Version(Ticket ticket, int expected)
    {
        if (expected != ticket.Version) throw new ApiError(409, "version_conflict");
    }
    public static void Audit(State state, User actor, Ticket ticket, string action) =>
        state.Audit.Add(new AuditEvent(Guid.NewGuid(), actor.Id, ticket.Id, action, ticket.Version, DateTimeOffset.UtcNow));
}
