using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Services;

public sealed record MonthlyPoint(string Label, int Borrowed, int Returned);
public sealed record TitleCount(string Title, string Author, int Count);
public sealed record ActivityItem(DateTime AtUtc, bool IsReturn, int LoanId, string StudentName, string BookTitle, string Accession);
public sealed record OverdueItem(int LoanId, string StudentName, string StudentNumber, string BookTitle,
    string Accession, DateOnly DueDate, int DaysOverdue, long EstimatedFineCentavos);

public sealed class DashboardData
{
    public DateOnly Today { get; init; }
    public int TotalStudents { get; init; }
    public int ActiveStudents { get; init; }
    public int TotalCopies { get; init; }
    public int AvailableCopies { get; init; }
    public int BorrowedCopies { get; init; }
    public int DueToday { get; init; }
    public int OverdueCount { get; init; }
    public int AllTimeTransactions { get; init; }
    /// <summary>Finalized fines on returned loans minus payments received.</summary>
    public long OutstandingAssessedFinesCentavos { get; init; }
    /// <summary>Not final: fines accruing on unreturned overdue loans as of today.</summary>
    public long EstimatedOverdueFinesCentavos { get; init; }
    public long TotalPaymentsCentavos { get; init; }
    public bool HasDemoData { get; init; }
    public IReadOnlyList<MonthlyPoint> Monthly { get; init; } = [];
    public IReadOnlyList<TitleCount> TopTitles { get; init; } = [];
    public IReadOnlyList<ActivityItem> Recent { get; init; } = [];
    public IReadOnlyList<OverdueItem> Overdue { get; init; } = [];
}

/// <summary>Computes every dashboard figure from the database on each request (nothing is cached).</summary>
public sealed class DashboardService(LibraryDbContext db, IPhilippineClock clock, FineService fines)
{
    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var nowUtc = clock.UtcNow;

        // DbContext is not thread-safe, so the queries run sequentially.
        var totalStudents = await db.Students.CountAsync(ct);
        var activeStudents = await db.Students.CountAsync(s => s.IsActive, ct);
        var totalCopies = await db.Books.CountAsync(b => !b.IsArchived, ct);
        var borrowedCopies = await db.Books.CountAsync(b => !b.IsArchived && b.Loans.Any(l => l.ReturnedAtUtc == null), ct);
        var dueToday = await db.Borrowers.CountAsync(l => l.ReturnedAtUtc == null && l.DueDate == today, ct);
        var allTime = await db.Borrowers.CountAsync(ct);
        var outstanding = await db.Borrowers.Where(l => l.AssessedFineCentavos != null)
            .SumAsync(l => (long?)(l.AssessedFineCentavos - l.PaidFineCentavos), ct) ?? 0;
        var payments = await db.Borrowers.SumAsync(l => (long?)l.PaidFineCentavos, ct) ?? 0;
        var hasDemo = await db.Students.AnyAsync(s => s.StudentNumber.StartsWith("DEMO-"), ct);

        var overdueLoans = await db.Borrowers.AsNoTracking()
            .Include(l => l.Student).Include(l => l.Book)
            .Where(l => l.ReturnedAtUtc == null && l.DueDate < today)
            .OrderBy(l => l.DueDate).ThenBy(l => l.Id).ToListAsync(ct);
        var overdueItems = overdueLoans.Select(l =>
        {
            var fine = fines.Assess(l, nowUtc);
            return new OverdueItem(l.Id, l.Student.FullName, l.Student.StudentNumber, l.Book.Title,
                l.Book.AccessionNumber, l.DueDate, fine.OverdueDays, fine.AmountCentavos);
        }).ToList();

        var borrowerTitles = await db.Borrowers
            .Select(l => new { l.Book.Title, l.Book.Author })
            .ToListAsync(ct);

        var topTitles = borrowerTitles
            .GroupBy(b => new { b.Title, b.Author })
            .Select(g => new TitleCount(g.Key.Title, g.Key.Author, g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Title)
            .Take(5).ToList();

        return new DashboardData
        {
            Today = today,
            TotalStudents = totalStudents,
            ActiveStudents = activeStudents,
            TotalCopies = totalCopies,
            BorrowedCopies = borrowedCopies,
            AvailableCopies = totalCopies - borrowedCopies,
            DueToday = dueToday,
            OverdueCount = overdueItems.Count,
            AllTimeTransactions = allTime,
            OutstandingAssessedFinesCentavos = outstanding,
            EstimatedOverdueFinesCentavos = overdueItems.Sum(o => o.EstimatedFineCentavos),
            TotalPaymentsCentavos = payments,
            HasDemoData = hasDemo,
            Monthly = await GetMonthlyAsync(today, ct),
            TopTitles = topTitles,
            Recent = await GetRecentAsync(ct),
            Overdue = overdueItems
        };
    }

    private async Task<IReadOnlyList<MonthlyPoint>> GetMonthlyAsync(DateOnly today, CancellationToken ct)
    {
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var startUtc = clock.StartOfLocalDayUtc(firstMonth);

        var borrowed = await db.Borrowers.Where(l => l.BorrowedAtUtc >= startUtc)
            .Select(l => l.BorrowedAtUtc).ToListAsync(ct);
        var returned = await db.Borrowers.Where(l => l.ReturnedAtUtc != null && l.ReturnedAtUtc >= startUtc)
            .Select(l => l.ReturnedAtUtc!.Value).ToListAsync(ct);

        // Bucket by Philippine month, not UTC month, so late-night activity lands in the right bar.
        int Key(DateTime utc) { var d = clock.ToLocalDate(utc); return d.Year * 12 + d.Month; }
        var borrowedByMonth = borrowed.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var returnedByMonth = returned.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, 12).Select(i =>
        {
            var month = firstMonth.AddMonths(i);
            var key = month.Year * 12 + month.Month;
            return new MonthlyPoint(month.ToString("MMM"),
                borrowedByMonth.GetValueOrDefault(key), returnedByMonth.GetValueOrDefault(key));
        }).ToList();
    }

    private async Task<IReadOnlyList<ActivityItem>> GetRecentAsync(CancellationToken ct)
    {
        var borrowedRecent = await db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
            .OrderByDescending(l => l.BorrowedAtUtc).Take(8).ToListAsync(ct);
        var returnedRecent = await db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
            .Where(l => l.ReturnedAtUtc != null).OrderByDescending(l => l.ReturnedAtUtc).Take(8).ToListAsync(ct);

        return borrowedRecent
            .Select(l => new ActivityItem(l.BorrowedAtUtc, false, l.Id, l.Student.FullName, l.Book.Title, l.Book.AccessionNumber))
            .Concat(returnedRecent.Select(l => new ActivityItem(l.ReturnedAtUtc!.Value, true, l.Id, l.Student.FullName, l.Book.Title, l.Book.AccessionNumber)))
            .OrderByDescending(a => a.AtUtc).Take(8).ToList();
    }
}
