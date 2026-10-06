using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FreeFlow.Windows.UI;

namespace FreeFlow.Windows.Services;

/// <summary>
/// The dictation pipeline: shortcut → record → transcribe → (cleanup) → paste.
/// All public members run on the UI thread.
/// </summary>
public sealed class DictationController : IDisposable
{
    private enum Phase { Idle, Recording, Processing }

    private static readonly TimeSpan MinimumRecording = TimeSpan.FromMilliseconds(300);

    private readonly AppSettings _settings;
    private readonly FlowBarWindow _flowBar;
    private readonly GlobalHotkeyHook _hook;
    private readonly AudioRecorder _recorder = new();
    private readonly Dispatcher _dispatcher;
    private Phase _phase = Phase.Idle;
    private DateTime _recordingStartedUtc;
    private string _appContext = "";
    private float _peakLevel;
    private CancellationTokenSource? _processingCts;

    public DictationHistory History { get; } = new();

    /// <summary>Asks the app to open Settings (e.g. no API key yet).</summary>
    public event Action? SettingsNeeded;

    public DictationController(AppSettings settings, FlowBarWindow flowBar, Dispatcher dispatcher)
    {
        _settings = settings;
        _flowBar = flowBar;
        _dispatcher = dispatcher;
        _hook = new GlobalHotkeyHook(HotkeyPreset.FromId(settings.HotkeyId), dispatcher);
        _hook.ComboPressed += OnComboPressed;
        _hook.ComboReleased += OnComboReleased;
        _hook.EscapePressed += OnEscape;
        _flowBar.StopRequested += () => { if (_phase == Phase.Recording) _ = FinishAsync(); };
        _recorder.LevelChanged += level =>
        {
            if (level > _peakLevel) _peakLevel = level;
            _flowBar.SetAudioLevel(level);
        };
    }

    public void Start() => _hook.Start();

    /// <summary>Call after settings change.</summary>
    public void ApplySettings()
    {
        _hook.Preset = HotkeyPreset.FromId(_settings.HotkeyId);
        _hook.ResetState();
    }

    public string HotkeyName => HotkeyPreset.FromId(_settings.HotkeyId).DisplayName;

    // ---------- Shortcut handling ----------

    private void OnComboPressed()
    {
        if (_settings.Mode == RecordingMode.Toggle)
        {
            if (_phase == Phase.Idle) BeginRecording();
            else if (_phase == Phase.Recording) _ = FinishAsync();
            return;
        }
        if (_phase == Phase.Idle) BeginRecording();
    }

    private void OnComboReleased()
    {
        if (_settings.Mode == RecordingMode.Hold && _phase == Phase.Recording)
        {
            _ = FinishAsync();
        }
    }

    private void OnEscape()
    {
        if (_phase == Phase.Recording)
        {
            _recorder.Stop();
            _phase = Phase.Idle;
            _flowBar.ShowMessage("Dictation cancelled", isError: false, TimeSpan.FromSeconds(1.5));
        }
        else if (_phase == Phase.Processing)
        {
            _processingCts?.Cancel();
        }
    }

    // ---------- Pipeline ----------

    private void BeginRecording()
    {
        if (!_settings.HasApiKey)
        {
            _flowBar.ShowMessage("Add your API key in FreeFlow Settings", isError: true, TimeSpan.FromSeconds(4));
            SettingsNeeded?.Invoke();
            return;
        }

        // Remember where the user is typing before anything else changes focus.
        _appContext = ForegroundAppInfo.Describe();
        _peakLevel = 0;

        try
        {
            _recorder.Start(_settings.MicrophoneDeviceNumber);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("microphone", ex);
            _flowBar.ShowMessage(ex is InvalidOperationException ? ex.Message : "Couldn't open the microphone. Check Windows privacy settings.",
                isError: true, TimeSpan.FromSeconds(5));
            if (_settings.PlaySounds) Chimes.Error();
            return;
        }

        _phase = Phase.Recording;
        _recordingStartedUtc = DateTime.UtcNow;
        _flowBar.ShowRecording(toggleMode: _settings.Mode == RecordingMode.Toggle);
        if (_settings.PlaySounds) Chimes.Start();
    }

    private async Task FinishAsync()
    {
        if (_phase != Phase.Recording) return;
        var heldFor = DateTime.UtcNow - _recordingStartedUtc;
        var wav = _recorder.Stop();

        if (heldFor < MinimumRecording || wav == null)
        {
            // An accidental tap: quietly do nothing.
            _phase = Phase.Idle;
            if (_settings.Mode == RecordingMode.Hold)
            {
                _flowBar.ShowMessage($"Hold {HotkeyName} while you talk", isError: false, TimeSpan.FromSeconds(2));
            }
            else
            {
                _flowBar.Dismiss();
            }
            return;
        }

        if (_settings.PlaySounds) Chimes.Stop();

        if (_peakLevel < 0.04f)
        {
            _phase = Phase.Idle;
            _flowBar.ShowMessage("No speech heard. Is the right microphone selected?", isError: true, TimeSpan.FromSeconds(4));
            return;
        }

        _phase = Phase.Processing;
        _flowBar.ShowTranscribing();
        _processingCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = _processingCts.Token;

        try
        {
            var raw = await TranscriptionService.TranscribeAsync(wav, _settings, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _flowBar.ShowMessage("Didn't catch that. Try again.", isError: false, TimeSpan.FromSeconds(2.5));
                return;
            }

            var finalText = raw;
            if (_settings.CleanupEnabled)
            {
                try
                {
                    finalText = await CleanupService.CleanAsync(raw, _appContext, _settings, ct);
                }
                catch (UserFacingException)
                {
                    finalText = raw; // cleanup is a nice-to-have; never lose the words
                }
            }

            if (string.IsNullOrWhiteSpace(finalText))
            {
                _flowBar.Dismiss();
                return;
            }

            History.Add(finalText);
            await _dispatcher.InvokeAsync(() => TextInserter.InsertAsync(finalText)).Task.Unwrap();
            _flowBar.Dismiss();
        }
        catch (OperationCanceledException)
        {
            _flowBar.ShowMessage("Dictation cancelled", isError: false, TimeSpan.FromSeconds(1.5));
        }
        catch (UserFacingException ex)
        {
            if (_settings.PlaySounds) Chimes.Error();
            _flowBar.ShowMessage(ex.Message, isError: true, TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("pipeline", ex);
            if (_settings.PlaySounds) Chimes.Error();
            _flowBar.ShowMessage("Something went wrong. Try again.", isError: true, TimeSpan.FromSeconds(5));
        }
        finally
        {
            _processingCts?.Dispose();
            _processingCts = null;
            _phase = Phase.Idle;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _recorder.Dispose();
        _processingCts?.Cancel();
    }
}
