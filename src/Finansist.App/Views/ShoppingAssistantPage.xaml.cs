using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ShoppingAssistantPage : ContentPage
{
    public ShoppingAssistantViewModel ViewModel { get; }
    public ShoppingAssistantPage(ShoppingAssistantViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel;
    }
    private async void OnModelTermsTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("llm-licenses.txt");
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            await Navigation.PushAsync(new ContentPage { Title = "Условия модели", Content = new ScrollView { Content = new Label { Text = text, Margin = new Thickness(20) } } });
        }
        catch (IOException) { await DisplayAlertAsync("Не удалось открыть", "Копия условий недоступна. Условия Gemma: ai.google.dev/gemma/terms", "Понятно"); }
    }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
    protected override void OnDisappearing() { ViewModel.CancelWork(); base.OnDisappearing(); }
}
