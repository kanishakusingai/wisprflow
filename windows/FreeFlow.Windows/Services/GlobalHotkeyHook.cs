using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FreeFlow.Windows.Interop;

namespace FreeFlow.Windows.Services;

/// <summary>
/// System-wide low-level keyboard hook that detects when a <see cref="HotkeyPreset"/> combo
/// is pressed and released, in any app. Needed because RegisterHotKey cannot report key-up,
/// which hold-to-talk depends on.
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    private const int VK_ESCAPE = 0x1B;
    private const ushort VK_MENU_MASK = 0xE8; // unassigned key, used to stop Win from opening Start

    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep a reference so the GC can't collect it
    private readonly HashSet<int> _down = new();
    private readonly HashSet<int> _swallowed = new();
    private IntPtr _hook = IntPtr.Zero;
    private bool _comboActive;
    private bool _comboUsedSinceAllUp;

    public HotkeyPreset Preset { get; set; }

    /// <summary>Combo went from "not all held" to "all held".</summary>
    public event Action? ComboPressed;
    /// <summary>Combo went from "all held" to "not all held".</summary>
    public event Action? ComboReleased;
    public event Action? EscapePressed;

    public GlobalHotkeyHook(HotkeyPreset preset, Dispatcher dispatcher)
    {
        Preset = preset;
        _dispatcher = dispatcher;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(module?.ModuleName),
            0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not install keyboard hook (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        // Ignore keys we (or other tools) synthesize, e.g. our own Ctrl+V.
        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var vk = (int)info.vkCode;
        var msg = (int)wParam;
        var isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        var isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
        var swallow = false;

        if (isDown)
        {
            _down.Add(vk);
            if (vk == VK_ESCAPE) Post(EscapePressed);
        }
        else if (isUp)
        {
            _down.Remove(vk);
        }

        var preset = Preset;
        var nowActive = preset.Keys.All(group => group.Any(_down.Contains));

        if (nowActive && !_comboActive)
        {
            _comboActive = true;
            _comboUsedSinceAllUp = true;
            Post(ComboPressed);
        }
        else if (!nowActive && _comboActive)
        {
            _comboActive = false;
            Post(ComboReleased);
        }

        // Swallow a normal trigger key (e.g. Space in Ctrl+Shift+Space) so it doesn't type.
        if (preset.SwallowsTriggerKey && preset.Keys[^1].Contains(vk))
        {
            if (isDown && nowActive)
            {
                _swallowed.Add(vk);
                swallow = true;
            }
            else if (isUp && _swallowed.Remove(vk))
            {
                swallow = true;
            }
        }

        // Releasing Win after Ctrl+Win would open Start; releasing a lone Alt focuses menus.
        // Tap a dummy key first so Windows treats it as a used combo and does nothing.
        if (isUp && _comboUsedSinceAllUp && preset.NeedsReleaseMask && HotkeyPreset.IsMaskableKey(vk))
        {
            SendMaskKey();
        }

        if (_down.Count == 0) _comboUsedSinceAllUp = false;

        return swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SendMaskKey()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = VK_MENU_MASK } } },
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = VK_MENU_MASK, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } },
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>Hook callbacks must return fast, so handlers run later on the UI thread.</summary>
    private void Post(Action? handler)
    {
        if (handler != null) _dispatcher.BeginInvoke(handler);
    }

    /// <summary>Forget held keys, e.g. after the PC wakes up or the shortcut changes.</summary>
    public void ResetState()
    {
        _down.Clear();
        _swallowed.Clear();
        _comboActive = false;
        _comboUsedSinceAllUp = false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
