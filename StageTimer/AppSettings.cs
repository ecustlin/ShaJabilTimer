using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace StageTimer;

/// <summary>用户设置，保存在 %APPDATA%\ShaJabilTimer\settings.json。</summary>
public sealed class AppSettings
{
    public const int MaxDurationSeconds = 999 * 60 + 59;

    public int DurationSeconds { get; set; } = 5 * 60;
    public int WarningSeconds { get; set; } = 120;
    public bool AllowOvertime { get; set; } = true;
    public bool FlashOnOvertime { get; set; } = true;
    public bool PlaySound { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool NoActivate { get; set; } = true;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? CompactWidth { get; set; }
    public double? CompactHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShaJabilTimer", "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load settings: {ex}");
        }
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex}");
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        DurationSeconds = Math.Clamp(DurationSeconds, 0, MaxDurationSeconds);
        WarningSeconds = Math.Clamp(WarningSeconds, 0, MaxDurationSeconds);
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.3, 1.0) : 1.0;
    }
}
