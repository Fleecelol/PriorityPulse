using System;
using System.Runtime.InteropServices;

namespace PriorityPulse
{
    internal sealed class TrayIcon : IDisposable
    {
        // win32 constants
        private const int WM_TRAY = 0x8001, NIM_ADD = 0, NIM_DELETE = 2;
        private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
        private const int WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205;
        private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
        private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

        // win32 structs
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        // win32 imports
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr v);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int r, IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        // state
        private readonly IntPtr _hwnd;
        private readonly WndProcDelegate _wndProc;
        private readonly IntPtr _oldWndProc;
        private NOTIFYICONDATA _nid;
        private readonly IntPtr _hIcon;
        private readonly bool _ownsIcon;

        public IntPtr IconHandle => _hIcon;
        public event Action? ShowRequested;
        public event Action? ExitRequested;

        // init
        public TrayIcon(IntPtr hwnd, string tooltip)
        {
            _hwnd = hwnd;
            _hIcon = LoadEmbeddedIcon(out _ownsIcon);
            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd, uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon, szTip = tooltip
            };
            Shell_NotifyIcon(NIM_ADD, ref _nid);
            _wndProc = WndProc;
            _oldWndProc = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wndProc));
        }

        // load icon from the exe's embedded resource
        private static IntPtr LoadEmbeddedIcon(out bool ownsHandle)
        {
            ownsHandle = false;
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var icon = ExtractIcon(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero && icon != (IntPtr)1)
                {
                    ownsHandle = true;
                    return icon;
                }
            }
            return IntPtr.Zero;
        }

        // tray click + context menu
        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == WM_TRAY)
            {
                int mouse = l.ToInt32() & 0xFFFF;
                if (mouse == WM_LBUTTONDBLCLK)
                    ShowRequested?.Invoke();
                else if (mouse == WM_RBUTTONUP)
                {
                    SetForegroundWindow(hWnd);
                    GetCursorPos(out var pt);
                    IntPtr menu = CreatePopupMenu();
                    AppendMenu(menu, MF_STRING, new IntPtr(1), "Show PriorityPulse");
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, "");
                    AppendMenu(menu, MF_STRING, new IntPtr(2), "Exit");
                    int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hWnd, IntPtr.Zero);
                    DestroyMenu(menu);
                    if (cmd == 1) ShowRequested?.Invoke();
                    if (cmd == 2) ExitRequested?.Invoke();
                }
            }
            return CallWindowProc(_oldWndProc, hWnd, msg, w, l);
        }

        // cleanup
        public void Dispose()
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            SetWindowLongPtr(_hwnd, -4, _oldWndProc);
            if (_ownsIcon && _hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
    }
}
