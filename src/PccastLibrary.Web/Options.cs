namespace PccastLibrary.Web;

/// <summary>Administrator credentials. Supplied by user secrets or environment variables, never by source control.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = "";
    /// <summary>Output of the <c>hash-password</c> command (ASP.NET Core Identity password hash).</summary>
    public string PasswordHash { get; set; } = "";
}

public sealed class LibraryOptions
{
    public const string SectionName = "Library";
    /// <summary>Default fine per overdue calendar day, in centavos (500 = ₱5.00). Snapshotted into each loan.</summary>
    public long DailyFineRateCentavos { get; set; } = 500;
    public int DefaultLoanDays { get; set; } = 7;
    public int MaxLoanDays { get; set; } = 120;
    public int SessionMinutes { get; set; } = 30;
}
