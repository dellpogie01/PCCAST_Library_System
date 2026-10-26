using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Books;

public class BookInput
{
    [Required, StringLength(40), Display(Name = "Accession number")]
    public string AccessionNumber { get; set; } = "";

    [StringLength(20), RegularExpression(@"^[0-9Xx\- ]*$", ErrorMessage = "ISBN may contain only digits, hyphens, spaces and X.")]
    public string? Isbn { get; set; }

    [Required, StringLength(300)] public string Title { get; set; } = "";
    [Required, StringLength(200)] public string Author { get; set; } = "";
    [Required, StringLength(100)] public string Category { get; set; } = "";
    [StringLength(200)] public string? Publisher { get; set; }

    [Range(1000, 2200, ErrorMessage = "Enter a four-digit year.")]
    [Display(Name = "Publication year")]
    public int? PublicationYear { get; set; }

    [Required, StringLength(50), Display(Name = "Shelf location")]
    public string ShelfLocation { get; set; } = "";
}

/// <summary>Serves both "Add book" (no id) and "Edit book" (with id).</summary>
public class EditModel(LibraryDbContext db, IPhilippineClock clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public BookInput Input { get; set; } = new();
    public bool IsNew => Id is null;
    public IReadOnlyList<string> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadCategoriesAsync(ct);
        if (Id is null) return Page();

        var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == Id, ct);
        if (book is null) return NotFound();
        Input = new BookInput
        {
            AccessionNumber = book.AccessionNumber, Isbn = book.Isbn, Title = book.Title, Author = book.Author,
            Category = book.Category, Publisher = book.Publisher, PublicationYear = book.PublicationYear,
            ShelfLocation = book.ShelfLocation
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadCategoriesAsync(ct);
        Normalize();
        if (Input.PublicationYear > clock.Today.Year + 1)
            ModelState.AddModelError("Input.PublicationYear", "The publication year cannot be in the future.");

        // Pre-check for a friendly message; the unique index remains the real guard.
        if (ModelState.IsValid &&
            await db.Books.AnyAsync(b => b.AccessionNumber == Input.AccessionNumber && b.Id != Id, ct))
            ModelState.AddModelError("Input.AccessionNumber", "This accession number is already used by another copy.");
        if (!ModelState.IsValid) return Page();

        Book book;
        if (Id is null)
        {
            book = new Book();
            db.Books.Add(book);
        }
        else
        {
            var existing = await db.Books.FirstOrDefaultAsync(b => b.Id == Id, ct);
            if (existing is null) return NotFound();
            book = existing;
        }

        book.AccessionNumber = Input.AccessionNumber;
        book.Isbn = Input.Isbn;
        book.Title = Input.Title;
        book.Author = Input.Author;
        book.Category = Input.Category;
        book.Publisher = Input.Publisher;
        book.PublicationYear = Input.PublicationYear;
        book.ShelfLocation = Input.ShelfLocation;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            ModelState.AddModelError("Input.AccessionNumber", "This accession number is already used by another copy.");
            return Page();
        }

        TempData["Success"] = IsNew ? "The book copy was added." : "The book copy was updated.";
        return RedirectToPage("/Books/Details", new { id = book.Id });
    }

    private void Normalize()
    {
        Input.AccessionNumber = Input.AccessionNumber?.Trim() ?? "";
        Input.Title = Input.Title?.Trim() ?? "";
        Input.Author = Input.Author?.Trim() ?? "";
        Input.Category = Input.Category?.Trim() ?? "";
        Input.ShelfLocation = Input.ShelfLocation?.Trim() ?? "";
        Input.Isbn = string.IsNullOrWhiteSpace(Input.Isbn) ? null : Input.Isbn.Trim();
        Input.Publisher = string.IsNullOrWhiteSpace(Input.Publisher) ? null : Input.Publisher.Trim();
    }

    private async Task LoadCategoriesAsync(CancellationToken ct) =>
        Categories = await db.Books.Select(b => b.Category).Distinct().OrderBy(c => c).ToListAsync(ct);
}
