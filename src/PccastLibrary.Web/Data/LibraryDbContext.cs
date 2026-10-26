using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PccastLibrary.Web.Data;

public class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Borrower> Borrowers => Set<Borrower>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native date type; make sure every timestamp round-trips as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(e =>
        {
            e.ToTable("Books");
            // NOCASE makes uniqueness and lookups case-insensitive ("acc-1" == "ACC-1").
            e.Property(b => b.AccessionNumber).HasMaxLength(40).IsRequired().UseCollation("NOCASE");
            e.Property(b => b.Isbn).HasMaxLength(20);
            e.Property(b => b.Title).HasMaxLength(300).IsRequired();
            e.Property(b => b.Author).HasMaxLength(200).IsRequired();
            e.Property(b => b.Category).HasMaxLength(100).IsRequired();
            e.Property(b => b.Publisher).HasMaxLength(200);
            e.Property(b => b.ShelfLocation).HasMaxLength(50).IsRequired();
            e.HasIndex(b => b.AccessionNumber).IsUnique();
            e.HasIndex(b => b.Isbn);
            e.HasIndex(b => b.Category);
        });

        modelBuilder.Entity<Student>(e =>
        {
            e.ToTable("Students");
            e.Property(s => s.StudentNumber).HasMaxLength(30).IsRequired().UseCollation("NOCASE");
            e.Property(s => s.FirstName).HasMaxLength(100).IsRequired();
            e.Property(s => s.LastName).HasMaxLength(100).IsRequired();
            e.Property(s => s.GradeLevel).HasMaxLength(50).IsRequired();
            e.Property(s => s.Section).HasMaxLength(100).IsRequired();
            e.Property(s => s.Email).HasMaxLength(200);
            e.Property(s => s.ContactNumber).HasMaxLength(30);
            e.HasIndex(s => s.StudentNumber).IsUnique();
            e.Ignore(s => s.FullName);
        });

        modelBuilder.Entity<Borrower>(e =>
        {
            e.ToTable("Borrowers", t =>
            {
                t.HasCheckConstraint("CK_Borrowers_Money",
                    "\"DailyFineRateCentavos\" >= 0 AND \"PaidFineCentavos\" >= 0 " +
                    "AND (\"AssessedFineCentavos\" IS NULL OR \"AssessedFineCentavos\" >= 0) " +
                    "AND \"PaidFineCentavos\" <= COALESCE(\"AssessedFineCentavos\", 0)");
                // A fine is assessed exactly when the loan is returned, never before or after.
                t.HasCheckConstraint("CK_Borrowers_ReturnState",
                    "(\"ReturnedAtUtc\" IS NULL) = (\"AssessedFineCentavos\" IS NULL)");
            });
            e.Property(b => b.Notes).HasMaxLength(500);
            e.HasOne(b => b.Student).WithMany(s => s.Loans).HasForeignKey(b => b.StudentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Book).WithMany(b => b.Loans).HasForeignKey(b => b.BookId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Ignore(b => b.IsReturned);
            e.HasIndex(b => b.StudentId);
            e.HasIndex(b => b.BookId);
            e.HasIndex(b => b.DueDate);
            // The core integrity rule: at most one unreturned loan per physical copy.
            e.HasIndex(b => b.BookId).IsUnique().HasFilter("\"ReturnedAtUtc\" IS NULL")
                .HasDatabaseName("UX_Borrowers_ActiveLoanPerBook");
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamped>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

public static class DbErrors
{
    /// <summary>True when the failure is a SQLite UNIQUE constraint violation (code 19 / 2067 / 1555).</summary>
    public static bool IsUniqueViolation(Exception ex) =>
        FindSqlite(ex) is { SqliteErrorCode: 19 } e && (e.SqliteExtendedErrorCode is 2067 or 1555 || e.Message.Contains("UNIQUE"));

    /// <summary>True when SQLite reported the database as busy/locked after the busy timeout.</summary>
    public static bool IsBusy(Exception ex) => FindSqlite(ex) is { SqliteErrorCode: 5 or 6 };

    private static SqliteException? FindSqlite(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SqliteException s) return s;
            ex = ex.InnerException;
        }
        return null;
    }
}
