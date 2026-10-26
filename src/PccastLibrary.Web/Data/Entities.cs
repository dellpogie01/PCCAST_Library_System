namespace PccastLibrary.Web.Data;

/// <summary>Entities that record when they were created and last modified (UTC).</summary>
public interface ITimestamped
{
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One physical copy of a book. Multiple copies of one title are separate rows with
/// different accession numbers. Availability is derived from <see cref="Borrower"/> rows
/// (an unreturned loan means the copy is out) and is deliberately not stored here.
/// </summary>
public class Book : ITimestamped
{
    public int Id { get; set; }
    public string AccessionNumber { get; set; } = "";
    public string? Isbn { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Publisher { get; set; }
    public int? PublicationYear { get; set; }
    public string ShelfLocation { get; set; } = "";

    /// <summary>Archived copies are retired from inventory but keep their borrowing history.</summary>
    public bool IsArchived { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<Borrower> Loans { get; set; } = new List<Borrower>();
}

public class Student : ITimestamped
{
    public int Id { get; set; }
    public string StudentNumber { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string GradeLevel { get; set; } = "";
    public string Section { get; set; } = "";
    public string? Email { get; set; }
    public string? ContactNumber { get; set; }

    /// <summary>Inactive students cannot create new loans but may still return existing ones.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<Borrower> Loans { get; set; } = new List<Borrower>();

    public string FullName => $"{LastName}, {FirstName}";
}

/// <summary>
/// A borrowing transaction (one student, one physical copy). Despite the table name this is
/// not a list of people. Money is stored as integer centavos.
/// </summary>
public class Borrower
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public int BookId { get; set; }
    public DateTime BorrowedAtUtc { get; set; }

    /// <summary>Due date as a Philippine (Asia/Manila) calendar date.</summary>
    public DateOnly DueDate { get; set; }

    public DateTime? ReturnedAtUtc { get; set; }

    /// <summary>Daily fine rate captured when the loan was created.</summary>
    public long DailyFineRateCentavos { get; set; }

    /// <summary>Finalized fine; null until the loan is returned.</summary>
    public long? AssessedFineCentavos { get; set; }

    /// <summary>Cumulative amount paid against the finalized fine (simple manual tracker).</summary>
    public long PaidFineCentavos { get; set; }

    public string? Notes { get; set; }

    public Student Student { get; set; } = null!;
    public Book Book { get; set; } = null!;

    public bool IsReturned => ReturnedAtUtc.HasValue;
}
