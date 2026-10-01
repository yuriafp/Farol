namespace Modern.Core.Pricing;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class PriceCalculator(IClock clock)
{
    public decimal Total(IEnumerable<decimal> prices, decimal weekendDiscount)
    {
        var subtotal = prices.Sum();
        var discount = IsWeekend(clock.UtcNow) ? weekendDiscount : 0m;
        return Math.Round(subtotal * (1 - discount), 2);
    }

    private static bool IsWeekend(DateTime date) =>
#if NET
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
#else
        date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
#endif
}
