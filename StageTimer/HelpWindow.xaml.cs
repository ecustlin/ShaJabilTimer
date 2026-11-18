using System.Windows;

namespace StageTimer;

public partial class HelpWindow : Window
{
    public HelpWindow(IReadOnlyCollection<string> failedHotkeys)
    {
        InitializeComponent();
        if (failedHotkeys.Count > 0)
        {
            HotkeyWarning.Text = $"以下快捷键已被其他程序占用，当前不可用：{string.Join("、", failedHotkeys)}";
            HotkeyWarning.Visibility = Visibility.Visible;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
