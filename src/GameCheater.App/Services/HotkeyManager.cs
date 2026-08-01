using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace GameCheater.App.Services;

/// <summary>
/// Global hotkeys (F1–F12) that fire even while the game is focused — the trainer staple.
/// Uses Win32 RegisterHotKey on a dedicated thread with its own message loop; on WM_HOTKEY it
/// marshals the action to the UI thread. Windows-only at runtime (the P/Invokes resolve there);
/// compiles anywhere. Call <see cref="SetBindings"/> whenever the key assignments change.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public static readonly string[] Keys =
        { "None", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };

    private readonly object _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    private (uint Vk, Action Action)[] _bindings = Array.Empty<(uint, Action)>();

    /// <summary>Replace all hotkey bindings. Keys that don't map or fail to register are skipped.</summary>
    public void SetBindings(IEnumerable<(string Key, Action Action)> bindings)
    {
        var list = new List<(uint, Action)>();
        foreach (var (key, action) in bindings)
        {
            uint vk = KeyToVk(key);
            if (vk != 0) list.Add((vk, action));
        }

        lock (_lock)
        {
            Stop();
            _bindings = list.ToArray();
            if (_bindings.Length == 0) return;

            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(Run) { IsBackground = true, Name = "GameCheater-Hotkeys" };
            _thread.Start();
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _ready!.Set();

        var actions = new Dictionary<int, Action>();
        for (int i = 0; i < _bindings.Length; i++)
        {
            if (RegisterHotKey(IntPtr.Zero, i + 1, 0, _bindings[i].Vk))
                actions[i + 1] = _bindings[i].Action;
            // a failed register (key already taken by the game/OS) is skipped silently
        }

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && actions.TryGetValue((int)msg.wParam, out var action))
                Dispatcher.UIThread.Post(action);
        }

        for (int i = 0; i < _bindings.Length; i++)
            UnregisterHotKey(IntPtr.Zero, i + 1);
    }

    private void Stop()
    {
        if (_thread is null) return;
        _ready?.Wait(500);                 // ensure the thread published its id
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
        _thread = null;
        _threadId = 0;
        _ready?.Dispose();
        _ready = null;
    }

    public void Dispose()
    {
        lock (_lock) Stop();
    }

    private static uint KeyToVk(string? key) => key switch
    {
        "F1" => 0x70,
        "F2" => 0x71,
        "F3" => 0x72,
        "F4" => 0x73,
        "F5" => 0x74,
        "F6" => 0x75,
        "F7" => 0x76,
        "F8" => 0x77,
        "F9" => 0x78,
        "F10" => 0x79,
        "F11" => 0x7A,
        "F12" => 0x7B,
        _ => 0,
    };

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
