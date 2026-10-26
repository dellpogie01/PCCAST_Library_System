namespace PccastLibrary.Web.Services;

/// <summary>Provides the current time in UTC and in the Philippine time zone (Asia/Manila, UTC+8, no DST).</summary>
public interface IPhilippineClock
{
    DateTime UtcNow { get; }
    /// <summary>Current Philippine calendar date.</summary>
    DateOnly Today { get; }
    DateTime ToLocal(DateTime utc);
    DateOnly ToLocalDate(DateTime utc);
    /// <summary>UTC instant at which the given Philippine date begins (00:00 local).</summary>
    DateTime StartOfLocalDayUtc(DateOnly localDate);
}

public sealed class PhilippineClock(TimeProvider timeProvider) : IPhilippineClock
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;
    public DateOnly Today => ToLocalDate(UtcNow);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    public DateTime StartOfLocalDayUtc(DateOnly localDate) =>
        TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zone);

    private static TimeZoneInfo ResolveZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Hosts without IANA/ICU data: the Philippines has used a fixed UTC+8 offset since 1978.
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Manila", TimeSpan.FromHours(8),
                "Philippine Standard Time", "Philippine Standard Time");
        }
    }
}
