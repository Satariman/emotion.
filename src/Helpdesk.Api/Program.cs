using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Helpdesk.Api;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Sessions>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
var app = builder.Build();
await app.Services.GetRequiredService<Store>().InitializeAsync(app.Environment.IsDevelopment());
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    try { await next(context); }
    catch (ApiError error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = error.Code });
    }
    catch (BadHttpRequestException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
    }
    catch (Exception error)
    {
        app.Logger.LogError("Request failed: {ExceptionType}", error.GetType().Name);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "rbpo-helpdesk" }));
app.MapPost("/auth/login", async (LoginRequest input, Store db, Sessions sessions) =>
{
    if (!Rules.Text(input.Login, 1, 64) || !Rules.Text(input.Password, 1, 256))
        throw new ApiError(400, "invalid_credentials_format");
    var user = await db.ReadAsync(state => state.Users.FirstOrDefault(u => u.Login == input.Login));
    if (!sessions.VerifyPassword(user, input.Password!)) throw new ApiError(401, "invalid_credentials");
    var token = sessions.Create(user!.Id);
    return Results.Ok(new { token, expiresInSeconds = 1800, user = user.Public() });
}).RequireRateLimiting("login");

var api = app.MapGroup("");
api.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var sessions = http.RequestServices.GetRequiredService<Sessions>();
    var id = sessions.Resolve(http.Request.Headers.Authorization.ToString());
    if (id is null) throw new ApiError(401, "authentication_required");
    var db = http.RequestServices.GetRequiredService<Store>();
    var user = await db.ReadAsync(state => state.Users.SingleOrDefault(u => u.Id == id && u.Active));
    if (user is null) throw new ApiError(401, "authentication_required");
    http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
    }, "opaque-session"));
    return await next(context);
});
api.MapPost("/auth/logout", (HttpContext http, Sessions sessions) =>
{
    sessions.Revoke(http.Request.Headers.Authorization.ToString());
    return Results.NoContent();
});
api.MapGet("/me", (HttpContext http, Store db) => db.ReadAsync(state => Rules.Actor(state, http).Public()));
api.MapGet("/tickets", (HttpContext http, Store db) => db.ReadAsync(state =>
{
    var user = Rules.Actor(state, http);
    return state.Tickets.Where(t => Rules.CanRead(user, t)).Select(t => t.Public()).ToArray();
}));
api.MapPost("/tickets", async (CreateTicket input, HttpContext http, Store db) =>
{
    Rules.RequireText(input.Title, 1, 120);
    Rules.RequireText(input.Description, 1, 4000);
    var result = await db.WriteAsync(state =>
    {
        var actor = Rules.Actor(state, http);
        if (actor.Role != "user") throw new ApiError(403, "user_role_required");
        var ticket = new Ticket(Guid.NewGuid(), actor.Id, input.Title!.Trim(), input.Description!.Trim(),
            "New", 1, DateTimeOffset.UtcNow, []);
        state.Tickets.Add(ticket);
        Rules.Audit(state, actor, ticket, "ticket_created");
        return ticket.Public();
    });
    return Results.Created($"/tickets/{result.Id}", result);
});
api.MapGet("/tickets/{id:guid}", (Guid id, HttpContext http, Store db) => db.ReadAsync(state =>
    Rules.VisibleTicket(state, http, id).Public()));
api.MapPatch("/tickets/{id:guid}", async (Guid id, EditTicket input, HttpContext http, Store db) =>
{
    Rules.RequireText(input.Title, 1, 120);
    Rules.RequireText(input.Description, 1, 4000);
    return await db.WriteAsync(state =>
    {
        var ticket = Rules.VisibleTicket(state, http, id);
        var actor = Rules.Actor(state, http);
        if (actor.Role != "user" || ticket.OwnerId != actor.Id) throw new ApiError(403, "owner_required");
        Rules.Version(ticket, input.ExpectedVersion);
        if (ticket.Status != "New") throw new ApiError(409, "ticket_already_in_progress");
        ticket.Title = input.Title!.Trim();
        ticket.Description = input.Description!.Trim();
        ticket.Version++;
        Rules.Audit(state, actor, ticket, "ticket_edited");
        return ticket.Public();
    });
});
api.MapPatch("/tickets/{id:guid}/status", async (Guid id, SetStatus input, HttpContext http, Store db) =>
    await db.WriteAsync(state =>
    {
        var ticket = Rules.VisibleTicket(state, http, id);
        var actor = Rules.Actor(state, http);
        if (actor.Role != "operator") throw new ApiError(403, "operator_role_required");
        Rules.Version(ticket, input.ExpectedVersion);
        if (!((ticket.Status == "New" && input.Status == "InProgress") ||
              (ticket.Status == "InProgress" && input.Status == "Resolved")))
            throw new ApiError(409, "invalid_status_transition");
        ticket.Status = input.Status!;
        ticket.Version++;
        Rules.Audit(state, actor, ticket, "status_changed");
        return ticket.Public();
    }));
api.MapGet("/tickets/{id:guid}/comments", (Guid id, HttpContext http, Store db) =>
    db.ReadAsync(state => Rules.VisibleTicket(state, http, id).Comments.ToArray()));
api.MapPost("/tickets/{id:guid}/comments", async (Guid id, AddComment input, HttpContext http, Store db) =>
{
    Rules.RequireText(input.Text, 1, 2000);
    return await db.WriteAsync(state =>
    {
        var ticket = Rules.VisibleTicket(state, http, id);
        var actor = Rules.Actor(state, http);
        Rules.Version(ticket, input.ExpectedVersion);
        if (ticket.Status == "Resolved") throw new ApiError(409, "ticket_resolved");
        var comment = new Comment(Guid.NewGuid(), actor.Id, input.Text!.Trim(), DateTimeOffset.UtcNow);
        ticket.Comments.Add(comment);
        ticket.Version++;
        Rules.Audit(state, actor, ticket, "comment_added");
        return new { comment, ticketVersion = ticket.Version };
    });
});
api.MapGet("/audit", (HttpContext http, Store db) => db.ReadAsync(state =>
{
    if (Rules.Actor(state, http).Role != "operator") throw new ApiError(403, "operator_role_required");
    return state.Audit.ToArray();
}));
app.Run();

record LoginRequest(string? Login, string? Password);
record CreateTicket(string? Title, string? Description);
record EditTicket(string? Title, string? Description, int ExpectedVersion);
record SetStatus(string? Status, int ExpectedVersion);
record AddComment(string? Text, int ExpectedVersion);
