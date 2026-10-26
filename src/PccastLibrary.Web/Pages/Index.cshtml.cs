using Microsoft.AspNetCore.Mvc.RazorPages;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages;

public class IndexModel(DashboardService dashboard, IPhilippineClock clock) : PageModel
{
    public DashboardData Data { get; private set; } = null!;
    public string Greeting { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Computed fresh from the database on every request.
        Data = await dashboard.GetAsync(ct);
        var hour = clock.ToLocal(clock.UtcNow).Hour;
        Greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
    }
}
