using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Services;

public sealed record ServiceResult(bool Succeeded, string? Error = null)
{
    public static ServiceResult Ok() => new(true);
    public static ServiceResult Fail(string error) => new(false, error);
}

public sealed record ServiceResult<T>(bool Succeeded, T? Value = default, string? Error = null)
{
    public static ServiceResult<T> Ok(T value) => new(true, value);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
}

/// <summary>
/// Borrowing, returning, and fine payments. Every rule that must hold under concurrency is
/// enforced by the database (filtered unique index, check constraints, guarded UPDATE
/// statements); the pre-checks here only produce friendly messages.
/// </summary>
public sealed class LoanService(
    LibraryDbContext db, IPhilippineClock clock, FineService fines, IOptions<LibraryOptions> options)
{
    private readonly LibraryOptions _options = options.Value;

    public async Task<ServiceResult<Borrower>> BorrowAsync(
        int studentId, int bookId, DateOnly? dueDate, string? notes, CancellationToken ct = default)
    {
        var today = clock.Today;
        var due = dueDate ?? today.AddDays(_options.DefaultLoanDays);
        if (due < today) return ServiceResult<Borrower>.Fail("The due date cannot be before today.");
        if (due > today.AddDays(_options.MaxLoanDays))
            return ServiceResult<Borrower>.Fail($"The due date cannot be more than {_options.MaxLoanDays} days away.");

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, ct);
            if (student is null) return ServiceResult<Borrower>.Fail("Student not found.");
            if (!student.IsActive) return ServiceResult<Borrower>.Fail("This student is inactive and cannot borrow books.");

            var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book is null) return ServiceResult<Borrower>.Fail("Book copy not found.");
            if (book.IsArchived) return ServiceResult<Borrower>.Fail("This copy is archived and cannot be borrowed.");
            if (await db.Borrowers.AnyAsync(l => l.BookId == bookId && l.ReturnedAtUtc == null, ct))
                return ServiceResult<Borrower>.Fail("This copy is already borrowed.");

            var loan = new Borrower
            {
                StudentId = studentId,
                BookId = bookId,
                BorrowedAtUtc = clock.UtcNow,
                DueDate = due,
                DailyFineRateCentavos = _options.DailyFineRateCentavos,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
            };
            db.Borrowers.Add(loan);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ServiceResult<Borrower>.Ok(loan);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Lost a race: another request created the active loan first.
            db.ChangeTracker.Clear();
            return ServiceResult<Borrower>.Fail("This copy was just borrowed by another transaction.");
        }
        catch (Exception ex) when (DbErrors.IsBusy(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult<Borrower>.Fail("The database is busy. Please try again.");
        }
    }

    /// <summary>Returns a loan exactly once and finalizes its fine as of the Philippine return date.</summary>
    public async Task<ServiceResult<LoanFine>> ReturnAsync(int loanId, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var loan = await db.Borrowers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId, ct);
            if (loan is null) return ServiceResult<LoanFine>.Fail("Loan not found.");
            if (loan.ReturnedAtUtc is not null) return ServiceResult<LoanFine>.Fail("This loan has already been returned.");

            var now = clock.UtcNow;
            var overdueDays = FineCalculator.OverdueDays(loan.DueDate, clock.ToLocalDate(now));
            var assessed = overdueDays * loan.DailyFineRateCentavos;

            // Guarded update: only one concurrent request can flip ReturnedAtUtc from NULL.
            var rows = await db.Borrowers
                .Where(l => l.Id == loanId && l.ReturnedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.ReturnedAtUtc, now)
                    .SetProperty(l => l.AssessedFineCentavos, assessed), ct);
            if (rows == 0) return ServiceResult<LoanFine>.Fail("This loan has already been returned.");

            await tx.CommitAsync(ct);
            return ServiceResult<LoanFine>.Ok(new LoanFine(overdueDays, assessed, true, loan.PaidFineCentavos));
        }
        catch (Exception ex) when (DbErrors.IsBusy(ex))
        {
            return ServiceResult<LoanFine>.Fail("The database is busy. Please try again.");
        }
    }

    /// <summary>
    /// Adds a payment to the cumulative paid amount. <paramref name="expectedPaidCentavos"/> is the
    /// amount the form was rendered with; the update only applies if it is still current, which
    /// makes a double-submitted form harmless.
    /// </summary>
    public async Task<ServiceResult> RecordPaymentAsync(
        int loanId, long amountCentavos, long expectedPaidCentavos, CancellationToken ct = default)
    {
        if (amountCentavos <= 0) return ServiceResult.Fail("The payment must be greater than zero.");

        var loan = await db.Borrowers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId, ct);
        if (loan is null) return ServiceResult.Fail("Loan not found.");
        if (loan.AssessedFineCentavos is not { } assessed)
            return ServiceResult.Fail("Fines can only be paid after the book is returned.");
        if (loan.PaidFineCentavos != expectedPaidCentavos)
            return ServiceResult.Fail("The balance has changed since this page was loaded. Please review and try again.");
        if (amountCentavos > assessed - loan.PaidFineCentavos)
            return ServiceResult.Fail($"The payment exceeds the remaining balance of {Money.Format(assessed - loan.PaidFineCentavos)}.");

        var rows = await db.Borrowers
            .Where(l => l.Id == loanId && l.AssessedFineCentavos != null
                        && l.PaidFineCentavos == expectedPaidCentavos
                        && l.PaidFineCentavos + amountCentavos <= l.AssessedFineCentavos)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PaidFineCentavos, l => l.PaidFineCentavos + amountCentavos), ct);
        return rows == 1
            ? ServiceResult.Ok()
            : ServiceResult.Fail("The payment was not recorded because the balance changed. Please try again.");
    }

    public LoanFine Assess(Borrower loan) => fines.Assess(loan);
}
