using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Pages.Books;

public class DetailsModel(LibraryDbContext db) : PageModel
{
    public Book Book { get; private set; } = null!;
    public IReadOnlyList<Borrower> History { get; private set; } = [];
    public Borrower? ActiveLoan => History.FirstOrDefault(l => l.ReturnedAtUtc is null);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct) => await LoadAsync(id, ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken ct)
    {
        // Single guarded UPDATE: archiving succeeds only while no unreturned loan exists for this copy.
        var rows = await db.Books
            .Where(b => b.Id == id && !b.IsArchived && !b.Loans.Any(l => l.ReturnedAtUtc == null))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsArchived, true).SetProperty(b => b.UpdatedAtUtc, DateTime.UtcNow), ct);

        if (rows == 1) TempData["Success"] = "The copy was archived.";
        else if (!await db.Books.AnyAsync(b => b.Id == id, ct)) return NotFound();
        else TempData["Error"] = "This copy cannot be archived while it is borrowed or if it is already archived.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRestoreAsync(int id, CancellationToken ct)
    {
        var rows = await db.Books.Where(b => b.Id == id && b.IsArchived)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsArchived, false).SetProperty(b => b.UpdatedAtUtc, DateTime.UtcNow), ct);
        if (rows == 0 && !await db.Books.AnyAsync(b => b.Id == id, ct)) return NotFound();
        TempData["Success"] = "The copy was restored to the inventory.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return false;
        Book = book;
        History = await db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
            .Where(l => l.BookId == id).OrderByDescending(l => l.BorrowedAtUtc).ToListAsync(ct);
        return true;
    }
}
