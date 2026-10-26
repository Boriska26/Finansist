using Android.Content;
using Android.Views;
using Finansist.App.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace Finansist.App.Platforms.Android;

public sealed class FinanceSwipeHostHandler : LayoutHandler
{
    private static readonly IPropertyMapper<FinanceSwipeHost, FinanceSwipeHostHandler> SwipeMapper =
        new PropertyMapper<FinanceSwipeHost, FinanceSwipeHostHandler>(LayoutHandler.Mapper)
        {
            [nameof(FinanceSwipeHost.IsOverview)] = (handler, _) => ((EdgeSwipeLayout)handler.PlatformView).UpdateExclusion()
        };
    public FinanceSwipeHostHandler() : base(SwipeMapper) { }
    protected override LayoutViewGroup CreatePlatformView()
    {
        var view = new EdgeSwipeLayout(Context, (FinanceSwipeHost)VirtualView) { CrossPlatformLayout = VirtualView };
        view.SetClipChildren(false);
        return view;
    }
}

internal sealed class EdgeSwipeLayout(Context context, FinanceSwipeHost host) : LayoutViewGroup(context)
{
    private readonly EdgeSwipeTracker tracker = new();
    private readonly float density = context.Resources?.DisplayMetrics?.Density ?? 1;
    private readonly float touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        base.OnLayout(changed, left, top, right, bottom);
        UpdateExclusion();
    }
    public void UpdateExclusion()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        var zoneHeight = (int)(EdgeSwipeTracker.ZoneHeight * density);
        var zoneWidth = (int)(EdgeSwipeTracker.EdgeWidth * density);
        var top = (Height - zoneHeight) / 2;
        using var rectangle = new global::Android.Graphics.Rect(host.IsOverview ? Width - zoneWidth : 0, top,
            host.IsOverview ? Width : zoneWidth, top + zoneHeight);
        SystemGestureExclusionRects = Width > 0 && top >= 0 ? [rectangle] : [];
    }
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        var x = e.GetX() / density; var y = e.GetY() / density;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                tracker.Begin(x, y, Width / density, Height / density, host.IsOverview);
                break;
            case MotionEventActions.Move:
                var wasCaptured = tracker.Captured;
                if (tracker.Move(x, y, touchSlop / density))
                {
                    if (!wasCaptured)
                    {
                        using var cancel = MotionEvent.Obtain(e);
                        if (cancel is null) { tracker.End(); return base.DispatchTouchEvent(e); }
                        cancel.Action = MotionEventActions.Cancel;
                        base.DispatchTouchEvent(cancel);
                    }
                    return true;
                }
                break;
            case MotionEventActions.Up:
                if (tracker.Captured)
                {
                    tracker.End();
                    host.NavigateFromEdge();
                    return true;
                }
                tracker.End();
                break;
            case MotionEventActions.Cancel:
                var captured = tracker.Captured;
                tracker.End();
                if (captured) return true;
                break;
        }
        return base.DispatchTouchEvent(e);
    }
}
