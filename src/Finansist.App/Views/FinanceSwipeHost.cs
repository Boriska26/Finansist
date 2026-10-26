using System.Windows.Input;

namespace Finansist.App.Views;

public sealed class FinanceSwipeHost : Grid
{
    public static readonly BindableProperty IsOverviewProperty = BindableProperty.Create(nameof(IsOverview), typeof(bool), typeof(FinanceSwipeHost), false);
    public static readonly BindableProperty ShowOverviewCommandProperty = BindableProperty.Create(nameof(ShowOverviewCommand), typeof(ICommand), typeof(FinanceSwipeHost));
    public static readonly BindableProperty ShowLedgerCommandProperty = BindableProperty.Create(nameof(ShowLedgerCommand), typeof(ICommand), typeof(FinanceSwipeHost));
    public bool IsOverview { get => (bool)GetValue(IsOverviewProperty); set => SetValue(IsOverviewProperty, value); }
    public ICommand? ShowOverviewCommand { get => (ICommand?)GetValue(ShowOverviewCommandProperty); set => SetValue(ShowOverviewCommandProperty, value); }
    public ICommand? ShowLedgerCommand { get => (ICommand?)GetValue(ShowLedgerCommandProperty); set => SetValue(ShowLedgerCommandProperty, value); }
    public void NavigateFromEdge()
    {
        var command = IsOverview ? ShowLedgerCommand : ShowOverviewCommand;
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
}
