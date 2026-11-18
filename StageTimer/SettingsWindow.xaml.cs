using System.Globalization;
using System.Windows;

namespace StageTimer;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;

        WarningBox.Text = settings.WarningSeconds.ToString(CultureInfo.InvariantCulture);
        AllowOvertimeBox.IsChecked = settings.AllowOvertime;
        FlashBox.IsChecked = settings.FlashOnOvertime;
        SoundBox.IsChecked = settings.PlaySound;
        TopmostBox.IsChecked = settings.AlwaysOnTop;
        NoActivateBox.IsChecked = settings.NoActivate;
        OpacitySlider.Value = settings.Opacity;
        UpdateOpacityLabel();

        Loaded += (_, _) => WarningBox.Focus();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateOpacityLabel();

    private void UpdateOpacityLabel()
    {
        if (OpacityLabel != null)
            OpacityLabel.Text = $"{OpacitySlider.Value * 100:0}%";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WarningBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int warning) ||
            warning < 0 || warning > AppSettings.MaxDurationSeconds)
        {
            ErrorText.Text = "提醒秒数请输入 0 或正整数。";
            ErrorText.Visibility = Visibility.Visible;
            WarningBox.Focus();
            WarningBox.SelectAll();
            return;
        }

        Settings.WarningSeconds = warning;
        Settings.AllowOvertime = AllowOvertimeBox.IsChecked == true;
        Settings.FlashOnOvertime = FlashBox.IsChecked == true;
        Settings.PlaySound = SoundBox.IsChecked == true;
        Settings.AlwaysOnTop = TopmostBox.IsChecked == true;
        Settings.NoActivate = NoActivateBox.IsChecked == true;
        Settings.Opacity = OpacitySlider.Value;
        DialogResult = true;
    }
}
