using Android.Content;
using Android.Views;
using Finansist.App.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace Finansist.App.Platforms.Android;

public sealed class FinanceChartHandler : GraphicsViewHandler
{
    protected override PlatformTouchGraphicsView CreatePlatformView() => new ScrollFriendlyChartView(Context);
    protected override void DisconnectHandler(PlatformTouchGraphicsView platformView)
    {
        platformView.Parent?.RequestDisallowInterceptTouchEvent(false);
        base.DisconnectHandler(platformView);
    }
}

internal sealed class ScrollFriendlyChartView(Context context) : PlatformTouchGraphicsView(context)
{
    private readonly ChartTouchTracker tracker = new();
    private readonly float slop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 8;
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                tracker.Begin(e.GetX(), e.GetY());
                // Observe the first movement before ScrollView decides to intercept it.
                Parent?.RequestDisallowInterceptTouchEvent(true);
                break;
            case MotionEventActions.Move:
                var direction = tracker.Move(e.GetX(), e.GetY(), slop);
                Parent?.RequestDisallowInterceptTouchEvent(direction != ChartTouchDirection.Vertical);
                // Let ScrollView intercept vertical movement; do not move the date marker.
                if (direction != ChartTouchDirection.Horizontal) return true;
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                tracker.End();
                break;
        }
        return base.OnTouchEvent(e);
    }
}
