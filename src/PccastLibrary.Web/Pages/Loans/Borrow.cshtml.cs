using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Loans;

/// <summary>Three-step checkout: pick an active student, pick an available copy, confirm the due date.</summary>
public class BorrowModel(LibraryDbContext db, LoanService loans, IPhilippineClock clock, IOptions<LibraryOptions> options) : PageModel
{
    private const int MaxResults = 8;

    [BindProperty(SupportsGet = true)] public int? StudentId { get; set; }
    [BindProperty(SupportsGet = true)] public int? BookId { get; set; }
    [BindProperty(SupportsGet = true)] public string? StudentQ { get; set; }
    [BindProperty(SupportsGet = true)] public string? BookQ { get; set; }
    [BindProperty] public DateOnly? DueDate { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public Student? Student { get; private set; }
    public Book? Book { get; private set; }
    public IReadOnlyList<Student> StudentResults { get; private set; } = [];
    public IReadOnlyList<Book> BookResults { get; private set; } = [];
    public DateOnly Today => clock.Today;
    public DateOnly DefaultDueDate => clock.Today.AddDays(options.Value.DefaultLoanDays);
    public long DailyRateCentavos => options.Value.DailyFineRateCentavos;
    public string? StudentProblem { get; private set; }
    public string? BookProblem { get; private set; }
    public bool CanConfirm => Student is not null && Book is not null && StudentProblem is null && BookProblem is null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        DueDate ??= DefaultDueDate;
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (StudentId is null || BookId is null)
        {
            TempData["Error"] = "Select a student and a book copy first.";
            return RedirectToPage(new { StudentId, BookId });
        }

        var result = await loans.BorrowAsync(StudentId.Value, BookId.Value, DueDate, Notes, ct);
        if (!result.Succeeded)
        {
            // Re-render the same step with the reason; the unique index guarantees no duplicate loan was saved.
            ModelState.AddModelError(string.Empty, result.Error!);
            await LoadAsync(ct);
            return Page();
        }

        TempData["Success"] = $"Borrowing recorded. Due {result.Value!.DueDate:MMM d, yyyy}.";
        return RedirectToPage("/Loans/Details", new { id = result.Value.Id });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (StudentId is not null)
        {
            Student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == StudentId, ct);
            if (Student is null) StudentProblem = "Student not found.";
            else if (!Student.IsActive) StudentProblem = "This student is inactive and cannot borrow books.";
        }
        else if (!string.IsNullOrWhiteSpace(StudentQ))
        {
            var pattern = StudentQ.ToContainsPattern();
            StudentResults = await db.Students.AsNoTracking()
                .Where(s => s.IsActive && (
                    EF.Functions.Like(s.StudentNumber, pattern, "\\") ||
                    EF.Functions.Like(s.FirstName + " " + s.LastName, pattern, "\\") ||
                    EF.Functions.Like(s.LastName + ", " + s.FirstName, pattern, "\\")))
                .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).Take(MaxResults).ToListAsync(ct);
        }

        if (BookId is not null)
        {
            Book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == BookId, ct);
            if (Book is null) BookProblem = "Book copy not found.";
            else if (Book.IsArchived) BookProblem = "This copy is archived and cannot be borrowed.";
            else if (await db.Borrowers.AnyAsync(l => l.BookId == BookId && l.ReturnedAtUtc == null, ct))
                BookProblem = "This copy is already borrowed.";
        }
        else if (!string.IsNullOrWhiteSpace(BookQ))
        {
            var pattern = BookQ.ToContainsPattern();
            BookResults = await db.Books.AsNoTracking()
                .Where(b => !b.IsArchived && !b.Loans.Any(l => l.ReturnedAtUtc == null) && (
                    EF.Functions.Like(b.AccessionNumber, pattern, "\\") ||
                    (b.Isbn != null && EF.Functions.Like(b.Isbn, pattern, "\\")) ||
                    EF.Functions.Like(b.Title, pattern, "\\") ||
                    EF.Functions.Like(b.Author, pattern, "\\")))
                .OrderBy(b => b.Title).ThenBy(b => b.AccessionNumber).Take(MaxResults).ToListAsync(ct);
        }
    }
}
