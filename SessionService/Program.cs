// Program.cs

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Hagi.Robust;
//using Hagi.Robust.Probes;

/// <summary>
/// Entry point for the minimal, HTTP-only SessionService.
/// Tracks online users with optional serverId. No RabbitMQ is used.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Main entry point that configures the web application and HTTP endpoints.
    /// </summary>
    public static async Task Main(string[] args)
    {
        // Create builder for the web application.
        var builder = WebApplication.CreateBuilder(args);

        // add hagi.robus
        builder.Services.AddHagiResilience();
        //builder.Services.AddSingleton<IStartupProbe>(sp => new RedisProbe("redis", 6379));

        // Register in-memory store for online users.
        builder.Services.AddSingleton<IOnlineUserStore, InMemoryOnlineUserStore>();

        // Build application.
        var app = builder.Build();

        // Get application version
        var applicationVersion = GetApplicationVersion();
        app.Logger.LogInformation("SessionService v{Version} starting...", applicationVersion);

        // Map endpoints.
        MapSessionEndpoints(app, applicationVersion);

        // Creates endpoint at /health/ready
        app.MapReadinessEndpoint();

        // Start the application.
        await app.RunAsync();
    }

    /// <summary>
    /// Reads the application version from version.txt or falls back to ServiceVersion.Current.
    /// </summary>
    private static string GetApplicationVersion()
    {
        const string versionFilePath = "version.txt";
        const string fallbackVersion = SessionService.ServiceVersion.Current;

        try
        {
            if (File.Exists(versionFilePath))
            {
                return File.ReadAllText(versionFilePath).Trim();
            }
        }
        catch (Exception)
        {
            // If reading fails, fall back to ServiceVersion
        }

        return fallbackVersion;
    }

    /// <summary>
    /// Maps all HTTP endpoints required by RelayService.
    /// </summary>
    private static void MapSessionEndpoints(WebApplication app, string applicationVersion)
    {
        // GET /version
        // Returns service name and version.
        app.MapGet("/version", () => new { service = "SessionService", version = applicationVersion });

        // POST /online/login
        // Registers or updates an online user session.
        app.MapPost("/online/login", async (LoginRequest request, IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine($"[LOGIN] User '{request.Username}' (Role: {request.Role}, ConnectionId: {request.ConnectionId}, ServerId: {request.ServerId ?? "null"})");

                // Upsert online user record.
                await store.UpsertAsync(new OnlineUser
                {
                    Username = request.Username,
                    Role = request.Role,
                    ConnectionId = request.ConnectionId,
                    ServerId = request.ServerId,
                    ConnectedAt = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow
                });

                Console.WriteLine($"[LOGIN] Successfully registered user '{request.Username}'");

                // Return 200 OK.
                return Results.Ok();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[LOGIN ERROR] Failed to register user '{request.Username}': {exception.Message}");
                throw;
            }
        });

        // POST /online/logout
        // Removes a user from the online list.
        app.MapPost("/online/logout", async (LogoutRequest request, IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine($"[LOGOUT] User '{request.Username}' (ConnectionId: {request.ConnectionId})");

                // Remove the user record.
                await store.RemoveAsync(request.Username);

                Console.WriteLine($"[LOGOUT] Successfully removed user '{request.Username}'");

                // Return 204 No Content.
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[LOGOUT ERROR] Failed to remove user '{request.Username}': {exception.Message}");
                throw;
            }
        });

        // POST /online/set-server
        // Updates the current serverId for a user.
        app.MapPost("/online/set-server", async (SetServerRequest request, IOnlineUserStore store) =>
        {
            try
            {
                var serverIdDisplay = request.ServerId ?? "null";
                Console.WriteLine($"[SET-SERVER] User '{request.Username}' setting serverId to '{serverIdDisplay}'");

                // Set serverId for the user.
                var updated = await store.SetServerAsync(request.Username, request.ServerId);

                if (updated)
                {
                    Console.WriteLine($"[SET-SERVER] Successfully updated serverId for user '{request.Username}' to '{serverIdDisplay}'");
                }
                else
                {
                    Console.WriteLine($"[SET-SERVER WARNING] User '{request.Username}' not found");
                }

                // Return 404 if user not found, else 200 OK.
                return updated ? Results.Ok() : Results.NotFound();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[SET-SERVER ERROR] Failed to set server for user '{request.Username}': {exception.Message}");
                throw;
            }
        });

        // GET /online
        // Returns a list of all online users.
        app.MapGet("/online", async (IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine("[QUERY] Fetching all online users");

                // Fetch all users.
                var users = await store.GetAllAsync();

                // Project to DTO to avoid over-sharing internal fields if needed.
                var result = users.Select(u => new OnlineUserView
                {
                    Username = u.Username,
                    Role = u.Role,
                    ServerId = u.ServerId
                }).ToList();

                Console.WriteLine($"[QUERY] Found {result.Count} online users");

                // Return 200 OK with result.
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[QUERY ERROR] Failed to fetch all online users: {exception.Message}");
                throw;
            }
        });

        // GET /online/{username}
        // Returns online status and role/server for a specific user.
        app.MapGet("/online/{username}", async (string username, IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine($"[QUERY] Checking online status for user '{username}'");

                // Get user by username.
                var user = await store.GetAsync(username);

                // Return 200 with online=false if not present.
                if (user is null)
                {
                    Console.WriteLine($"[QUERY] User '{username}' is offline");
                    return Results.Ok(new OnlineStatusView
                    {
                        Online = false,
                        Role = null,
                        ServerId = null,
                        Username = null
                    });
                }

                // Return online details.
                Console.WriteLine($"[QUERY] User '{username}' is online (Role: {user.Role}, ServerId: {user.ServerId ?? "null"})");
                return Results.Ok(new OnlineStatusView
                {
                    Online = true,
                    Role = user.Role,
                    ServerId = user.ServerId,
                    Username = user.Username
                });
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[QUERY ERROR] Failed to check status for user '{username}': {exception.Message}");
                throw;
            }
        });

        // GET /online/players
        // Returns all online users with role == "player".
        app.MapGet("/online/players", async (IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine("[QUERY] Fetching all online players");

                // Filter players.
                var players = (await store.GetAllAsync())
                    .Where(u => string.Equals(u.Role, "player", StringComparison.OrdinalIgnoreCase))
                    .Select(u => new { u.Username })
                    .ToList();

                Console.WriteLine($"[QUERY] Found {players.Count} online players");

                // Return 200 OK with result.
                return Results.Ok(players);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[QUERY ERROR] Failed to fetch online players: {exception.Message}");
                throw;
            }
        });

        // GET /online/server/{serverId}
        // Returns all online users attached to a specific server.
        app.MapGet("/online/server/{serverId}", async (string serverId, IOnlineUserStore store) =>
        {
            try
            {
                Console.WriteLine($"[QUERY] Fetching all users on server '{serverId}'");

                // Filter by server id.
                var users = (await store.GetAllAsync())
                    .Where(u => string.Equals(u.ServerId, serverId, StringComparison.Ordinal))
                    .Select(u => new { u.Username })
                    .ToList();

                Console.WriteLine($"[QUERY] Found {users.Count} users on server '{serverId}'");

                // Return 200 OK with result.
                return Results.Ok(users);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[QUERY ERROR] Failed to fetch users for server '{serverId}': {exception.Message}");
                throw;
            }
        });
    }
}

/// <summary>
/// Contract for a store that tracks online users.
/// </summary>
public interface IOnlineUserStore
{
    /// <summary>
    /// Inserts or updates a user's online record.
    /// </summary>
    Task UpsertAsync(OnlineUser user);

    /// <summary>
    /// Removes a user's online record by username.
    /// </summary>
    Task RemoveAsync(string username);

    /// <summary>
    /// Gets a single user by username, or null if not found.
    /// </summary>
    Task<OnlineUser?> GetAsync(string username);

    /// <summary>
    /// Gets a snapshot of all online users.
    /// </summary>
    Task<IReadOnlyCollection<OnlineUser>> GetAllAsync();

    /// <summary>
    /// Sets the serverId for a user if present.
    /// Pass null to clear the serverId (e.g., when leaving a server).
    /// Returns true if updated, false if user missing.
    /// </summary>
    Task<bool> SetServerAsync(string username, string? serverId);
}

/// <summary>
/// In-memory implementation of IOnlineUserStore using a thread-safe dictionary.
/// </summary>
public sealed class InMemoryOnlineUserStore : IOnlineUserStore
{
    // Thread-safe store keyed by username.
    private readonly ConcurrentDictionary<string, OnlineUser> _users = new();

    /// <summary>
    /// Inserts or updates the user's online record.
    /// </summary>
    public Task UpsertAsync(OnlineUser user)
    {
        // Upsert semantics: the latest connect wins.
        _users.AddOrUpdate(user.Username, user, (_, __) => user);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a user by username.
    /// </summary>
    public Task RemoveAsync(string username)
    {
        // Attempt to remove; ignore the out value.
        _users.TryRemove(username, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a user by username.
    /// </summary>
    public Task<OnlineUser?> GetAsync(string username)
    {
        // Try to resolve current snapshot.
        _users.TryGetValue(username, out var user);
        return Task.FromResult(user);
    }

    /// <summary>
    /// Gets a snapshot of all online users.
    /// </summary>
    public Task<IReadOnlyCollection<OnlineUser>> GetAllAsync()
    {
        // Snapshot into a list to avoid external mutation.
        IReadOnlyCollection<OnlineUser> snapshot = _users.Values.ToList();
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Sets the serverId for an existing user.
    /// Pass null to clear the serverId (e.g., when leaving a server).
    /// </summary>
    public Task<bool> SetServerAsync(string username, string? serverId)
    {
        // Try to get and mutate in place.
        if (_users.TryGetValue(username, out var user))
        {
            user.ServerId = serverId;
            user.LastSeen = DateTime.UtcNow;
            _users[username] = user;
            return Task.FromResult(true);
        }

        // User not found.
        return Task.FromResult(false);
    }
}

/// <summary>
/// Domain model representing an online user session.
/// </summary>
public sealed class OnlineUser
{
    /// <summary>
    /// Username serves as the unique identifier and is provided by Auth/Relay.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Role for authorization decisions (e.g., admin or player).
    /// </summary>
    [Required]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Connection identifier generated by Relay.
    /// </summary>
    [Required]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Optional current server identifier for same-server messaging.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Timestamp when the user was marked online.
    /// </summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// Timestamp of last activity (e.g., heartbeat or state change).
    /// </summary>
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Request payload for /online/login.
/// </summary>
public sealed class LoginRequest
{
    /// <summary>
    /// Username serves as the unique identifier.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Role (admin or player).
    /// </summary>
    [Required]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Relay connection identifier.
    /// </summary>
    [Required]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Optional current server identifier.
    /// </summary>
    public string? ServerId { get; set; }
}

/// <summary>
/// Request payload for /online/logout.
/// </summary>
public sealed class LogoutRequest
{
    /// <summary>
    /// Username.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Relay connection identifier.
    /// </summary>
    [Required]
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Request payload for /online/set-server.
/// </summary>
public sealed class SetServerRequest
{
    /// <summary>
    /// Username.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Target server identifier. Set to null to clear the serverId when leaving a server.
    /// </summary>
    public string? ServerId { get; set; }
}

/// <summary>
/// View model for /online/{username}.
/// </summary>
public sealed class OnlineStatusView
{
    /// <summary>
    /// Whether the user is online.
    /// </summary>
    public bool Online { get; set; }

    /// <summary>
    /// User role if online.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Current server id if online.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Display name if online.
    /// </summary>
    public string? Username { get; set; }
}

/// <summary>
/// Public view model for listing online users.
/// </summary>
public sealed class OnlineUserView
{
    /// <summary>
    /// Username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Role.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Current server id.
    /// </summary>
    public string? ServerId { get; set; }
}
