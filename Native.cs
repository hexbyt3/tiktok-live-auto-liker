using System.Runtime.InteropServices;

namespace TikTokLiveAutoLiker;

static class Native
{
    public const int WmHotkey = 0x0312;
    public const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint hwnd, int id);
}
