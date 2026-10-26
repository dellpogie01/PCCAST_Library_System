using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PccastLibrary.Web.Services;

/// <summary>
/// Validates the single administrator account against configuration. Only a salted
/// ASP.NET Core Identity password hash is held; the plain password is never stored or logged.
/// </summary>
public sealed class AdminAuthenticator(IOptions<AdminOptions> options, ILogger<AdminAuthenticator> logger)
{
    private static readonly PasswordHasher<string> Hasher = new();
    // Used to burn comparable CPU time when the account is unknown or unconfigured.
    private static readonly string DummyHash = Hasher.HashPassword("dummy", Guid.NewGuid().ToString("N"));
    private readonly AdminOptions _admin = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_admin.Username) && !string.IsNullOrWhiteSpace(_admin.PasswordHash);

    public string DisplayName => string.IsNullOrWhiteSpace(_admin.Username) ? "Administrator" : _admin.Username;

    public static string HashPassword(string password) => Hasher.HashPassword("administrator", password);

    public bool Validate(string? username, string? password)
    {
        username ??= "";
        password ??= "";
        var hash = IsConfigured ? _admin.PasswordHash : DummyHash;

        PasswordVerificationResult result;
        try
        {
            result = Hasher.VerifyHashedPassword("administrator", hash, password);
        }
        catch (FormatException)
        {
            logger.LogError("Admin:PasswordHash is not a valid password hash. Regenerate it with the hash-password command.");
            return false;
        }

        var usernameMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()),
            Encoding.UTF8.GetBytes(_admin.Username.Trim().ToLowerInvariant()));
        return IsConfigured && usernameMatches && result != PasswordVerificationResult.Failed;
    }
}
