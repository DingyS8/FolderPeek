using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using FolderPeek.Native;

namespace FolderPeek;

/// <summary>
/// Hook global de mouse (WH_MOUSE_LL) SÓ para cliques do botão esquerdo.
///
/// Polling não serve para clique: um clique rápido dura menos que o tick de 50 ms.
/// Para o hook não atrasar o mouse do sistema, ele roda numa thread própria que
/// não faz mais nada: o callback só copia o ponto, agenda o trabalho na thread de
/// UI (BeginInvoke) e devolve na hora. Nunca engole o clique.
/// </summary>
internal sealed class MouseClickHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly Dispatcher _dispatcher;
    private readonly HookProc _proc; // referência viva: o GC não pode coletar o delegate do hook
    private readonly Thread _thread;
    private uint _threadId;

    /// <summary>(apertou?, ponto em pixels físicos) — disparado na thread de UI.</summary>
    public event Action<bool, POINT>? LeftButton;

    public MouseClickHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "FolderPeek mouse hook" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Log.Write($"falha ao instalar hook de mouse: {Marshal.GetLastWin32Error()}");
            return;
        }
        Log.Debug("hook de clique instalado");
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { /* só mantém o hook vivo */ }
        UnhookWindowsHookEx(hook);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP)
            {
                var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
                bool down = msg == WM_LBUTTONDOWN;
                _dispatcher.BeginInvoke(() => LeftButton?.Invoke(down, pt));
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
