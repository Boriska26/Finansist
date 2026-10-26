using Finansist.Core.Finance;

namespace Finansist.Core.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0,01", 1L)]
    [InlineData("19.99", 1999L)]
    [InlineData("123", 12300L)]
    [InlineData(" 10,5 ", 1050L)]
    [InlineData("1000000000000", Money.MaximumMinor)]
    public void Parse_ProducesExactKopecks(string value, long expected) => Assert.Equal(expected, Money.Parse(value));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,001")]
    [InlineData("1.000,10")]
    [InlineData("1e3")]
    [InlineData("1 2")]
    [InlineData("NaN")]
    [InlineData("1000000000000.01")]
    public void InvalidInput_IsRejectedRatherThanRounded(string value) =>
        Assert.Throws<FinanceValidationException>(() => Money.Parse(value));

    [Fact]
    public void OpeningBalance_AllowsZeroAndNegative()
    {
        Assert.Equal(0, Money.Parse("0", true));
        Assert.Equal(-101, Money.Parse("-1,01", true));
    }

    [Fact]
    public void Ledger_DetectsOverflowRatherThanWrapping()
    {
        var account = new Account("a", "Synthetic", long.MaxValue);
        var operation = new FinanceOperation("o", OperationKind.Income, "a", null, 1, new DateOnly(2026, 1, 1), "", "");
        Assert.Throws<OverflowException>(() => Ledger.Calculate([account], [operation]));
    }
}
