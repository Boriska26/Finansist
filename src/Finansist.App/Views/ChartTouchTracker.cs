namespace Finansist.App.Views;

internal enum ChartTouchDirection { Pending, Horizontal, Vertical }

// Lock the direction once per touch sequence, after Android's touch slop.
internal sealed class ChartTouchTracker
{
    private float startX;
    private float startY;
    public ChartTouchDirection Direction { get; private set; }
    public void Begin(float x, float y)
    {
        startX = x; startY = y; Direction = ChartTouchDirection.Pending;
    }
    public ChartTouchDirection Move(float x, float y, float slop)
    {
        if (Direction != ChartTouchDirection.Pending) return Direction;
        var dx = Math.Abs(x - startX); var dy = Math.Abs(y - startY);
        if (Math.Max(dx, dy) <= slop) return Direction;
        // Diagonal movements favour scrolling. Horizontal selection needs clear intent.
        Direction = dx > dy * 1.25f ? ChartTouchDirection.Horizontal : ChartTouchDirection.Vertical;
        return Direction;
    }
    public void End() => Direction = ChartTouchDirection.Pending;
}
