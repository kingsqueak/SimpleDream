using Avalonia.Controls;

namespace SimpleDream.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Heading.Text = $"SimpleDream {App.Version}";
    }
}
