using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using FreeFlow.Windows.Interop;

namespace FreeFlow.Windows.Services;

/// <summary>
/// Types text into whatever app has focus by putting it on the clipboard and pressing Ctrl+V,
/// then puts your previous clipboard back. Must be called on the UI (STA) thread.
/// </summary>
public static class TextInserter
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    public static async Task InsertAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var previous = SnapshotClipboard();

        if (!TrySetClipboardText(text))
        {
            throw new UserFacingException("Couldn't use the clipboard. Text was saved to history.");
        }

        // If the user is still physically holding modifiers from the shortcut (e.g. Win),
        // Ctrl+V would turn into Win+Ctrl+V. Release them for the paste.
        await WaitForModifiersReleasedAsync();
        SendCtrlV();

        // Give the target app time to read the clipboard before restoring it.
        await Task.Delay(400);
        RestoreClipboard(previous);
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        for (var i = 0; i < 40; i++) // up to ~1s
        {
            if (!IsDown(VK_LWIN) && !IsDown(VK_RWIN) && !IsDown(VK_MENU) && !IsDown(VK_SHIFT) && !IsDown(VK_CONTROL))
            {
                return;
            }
            await Task.Delay(25);
        }
    }

    private static bool IsDown(ushort vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 },
        },
    };

    private static bool TrySetClipboardText(string text)
    {
        // The clipboard is briefly locked if another app is using it, so retry a few times.
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var data = new DataObject(DataFormats.UnicodeText, text);
                // Temporary paste buffer: don't add it to Win+V clipboard history or cloud sync.
                data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
                data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                System.Threading.Thread.Sleep(30);
            }
        }
        return false;
    }

    /// <summary>Copies every format currently on the clipboard (text, images, files...).</summary>
    private static Dictionary<string, object>? SnapshotClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            var copy = new Dictionary<string, object>();
            foreach (var format in data.GetFormats(false))
            {
                try
                {
                    var value = data.GetData(format, false);
                    if (value != null) copy[format] = value;
                }
                catch (Exception)
                {
                    // Some formats can't be read by .NET; skip them.
                }
            }
            return copy;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void RestoreClipboard(Dictionary<string, object>? snapshot)
    {
        try
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                Clipboard.Clear();
                return;
            }
            var data = new DataObject();
            foreach (var (format, value) in snapshot) data.SetData(format, value);
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception)
        {
            // Restoring is best-effort: the dictated text is already pasted.
        }
    }
}
