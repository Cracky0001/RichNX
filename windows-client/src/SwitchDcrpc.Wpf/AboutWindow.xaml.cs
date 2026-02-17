using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace SwitchDcrpc.Wpf;

public partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/Cracky0001/RichNX";

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        VersionTextBlock.Text = $"Version: {version}";
    }

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenRepo();
        e.Handled = true;
    }

    private void OpenRepoButton_Click(object sender, RoutedEventArgs e)
    {
        OpenRepo();
    }

    private static void OpenRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepoUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch errors.
        }
    }
}
