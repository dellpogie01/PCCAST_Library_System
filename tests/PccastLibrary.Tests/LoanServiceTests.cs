using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Tests;

public class LoanServiceTests
{
    [Fact]
    public async Task Borrow_DefaultsDueDateToSevenDaysAfterManilaDate_AndSnapshotsRate()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();

        var result = await t.NewLoanService(db, 750).BorrowAsync(s, b, null, null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new DateOnly(2026, 10, 10), result.Value!.DueDate);
        Assert.Equal(750, result.Value.DailyFineRateCentavos);
    }

    [Fact]
    public async Task Borrow_NearManilaMidnight_UsesManilaDate()
    {
        // 2026-10-03 17:00 UTC is already Oct 4 in Manila.
        using var t = new TestDatabase(new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero));
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        var result = await t.NewLoanService(db).BorrowAsync(s, b, null, null);
        Assert.Equal(new DateOnly(2026, 10, 11), result.Value!.DueDate);
    }

    [Fact]
    public async Task Borrow_RejectsUnavailableCopyArchivedCopyAndInactiveStudent()
    {
        using var t = new TestDatabase();
        var s1 = await t.AddStudentAsync("S1");
        var s2 = await t.AddStudentAsync("S2");
        var inactive = await t.AddStudentAsync("S3", active: false);
        var book = await t.AddBookAsync("B1");
        var spare = await t.AddBookAsync("B2");
        await using var db = t.NewContext();
        var svc = t.NewLoanService(db);

        Assert.True((await svc.BorrowAsync(s1, book, null, null)).Succeeded);
        var again = await svc.BorrowAsync(s2, book, null, null);
        Assert.False(again.Succeeded);
        Assert.Contains("already borrowed", again.Error);

        Assert.False((await svc.BorrowAsync(inactive, spare, null, null)).Succeeded);

        await db.Books.Where(x => x.Id == spare).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsArchived, true));
        Assert.False((await svc.BorrowAsync(s2, spare, null, null)).Succeeded);

        Assert.False((await svc.BorrowAsync(s2, spare + 100, null, null)).Succeeded);
        Assert.False((await svc.BorrowAsync(s2, book, new DateOnly(2026, 10, 1), null)).Succeeded, "past due date");
    }

    [Fact]
    public async Task Borrow_ConcurrentRequestsForOneCopy_CreateExactlyOneActiveLoan()
    {
        using var t = new TestDatabase();
        var book = await t.AddBookAsync("B1");
        var students = new List<int>();
        for (var i = 0; i < 12; i++) students.Add(await t.AddStudentAsync($"S{i}"));

        var results = await Task.WhenAll(students.Select(id => Task.Run(async () =>
        {
            await using var db = t.NewContext(); // each request has its own context and connection
            return await t.NewLoanService(db).BorrowAsync(id, book, null, null);
        })));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        await using var check = t.NewContext();
        Assert.Equal(1, await check.Borrowers.CountAsync(l => l.BookId == book && l.ReturnedAtUtc == null));
    }

    [Fact]
    public async Task Database_RejectsSecondActiveLoanEvenWithoutServiceChecks()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        db.Borrowers.Add(new Borrower { StudentId = s, BookId = b, BorrowedAtUtc = DateTime.UtcNow, DueDate = new DateOnly(2026, 10, 10), DailyFineRateCentavos = 500 });
        await db.SaveChangesAsync();

        db.Borrowers.Add(new Borrower { StudentId = s, BookId = b, BorrowedAtUtc = DateTime.UtcNow, DueDate = new DateOnly(2026, 10, 10), DailyFineRateCentavos = 500 });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task Database_EnforcesUniqueNumbersForeignKeysAndMoneyChecks()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));

        await using (var db = t.NewContext())
        {
            db.Books.Add(new Book { AccessionNumber = "b1", Title = "x", Author = "x", Category = "x", ShelfLocation = "x" });
            var dup = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(DbErrors.IsUniqueViolation(dup), "accession numbers are unique, case-insensitively");
        }
        await using (var db = t.NewContext())
        {
            db.Students.Add(new Student { StudentNumber = "s1", FirstName = "x", LastName = "x", GradeLevel = "x", Section = "x" });
            var dup = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(DbErrors.IsUniqueViolation(dup));
        }
        await using (var db = t.NewContext())
        {
            db.Borrowers.Add(new Borrower { StudentId = 999, BookId = b, BorrowedAtUtc = DateTime.UtcNow, DueDate = new DateOnly(2026, 10, 10), DailyFineRateCentavos = 500 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); // foreign key
        }
        await using (var db = t.NewContext())
        {
            db.Borrowers.Add(new Borrower { StudentId = s, BookId = b, BorrowedAtUtc = DateTime.UtcNow, DueDate = new DateOnly(2026, 10, 10),
                DailyFineRateCentavos = 500, ReturnedAtUtc = DateTime.UtcNow, AssessedFineCentavos = 500, PaidFineCentavos = 600 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); // overpayment check constraint
        }
    }

    [Fact]
    public async Task Return_WorksOnce_AndConcurrentReturnsCannotDoubleReturn()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        int loanId;
        await using (var db = t.NewContext())
            loanId = (await t.NewLoanService(db).BorrowAsync(s, b, null, null)).Value!.Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = t.NewContext();
            return await t.NewLoanService(db).ReturnAsync(loanId);
        })));
        Assert.Equal(1, results.Count(r => r.Succeeded));

        await using var verify = t.NewContext();
        var again = await t.NewLoanService(verify).ReturnAsync(loanId);
        Assert.False(again.Succeeded);
        Assert.Contains("already been returned", again.Error);

        // The copy is available again.
        var next = await t.AddStudentAsync("S2");
        Assert.True((await t.NewLoanService(verify).BorrowAsync(next, b, null, null)).Succeeded);
    }

    [Theory]
    [InlineData("2026-10-10T15:59:59Z", 0)]
    [InlineData("2026-10-10T16:00:00Z", 500)]
    [InlineData("2026-10-13T04:00:00Z", 1500)]
    public async Task Return_FinalizesFineAtManilaDate_UsingSnapshotRate(string returnedAtUtc, long expectedFine)
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        var loan = (await t.NewLoanService(db, 500).BorrowAsync(s, b, new DateOnly(2026, 10, 10), null)).Value!;

        t.Time.Set(DateTimeOffset.Parse(returnedAtUtc));
        var result = await t.NewLoanService(db, 9999).ReturnAsync(loan.Id); // later config change must not matter

        Assert.True(result.Succeeded);
        Assert.Equal(expectedFine, result.Value!.AmountCentavos);
        await using var verify = t.NewContext();
        Assert.Equal(expectedFine, (await verify.Borrowers.SingleAsync()).AssessedFineCentavos);
    }

    [Fact]
    public async Task Payments_PartialThenFull_ProduceCorrectBalances_AndRejectInvalidAmounts()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        var svc = t.NewLoanService(db);
        var loan = (await svc.BorrowAsync(s, b, new DateOnly(2026, 10, 10), null)).Value!;

        Assert.False((await svc.RecordPaymentAsync(loan.Id, 100, 0)).Succeeded, "cannot pay before return");

        t.Time.Set(DateTimeOffset.Parse("2026-10-13T04:00:00Z")); // 3 days late → ₱15.00
        await svc.ReturnAsync(loan.Id);

        Assert.False((await svc.RecordPaymentAsync(loan.Id, -100, 0)).Succeeded, "negative");
        Assert.False((await svc.RecordPaymentAsync(loan.Id, 0, 0)).Succeeded, "zero");
        Assert.False((await svc.RecordPaymentAsync(loan.Id, 1501, 0)).Succeeded, "overpayment");

        Assert.True((await svc.RecordPaymentAsync(loan.Id, 600, 0)).Succeeded);
        // Double-submitting the same form (same expected paid amount) is rejected.
        Assert.False((await svc.RecordPaymentAsync(loan.Id, 600, 0)).Succeeded);
        Assert.False((await svc.RecordPaymentAsync(loan.Id, 1000, 600)).Succeeded, "exceeds remaining balance of 900");

        await using (var v = t.NewContext())
        {
            var fine = t.NewLoanService(v).Assess(await v.Borrowers.SingleAsync());
            Assert.Equal(1500, fine.AmountCentavos);
            Assert.Equal(600, fine.PaidCentavos);
            Assert.Equal(900, fine.BalanceCentavos);
        }

        Assert.True((await svc.RecordPaymentAsync(loan.Id, 900, 600)).Succeeded);
        await using var final = t.NewContext();
        Assert.Equal(0, t.NewLoanService(final).Assess(await final.Borrowers.SingleAsync()).BalanceCentavos);
        Assert.False((await svc.RecordPaymentAsync(loan.Id, 1, 1500)).Succeeded, "fully paid");
    }

    [Fact]
    public async Task InactiveStudent_CanStillReturnExistingLoan()
    {
        using var t = new TestDatabase();
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        var loan = (await t.NewLoanService(db).BorrowAsync(s, b, null, null)).Value!;
        await db.Students.Where(x => x.Id == s).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false));

        Assert.True((await t.NewLoanService(db).ReturnAsync(loan.Id)).Succeeded);
    }

    [Fact]
    public async Task Dashboard_TotalsAgreeWithUnderlyingRecords()
    {
        using var t = new TestDatabase(new DateTimeOffset(2026, 10, 26, 4, 0, 0, TimeSpan.Zero));
        await using (var seedDb = t.NewContext())
            Assert.True(await DemoDataSeeder.SeedAsync(seedDb, t.Clock));

        await using var db = t.NewContext();
        var data = await new DashboardService(db, t.Clock, new FineService(t.Clock)).GetAsync();
        var today = t.Clock.Today.ToString("yyyy-MM-dd");

        async Task<long> Scalar(string sql) => (await db.Database.SqlQueryRaw<long>(sql).ToListAsync()).Single();

        Assert.Equal(await Scalar("SELECT COUNT(*) AS Value FROM Students"), data.TotalStudents);
        Assert.Equal(await Scalar("SELECT COUNT(*) AS Value FROM Students WHERE IsActive = 1"), data.ActiveStudents);
        Assert.Equal(await Scalar("SELECT COUNT(*) AS Value FROM Books WHERE IsArchived = 0"), data.TotalCopies);
        Assert.Equal(await Scalar("SELECT COUNT(*) AS Value FROM Borrowers WHERE ReturnedAtUtc IS NULL"), data.BorrowedCopies);
        Assert.Equal(data.TotalCopies - data.BorrowedCopies, data.AvailableCopies);
        Assert.Equal(await Scalar($"SELECT COUNT(*) AS Value FROM Borrowers WHERE ReturnedAtUtc IS NULL AND DueDate = '{today}'"), data.DueToday);
        Assert.Equal(await Scalar($"SELECT COUNT(*) AS Value FROM Borrowers WHERE ReturnedAtUtc IS NULL AND DueDate < '{today}'"), data.OverdueCount);
        Assert.Equal(await Scalar("SELECT COUNT(*) AS Value FROM Borrowers"), data.AllTimeTransactions);
        Assert.Equal(await Scalar("SELECT COALESCE(SUM(AssessedFineCentavos - PaidFineCentavos), 0) AS Value FROM Borrowers WHERE ReturnedAtUtc IS NOT NULL"), data.OutstandingAssessedFinesCentavos);
        Assert.Equal(await Scalar("SELECT COALESCE(SUM(PaidFineCentavos), 0) AS Value FROM Borrowers"), data.TotalPaymentsCentavos);
        Assert.Equal(data.Overdue.Sum(o => o.EstimatedFineCentavos), data.EstimatedOverdueFinesCentavos);
        Assert.True(data.OverdueCount > 0 && data.DueToday > 0 && data.OutstandingAssessedFinesCentavos > 0 && data.TotalPaymentsCentavos > 0);
        Assert.True(data.HasDemoData);
    }

    [Fact]
    public async Task Dashboard_RecalculatesAfterManilaMidnight()
    {
        using var t = new TestDatabase(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero)); // Oct 9, 18:00 Manila
        var (s, b) = (await t.AddStudentAsync("S1"), await t.AddBookAsync("B1"));
        await using var db = t.NewContext();
        await t.NewLoanService(db).BorrowAsync(s, b, new DateOnly(2026, 10, 10), null);
        var dash = new DashboardService(db, t.Clock, new FineService(t.Clock));

        var before = await dash.GetAsync();
        Assert.Equal((0, 0), (before.DueToday, before.OverdueCount));

        t.Time.Set(new DateTimeOffset(2026, 10, 9, 16, 0, 0, TimeSpan.Zero)); // Oct 10 00:00 Manila
        var due = await dash.GetAsync();
        Assert.Equal((1, 0), (due.DueToday, due.OverdueCount));

        t.Time.Set(new DateTimeOffset(2026, 10, 10, 16, 0, 0, TimeSpan.Zero)); // Oct 11 00:00 Manila
        var overdue = await dash.GetAsync();
        Assert.Equal((0, 1), (overdue.DueToday, overdue.OverdueCount));
        Assert.Equal(500, overdue.EstimatedOverdueFinesCentavos);
        Assert.Equal(0, overdue.OutstandingAssessedFinesCentavos); // estimates never mix into finalized fines
    }
}
