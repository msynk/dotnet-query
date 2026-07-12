using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DotNetQuery.Sample.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnRefetchClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RefetchAsync();
        }
    }
}
