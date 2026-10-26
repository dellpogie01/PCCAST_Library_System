using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Loans;

public class ReturnModel(LibraryDbContext db, LoanService loans) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public Borrower? Loan { get; private set; }
    public IReadOnlyList<Borrower> Results { get; private set; } = [];
    public LoanFine? Fine { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Id is not null)
        {
            Loan = await db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
                .FirstOrDefaultAsync(l => l.Id == Id, ct);
            if (Loan is null) return NotFound();
            Fine = loans.Assess(Loan);
            return Page();
        }

        // No loan chosen yet: list active loans, optionally narrowed by a search.
        var query = db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
            .Where(l => l.ReturnedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var pattern = Q.ToContainsPattern();
            query = query.Where(l =>
                EF.Functions.Like(l.Student.StudentNumber, pattern, "\\") ||
                EF.Functions.Like(l.Student.FirstName + " " + l.Student.LastName, pattern, "\\") ||
                EF.Functions.Like(l.Student.LastName + ", " + l.Student.FirstName, pattern, "\\") ||
                EF.Functions.Like(l.Book.AccessionNumber, pattern, "\\") ||
                EF.Functions.Like(l.Book.Title, pattern, "\\"));
        }
        Results = await query.OrderBy(l => l.DueDate).ThenBy(l => l.Id).Take(12).ToListAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Id is null) return BadRequest();
        var result = await loans.ReturnAsync(Id.Value, ct);
        if (!result.Succeeded)
        {
            // Covers duplicate returns: the guarded UPDATE only succeeds once per loan.
            TempData["Error"] = result.Error;
            return RedirectToPage("/Loans/Details", new { id = Id });
        }

        var fine = result.Value!;
        TempData["Success"] = fine.AmountCentavos == 0
            ? "Book returned. No fine is due."
            : $"Book returned. Final fine: {Money.Format(fine.AmountCentavos)} for {fine.OverdueDays} overdue {(fine.OverdueDays == 1 ? "day" : "days")}.";
        return RedirectToPage("/Loans/Details", new { id = Id });
    }
}
