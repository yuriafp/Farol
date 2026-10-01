using Modern.Core.Pricing;
using Xunit;

namespace Modern.Tests;

public sealed class PriceCalculatorTests
{
    private static readonly DateTime Saturday = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Thursday = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Total_applies_the_weekend_discount()
    {
        var calculator = new PriceCalculator(new FixedClock(Saturday));

        Assert.Equal(90m, calculator.Total([60m, 40m], 0.1m));
    }

    [Fact]
    public void Total_charges_full_price_on_weekdays()
    {
        var calculator = new PriceCalculator(new FixedClock(Thursday));

        Assert.Equal(100m, calculator.Total([60m, 40m], 0.1m));
    }

    [Theory]
    [InlineData(10.004, 10.00)]
    [InlineData(10.006, 10.01)]
    public void Total_rounds_to_cents(decimal price, decimal expected)
    {
        var calculator = new PriceCalculator(new FixedClock(Thursday));

        Assert.Equal(expected, calculator.Total([price], 0m));
    }
}

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_is_in_utc() => Assert.Equal(DateTimeKind.Utc, new SystemClock().UtcNow.Kind);
}

internal sealed class FixedClock(DateTime now) : IClock
{
    public DateTime UtcNow => now;
}
