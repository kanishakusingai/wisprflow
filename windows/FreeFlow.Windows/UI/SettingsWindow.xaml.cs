using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using FreeFlow.Windows.Services;

namespace FreeFlow.Windows.UI;

public partial class SettingsWindow : Window
{
    private sealed record LanguageOption(string Code, string Name);

    private static readonly LanguageOption[] Languages =
    {
        new("", "Auto-detect"),
        new("en", "English"), new("es", "Spanish"), new("fr", "French"), new("de", "German"),
        new("it", "Italian"), new("pt", "Portuguese"), new("nl", "Dutch"), new("hi", "Hindi"),
        new("ur", "Urdu"), new("ar", "Arabic"), new("zh", "Chinese"), new("ja", "Japanese"),
        new("ko", "Korean"), new("ru", "Russian"), new("tr", "Turkish"), new("pl", "Polish"),
        new("ro", "Romanian"), new("uk", "Ukrainian"), new("id", "Indonesian"),
    };

    private readonly AppSettings _settings;

    /// <summary>Raised after settings are saved, so the app can apply them right away.</summary>
    public event Action? Saved;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadIntoForm();
    }

    public void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(ok ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A));
    }

    private void LoadIntoForm()
    {
        ApiKeyBox.Password = _settings.ApiKey;
        BaseUrlBox.Text = _settings.ApiBaseUrl;

        HotkeyCombo.ItemsSource = HotkeyPreset.All;
        HotkeyCombo.SelectedItem = HotkeyPreset.FromId(_settings.HotkeyId);
        HoldRadio.IsChecked = _settings.Mode == RecordingMode.Hold;
        ToggleRadio.IsChecked = _settings.Mode == RecordingMode.Toggle;

        var mics = new List<MicrophoneInfo> { new(-1, "Windows default microphone") };
        try { mics.AddRange(AudioRecorder.ListMicrophones()); } catch (Exception) { /* no audio stack */ }
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.DeviceNumber == _settings.MicrophoneDeviceNumber) ?? mics[0];

        TranscriptionModelCombo.Text = _settings.TranscriptionModel;
        LanguageCombo.ItemsSource = Languages;
        LanguageCombo.SelectedItem = Languages.FirstOrDefault(l => l.Code == _settings.Language) ?? Languages[0];

        CleanupCheck.IsChecked = _settings.CleanupEnabled;
        CleanupModelCombo.Text = _settings.CleanupModel;
        VocabularyBox.Text = _settings.CustomVocabulary;
        SoundsCheck.IsChecked = _settings.PlaySounds;
        StartupCheck.IsChecked = _settings.LaunchAtStartup;

        UpdateTryItHint();
    }

    private void UpdateTryItHint()
    {
        var preset = HotkeyCombo.SelectedItem as HotkeyPreset ?? HotkeyPreset.Default;
        TryItHint.Text = ToggleRadio.IsChecked == true
            ? $"Click in the box, tap {preset.DisplayName}, speak, then tap it again."
            : $"Click in the box, hold {preset.DisplayName}, speak, then let go.";
    }

    private bool SaveFromForm()
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        try
        {
            ApiClient.NormalizeBaseUrl(baseUrl);
        }
        catch (UserFacingException ex)
        {
            MessageBox.Show(this, ex.Message, "FreeFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.ApiKey = ApiKeyBox.Password;
        _settings.ApiBaseUrl = baseUrl;
        _settings.HotkeyId = (HotkeyCombo.SelectedItem as HotkeyPreset ?? HotkeyPreset.Default).Id;
        _settings.Mode = ToggleRadio.IsChecked == true ? RecordingMode.Toggle : RecordingMode.Hold;
        _settings.MicrophoneDeviceNumber = (MicCombo.SelectedItem as MicrophoneInfo)?.DeviceNumber ?? -1;
        _settings.TranscriptionModel = string.IsNullOrWhiteSpace(TranscriptionModelCombo.Text) ? "whisper-large-v3-turbo" : TranscriptionModelCombo.Text.Trim();
        _settings.Language = (LanguageCombo.SelectedItem as LanguageOption)?.Code ?? "";
        _settings.CleanupEnabled = CleanupCheck.IsChecked == true;
        _settings.CleanupModel = string.IsNullOrWhiteSpace(CleanupModelCombo.Text) ? "openai/gpt-oss-20b" : CleanupModelCombo.Text.Trim();
        _settings.CustomVocabulary = VocabularyBox.Text;
        _settings.PlaySounds = SoundsCheck.IsChecked == true;
        _settings.LaunchAtStartup = StartupCheck.IsChecked == true;
        _settings.HasCompletedSetup = true;

        SettingsStore.Save(_settings);
        StartupManager.Apply(_settings.LaunchAtStartup);
        Saved?.Invoke();
        UpdateTryItHint();
        return true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveFromForm()) return;
        SavedText.Visibility = Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); SavedText.Visibility = Visibility.Collapsed; };
        timer.Start();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResult.Visibility = Visibility.Visible;
        TestResult.Text = "Checking…";
        try
        {
            var error = await ApiClient.TestConnectionAsync(BaseUrlBox.Text, ApiKeyBox.Password.Trim());
            TestResult.Text = error == null ? "✓ Key works. Click Save, then try dictating." : "✗ " + error;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
