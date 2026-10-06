using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeFlow.Windows.Services;

public enum RecordingMode
{
    /// <summary>Hold the shortcut while you talk, release to finish.</summary>
    Hold,
    /// <summary>Tap once to start, tap again (or press stop) to finish.</summary>
    Toggle,
}

/// <summary>User preferences. Saved as JSON in %APPDATA%\FreeFlow\settings.json.</summary>
public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string TranscriptionModel { get; set; } = "whisper-large-v3-turbo";
    public bool CleanupEnabled { get; set; } = true;
    public string CleanupModel { get; set; } = "openai/gpt-oss-20b";
    public string HotkeyId { get; set; } = HotkeyPreset.Default.Id;
    public RecordingMode Mode { get; set; } = RecordingMode.Hold;
    /// <summary>Optional ISO-639-1 code (e.g. "en"). Empty = auto-detect.</summary>
    public string Language { get; set; } = "";
    /// <summary>Names, jargon and product terms, one per line or comma separated.</summary>
    public string CustomVocabulary { get; set; } = "";
    public bool PlaySounds { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public int MicrophoneDeviceNumber { get; set; } = -1;
    public bool HasCompletedSetup { get; set; }

    /// <summary>API key, encrypted for the current Windows user with DPAPI. Never stored in plain text.</summary>
    [JsonInclude]
    public string? ProtectedApiKey { get; private set; }

    [JsonIgnore]
    public string ApiKey
    {
        get => Unprotect(ProtectedApiKey);
        set => ProtectedApiKey = string.IsNullOrWhiteSpace(value) ? null : Protect(value.Trim());
    }

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<string> VocabularyTerms()
    {
        var terms = new List<string>();
        foreach (var raw in CustomVocabulary.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim();
            if (term.Length > 0 && !terms.Contains(term)) terms.Add(term);
        }
        return terms;
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FreeFlow.Windows.ApiKey.v1");

    private static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            // Copied from another user/PC, or corrupted: treat as missing.
            return "";
        }
    }
}

public static class SettingsStore
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeFlow");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Unreadable settings: fall back to defaults rather than crashing.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
