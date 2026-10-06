using System.Collections.Generic;
using System.Linq;

namespace FreeFlow.Windows.Services;

/// <summary>
/// A push-to-talk shortcut: every key in <see cref="Keys"/> must be held at the same time.
/// Each entry is a group of interchangeable virtual-key codes (e.g. left or right Ctrl).
/// </summary>
public sealed class HotkeyPreset
{
    // Virtual-key codes
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int VK_SPACE = 0x20;
    private const int VK_F8 = 0x77;

    private static readonly int[] Ctrl = { VK_LCONTROL, VK_RCONTROL };
    private static readonly int[] Win = { VK_LWIN, VK_RWIN };
    private static readonly int[] Shift = { VK_LSHIFT, VK_RSHIFT };
    private static readonly int[] Alt = { VK_LMENU, VK_RMENU };

    public string Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<int[]> Keys { get; }

    /// <summary>
    /// True when releasing the combo would otherwise trigger something: Win opens Start,
    /// a lone Alt focuses the app's menu bar. The hook masks those releases.
    /// </summary>
    public bool NeedsReleaseMask => Keys.Any(group => group.Any(IsMaskableKey));

    public static bool IsMaskableKey(int vk) => vk is VK_LWIN or VK_RWIN or VK_LMENU or VK_RMENU;

    /// <summary>
    /// True when the last key of the combo is a normal key that would otherwise type into the
    /// focused app (e.g. Space), so the hook must swallow it.
    /// </summary>
    public bool SwallowsTriggerKey { get; }

    private HotkeyPreset(string id, string displayName, bool swallowsTriggerKey, params int[][] keys)
    {
        Id = id;
        DisplayName = displayName;
        Keys = keys;
        SwallowsTriggerKey = swallowsTriggerKey;
    }

    public static readonly HotkeyPreset CtrlWin = new("ctrl-win", "Ctrl + Win", false, Ctrl, Win);
    public static readonly HotkeyPreset RightAlt = new("right-alt", "Right Alt", false, new[] { VK_RMENU });
    public static readonly HotkeyPreset RightCtrl = new("right-ctrl", "Right Ctrl", false, new[] { VK_RCONTROL });
    public static readonly HotkeyPreset CtrlShiftSpace = new("ctrl-shift-space", "Ctrl + Shift + Space", true, Ctrl, Shift, new[] { VK_SPACE });
    public static readonly HotkeyPreset AltSpace = new("alt-space", "Alt + Space", true, Alt, new[] { VK_SPACE });
    public static readonly HotkeyPreset F8 = new("f8", "F8", true, new[] { VK_F8 });

    /// <summary>Same default as Wispr Flow on Windows.</summary>
    public static HotkeyPreset Default => CtrlWin;

    public static IReadOnlyList<HotkeyPreset> All { get; } = new[] { CtrlWin, RightAlt, RightCtrl, CtrlShiftSpace, AltSpace, F8 };

    public static HotkeyPreset FromId(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    public override string ToString() => DisplayName;
}
