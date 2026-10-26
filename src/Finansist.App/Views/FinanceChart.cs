using System.Globalization;
using Finansist.Core.Finance;

namespace Finansist.App.Views;

// Rendering and touch selection only; all financial calculations live in Core.
public sealed class FinanceChart : GraphicsView, IDrawable
{
    public static readonly BindableProperty ResultProperty = BindableProperty.Create(nameof(Result), typeof(OverviewResult), typeof(FinanceChart), null, propertyChanged: Changed);
    public static readonly BindableProperty IsBalanceProperty = BindableProperty.Create(nameof(IsBalance), typeof(bool), typeof(FinanceChart), false, propertyChanged: Changed);
    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(FinanceChart), -1, BindingMode.TwoWay, propertyChanged: Changed);
    public OverviewResult? Result { get => (OverviewResult?)GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public bool IsBalance { get => (bool)GetValue(IsBalanceProperty); set => SetValue(IsBalanceProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    private static void Changed(BindableObject sender, object oldValue, object newValue) => ((FinanceChart)sender).Invalidate();
    public FinanceChart()
    {
        Drawable = this;
        StartInteraction += (_, args) => SelectPoint(args.Touches);
        DragInteraction += (_, args) => SelectPoint(args.Touches);
        Loaded += (_, _) => { if (Application.Current is { } app) app.RequestedThemeChanged += ThemeChanged; };
        Unloaded += (_, _) => { if (Application.Current is { } app) app.RequestedThemeChanged -= ThemeChanged; };
    }
    private void SelectPoint(PointF[] touches)
    {
        if (Result is not { Points.Count: > 0 } r || touches.Length == 0 || Width <= 72) return;
        SelectedIndex = Math.Clamp((int)((touches[0].X - 56) / ((Width - 68) / r.Points.Count)), 0, r.Points.Count - 1);
    }
    private void ThemeChanged(object? sender, AppThemeChangedEventArgs args) => Invalidate();
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (Result is not { Points.Count: > 0 } result) return;
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var text = Color.FromArgb(dark ? "#B8BBC4" : "#62666F");
        var grid = Color.FromArgb(dark ? "#3C4048" : "#DDE0E6");
        var blue = Color.FromArgb(dark ? "#A8C7FA" : "#315DA8");
        var orange = Color.FromArgb(dark ? "#E7AE84" : "#A9571D");
        var points = result.Points;
        var values = IsBalance ? points.Where(p => p.BalanceMinor.HasValue).Select(p => (double)p.BalanceMinor!.Value).ToArray()
            : points.SelectMany(p => new[] { (double)p.IncomeMinor, p.ExpenseMinor }).ToArray();
        canvas.FontSize = 11; canvas.FontColor = text;
        if (values.Length == 0)
        {
            canvas.DrawString("Нет истории остатка за эти даты", 8, 95, dirtyRect.Width - 16, 50, HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }
        double min = Math.Min(0, values.Min()), max = Math.Max(0, values.Max());
        if (max == min) max = min + 100;
        var left = 56f; var top = 18f; var width = Math.Max(1, dirtyRect.Width - left - 12); var height = Math.Max(1, dirtyRect.Height - top - 35);
        float Y(double value) => top + height - (float)((value - min) / (max - min) * height);
        var step = width / points.Count;
        float X(int index) => left + step * (index + .5f);
        for (var tick = 0; tick <= 4; tick++)
        {
            var value = min + (max - min) * tick / 4;
            var y = Y(value); canvas.StrokeColor = grid; canvas.StrokeSize = 1; canvas.DrawLine(left, y, left + width, y);
            canvas.FontColor = text;
            canvas.DrawString(Compact(value / 100), 0, y - 8, left - 5, 16, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
        canvas.FontColor = text;
        canvas.DrawString("₽", 0, 0, 45, 16, HorizontalAlignment.Right, VerticalAlignment.Center);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(points.Count / 5d));
        for (var i = 0; i < points.Count; i++)
        {
            var x = X(i); var point = points[i];
            if (i == SelectedIndex)
            {
                canvas.StrokeColor = text; canvas.StrokeSize = 1; canvas.DrawLine(x, top, x, top + height);
            }
            if (!IsBalance)
            {
                var bar = Math.Min(16f, step * .34f);
                canvas.FillColor = blue; canvas.FillRectangle(x - bar - 1, Y(point.IncomeMinor), bar, Math.Max(0, Y(0) - Y(point.IncomeMinor)));
                canvas.FillColor = orange; canvas.FillRectangle(x + 1, Y(point.ExpenseMinor), bar, Math.Max(0, Y(0) - Y(point.ExpenseMinor)));
            }
            else if (point.BalanceMinor is { } balance)
            {
                canvas.StrokeColor = blue; canvas.StrokeSize = 2.5f;
                if (i > 0 && points[i - 1].BalanceMinor is { } previous) canvas.DrawLine(X(i - 1), Y(previous), x, Y(balance));
                canvas.FillColor = blue; canvas.FillCircle(x, Y(balance), i == SelectedIndex ? 5 : 3);
            }
            if (i % labelEvery == 0)
            {
                canvas.FontColor = text;
                canvas.DrawString(point.Label, x - 23, top + height + 8, 46, 22, HorizontalAlignment.Center, VerticalAlignment.Center);
            }
        }
    }
    private static string Compact(double rubles)
    {
        var absolute = Math.Abs(rubles);
        return absolute >= 1_000_000_000 ? (rubles / 1_000_000_000).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " млрд"
            : absolute >= 1_000_000 ? (rubles / 1_000_000).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " млн"
            : absolute >= 1000 ? (rubles / 1000).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " тыс"
            : rubles.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
    }
}
