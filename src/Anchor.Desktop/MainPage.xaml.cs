using Microsoft.UI.Xaml.Controls;
using Anchor_Desktop.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Anchor_Desktop;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new(App.Services);

    public MainPage()
    {
        InitializeComponent();
        Unloaded += async (_, _) => await ViewModel.DisposeAsync();
    }
}
