using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Books;

public sealed class BookRow
{
    public required Book Book { get; init; }
    public bool IsBorrowed { get; init; }
}

public class IndexModel(LibraryDbContext db) : PageModel
{
    private const int PageSize = 15;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public PagedList<BookRow> Books { get; private set; } = null!;
    public IReadOnlyList<string> Categories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        IQueryable<Book> query = db.Books.AsNoTracking();

        query = Status switch
        {
            "available" => query.Where(b => !b.IsArchived && !b.Loans.Any(l => l.ReturnedAtUtc == null)),
            "borrowed" => query.Where(b => !b.IsArchived && b.Loans.Any(l => l.ReturnedAtUtc == null)),
            "archived" => query.Where(b => b.IsArchived),
            "all" => query,
            _ => query.Where(b => !b.IsArchived) // default view: current inventory
        };

        if (!string.IsNullOrWhiteSpace(Category)) query = query.Where(b => b.Category == Category);

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var pattern = Q.ToContainsPattern();
            query = query.Where(b =>
                EF.Functions.Like(b.AccessionNumber, pattern, "\\") ||
                (b.Isbn != null && EF.Functions.Like(b.Isbn, pattern, "\\")) ||
                EF.Functions.Like(b.Title, pattern, "\\") ||
                EF.Functions.Like(b.Author, pattern, "\\"));
        }

        var desc = Dir == "desc";
        query = (Sort?.ToLowerInvariant()) switch
        {
            "title" => desc ? query.OrderByDescending(b => b.Title) : query.OrderBy(b => b.Title),
            "author" => desc ? query.OrderByDescending(b => b.Author) : query.OrderBy(b => b.Author),
            "category" => desc ? query.OrderByDescending(b => b.Category) : query.OrderBy(b => b.Category),
            "added" => desc ? query.OrderByDescending(b => b.CreatedAtUtc) : query.OrderBy(b => b.CreatedAtUtc),
            _ => desc ? query.OrderByDescending(b => b.AccessionNumber) : query.OrderBy(b => b.AccessionNumber)
        };

        Books = await PagedList<BookRow>.CreateAsync(
            query.Select(b => new BookRow { Book = b, IsBorrowed = b.Loans.Any(l => l.ReturnedAtUtc == null) }),
            PageNumber, PageSize, ct);
        Categories = await db.Books.Select(b => b.Category).Distinct().OrderBy(c => c).ToListAsync(ct);
    }
}
