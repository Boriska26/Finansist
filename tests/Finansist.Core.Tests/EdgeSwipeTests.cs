using Finansist.App.Views;

namespace Finansist.Core.Tests;

public sealed class EdgeSwipeTests
{
    [Theory]
    [InlineData(false, 10, 90)]
    [InlineData(true, 390, 310)]
    public void InwardSwipeFromActiveMiddleEdgeCaptures(bool overview, float start, float end)
    {
        var tracker = new EdgeSwipeTracker(); tracker.Begin(start, 400, 400, 800, overview);
        Assert.True(tracker.Move(end, 402, 8)); Assert.True(tracker.Captured);
        tracker.End(); Assert.False(tracker.Captured);
    }
    [Theory]
    [InlineData(false, 200, 400, 300, 400)]
    [InlineData(true, 200, 400, 100, 400)]
    [InlineData(false, 390, 400, 399, 400)]
    [InlineData(true, 10, 400, 0, 400)]
    [InlineData(false, 10, 100, 100, 100)]
    [InlineData(true, 390, 700, 300, 700)]
    public void ChartsInactiveEdgeAndOutsideMiddleNeverNavigate(bool overview, float x, float y, float endX, float endY)
    {
        var tracker = new EdgeSwipeTracker(); tracker.Begin(x, y, 400, 800, overview);
        Assert.False(tracker.Move(endX, endY, 8)); Assert.False(tracker.Captured);
    }
    [Theory]
    [InlineData(12, 470)]
    [InlineData(12, 330)]
    [InlineData(70, 460)]
    [InlineData(-60, 400)]
    public void VerticalDiagonalAndOutwardMovementReleaseToContent(float x, float y)
    {
        var tracker = new EdgeSwipeTracker(); tracker.Begin(10, 400, 400, 800, false);
        Assert.False(tracker.Move(x, y, 8));
        Assert.False(tracker.Move(150, 400, 8));
    }
    [Fact]
    public void TapOrShortSwipeNeverCapturesAndNewSequenceResetsDirection()
    {
        var tracker = new EdgeSwipeTracker(); tracker.Begin(10, 400, 400, 800, false);
        Assert.False(tracker.Move(14, 401, 8)); Assert.False(tracker.Move(40, 400, 8));
        tracker.End(); Assert.False(tracker.Captured);
        tracker.Begin(390, 400, 400, 800, true);
        Assert.True(tracker.Move(300, 400, 8));
    }
}
