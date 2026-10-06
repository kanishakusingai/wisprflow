# FreeFlow for Windows

Hold a shortcut in any app, talk, let go, and your words get typed where your cursor is.
A floating **Flow Bar** above the taskbar shows a live waveform while you speak.

This is a Windows port of [FreeFlow](https://github.com/zachlatta/freeflow) (MIT), written in C# / WPF.

## Install

1. Download `FreeFlow.exe` from this repo's **Releases** page. It's a single self-contained file; there's nothing else to install.
2. Double-click it. Windows SmartScreen may say "Windows protected your PC" because the app isn't code-signed: click **More info → Run anyway**.
3. Settings opens. Paste a free API key from [console.groq.com/keys](https://console.groq.com/keys), click **Test**, then **Save**.
4. Click in the **Try it** box, hold **Ctrl + Win**, say something, and let go.

FreeFlow then runs in the system tray (near the clock). Right-click the icon for Settings, recent dictations, or Quit.

Requires Windows 10 or 11 (64-bit). If dictation hears nothing, check **Settings → Privacy & security → Microphone → Let desktop apps access your microphone**.

## Features

- **Hold to talk** or **tap to start / tap to stop** (hands-free) with a choice of shortcuts: Ctrl + Win (default), Right Alt, Right Ctrl, Ctrl + Shift + Space, Alt + Space, F8
- **Esc** cancels while recording
- **Flow Bar** overlay: live waveform, recording timer, voice-reactive glow, transcribing animation and error messages. It never takes focus away from the app you're typing in
- **Smart cleanup** (optional): removes filler words, fixes punctuation and applies self-corrections ("Thursday, no actually Wednesday" → "Wednesday"), using the same prompt as the macOS app
- **Custom vocabulary** for names and jargon
- Language picker or auto-detect, microphone picker
- **Recent dictations** in the tray menu (kept in memory only)
- Restores your clipboard after pasting, and keeps dictations out of Win+V clipboard history
- Works with Groq by default, or any OpenAI-compatible API (Settings → Advanced)
- Optional start at sign-in and soft start/stop sounds

## Privacy

- Audio is sent only to the API you configure. There's no FreeFlow server and no analytics.
- Your API key is encrypted with Windows DPAPI, so it can only be decrypted by your Windows account.
- Settings live in `%APPDATA%\FreeFlow\settings.json`. Transcripts are never written to disk.
- For better formatting, the cleanup step receives the name and title of the window you're typing in. No screenshots are taken.
- `%APPDATA%\FreeFlow\error.log` records error messages only, never audio or text.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Works on Windows, macOS or Linux:

```bash
cd windows/FreeFlow.Windows
dotnet publish -c Release -o publish
# -> publish/FreeFlow.exe
```

## Code map

| Path | What it does |
|------|--------------|
| `App.xaml.cs` | Startup, single instance, tray icon |
| `Services/DictationController.cs` | The pipeline: shortcut → record → transcribe → clean up → paste |
| `Services/GlobalHotkeyHook.cs` | System-wide keyboard hook for hold-to-talk |
| `Services/AudioRecorder.cs` | 16 kHz mono microphone capture + loudness level |
| `Services/TranscriptionService.cs` / `CleanupService.cs` | API calls |
| `Services/TextInserter.cs` | Clipboard + Ctrl+V paste with clipboard restore |
| `Services/AppSettings.cs` | Settings and the DPAPI-encrypted API key |
| `UI/FlowBarWindow.xaml` | The floating Flow Bar |
| `UI/SettingsWindow.xaml` | Settings |
