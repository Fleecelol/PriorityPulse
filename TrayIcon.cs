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
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        // win32 imports
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr v);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int r, IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        // state
        private readonly IntPtr _hwnd;
        private readonly WndProcDelegate _wndProc;
        private readonly IntPtr _oldWndProc;
        private NOTIFYICONDATA _nid;
        private readonly IntPtr _hIcon;

        public IntPtr IconHandle => _hIcon;
        public event Action? ShowRequested;
        public event Action? ExitRequested;

        // init
        public TrayIcon(IntPtr hwnd, string tooltip)
        {
            _hwnd = hwnd;
            _hIcon = BuildIcon();
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

        // icon render — gear + sonar arcs, pure GDI
        private static IntPtr BuildIcon()
        {
            const int S = 32;
            var px = new uint[S * S];

            static uint Premul(byte r, byte g, byte b, byte a = 255) =>
                (uint)(b * a / 255) | ((uint)(g * a / 255) << 8) | ((uint)(r * a / 255) << 16) | ((uint)a << 24);

            uint bg = Premul(0x18, 0x18, 0x18);

            // rounded-rect background
            const int RR = 5;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int ncx = x < RR ? RR : (x >= S - RR ? S - 1 - RR : -1);
                    int ncy = y < RR ? RR : (y >= S - RR ? S - 1 - RR : -1);
                    bool corner = ncx >= 0 && ncy >= 0;
                    double d = corner ? Math.Sqrt((x - ncx) * (x - ncx) + (y - ncy) * (y - ncy)) : 0;
                    px[y * S + x] = corner && d > RR ? 0u : bg;
                }

            // gear body + teeth
            double gcx = 11.5, gcy = 15.5, holeR = 2.8, bodyR = 6.2, toothR = 8.2;
            double seg = 2.0 * Math.PI / 6, toothFrac = 0.45;

            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    double dx = x - gcx, dy = y - gcy, r = Math.Sqrt(dx * dx + dy * dy);
                    if (r < holeR || r > toothR + 1.0) continue;
                    double mod = ((Math.Atan2(dy, dx) % seg) + seg) % seg;
                    double outer = mod < seg * toothFrac ? toothR : bodyR;
                    double alpha = Math.Clamp(outer - r + 0.8, 0, 1);
                    if (alpha > 0.15)
                        px[y * S + x] = Premul(0xFF, 0xFF, 0xFF, (byte)(alpha * 255));
                }

            // gear center hole
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    if (Math.Sqrt((x - gcx) * (x - gcx) + (y - gcy) * (y - gcy)) < holeR - 0.5)
                        px[y * S + x] = bg;

            // sonar arcs
            double arcMin = -Math.PI * 0.32, arcMax = Math.PI * 0.32, arcThick = 0.85;
            foreach (double R in new[] { 11.5, 15.5, 19.5 })
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        double dx = x - gcx, dy = y - gcy;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (Math.Abs(dist - R) > arcThick + 0.5) continue;
                        double angle = Math.Atan2(dy, dx);
                        if (angle < arcMin || angle > arcMax) continue;
                        double fade = 1.0 - Math.Pow(Math.Max(Math.Abs(angle) - (arcMax - 0.25), 0) / 0.25, 2);
                        double a = Math.Clamp(arcThick - Math.Abs(dist - R) + 0.5, 0, 1) * fade;
                        if (a > 0.1)
                        {
                            byte na = (byte)Math.Min(255, (byte)(a * 255) + (byte)(px[y * S + x] >> 24));
                            px[y * S + x] = Premul(0xFF, 0xFF, 0xFF, na);
                        }
                    }

            return PixelsToHIcon(px, S);
        }

        // pixel array to HICON
        private static IntPtr PixelsToHIcon(uint[] px, int S)
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = S, biHeight = -S, biPlanes = 1, biBitCount = 32
                }
            };
            IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr hBmp = CreateDIBSection(hdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bits != IntPtr.Zero)
                Marshal.Copy(Array.ConvertAll(px, p => (int)p), 0, bits, px.Length);

            var gc = GCHandle.Alloc(new byte[((S + 15) / 16) * 2 * S], GCHandleType.Pinned);
            IntPtr hMask = CreateBitmap(S, S, 1, 1, gc.AddrOfPinnedObject());
            gc.Free();

            var ii = new ICONINFO { fIcon = true, hbmColor = hBmp, hbmMask = hMask };
            IntPtr hIcon = CreateIconIndirect(ref ii);
            DeleteObject(hBmp); DeleteObject(hMask); DeleteDC(hdc);
            return hIcon;
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
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
    }
}
