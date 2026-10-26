using Finansist.App.Views;

namespace Finansist.Core.Tests;

public sealed class ChartTouchTests
{
    [Fact]
    public void SmallMovementKeepsPendingUntilTouchSlopIsCrossed()
    {
        var tracker = new ChartTouchTracker(); tracker.Begin(50, 50);
        Assert.Equal(ChartTouchDirection.Pending, tracker.Move(55, 55, 8));
        Assert.Equal(ChartTouchDirection.Pending, tracker.Move(58, 50, 8));
        Assert.Equal(ChartTouchDirection.Horizontal, tracker.Move(59, 50, 8));
    }

    [Theory]
    [InlineData(100, 4)]
    [InlineData(-100, -4)]
    public void HorizontalSelectionKeepsOwnershipDespiteLaterVerticalDrift(float dx, float dy)
    {
        var tracker = new ChartTouchTracker(); tracker.Begin(200, 200);
        Assert.Equal(ChartTouchDirection.Horizontal, tracker.Move(200 + dx, 200 + dy, 8));
        Assert.Equal(ChartTouchDirection.Horizontal, tracker.Move(200, 350, 8));
        tracker.End(); Assert.Equal(ChartTouchDirection.Pending, tracker.Direction);
    }

    [Theory]
    [InlineData(2, 50)]
    [InlineData(2, -50)]
    [InlineData(50, 50)]
    [InlineData(50, 45)]
    public void VerticalAndDiagonalMovementRemainScrollingEvenAfterHorizontalDrift(float dx, float dy)
    {
        var tracker = new ChartTouchTracker(); tracker.Begin(100, 100);
        Assert.Equal(ChartTouchDirection.Vertical, tracker.Move(100 + dx, 100 + dy, 8));
        Assert.Equal(ChartTouchDirection.Vertical, tracker.Move(300, 100, 8));
        tracker.Begin(0, 0);
        Assert.Equal(ChartTouchDirection.Horizontal, tracker.Move(30, 0, 8));
    }

    [Fact]
    public void UsesDeviceTouchSlopRatherThanTreatingTinyMovementAsDrag()
    {
        var tracker = new ChartTouchTracker(); tracker.Begin(0, 0);
        Assert.Equal(ChartTouchDirection.Pending, tracker.Move(20, 0, 24));
        Assert.Equal(ChartTouchDirection.Vertical, tracker.Move(1, 25, 24));
    }
}
