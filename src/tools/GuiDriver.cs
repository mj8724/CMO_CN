using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class GuiDriver
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    public class WinInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public bool Visible;
        public string ClassName;
        public string Title;
        public RECT Rect;
    }

    public static List<WinInfo> ListWindows(uint targetPid = 0)
    {
        List<WinInfo> list = new List<WinInfo>();
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (targetPid != 0 && p != targetPid) return true;

            StringBuilder sbCls = new StringBuilder(256);
            GetClassName(h, sbCls, sbCls.Capacity);
            StringBuilder sbTxt = new StringBuilder(512);
            GetWindowText(h, sbTxt, sbTxt.Capacity);
            bool vis = IsWindowVisible(h);
            RECT r;
            GetWindowRect(h, out r);

            list.Add(new WinInfo
            {
                Hwnd = h,
                Pid = p,
                Visible = vis,
                ClassName = sbCls.ToString(),
                Title = sbTxt.ToString(),
                Rect = r
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    public static bool FocusWindow(IntPtr hwnd, bool maximize = false)
    {
        if (maximize)
        {
            ShowWindow(hwnd, SW_MAXIMIZE);
        }
        else if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST
        BringWindowToTop(hwnd);
        bool ok = SetForegroundWindow(hwnd);
        if (!ok)
        {
            // Windows 只允许“正在接收输入”的线程抢前台。测试由后台进程驱动时
            // SetForegroundWindow 会一直被拒；临时附加到当前前台线程即可通过。
            IntPtr foreground = GetForegroundWindow();
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attached = AttachThreadInput(foregroundThread, currentThread, true);
                ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                ok = SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_NOTOPMOST
        return ok;
    }

    public static Bitmap CaptureScreen()
    {
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(0, 0, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    public static Bitmap CaptureWindow(IntPtr hwnd)
    {
        FocusWindow(hwnd);
        Thread.Sleep(200);
        RECT r;
        GetWindowRect(hwnd, out r);
        if (r.Width <= 0 || r.Height <= 0) return CaptureScreen();

        Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    public static void Click(int x, int y, bool dbl = false, bool right = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        if (right)
        {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }
        else
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if (dbl)
            {
                Thread.Sleep(80);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
        }
    }

    public static string SendEngineCmd(string gameDir, string cmd, int timeoutMs = 8000)
    {
        string cmdFile = Path.Combine(gameDir, "CMOLocalizer_Cmd.txt");
        string resFile = Path.Combine(gameDir, "CMOLocalizer_CmdResult.json");

        try { if (File.Exists(resFile)) File.Delete(resFile); } catch { }
        File.WriteAllText(cmdFile, cmd, Encoding.UTF8);

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(100);
            if (File.Exists(resFile))
            {
                try
                {
                    string content = File.ReadAllText(resFile, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(content)) return content;
                }
                catch { }
            }
        }
        return "{\"status\":\"timeout\",\"error\":\"No response from engine within " + timeoutMs + "ms\"}";
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  GuiDriver list [pid]");
            Console.WriteLine("  GuiDriver focus <hwnd>");
            Console.WriteLine("  GuiDriver capture <out.png> [hwnd]");
            Console.WriteLine("  GuiDriver click <x> <y> [dbl|right]");
            Console.WriteLine("  GuiDriver key <keys>");
            Console.WriteLine("  GuiDriver cmd <gameDir> <commandText>");
            return 0;
        }

        string action = args[0].ToLowerInvariant();
        try
        {
            if (action == "list")
            {
                uint pid = args.Length > 1 ? uint.Parse(args[1]) : 0;
                List<WinInfo> list = ListWindows(pid);
                foreach (WinInfo w in list)
                {
                    Console.WriteLine(string.Format("{0,-10} pid={1,-7} vis={2,-6} rect=[{3},{4},{5}x{6}] cls={7,-30} txt={8}",
                        w.Hwnd.ToString(), w.Pid, w.Visible, w.Rect.Left, w.Rect.Top, w.Rect.Width, w.Rect.Height,
                        w.ClassName, w.Title));
                }
            }
            else if (action == "focus" && args.Length > 1)
            {
                IntPtr h = new IntPtr(long.Parse(args[1]));
                bool max = args.Length > 2 && args[2] == "max";
                bool ok = FocusWindow(h, max);
                Console.WriteLine("Focus result: " + ok);
            }
            else if (action == "capture" && args.Length > 1)
            {
                string outPath = args[1];
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Bitmap bmp;
                if (args.Length > 2)
                {
                    IntPtr h = new IntPtr(long.Parse(args[2]));
                    bmp = CaptureWindow(h);
                }
                else
                {
                    bmp = CaptureScreen();
                }

                bmp.Save(outPath, ImageFormat.Png);
                Console.WriteLine("Saved screenshot to " + outPath + " [" + bmp.Width + "x" + bmp.Height + "]");
            }
            else if (action == "click" && args.Length >= 3)
            {
                int x = int.Parse(args[1]);
                int y = int.Parse(args[2]);
                bool dbl = args.Length > 3 && args[3] == "dbl";
                bool right = args.Length > 3 && args[3] == "right";
                Click(x, y, dbl, right);
                Console.WriteLine(string.Format("Clicked at ({0}, {1}) dbl={2} right={3}", x, y, dbl, right));
            }
            else if (action == "key" && args.Length > 1)
            {
                SendKeys.SendWait(args[1]);
                Console.WriteLine("Sent keys: " + args[1]);
            }
            else if (action == "children" && args.Length > 1)
            {
                // 进程外枚举子窗口（只看 Win32 句柄/类名/矩形，不碰托管控件）。
                IntPtr parent = new IntPtr(long.Parse(args[1]));
                List<WinInfo> kids = new List<WinInfo>();
                EnumChildWindows(parent, delegate(IntPtr h, IntPtr lp)
                {
                    WinInfo wi = new WinInfo();
                    wi.Hwnd = h;
                    uint pid2;
                    GetWindowThreadProcessId(h, out pid2);
                    wi.Pid = pid2;
                    wi.Visible = IsWindowVisible(h);
                    StringBuilder cls = new StringBuilder(256);
                    GetClassName(h, cls, cls.Capacity);
                    wi.ClassName = cls.ToString();
                    StringBuilder txt = new StringBuilder(512);
                    GetWindowText(h, txt, txt.Capacity);
                    wi.Title = txt.ToString();
                    RECT rc;
                    GetWindowRect(h, out rc);
                    wi.Rect = rc;
                    kids.Add(wi);
                    return true;
                }, IntPtr.Zero);
                foreach (WinInfo w in kids)
                {
                    Console.WriteLine(string.Format("{0,-10} vis={1,-6} rect=[{2},{3},{4}x{5}] cls={6,-34} txt={7}",
                        w.Hwnd.ToString(), w.Visible, w.Rect.Left, w.Rect.Top, w.Rect.Width, w.Rect.Height,
                        w.ClassName, w.Title));
                }
            }
            else if (action == "postchar" && args.Length >= 3)
            {
                // 向指定控件句柄投递 WM_CHAR（游戏窗口常拒绝 SendKeys 的输入，
                // 但控件级消息仍走同一 WinForms 文本变更/事件链路）。
                IntPtr h = new IntPtr(long.Parse(args[1]));
                string text = args[2];
                foreach (char ch in text) PostMessage(h, 0x0102, (IntPtr)ch, IntPtr.Zero);
                Console.WriteLine(string.Format("Posted {0} char(s) to {1}", text.Length, h));
            }
            else if (action == "cmd" && args.Length >= 3)
            {
                string gDir = args[1];
                string cmdText = args[2];
                string res = SendEngineCmd(gDir, cmdText);
                Console.WriteLine(res);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
