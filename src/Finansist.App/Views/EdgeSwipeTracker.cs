namespace Finansist.App.Views;

internal sealed class EdgeSwipeTracker
{
    public const float EdgeWidth = 28;
    public const float ZoneHeight = 160;
    public const float Distance = 56;
    private float startX;
    private float startY;
    private bool overview;
    private bool eligible;
    public bool Captured { get; private set; }
    public void Begin(float x, float y, float width, float height, bool isOverview)
    {
        startX = x; startY = y; overview = isOverview; Captured = false;
        eligible = width > EdgeWidth * 2 && height >= ZoneHeight &&
            Math.Abs(y - height / 2) <= ZoneHeight / 2 &&
            (overview ? x >= width - EdgeWidth && x <= width : x >= 0 && x <= EdgeWidth);
    }
    public bool Move(float x, float y, float slop)
    {
        if (Captured) return true;
        if (!eligible) return false;
        var inward = (x - startX) * (overview ? -1 : 1);
        var vertical = Math.Abs(y - startY);
        if (Math.Max(Math.Abs(x - startX), vertical) <= slop) return false;
        if (inward <= vertical * 1.5f) { eligible = false; return false; }
        Captured = inward >= Math.Max(Distance, slop);
        return Captured;
    }
    public void End() { eligible = false; Captured = false; }
}
