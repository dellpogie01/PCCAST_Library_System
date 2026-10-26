using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web;

public static class DatabaseLocation
{
    /// <summary>
    /// Database file path: <c>Database:Path</c> when configured (use an absolute path on persistent
    /// storage in deployment), otherwise <c>App_Data/pccast-library.db</c> under the content root.
    /// </summary>
    public static string Resolve(IConfiguration configuration, string contentRoot)
    {
        var configured = configuration["Database:Path"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "App_Data", "pccast-library.db")
            : Path.GetFullPath(configured, contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Applies pending migrations and enables write-ahead logging for better read/write concurrency.</summary>
    public static async Task MigrateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>Operational commands: <c>hash-password</c>, <c>backup</c>, <c>seed-demo</c>.</summary>
public static class Cli
{
    private static readonly string[] Commands = ["hash-password", "backup", "seed-demo"];

    public static bool IsCommand(string arg) => Commands.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "hash-password": return HashPassword(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
            case "backup": return await BackupAsync(app, args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
            case "seed-demo": return await SeedAsync(app);
            default: return 2;
        }
    }

    private static int HashPassword(string? passwordArg = null)
    {
        string password;
        if (!string.IsNullOrEmpty(passwordArg))
        {
            password = passwordArg;
        }
        else
        {
            Console.Write("New administrator password (min. 12 characters): ");
            password = ReadHidden();
            Console.Write("Repeat password: ");
            var repeat = ReadHidden();
            if (password != repeat) { Console.Error.WriteLine("Passwords do not match."); return 1; }
        }

        if (password.Length < 12) { Console.Error.WriteLine("Password must be at least 12 characters."); return 1; }

        Console.WriteLine();
        Console.WriteLine("Password hash (not reversible; safe to store in configuration):");
        Console.WriteLine(Services.AdminAuthenticator.HashPassword(password));
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected) return (Console.ReadLine() ?? "").TrimEnd('\r', '\n');
        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }

    private static async Task<int> BackupAsync(WebApplication app, string? target)
    {
        await DatabaseLocation.MigrateAsync(app.Services);
        target ??= Path.Combine(AppContext.BaseDirectory, $"pccast-library-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        target = Path.GetFullPath(target);
        if (File.Exists(target)) { Console.Error.WriteLine($"Refusing to overwrite existing file: {target}"); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        // VACUUM INTO writes a consistent snapshot even while the application is running.
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {target}");
        Console.WriteLine($"Backup written to {target}");
        return 0;
    }

    private static async Task<int> SeedAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            Console.Error.WriteLine("seed-demo only runs in the Development environment (set ASPNETCORE_ENVIRONMENT=Development).");
            return 1;
        }
        await DatabaseLocation.MigrateAsync(app.Services);
        using var scope = app.Services.CreateScope();
        var seeded = await DemoDataSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<LibraryDbContext>(),
            scope.ServiceProvider.GetRequiredService<Services.IPhilippineClock>());
        Console.WriteLine(seeded ? "Fictional demo data added." : "Database already contains data; nothing was added.");
        return 0;
    }
}
