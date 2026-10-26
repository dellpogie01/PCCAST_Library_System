using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PccastLibrary.Web;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Tests;

/// <summary>Deterministic clock for tests.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>A throw-away SQLite file built from the real migrations, with the same WAL setting as the app.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pccast-test-{Guid.NewGuid():N}.db");

    public ManualTimeProvider Time { get; }
    public IPhilippineClock Clock { get; }
    public string Path_ => _path;

    /// <param name="utcNow">Defaults to 2026-10-03 02:00 UTC (10:00 in Manila).</param>
    public TestDatabase(DateTimeOffset? utcNow = null)
    {
        Time = new ManualTimeProvider(utcNow ?? new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero));
        Clock = new PhilippineClock(Time);
        using var db = NewContext();
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public LibraryDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite($"Data Source={_path}").Options);

    public LoanService NewLoanService(LibraryDbContext db, long rateCentavos = 500) =>
        new(db, Clock, new FineService(Clock), Options.Create(new LibraryOptions { DailyFineRateCentavos = rateCentavos }));

    public async Task<int> AddBookAsync(string accession, string title = "Test Title")
    {
        await using var db = NewContext();
        var book = new Book { AccessionNumber = accession, Title = title, Author = "Author", Category = "General", ShelfLocation = "A-1" };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    public async Task<int> AddStudentAsync(string number, bool active = true)
    {
        await using var db = NewContext();
        var student = new Student { StudentNumber = number, FirstName = "Test", LastName = number, GradeLevel = "Grade 11", Section = "A", IsActive = active };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { /* best effort */ }
    }
}
