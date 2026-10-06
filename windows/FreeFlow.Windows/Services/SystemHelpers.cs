using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text;
using FreeFlow.Windows.Interop;
using Microsoft.Win32;

namespace FreeFlow.Windows.Services;

/// <summary>Describes the app you're dictating into, as a formatting hint for cleanup.</summary>
public static class ForegroundAppInfo
{
    public static string Describe()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";

            var title = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var processName = "";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
            }

            // Only the app and window title are used; nothing on screen is captured.
            var description = $"App: {processName}. Window title: {title}";
            return description.Length > 300 ? description[..300] : description;
        }
        catch (Exception)
        {
            return "";
        }
    }
}

/// <summary>Recent dictations, kept in memory only (never written to disk).</summary>
public sealed class DictationHistory
{
    private const int Capacity = 15;
    private readonly LinkedList<string> _items = new();

    public event Action? Changed;

    public IReadOnlyCollection<string> Items => _items;
    public string? Latest => _items.First?.Value;

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _items.AddFirst(text);
        while (_items.Count > Capacity) _items.RemoveLast();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Adds/removes FreeFlow from "run when I sign in" (current user only, no admin).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FreeFlow";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                key.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Locked-down PCs may block this; not fatal.
        }
    }
}

/// <summary>Short, soft start/stop chimes generated in code (no sound files needed).</summary>
public static class Chimes
{
    private static readonly Lazy<SoundPlayer> StartSound = new(() => Make(new[] { 660.0, 880.0 }));
    private static readonly Lazy<SoundPlayer> StopSound = new(() => Make(new[] { 880.0, 660.0 }));
    private static readonly Lazy<SoundPlayer> ErrorSound = new(() => Make(new[] { 330.0, 247.0 }));

    public static void Start() => Play(StartSound);
    public static void Stop() => Play(StopSound);
    public static void Error() => Play(ErrorSound);

    private static void Play(Lazy<SoundPlayer> sound)
    {
        try { sound.Value.Play(); } catch (Exception) { /* no audio device: ignore */ }
    }

    private static SoundPlayer Make(double[] notes)
    {
        const int rate = 44_100;
        const double noteSeconds = 0.07;
        var samplesPerNote = (int)(rate * noteSeconds);
        var total = samplesPerNote * notes.Length;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + total * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(total * 2);

        for (var n = 0; n < notes.Length; n++)
        {
            for (var i = 0; i < samplesPerNote; i++)
            {
                var t = (double)i / rate;
                // Quick fade in/out so it sounds like a soft "blip", not a click.
                var envelope = Math.Min(1.0, i / 300.0) * Math.Min(1.0, (samplesPerNote - i) / 900.0);
                var sample = Math.Sin(2 * Math.PI * notes[n] * t) * envelope * 0.18;
                w.Write((short)(sample * short.MaxValue));
            }
        }
        w.Flush();
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}

/// <summary>
/// Tiny error log at %APPDATA%\FreeFlow\error.log for troubleshooting.
/// Only exception types and messages are written: never audio or transcripts.
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static void Write(string where, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var path = Path.Combine(SettingsStore.Folder, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:u} [{where}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}
