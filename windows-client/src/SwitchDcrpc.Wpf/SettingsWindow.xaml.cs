using System.Windows;

namespace SwitchDcrpc.Wpf;

public partial class SettingsWindow : Window
{
    public SettingsWindow(bool startWithWindows, bool connectOnStartup, bool showGithubButton)
    {
        InitializeComponent();
        StartWithWindowsCheckBox.IsChecked = startWithWindows;
        ConnectOnStartupCheckBox.IsChecked = connectOnStartup;
        ShowGithubButtonCheckBox.IsChecked = showGithubButton;
    }

    public bool StartWithWindows => StartWithWindowsCheckBox.IsChecked == true;

    public bool ConnectOnStartup => ConnectOnStartupCheckBox.IsChecked == true;
    public bool ShowGithubButton => ShowGithubButtonCheckBox.IsChecked == true;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
