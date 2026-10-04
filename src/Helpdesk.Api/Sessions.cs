using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api;

public sealed class Sessions
{
    public static readonly PasswordHasher<User> Hasher = new(Options.Create(new PasswordHasherOptions
    {
        IterationCount = 210_000
    }));
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly User dummy = new(Guid.Empty, "dummy", "user", "");
    private readonly string dummyHash;
    public Sessions() => dummyHash = Hasher.HashPassword(dummy, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public bool VerifyPassword(User? user, string password)
    {
        var result = Hasher.VerifyHashedPassword(user ?? dummy, user?.PasswordHash ?? dummyHash, password);
        return user is { Active: true } && result != PasswordVerificationResult.Failed;
    }
    public string Create(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in sessions)
            if (entry.Value.ExpiresAt <= now) sessions.TryRemove(entry.Key, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[Digest(token)] = new Session(userId, now.AddMinutes(30));
        return token;
    }
    public Guid? Resolve(string authorization)
    {
        var token = Token(authorization);
        if (token is null || !sessions.TryGetValue(Digest(token), out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(Digest(token), out _);
            return null;
        }
        return session.UserId;
    }
    public void Revoke(string authorization)
    {
        var token = Token(authorization);
        if (token is not null) sessions.TryRemove(Digest(token), out _);
    }
    private static string? Token(string authorization) => authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && authorization.Length == 71 && authorization[7..].All(Uri.IsHexDigit)
        ? authorization[7..].ToUpperInvariant() : null;
    private static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record Session(Guid UserId, DateTimeOffset ExpiresAt);
}
