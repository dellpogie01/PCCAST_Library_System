using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Pages.Shared;

public sealed class LoanTableModel
{
    public required IReadOnlyList<Borrower> Loans { get; init; }
    public bool ShowStudent { get; init; } = true;
    public bool ShowBook { get; init; } = true;
    public bool ShowActions { get; init; } = true;
    public string EmptyTitle { get; init; } = "No transactions";
    public string EmptyText { get; init; } = "Nothing to show here yet.";
}
