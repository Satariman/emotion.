using System.Text.Json;

namespace Helpdesk.Api;

// One process owns the file. All checks and writes share the same critical section.
public sealed class Store
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private State state = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public Store(IConfiguration config, IWebHostEnvironment environment)
    {
        path = Path.GetFullPath(config["HELPDESK_DATA_PATH"] ?? Path.Combine(environment.ContentRootPath, "data", "store.json"));
    }
    public async Task InitializeAsync(bool development)
    {
        if (File.Exists(path))
        {
            state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path), Json)
                ?? throw new InvalidDataException("Invalid data store");
            return;
        }
        if (!development) throw new InvalidOperationException("Demo bootstrap requires Development environment.");
        foreach (var (login, role, password) in new[]
        {
            ("alice", "user", "Alice-demo-2026!"),
            ("bob", "user", "Bob-demo-2026!"),
            ("operator", "operator", "Operator-demo-2026!")
        })
        {
            var user = new User(Guid.NewGuid(), login, role, "");
            state.Users.Add(user with { PasswordHash = Sessions.Hasher.HashPassword(user, password) });
        }
        await SaveAsync(state);
    }
    public async Task<T> ReadAsync<T>(Func<State, T> read)
    {
        await gate.WaitAsync();
        try { return read(state); }
        finally { gate.Release(); }
    }
    public async Task<T> WriteAsync<T>(Func<State, T> write)
    {
        await gate.WaitAsync();
        try
        {
            var working = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(state, Json), Json)!;
            var result = write(working);
            await SaveAsync(working);
            state = working;
            return result;
        }
        finally { gate.Release(); }
    }
    private async Task SaveAsync(State value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
