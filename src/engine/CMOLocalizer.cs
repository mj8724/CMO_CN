using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public class LocalizerDomainManager : CustomAppDomainManager
    {
    }

    public class CustomAppDomainManager : AppDomainManager
    {
        private static string logPath = "";
        private static Dictionary<int, string> offsetMap = new Dictionary<int, string>();
        private static Dictionary<string, string> exactMap = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> partialMap = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> wordsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> normExactMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // High performance dynamic translation cache
        private static readonly Dictionary<string, string> dynamicCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object cacheLock = new object();
        private const int MAX_CACHE_SIZE = 16384;

        private static void Log(string msg)
        {
            try
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    File.AppendAllText(logPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, msg), Encoding.UTF8);
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 崩溃取证：注入进程里的任何未处理异常都必须留下可分析的堆栈。
        // 背景（2026-10-16 用户实测）：在搜索框/任务名里输入一个字符即闪退；
        // 事件日志显示 Command.exe 两次挂在 PresentationFramework（Environment.FailFast）
        // 与 System.Windows.Forms（System.StackOverflowException）——都是异常从我们挂上
        // 的回调里逃逸的签名。没有堆栈就无法定位，故先取证。
        // ─────────────────────────────────────────────────────────────────────
        private static string crashLogPath = "";
        private static bool crashGuardInstalled = false;
        private const int MAX_MUTATION_DEPTH = 8;
        [ThreadStatic]
        private static int _mutationDepth;
        [ThreadStatic]
        private static bool _depthCapLogged;

        private static bool IsOurFrame(string stack)
        {
            return !string.IsNullOrEmpty(stack) && stack.IndexOf("CMOLocalizer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogCrash(string source, Exception ex, bool terminating, bool swallowed)
        {
            try
            {
                if (string.IsNullOrEmpty(crashLogPath))
                {
                    crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer_Crash.log");
                }
                string text = ex != null ? ex.ToString() : "(no exception object)";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + source + " =====");
                sb.AppendLine("terminating=" + terminating + "  swallowed=" + swallowed
                              + "  ourFrame=" + IsOurFrame(text) + "  pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                sb.AppendLine(text);
                sb.AppendLine();
                File.AppendAllText(crashLogPath, sb.ToString(), Encoding.UTF8);
                Log("CRASH [" + source + "] swallowed=" + swallowed + " ourFrame=" + IsOurFrame(text)
                    + " -> " + (ex != null ? ex.GetType().Name : "?") + ": "
                    + (ex != null ? ex.Message : ""));
            }
            catch { }
        }

        /// <summary>
        /// 安装三道未处理异常入口的取证器。原则：
        /// · 一律记录完整堆栈（崩溃后仍可分析）；
        /// · 只有堆栈里出现本引擎帧时才「吞掉」它——绝不让我们的缺陷变成用户的闪退；
        /// · 游戏自身的异常保持原行为（记录后重新抛出，行为不变）。
        /// </summary>
        private static void InstallCrashGuard()
        {
            if (crashGuardInstalled) return;
            crashGuardInstalled = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating, false);
                };
            }
            catch { }
            try
            {
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    bool ours = e.Exception != null && IsOurFrame(e.Exception.ToString());
                    LogCrash("Application.ThreadException", e.Exception, false, ours);
                    if (!ours && e.Exception != null) throw e.Exception;
                };
            }
            catch { }
            Log("CrashGuard installed (log: CMOLocalizer_Crash.log)");
        }

        /// <summary>
        /// 改写控件/文档文本前的统一重入门闸。背景：我们在游戏自己的事件处理链里改写文本，
        /// 改写又会触发对方的回调，形成同步 A→B→A 递归，最终 0xc00000fd 栈溢出闪退。
        /// 超过深度上限直接拒绝本次改写并记录一次堆栈（既止血又留下定位证据）。
        /// </summary>
        private static bool EnterMutation(string what)
        {
            if (_mutationDepth >= MAX_MUTATION_DEPTH)
            {
                if (!_depthCapLogged)
                {
                    _depthCapLogged = true;
                    Log("MUTATION DEPTH CAP(" + MAX_MUTATION_DEPTH + ") hit by " + what
                        + "; refused. stack:\r\n" + Environment.StackTrace);
                }
                return false;
            }
            _mutationDepth++;
            return true;
        }

        private static void ExitMutation()
        {
            if (_mutationDepth > 0) _mutationDepth--;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 回写拉锯熔断：游戏把英文写回、我们又翻成中文，若它每收到我们的回写就再写回，
        // 就是一场无终止的拉锯（表现为卡死/后台忙碌/最终栈溢出）。同一个控件被反复
        // 写成同一个目标值时计数，超限就放弃该控件并留证。目标值在变时永不计入，
        // 因此不会误伤燃油/时钟这类本当持续更新的面板。
        // ─────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<int, string> lastWriteTarget = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> writeRepeatCount = new Dictionary<int, int>();
        private static readonly HashSet<int> writeBlocked = new HashSet<int>();
        private static readonly object writeBurstLock = new object();
        private const int WRITE_REPEAT_LIMIT = 6;

        private static bool IsWriteBlocked(int key)
        {
            lock (writeBurstLock) { return writeBlocked.Contains(key); }
        }

        /// <summary>
        /// 回写前调用。target = 本次要写入的目标文本。
        /// 同一个目标值在同一个控件上被重复写入超过 WRITE_REPEAT_LIMIT 次，
        /// 判定为与游戏拉锯，永久放弃该控件并记录一次堆栈。
        /// </summary>
        private static bool AllowWrite(int key, string target, string what)
        {
            lock (writeBurstLock)
            {
                if (writeBlocked.Contains(key)) return false;
                string prev;
                if (lastWriteTarget.TryGetValue(key, out prev) && prev == target)
                {
                    int n;
                    writeRepeatCount.TryGetValue(key, out n);
                    n++;
                    writeRepeatCount[key] = n;
                    if (n > WRITE_REPEAT_LIMIT)
                    {
                        writeBlocked.Add(key);
                        Log("WRITE LOOP BLOCKED " + what + " after " + n + " identical writes; stack:\r\n"
                            + Environment.StackTrace);
                        return false;
                    }
                }
                else
                {
                    lastWriteTarget[key] = target;
                    writeRepeatCount[key] = 0;
                }
                return true;
            }
        }

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                logPath = Path.Combine(baseDir, "CMOLocalizer.log");
                Log("=== CMOLocalizer v4.0 Engine Starting ===");
                InstallCrashGuard();

                SetupChineseDateTimeCulture();

                // Start hook thread
                Thread hookThread = new Thread(HookWorker);
                hookThread.IsBackground = true;
                hookThread.SetApartmentState(ApartmentState.STA);
                hookThread.Start();

                // 启动启动弹窗与错误提示静默自动消除守护线程
                Thread dialogDismissThread = new Thread(AutoDismissStartupDialogs);
                dialogDismissThread.IsBackground = true;
                dialogDismissThread.Start();
            }
            catch (Exception ex)
            {
                Log("InitializeNewDomain Exception: " + ex.Message);
            }

            base.InitializeNewDomain(appDomainInfo);
        }

        private static bool uiCultureConfigured = false;
        private static void PatchCulture(CultureInfo ci)
        {
            if (ci == null) return;
            try
            {
                BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                FieldInfo fRoCi = typeof(CultureInfo).GetField("m_isReadOnly", bf);
                if (fRoCi != null) fRoCi.SetValue(ci, false);

                DateTimeFormatInfo dtf = ci.DateTimeFormat;
                FieldInfo fRoDtf = typeof(DateTimeFormatInfo).GetField("m_isReadOnly", bf);
                if (fRoDtf != null) fRoDtf.SetValue(dtf, false);

                dtf.LongDatePattern = "yyyy'年'M'月'd'日' dddd";
                dtf.DayNames = new string[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
                dtf.AbbreviatedDayNames = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
                dtf.MonthNames = new string[] { "1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月", "" };
                dtf.AbbreviatedMonthNames = new string[] { "1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月", "" };
            }
            catch { }
        }

        private static void SetupChineseDateTimeCulture()
        {
            try
            {
                PatchCulture(CultureInfo.GetCultureInfo("en-US"));
                PatchCulture(CultureInfo.GetCultureInfo("en-us"));
                PatchCulture(CultureInfo.InvariantCulture);
                PatchCulture(CultureInfo.CurrentCulture);
                PatchCulture(Thread.CurrentThread.CurrentCulture);

                try { CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US"); } catch { }
                try { CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US"); } catch { }
                Log("SetupChineseDateTimeCulture configured successfully.");
            }
            catch (Exception ex)
            {
                Log("SetupChineseDateTimeCulture error: " + ex.Message);
            }
        }

        private class MapPlaceEntry
        {
            public string Name;
            public double Lat;
            public double Lon;
            public int Level;
        }

        private static readonly List<MapPlaceEntry> mapPlaces = new List<MapPlaceEntry>();
        private sealed class TileClient
        {
            public readonly TcpClient Client;
            public readonly long AcceptedAt;

            public TileClient(TcpClient client)
            {
                Client = client;
                AcceptedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        private static TcpListener tileListener = null;
        private const int TILE_CLIENT_TIMEOUT_MS = 1500;
        private const int TILE_WORKERS = 4;
        private const int TILE_QUEUE_CAPACITY = 64;
        private const int TILE_REQUEST_LOG_LIMIT = 16;
        private static readonly object tileRequestLogLock = new object();
        private static int tileRequestLogCount;

        private static void StartLocalTileServer(string baseDir)
        {
            try
            {
                string synWwp = Path.Combine(baseDir, @"WW\PlacenameSets\Synoptic\0000_0000.wwp");
                string citWwp = Path.Combine(baseDir, @"WW\PlacenameSets\Cities\0000_0000.wwp");
                Action<string, int> loadWwp = (path, lvl) =>
                {
                    if (!File.Exists(path)) return;
                    try
                    {
                        using (BinaryReader br = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                        {
                            int count = br.ReadInt32();
                            for (int i = 0; i < count; i++)
                            {
                                string name = br.ReadString();
                                float lat = br.ReadSingle();
                                float lon = br.ReadSingle();
                                br.ReadUInt32();
                                mapPlaces.Add(new MapPlaceEntry { Name = name, Lat = lat, Lon = lon, Level = lvl });
                            }
                        }
                    }
                    catch { }
                };

                loadWwp(synWwp, 0);
                loadWwp(citWwp, 1);

                tileListener = new TcpListener(IPAddress.Loopback, 0);
                tileListener.Start();
                int port = ((IPEndPoint)tileListener.LocalEndpoint).Port;

                string localUrl = "http://127.0.0.1:" + port + "/MapTiles/Labels/";
                offsetMap[1789870] = localUrl;
                offsetMap[21564] = "zh-CN";
                offsetMap[62636] = "zh-CN";
                Log("LocalTileServer started on " + localUrl + " with " + mapPlaces.Count + " Chinese landmarks.");

                // Game downloads also use the CLR pool. Keep both accepting and
                // serving independent, with bounded workers and queued sockets.
                var requests = new System.Collections.Concurrent.BlockingCollection<TileClient>(TILE_QUEUE_CAPACITY);
                for (int i = 0; i < TILE_WORKERS; i++)
                {
                    Thread worker = new Thread(() =>
                    {
                        foreach (TileClient request in requests.GetConsumingEnumerable()) HandleTileClient(request);
                    });
                    worker.IsBackground = true;
                    worker.Name = "CMO Chinese map tile worker " + i;
                    worker.Start();
                }

                Thread serverThread = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            TileClient request = new TileClient(tileListener.AcceptTcpClient());
                            if (!requests.TryAdd(request))
                            {
                                request.Client.Close();
                                LogTileRequest("queue full; client closed");
                            }
                        }
                    }
                    catch (Exception ex) { LogTileRequest("accept stopped: " + ex.Message); }
                    finally { requests.CompleteAdding(); }
                });
                serverThread.IsBackground = true;
                serverThread.Name = "CMO Chinese map tiles";
                serverThread.Start();
            }
            catch (Exception ex)
            {
                Log("StartLocalTileServer error: " + ex.Message);
            }
        }

        private static void LogTileRequest(string message)
        {
            if (Interlocked.Increment(ref tileRequestLogCount) <= TILE_REQUEST_LOG_LIMIT)
                lock (tileRequestLogLock) Log("LocalTile " + message);
        }

        private static string ReadTileRequestLine(StreamReader reader, NetworkStream stream, long acceptedAt)
        {
            long deadline = acceptedAt + TILE_CLIENT_TIMEOUT_MS * System.Diagnostics.Stopwatch.Frequency / 1000;
            StringBuilder line = new StringBuilder();
            while (true)
            {
                long remaining = deadline - System.Diagnostics.Stopwatch.GetTimestamp();
                if (remaining <= 0) throw new TimeoutException("first request line exceeded 1500ms from accept");
                stream.ReadTimeout = Math.Max(1, (int)Math.Ceiling(remaining * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                int next = reader.Read();
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException("first request line exceeded 1500ms from accept");
                if (next < 0) return null;
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= 1024) throw new InvalidDataException("request line exceeds 1024 bytes");
                line.Append((char)next);
            }
        }

        private static void HandleTileClient(TileClient request)
        {
            try
            {
                using (request.Client)
                using (NetworkStream ns = request.Client.GetStream())
                using (StreamReader sr = new StreamReader(ns, Encoding.ASCII))
                using (BinaryWriter bw = new BinaryWriter(ns))
                {
                    ns.WriteTimeout = TILE_CLIENT_TIMEOUT_MS;
                    string reqLine = ReadTileRequestLine(sr, ns, request.AcceptedAt);
                    if (string.IsNullOrEmpty(reqLine)) return;

                    // Parse "GET /MapTiles/Labels/{z}/{x}/{y}.png HTTP/1.1"
                    int z, x, y;
                    Match m = Regex.Match(reqLine, @"^GET /MapTiles/Labels/([0-9]{1,2})/([0-9]{1,7})/([0-9]{1,7})\.png HTTP/1\.[01]$");
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out z) || z > 22
                        || !int.TryParse(m.Groups[2].Value, out x) || x >= (1 << z)
                        || !int.TryParse(m.Groups[3].Value, out y) || y >= (1 << z))
                    {
                        LogTileRequest("rejected " + reqLine);
                        bw.Write(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        bw.Flush();
                        return;
                    }

                    int candidates, rendered;
                    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                    byte[] pngBytes = RenderChineseTileBytes(z, x, y, out candidates, out rendered);
                    string header = string.Format(
                        "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {0}\r\nConnection: close\r\n\r\n",
                        pngBytes.Length);
                    bw.Write(Encoding.ASCII.GetBytes(header));
                    bw.Write(pngBytes);
                    bw.Flush();
                    LogTileRequest(string.Format("z={0} x={1} y={2} candidates={3} rendered={4} bytes={5} ms={6}",
                        z, x, y, candidates, rendered, pngBytes.Length, elapsed.ElapsedMilliseconds));
                }
            }
            catch (Exception ex) { LogTileRequest("error " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static byte[] RenderChineseTileBytes(int z, int x, int y, out int candidates, out int rendered)
        {
            double n = Math.Pow(2.0, z);
            double minLon = x / n * 360.0 - 180.0;
            double maxLon = (x + 1) / n * 360.0 - 180.0;
            double maxLat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
            double minLat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * (y + 1) / n))) * 180.0 / Math.PI;

            List<MapPlaceEntry> inTile = new List<MapPlaceEntry>();
            for (int i = 0; i < mapPlaces.Count; i++)
            {
                MapPlaceEntry p = mapPlaces[i];
                if (p.Lon >= minLon && p.Lon <= maxLon && p.Lat >= minLat && p.Lat <= maxLat)
                {
                    if (z <= 5) { if (p.Level == 0) inTile.Add(p); }
                    else { inTile.Add(p); }
                }
            }

            candidates = inTile.Count;
            rendered = 0;
            using (Bitmap bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    if (inTile.Count > 0)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        inTile.Sort((a, b) => a.Level.CompareTo(b.Level));

                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;

                        List<RectangleF> occupied = new List<RectangleF>();
                        foreach (MapPlaceEntry p in inTile)
                        {
                            double xExact = (p.Lon + 180.0) / 360.0 * n;
                            double latRad = p.Lat * Math.PI / 180.0;
                            double yExact = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
                            float rawPx = (float)((xExact - x) * 256.0);
                            float rawPy = (float)((yExact - y) * 256.0);

                            float fontSize = (p.Level == 0) ? (z <= 3 ? 9.5f : (z <= 4 ? 10.5f : 11.5f)) : (z <= 7 ? 9.0f : 8.5f);
                            FontStyle fStyle = (p.Level == 0) ? FontStyle.Bold : FontStyle.Regular;

                            using (Font f = new Font("Microsoft YaHei", fontSize, fStyle))
                            {
                                SizeF sz = g.MeasureString(p.Name, f);
                                float halfW = sz.Width * 0.5f;
                                float halfH = sz.Height * 0.5f;
                                float px = Math.Max(halfW + 3f, Math.Min(256f - halfW - 3f, rawPx));
                                float py = Math.Max(halfH + 3f, Math.Min(256f - halfH - 3f, rawPy));

                                RectangleF bbox = new RectangleF(px - halfW - 2f, py - halfH - 2f, sz.Width + 4f, sz.Height + 4f);
                                bool collides = false;
                                foreach (RectangleF occ in occupied)
                                {
                                    if (bbox.IntersectsWith(occ)) { collides = true; break; }
                                }
                                if (collides) continue;
                                occupied.Add(bbox);

                                using (GraphicsPath gp = new GraphicsPath())
                                {
                                    gp.AddString(p.Name, new FontFamily("Microsoft YaHei"), (int)fStyle, fontSize, new PointF(px, py), sf);
                                    Color penC = (p.Level == 0) ? Color.FromArgb(220, 15, 15, 15) : Color.FromArgb(200, 25, 25, 25);
                                    float penW = (p.Level == 0) ? 3.0f : 2.2f;
                                    using (Pen pen = new Pen(penC, penW))
                                    {
                                        pen.LineJoin = LineJoin.Round;
                                        g.DrawPath(pen, gp);
                                    }
                                    Color txtC = (p.Level == 0) ? Color.FromArgb(255, 255, 255) : Color.FromArgb(240, 240, 240);
                                    using (SolidBrush br = new SolidBrush(txtC))
                                    {
                                        g.FillPath(br, gp);
                                    }
                                    rendered++;
                                }
                            }
                        }
                    }
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static void HookWorker()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dictPath = Path.Combine(baseDir, "CMOLocalizer.json");
                if (File.Exists(dictPath))
                {
                    LoadDictionaries(dictPath);
                    Log(string.Format("Loaded dictionaries: {0} offsets, {1} exact texts, {2} partial terms, {3} word roots.",
                        offsetMap.Count, exactMap.Count, partialMap.Count, wordsMap.Count));
                }
                else
                {
                    Log("Warning: CMOLocalizer.json not found at " + dictPath);
                }

                // 启动本地毫秒级中文瓦片动态渲染服务器，接管在线英文瓦片请求
                StartLocalTileServer(baseDir);

                // Start UI watcher loop in a background thread to translate WinForms controls
                Thread uiThread = new Thread(StartUIWatcher);
                uiThread.IsBackground = true;
                uiThread.Start();

                // Wait for target assembly and types to load
                int tries = 0;
                bool injectedEngine = false;
                while (tries < 120)
                {
                    tries++;
                    Thread.Sleep(500);

                    if (!injectedEngine)
                    {
                        injectedEngine = TryInjectCoreDecryption();
                    }

                    if (injectedEngine)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("HookWorker Error: " + ex.ToString());
            }
        }

        private static bool IsForbiddenCodeOrEnum(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            // 严禁包含Lua代码/函数调用/SQL/路径/格式串/沙箱白名单
            if (s.StartsWith("ScenEdit_") || s.StartsWith("math.") || s.StartsWith("string.") || s.StartsWith("table.") || s.StartsWith("LUA_") || s.StartsWith("VP_") || s.StartsWith("Tool_") || s.StartsWith("World_")) return true;
            if (s.Contains("ScenEdit_") || s.Contains("SELECT ") || s.Contains("INSERT ") || s.Contains("UPDATE ") || s.Contains("DELETE ") || s.Contains(" FROM ") || s.Contains(" WHERE ") || s.Contains("sqlite")) return true;
            if (s.Contains(" = ") || s.Contains(" =\"") || s.Contains(" ='") || s.Contains("= '") || s.Contains("','") || s.Contains("', '") || s.Contains("({\"") || s.Contains("({'")) return true;
            if (s.Contains("break,do") || s.Contains("elseif") || s.Contains("collectgarbage") || s.Contains("getmetatable") || s.Contains("setmetatable") || s.Contains("theTable")) return true;
            if (s.Contains("ASSOCIATORS OF") || s.Contains("Win32_")) return true;
            if (s.Contains("{0:F") || s.Contains("{0:D") || s.Contains("{0:X")) return true;
            // 知名代码枚举
            if (s == "DockingOps" || s == "AirOps" || s == "CargoOps" || s == "ScenEdit") return true;
            return false;
        }

        private static bool TryInjectCoreDecryption()
        {
            try
            {
                Type tDecrypt;
                FieldInfo fStrings, fOffsets, fBuf;
                byte[] buf;
                string note;
                if (!TryFindPool(out tDecrypt, out fStrings, out fOffsets, out buf, out fBuf, out note))
                {
                    Log("CoreDecryption: string pool not available on this game build — offset injection disabled. " + note);
                    return false;
                }

                BindingFlags bf = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                FieldInfo fDebug = tDecrypt.GetField("gTbYFnJa7EN", bf);   // 旧构建字段名（新构建已改）
                if (fDebug != null)
                {
                    fDebug.SetValue(null, 100);
                    Log("Set gTbYFnJa7EN anti-debug flag to 100.");
                }
                else
                {
                    Log("CoreDecryption: anti-debug flag field renamed by this build — skipped（不参与注入）.");
                }

                List<string> strList = (List<string>)fStrings.GetValue(null);
                List<int> offList = (List<int>)fOffsets.GetValue(null);
                if (strList == null || offList == null || offsetMap.Count == 0)
                {
                    Log(string.Format("CoreDecryption: pool not ready (strList={0} offList={1} offsetMap={2}) — offset injection skipped.",
                        strList != null, offList != null, offsetMap.Count));
                    return false;
                }

                Log("CoreDecryption: " + note);

                int count = 0;
                int outOfRange = 0;
                lock (strList)
                {
                    foreach (KeyValuePair<int, string> kvp in offsetMap)
                    {
                        int off = kvp.Key;
                        string translated = kvp.Value;
                        if (IsForbiddenCodeOrEnum(translated))
                        {
                            continue;
                        }
                        if (off < 0 || off + 4 > buf.Length)
                        {
                            outOfRange++;
                            continue;
                        }
                        int newIdx = strList.Count;
                        strList.Add(translated);
                        offList.Add(off);

                        buf[off] = (byte)newIdx;
                        buf[off + 1] = (byte)(newIdx >> 8);
                        buf[off + 2] = (byte)(newIdx >> 16);
                        buf[off + 3] = (byte)(newIdx >> 24);
                        count++;
                    }
                }
                Log(string.Format("Successfully pre-populated {0} translated strings into {1} string pool! (offsets out of range: {2})",
                    count, tDecrypt.Name, outOfRange));
                return true;
            }
            catch (Exception ex)
            {
                Log("TryInjectCoreDecryption Exception: " + ex.ToString());
            }
            return false;
        }

        /// <summary>
        /// 定位游戏解密字符串池：混淆类/字段名随游戏更新变化（R31：1900.20 → 1900.23 字段名全变），
        /// 所以名字优先、结构特征（静态 List&lt;string&gt; + List&lt;int&gt; + byte[]）兜底。
        /// </summary>
        private static bool TryFindPool(out Type tDecrypt, out FieldInfo fStrings, out FieldInfo fOffsets, out byte[] buf, out FieldInfo fBuf, out string note)
        {
            tDecrypt = null;
            fStrings = null;
            fOffsets = null;
            buf = null;
            fBuf = null;
            note = "";

            Assembly host = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an == "Command" || an.StartsWith("Command"))
                {
                    host = asm;
                    break;
                }
            }
            if (host == null)
            {
                note = "Command assembly not found.";
                return false;
            }

            Type byName = host.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU", false);
            if (byName != null && HasPoolShape(byName))
            {
                tDecrypt = byName;
            }
            else
            {
                Type[] types = null;
                try { types = host.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                catch (Exception ex) { note = "type scan failed: " + ex.Message; }
                if (types != null)
                {
                    foreach (Type t in types)
                    {
                        if (t != null && t != byName && HasPoolShape(t))
                        {
                            tDecrypt = t;
                            break;
                        }
                    }
                }
            }
            if (tDecrypt == null)
            {
                note = "pool class not found（游戏更新后需重做注入点探测，见 docs/BUG_REGISTRY.md R31）.";
                return false;
            }

            BindingFlags bf = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            List<FieldInfo> bufFields = new List<FieldInfo>();
            foreach (FieldInfo fi in tDecrypt.GetFields(bf))
            {
                Type ft = fi.FieldType;
                if (fStrings == null && ft == typeof(List<string>)) fStrings = fi;
                else if (fOffsets == null && ft == typeof(List<int>)) fOffsets = fi;
                else if (ft == typeof(byte[])) bufFields.Add(fi);
            }
            if (fStrings == null || fOffsets == null || bufFields.Count == 0)
            {
                note = string.Format("pool fields incomplete in {0} (strings={1} offsets={2} byte[]={3}).",
                    tDecrypt.FullName, fStrings != null, fOffsets != null, bufFields.Count);
                return false;
            }

            string candidates = "";
            foreach (FieldInfo fi in bufFields)
            {
                byte[] b = null;
                try { b = (byte[])fi.GetValue(null); }
                catch (Exception) { }
                if (b == null) continue;
                candidates += (candidates.Length > 0 ? ", " : "") + fi.Name + "=" + b.Length;
                if (buf == null || b.Length > buf.Length)
                {
                    buf = b;
                    fBuf = fi;
                }
            }
            if (buf == null)
            {
                note = "pool buffer not initialised (byte[]: " + candidates + ").";
                return false;
            }

            note = string.Format("pool class {0}; strings={1} buffer={2} ({3} B; byte[] candidates: {4})",
                tDecrypt.FullName, fStrings.Name, fBuf.Name, buf.Length, candidates);
            return true;
        }

        /// <summary>
        /// 只读探针：导出本构建的「偏移 → 当前字符串」全表，供离线核对/重采偏移
        /// （对照 dict/strings_pool.json 里的英文源串）。
        /// </summary>
        private static string HandlePoolDump()
        {
            try
            {
                Type tDecrypt;
                FieldInfo fStrings, fOffsets, fBuf;
                byte[] buf;
                string note;
                if (!TryFindPool(out tDecrypt, out fStrings, out fOffsets, out buf, out fBuf, out note))
                {
                    return "{\"status\":\"error\",\"message\":\"" + JsonEsc(note) + "\"}";
                }

                List<string> strList = (List<string>)fStrings.GetValue(null);
                List<int> offList = (List<int>)fOffsets.GetValue(null);
                if (strList == null || offList == null)
                {
                    return "{\"status\":\"error\",\"message\":\"pool lists null\"}";
                }

                StringBuilder sb = new StringBuilder();
                int pairs = 0;
                lock (strList)
                {
                    int n = Math.Min(strList.Count, offList.Count);
                    for (int i = 0; i < n; i++)
                    {
                        string text = strList[i];
                        if (text == null) continue;
                        sb.Append(offList[i]).Append('\t').Append(text.Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
                        pairs++;
                    }
                }

                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer_PoolDump.tsv");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Log(string.Format("POOL_DUMP: {0} pairs -> {1} ({2})", pairs, path, note));
                return "{\"status\":\"ok\",\"pairs\":" + pairs + ",\"strList\":" + strList.Count + ",\"offList\":" + offList.Count
                     + ",\"buffer\":" + (buf != null ? buf.Length : 0) + ",\"file\":\"" + JsonEsc(path) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"status\":\"error\",\"message\":\"" + JsonEsc(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 结构判据：混淆类名/字段名会随游戏更新变化，但「静态 List&lt;string&gt; +
        /// 静态 List&lt;int&gt; + 静态 byte[]」这一组合是解密字符串池的稳定特征。
        /// </summary>
        private static bool HasPoolShape(Type t)
        {
            try
            {
                if (t == null || t.IsGenericTypeDefinition) return false;
                bool hasStrings = false, hasOffsets = false, hasBuf = false;
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Type ft = fi.FieldType;
                    if (ft == typeof(List<string>)) hasStrings = true;
                    else if (ft == typeof(List<int>)) hasOffsets = true;
                    else if (ft == typeof(byte[])) hasBuf = true;
                }
                return hasStrings && hasOffsets && hasBuf;
            }
            catch { return false; }
        }

        private static void StartUIWatcher()
        {
            Log("Starting UI Watcher loop (Adaptive interval)...");
            int loopCount = 0;
            while (true)
            {
                try
                {
                    // Scan faster during startup (first 30s), then settle to 1500ms
                    int interval = (loopCount < 40) ? 600 : 1500;
                    Thread.Sleep(interval);
                    loopCount++;

                    // Process external automated test commands (if any)
                    try
                    {
                        ProcessTestCommands();
                    }
                    catch { }

                    FormCollection openForms = Application.OpenForms;
                    if (openForms != null && openForms.Count > 0)
                    {
                        foreach (Form frm in openForms)
                        {
                            if (frm != null && !frm.IsDisposed)
                            {
                                if (frm.InvokeRequired)
                                {
                                    frm.BeginInvoke((MethodInvoker)delegate
                                    {
                                        try
                                        {
                                            if (!uiCultureConfigured || Thread.CurrentThread.CurrentCulture.DateTimeFormat.LongDatePattern != "yyyy'年'M'月'd'日' dddd")
                                            {
                                                uiCultureConfigured = true;
                                                SetupChineseDateTimeCulture();
                                            }
                                            TranslateControlTree(frm);
                                        }
                                        catch { }
                                    });
                                }
                                else
                                {
                                    PatchMainFormFonts(frm);
                                    TranslateControlTree(frm);
                                }
                            }
                        }
                    }

                    // Scan WPF Windows (including StartGameWindow / HwndWrapper)
                    try
                    {
                        TranslateWpfWindows();
                    }
                    catch { }

                    // WinForms windows created on helper threads never appear in
                    // Application.OpenForms; resolve them from their HWND.
                    try
                    {
                        TranslateFormsByHwnd();
                    }
                    catch { }
                }
                catch { }
            }
        }

        /// <summary>
        /// Walk every top-level window of this process and translate the WinForms
        /// forms that Application.OpenForms cannot see (modal dialogs and forms
        /// owned by secondary UI threads).
        /// </summary>
        private static void TranslateFormsByHwnd()
        {
            if (currentPid == 0)
            {
                currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid != currentPid) return true;
                        if (!IsWindowVisible(hWnd)) return true;

                        StringBuilder sb = new StringBuilder(256);
                        GetClassName(hWnd, sb, sb.Capacity);
                        string cls = sb.ToString();
                        if (cls.StartsWith("HwndWrapper")) return true;

                        Control c = Control.FromHandle(hWnd);
                        Form frm = c as Form;
                        if (frm == null || frm.IsDisposed) return true;
                        int h = frm.GetHashCode();
                        if (watchedByHwndForms.Contains(h)) return true;
                        watchedByHwndForms.Add(h);

                        frm.BeginInvoke((MethodInvoker)delegate
                        {
                            try
                            {
                                TranslateControlTree(frm);
                            }
                            catch { }
                        });
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        private static readonly HashSet<int> watchedByHwndForms = new HashSet<int>();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;
        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static uint currentPid = 0;

        private static void AutoDismissStartupDialogs()
        {
            if (currentPid == 0)
            {
                currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }

            int count = 0;
            // 启动前 60 秒高频检测并消除偶发的 Application_Startup 对话框及横幅
            while (count < 300)
            {
                count++;
                try
                {
                    EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == currentPid)
                        {
                            StringBuilder sbClass = new StringBuilder(256);
                            GetClassName(hWnd, sbClass, sbClass.Capacity);
                            string cls = sbClass.ToString();

                            StringBuilder sbText = new StringBuilder(512);
                            GetWindowText(hWnd, sbText, sbText.Capacity);
                            string txt = sbText.ToString();

                            // The start-up error is raised by the game's own
                            // WinForms form (class WindowsForms10.Window...), NOT a
                            // #32770 MessageBox, and its title is translated by this
                            // very dictionary -- so match the class prefix plus the
                            // phrase in either language.
                            if (cls == "#32770"
                                || txt.Contains("Application_Startup")
                                || txt.Contains("Error in Application")
                                || txt.Contains("application startup")
                                || txt.Contains("应用程序启动"))
                            {
                                Log("AutoDismissStartupDialogs: Closing dialog '" + txt + "'");
                                // 查找 OK 按钮并点击
                                EnumChildWindows(hWnd, delegate(IntPtr hChild, IntPtr lp)
                                {
                                    StringBuilder btnText = new StringBuilder(64);
                                    GetWindowText(hChild, btnText, btnText.Capacity);
                                    if (btnText.ToString() == "确定" || btnText.ToString().ToUpper() == "OK")
                                    {
                                        SendMessage(hChild, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                                    }
                                    return true;
                                }, IntPtr.Zero);
                                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }
                            else if (txt.Contains("CPEBannerForm") || cls.Contains("CPEBannerForm"))
                            {
                                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                Thread.Sleep(200);
            }
        }

        private static void TranslateWpfWindows()
        {
            InstallWpfRenderHook();
            try
            {
                if (currentPid == 0)
                {
                    currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                }

                // Enumerate all HwndWrapper windows in current process (covers all WPF windows across any UI thread)
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == currentPid)
                        {
                            StringBuilder sb = new StringBuilder(256);
                            GetClassName(hWnd, sb, sb.Capacity);
                            string cls = sb.ToString();
                            if (cls.StartsWith("HwndWrapper"))
                            {
                                System.Windows.Interop.HwndSource src = System.Windows.Interop.HwndSource.FromHwnd(hWnd);
                                if (src != null && src.Dispatcher != null)
                                {
                                    src.Dispatcher.BeginInvoke(new Action(delegate
                                    {
                                        try
                                        {
                                            if (src.RootVisual != null)
                                            {
                                                System.Windows.DependencyObject rootDep = src.RootVisual as System.Windows.DependencyObject;
                                                System.Windows.Window win = rootDep != null ? System.Windows.Window.GetWindow(rootDep) : null;
                                                if (win != null)
                                                {
                                                    string wt = win.Title;
                                                    string trWt = TranslateTitleText(wt);
                                                    if (!string.IsNullOrEmpty(trWt) && trWt != wt) win.Title = trWt;
                                                }
                                                TranslateWpfElement(src.RootVisual);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log("WPF Dispatcher invoke error: " + ex.Message);
                                        }
                                    }));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("EnumWindows item error: " + ex.Message);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log("TranslateWpfWindows outer error: " + ex.Message);
            }
        }

        private static int wpfTranslatedCount = 0;

        private static readonly HashSet<int> hookedWpfElements = new HashSet<int>();
        [ThreadStatic]
        private static bool _inWpfTextChanged;

        private static readonly List<System.Windows.DependencyObject> liveWpfTargets = new List<System.Windows.DependencyObject>();
        private static bool _renderHookInstalled;
        private static int _renderFrame;
        [ThreadStatic] private static bool _inRenderPass;
        private static readonly object _wpfLogLock = new object();
        private static readonly List<string> _wpfLog = new List<string>();

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static void LogWpfWrite(string kind, string oldText, string newText)
        {
            try
            {
                lock (_wpfLogLock)
                {
                    if (_wpfLog.Count >= 400) _wpfLog.RemoveAt(0);
                    _wpfLog.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "|" + kind + "|" +
                                Trunc(oldText, 70) + " ==> " + Trunc(newText, 70));
                }
            }
            catch { }
        }

        private static string DumpWpfLog()
        {
            StringBuilder sb = new StringBuilder();
            string[] arr;
            lock (_wpfLogLock)
            {
                arr = _wpfLog.ToArray();
            }
            sb.Append("{\"status\":\"ok\",\"count\":").Append(arr.Length).Append(",\"log\":[");
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(JsonEsc(arr[i])).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string ReadWpfText(System.Windows.DependencyObject dep)
        {
            try
            {
                System.Windows.Controls.TextBlock tb = dep as System.Windows.Controls.TextBlock;
                if (tb != null) return tb.Text;
                System.Windows.Documents.Run run = dep as System.Windows.Documents.Run;
                if (run != null) return run.Text;
                System.Windows.Controls.ContentControl cc = dep as System.Windows.Controls.ContentControl;
                if (cc != null) return cc.Content as string;
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 记住“确实需要翻译”的少量 WPF 元素（单位状态面板、状态栏、消息日志等实时文本）。
        /// 游戏每帧/每秒把绑定源刷成英文时，这些元素就是频闪的来源。
        /// </summary>
        private static void TrackLiveWpfTarget(System.Windows.DependencyObject dep)
        {
            if (dep == null) return;
            try
            {
                lock (liveWpfTargets)
                {
                    if (liveWpfTargets.Count >= 400) return;
                    for (int i = 0; i < liveWpfTargets.Count; i++)
                    {
                        if (object.ReferenceEquals(liveWpfTargets[i], dep)) return;
                    }
                    liveWpfTargets.Add(dep);
                }
            }
            catch { }
        }

        /// <summary>
        /// 每帧渲染前把被游戏刷回英文的实时文本改回中文。
        /// 不挂钩依赖属性（那会在属性系统回调里重入并导致游戏崩溃），
        /// 而是在 CompositionTarget.Rendering（绘制前触发）里重译少量已知元素，
        /// 因此英文不会有机会被画到屏幕上——彻底消除「一会中文一会英文」。
        /// </summary>
        private static void InstallWpfRenderHook()
        {
            if (_renderHookInstalled) return;
            _renderHookInstalled = true;
            try
            {
                System.Windows.Media.CompositionTarget.Rendering += new EventHandler(OnWpfRenderTick);
                Log("WPF render hook installed (per-frame retranslate of live text)");
            }
            catch (Exception ex)
            {
                Log("WPF render hook failed: " + ex.Message);
            }
        }

        private static void OnWpfRenderTick(object sender, EventArgs e)
        {
            if (_inRenderPass) return;
            _inRenderPass = true;
            try
            {
                // 每 8 帧（约 130ms）做一次全量补扫：换单位/换面板时会新建 WPF 元素，
                // 新元素尚未进入 liveWpfTargets，全量扫描保证它首帧后立刻变中文。
                _renderFrame++;
                if ((_renderFrame & 7) == 0)
                {
                    try { TranslateWpfWindows(); } catch { }
                }

                System.Windows.DependencyObject[] snapshot;
                lock (liveWpfTargets)
                {
                    if (liveWpfTargets.Count == 0) return;
                    snapshot = liveWpfTargets.ToArray();
                }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        System.Windows.DependencyObject dep = snapshot[i];
                        if (dep == null) continue;
                        string cur = ReadWpfText(dep);
                        if (string.IsNullOrEmpty(cur) || ContainsChinese(cur)) continue;
                        TranslateWpfElement(dep);
                    }
                    catch { }
                }
            }
            catch { }
            finally { _inRenderPass = false; }
        }

        /// <summary>把控件树（类型/名称/可见/尺寸/文本）导出为 JSON，用于定位自绘或数据绑定控件。</summary>
        private static void DumpControlTree(Control c, StringBuilder sb, ref bool first, int depth)
        {
            if (c == null || depth > 6) return;
            try
            {
                string txt = "";
                try { txt = c.Text ?? ""; } catch { }
                if (txt.Length > 120) txt = txt.Substring(0, 120);
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"d\":").Append(depth)
                  .Append(",\"t\":\"").Append(JsonEsc(c.GetType().Name))
                  .Append("\",\"n\":\"").Append(JsonEsc(c.Name))
                  .Append("\",\"v\":").Append(c.Visible ? "true" : "false")
                  .Append(",\"b\":\"").Append(c.Bounds.Width).Append('x').Append(c.Bounds.Height)
                  .Append("\",\"text\":\"").Append(JsonEsc(txt)).Append("\"}");
                foreach (Control child in c.Controls)
                {
                    DumpControlTree(child, sb, ref first, depth + 1);
                }
            }
            catch { }
        }

        private static string HandleCtrlTree(string cmd)
        {
            string filter = cmd.Length > "CTRL_TREE".Length ? cmd.Substring("CTRL_TREE".Length).Trim() : "";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"status\":\"ok\",\"filter\":\"").Append(JsonEsc(filter)).Append("\",\"nodes\":[");
            bool first = true;
            FormCollection forms = Application.OpenForms;
            if (forms != null)
            {
                foreach (Form f in forms)
                {
                    if (f == null || f.IsDisposed) continue;
                    if (filter.Length > 0)
                    {
                        string ft = f.Text ?? "";
                        string fn = f.Name ?? "";
                        if (ft.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            fn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    DumpControlTree(f, sb, ref first, 0);
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void TranslateWpfElement(System.Windows.DependencyObject dep)
        {
            if (dep == null) return;
            TranslateWpfElementInner(dep);
        }

        private static void TranslateWpfElementInner(System.Windows.DependencyObject dep)
        {
            try
            {
                if (dep is System.Windows.Controls.HeaderedContentControl)
                {
                    System.Windows.Controls.HeaderedContentControl hc = (System.Windows.Controls.HeaderedContentControl)dep;
                    if (hc.Header is string)
                    {
                        string s = (string)hc.Header;
                        if (!string.IsNullOrEmpty(s))
                        {
                            string trans = TranslateString(s);
                            if (trans != s) hc.Header = trans;
                        }
                    }
                    else if (hc.Header is System.Windows.DependencyObject)
                    {
                        TranslateWpfElement((System.Windows.DependencyObject)hc.Header);
                    }
                }
                if (dep is System.Windows.Controls.HeaderedItemsControl)
                {
                    System.Windows.Controls.HeaderedItemsControl hic = (System.Windows.Controls.HeaderedItemsControl)dep;
                    if (hic.Header is string)
                    {
                        string s = (string)hic.Header;
                        if (!string.IsNullOrEmpty(s))
                        {
                            string trans = TranslateString(s);
                            if (trans != s) hic.Header = trans;
                        }
                    }
                    else if (hic.Header is System.Windows.DependencyObject)
                    {
                        TranslateWpfElement((System.Windows.DependencyObject)hic.Header);
                    }
                }
                if (dep is System.Windows.Controls.TextBlock)
                {
                    System.Windows.Controls.TextBlock tb = (System.Windows.Controls.TextBlock)dep;
                    int tbHash = tb.GetHashCode();
                    if (!hookedWpfElements.Contains(tbHash))
                    {
                        hookedWpfElements.Add(tbHash);
                        try
                        {
                            var desc = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                                System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock));
                            if (desc != null)
                            {
                                desc.AddValueChanged(tb, (s, e) =>
                                {
                                    if (_inWpfTextChanged) return;
                                    if (!EnterMutation("WpfTextBlockHook")) return;
                                    try
                                    {
                                        _inWpfTextChanged = true;
                                        System.Windows.Controls.TextBlock targetTb = s as System.Windows.Controls.TextBlock;
                                        if (targetTb != null && !string.IsNullOrEmpty(targetTb.Text))
                                        {
                                            string cur = targetTb.Text;
                                            if (cur.Contains("kg") && (cur.Contains("fuel") || cur.Contains("燃油") || cur.Contains("Tank") || cur.Contains("坦克") || cur.Contains("reserve")))
                                            {
                                                string trF = TranslateFuelPanelText(cur);
                                                if (trF != cur)
                                                {
                                                    SetTextKeepBinding(targetTb, trF);
                                                    return;
                                                }
                                            }
                                            if (!ContainsChinese(cur))
                                            {
                                                string tr = TranslateString(cur);
                                                if (string.IsNullOrEmpty(tr) || tr == cur)
                                                {
                                                    tr = TranslateMilitaryUnitName(cur);
                                                }
                                                if (tr != cur) SetTextKeepBinding(targetTb, tr);
                                            }
                                        }
                                    }
                                    catch { }
                                    finally { _inWpfTextChanged = false; ExitMutation(); }
                                });
                            }
                        }
                        catch { }
                    }

                    // 递归处理 Inlines (如包含 Hyperlink 的 Loadout 链接)
                    // 消息日志条目是 Run/Span 混合结构，必须递归下沉，否则正文
                    // 永远停在英文；SetCurrentValue 保留原绑定，避免刷新回退。
                    if (tb.Inlines != null && tb.Inlines.Count > 0)
                    {
                        List<System.Windows.Documents.Inline> inlines =
                            new List<System.Windows.Documents.Inline>();
                        foreach (System.Windows.Documents.Inline inl in tb.Inlines) inlines.Add(inl);
                        foreach (System.Windows.Documents.Inline inl in inlines)
                        {
                            System.Windows.Documents.Hyperlink hl = inl as System.Windows.Documents.Hyperlink;
                            if (hl != null)
                            {
                                List<System.Windows.Documents.Inline> subs =
                                    new List<System.Windows.Documents.Inline>();
                                foreach (System.Windows.Documents.Inline sub in hl.Inlines) subs.Add(sub);
                                foreach (System.Windows.Documents.Inline sub in subs)
                                {
                                    System.Windows.Documents.Run subRun = sub as System.Windows.Documents.Run;
                                    if (subRun != null && !string.IsNullOrEmpty(subRun.Text))
                                    {
                                        string trSub = TranslateCellText(subRun.Text);
                                        if (trSub != subRun.Text) SetRunTextKeepBinding(subRun, trSub);
                                    }
                                }
                                continue;
                            }
                            TranslateFlowInline(inl);
                        }
                    }
                    else if (!string.IsNullOrEmpty(tb.Text))
                    {
                        string cur = tb.Text;
                        if (cur.Contains("kg") && (cur.Contains("fuel") || cur.Contains("燃油") || cur.Contains("Tank") || cur.Contains("坦克") || cur.Contains("reserve")))
                        {
                            string trF = TranslateFuelPanelText(cur);
                            if (trF != cur)
                            {
                                tb.Text = trF;
                                return;
                            }
                        }
                        string trans = TranslateString(tb.Text);
                        if (string.IsNullOrEmpty(trans) || trans == tb.Text)
                        {
                            trans = TranslateMilitaryUnitName(tb.Text);
                        }
                        if (trans != tb.Text)
                        {
                            string old = tb.Text;
                            SetTextKeepBinding(tb, trans);
                            TrackLiveWpfTarget(tb);
                            LogWpfWrite("TextBlock", old, trans);
                            wpfTranslatedCount++;
                            if (wpfTranslatedCount <= 20) Log("WPF TextBlock Translated: " + old + " -> " + trans);
                        }
                    }
                }
                else if (dep is System.Windows.Controls.ContentControl)
                {
                    System.Windows.Controls.ContentControl cc = (System.Windows.Controls.ContentControl)dep;
                    int ccHash = cc.GetHashCode();
                    if (!hookedWpfElements.Contains(ccHash))
                    {
                        hookedWpfElements.Add(ccHash);
                        try
                        {
                            var desc = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                                System.Windows.Controls.ContentControl.ContentProperty, typeof(System.Windows.Controls.ContentControl));
                            if (desc != null)
                            {
                                desc.AddValueChanged(cc, (s, e) =>
                                {
                                    if (_inWpfTextChanged) return;
                                    if (!EnterMutation("WpfContentHook")) return;
                                    try
                                    {
                                        _inWpfTextChanged = true;
                                        var targetCc = s as System.Windows.Controls.ContentControl;
                                        if (targetCc != null && targetCc.Content is string)
                                        {
                                            string str = (string)targetCc.Content;
                                            if (!string.IsNullOrEmpty(str) && !ContainsChinese(str))
                                            {
                                                string tr = TranslateString(str);
                                                if (tr != str) SetContentKeepBinding(targetCc, tr);
                                            }
                                        }
                                    }
                                    catch { }
                                    finally { _inWpfTextChanged = false; ExitMutation(); }
                                });
                            }
                        }
                        catch { }
                    }

                    if (cc.Content is string)
                    {
                        string s = (string)cc.Content;
                        if (!string.IsNullOrEmpty(s))
                        {
                            string trans = TranslateString(s);
                            if (trans != s)
                            {
                                LogWpfWrite("ContentControl", s, trans);
                                TrackLiveWpfTarget(cc);
                                SetContentKeepBinding(cc, trans);
                                wpfTranslatedCount++;
                                if (wpfTranslatedCount <= 20) Log("WPF ContentControl Translated: " + s + " -> " + trans);
                            }
                        }
                    }
                    else if (cc.Content is System.Windows.DependencyObject)
                    {
                        TranslateWpfElement((System.Windows.DependencyObject)cc.Content);
                    }
                }

                // Message log / briefing surfaces render through Documents, not
                // TextBlocks: a FlowDocument's Runs are invisible to the TextBlock
                // pass, which is why the interactive message log stayed English and
                // flickered on every refresh.
                System.Windows.Documents.FlowDocument flowDoc = null;
                System.Windows.Controls.RichTextBox rtb = dep as System.Windows.Controls.RichTextBox;
                if (rtb != null)
                {
                    // 用户正在该富文本里输入时绝不碰其文档（与输入框同一类拉锯风险）
                    bool focusedRtb = false;
                    try { focusedRtb = rtb.IsKeyboardFocusWithin; } catch { }
                    if (!focusedRtb) flowDoc = rtb.Document;
                }
                if (flowDoc == null)
                {
                    Type dt = dep.GetType();
                    // 同族反射点收敛到带缓存的 Reflect.Prop（评审 O-02 / PRF-003）：
                    // 语义与 dt.GetProperty(...) 完全一致，未命中仍返回 null。
                    PropertyInfo piDoc = Reflect.Prop(dt, "Document", BindingFlags.Public | BindingFlags.Instance);
                    if (piDoc != null && piDoc.PropertyType == typeof(System.Windows.Documents.FlowDocument))
                    {
                        try { flowDoc = piDoc.GetValue(dep, null) as System.Windows.Documents.FlowDocument; } catch { }
                    }
                }
                if (flowDoc != null) TranslateFlowDocument(flowDoc);

                // Window captions live outside the visual tree.
                System.Windows.Window wnd = dep as System.Windows.Window;
                if (wnd != null)
                {
                    string wt = wnd.Title;
                    if (!string.IsNullOrEmpty(wt))
                    {
                        string trWt = TranslateTitleText(wt);
                        if (!string.IsNullOrEmpty(trWt) && trWt != wt) wnd.Title = trWt;
                    }
                }

                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep);
                for (int i = 0; i < count; i++)
                {
                    TranslateWpfElement(System.Windows.Media.VisualTreeHelper.GetChild(dep, i));
                }
            }
            catch { }
        }


        /// <summary>
        /// WPF text surfaces (message log, briefings) are data-bound.  Assigning
        /// .Text/.Content replaces the binding, so the next game refresh re-pushes
        /// the English source and the surface flickers.  SetCurrentValue changes
        /// the effective value only, leaving the binding intact.
        /// </summary>
        private static void SetTextKeepBinding(System.Windows.Controls.TextBlock tb, string text)
        {
            if (tb == null || string.IsNullOrEmpty(text)) return;
            if (!EnterMutation("SetTextKeepBinding")) return;
            try
            {
                if (!AllowWrite(tb.GetHashCode(), text, "WPF TextBlock")) return;
                tb.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, text);
            }
            catch
            {
                try { tb.Text = text; } catch { }
            }
            finally { ExitMutation(); }
        }

        private static void SetContentKeepBinding(System.Windows.Controls.ContentControl cc, string text)
        {
            try
            {
                cc.SetCurrentValue(System.Windows.Controls.ContentControl.ContentProperty, text);
            }
            catch
            {
                try { cc.Content = text; } catch { }
            }
        }

        private static void SetRunTextKeepBinding(System.Windows.Documents.Run run, string text)
        {
            try
            {
                run.SetCurrentValue(System.Windows.Documents.Run.TextProperty, text);
            }
            catch
            {
                try { run.Text = text; } catch { }
            }
        }

        private static int flowTranslatedCount = 0;

        /// <summary>
        /// Translate every Run of a FlowDocument in place.  Runs are the leaf text
        /// holders for the message log and briefing panes; rewriting them keeps
        /// paragraph structure and scroll position intact.
        /// </summary>
        private static void TranslateFlowDocument(System.Windows.Documents.FlowDocument doc)
        {
            if (doc == null) return;
            try
            {
                foreach (System.Windows.Documents.Block block in doc.Blocks)
                {
                    System.Windows.Documents.Section section = block as System.Windows.Documents.Section;
                    if (section != null)
                    {
                        foreach (System.Windows.Documents.Block inner in section.Blocks) TranslateFlowBlock(inner);
                    }
                    else
                    {
                        TranslateFlowBlock(block);
                    }
                }
            }
            catch { }
        }

        private static void TranslateFlowBlock(System.Windows.Documents.Block block)
        {
            System.Windows.Documents.Paragraph para = block as System.Windows.Documents.Paragraph;
            if (para == null) return;
            foreach (System.Windows.Documents.Inline inline in para.Inlines) TranslateFlowInline(inline);
        }

        private static void TranslateFlowInline(System.Windows.Documents.Inline inline)
        {
            if (inline == null) return;
            System.Windows.Documents.Run run = inline as System.Windows.Documents.Run;
            if (run != null)
            {
                string txt = run.Text;
                if (!string.IsNullOrEmpty(txt) && !ContainsChinese(txt))
                {
                    string tr = TranslateString(txt);
                    if (string.IsNullOrEmpty(tr) || tr == txt) tr = TranslateMilitaryUnitName(txt);
                    if (string.IsNullOrEmpty(tr) || tr == txt) tr = TranslateCellText(txt);
                    if (!string.IsNullOrEmpty(tr) && tr != txt)
                    {
                        SetRunTextKeepBinding(run, tr);
                                    TrackLiveWpfTarget(run);
                        flowTranslatedCount++;
                        if (flowTranslatedCount <= 10) Log("WPF Run Translated: " + txt + " -> " + tr);
                    }
                }
                return;
            }
            System.Windows.Documents.Span span = inline as System.Windows.Documents.Span;
            if (span != null)
            {
                List<System.Windows.Documents.Inline> children = new List<System.Windows.Documents.Inline>();
                foreach (System.Windows.Documents.Inline sub in span.Inlines) children.Add(sub);
                foreach (System.Windows.Documents.Inline sub in children) TranslateFlowInline(sub);
            }
        }

        private static readonly HashSet<int> hookedTextChanged = new HashSet<int>();
        [ThreadStatic]
        private static bool _inControlTextChanged;

        private static bool fontsPatched = false;
        private static void PatchMainFormFonts(Form form)
        {
            if (fontsPatched || form == null || form.GetType().Name != "MainForm") return;
            try
            {
                Type t = form.GetType();
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (fi.FieldType == typeof(Font))
                    {
                        Font oldF = fi.GetValue(form) as Font;
                        if (oldF != null && oldF.FontFamily.Name != "Microsoft YaHei")
                        {
                            Font newF = new Font("Microsoft YaHei", oldF.Size, oldF.Style);
                            fi.SetValue(form, newF);
                            Log("Patched MainForm Font field " + fi.Name + " from " + oldF.FontFamily.Name + " to Microsoft YaHei");
                        }
                    }
                }
                fontsPatched = true;
            }
            catch (Exception ex)
            {
                Log("PatchMainFormFonts error: " + ex.Message);
            }
        }

        private static void HookControlTextChanged(Control ctrl)
        {
            if (ctrl == null) return;
            int h = ctrl.GetHashCode();
            if (hookedTextChanged.Contains(h)) return;
            hookedTextChanged.Add(h);

            // ComboBox / ListControl / UpDownBase: never touch .Text (it destroys
            // SelectedIndex and the game's internal key lookups).  Translate the
            // ITEM strings in place, synchronously, the moment the game refills
            // them or opens the drop-down.
            ComboBox cb = ctrl as ComboBox;
            if (cb != null)
            {
                EventHandler refill = (s, e) =>
                {
                    try { LocalizeComboBoxSafe(cb); }
                    catch { }
                };
                cb.TextChanged += refill;
                cb.SelectedIndexChanged += refill;
                cb.DropDown += refill;
                cb.HandleCreated += refill;
                return;
            }
            ListBox lb = ctrl as ListBox;
            if (lb != null)
            {
                EventHandler refillList = (s, e) =>
                {
                    try { LocalizeListBoxSafe(lb); }
                    catch { }
                };
                lb.TextChanged += refillList;
                lb.SelectedIndexChanged += refillList;
                lb.HandleCreated += refillList;
                return;
            }
            if (ctrl is ListControl || ctrl is UpDownBase) return;

            // TextBoxBase: read-only status/log surfaces are re-translated in the
            // same call stack as the game's own refresh, which is what removes the
            // English<->Chinese sawtooth.  Editable boxes keep their user input.
            TextBoxBase tbb = ctrl as TextBoxBase;
            if (tbb != null)
            {
                tbb.TextChanged += (s, e) =>
                {
                    if (_inControlTextChanged) return;
                    TextBoxBase box = s as TextBoxBase;
                    if (box == null || box.IsDisposed || box.Focused) return;
                    TextBox tb2 = box as TextBox;
                    bool readOnlyBox = tb2 != null && tb2.ReadOnly;
                    if (!readOnlyBox && box.GetType().Name.IndexOf("RichText", StringComparison.OrdinalIgnoreCase) < 0) return;
                    // 同一个源文本被反复回写 = 与游戏拉锯（用户实测：输入一个字符后闪退），
                    // 计数超限即放弃该控件，不再参与翻译。
                    string curText = "";
                    try { curText = box.Text; } catch { }
                    if (!AllowWrite(box.GetHashCode(), curText, "TextBoxBase '" + box.Name + "'")) return;
                    if (!EnterMutation("TextBoxBaseTextChanged")) return;
                    try
                    {
                        _inControlTextChanged = true;
                        TranslateControlText(box);
                    }
                    catch { }
                    finally { _inControlTextChanged = false; ExitMutation(); }
                };
                return;
            }

            ctrl.TextChanged += (s, e) =>
            {
                if (_inControlTextChanged) return;
                Control c = s as Control;
                if (c == null || c.IsDisposed) return;
                string curText2 = "";
                try { curText2 = c.Text; } catch { }
                if (!AllowWrite(c.GetHashCode(), curText2, "Control '" + c.Name + "'")) return;
                if (!EnterMutation("ControlTextChanged")) return;
                try
                {
                    _inControlTextChanged = true;
                    TranslateControlText(c);
                }
                catch { }
                finally { _inControlTextChanged = false; ExitMutation(); }
            };
        }

        /// <summary>
        /// Item-level localization for combo boxes.  Only the item strings change:
        /// SelectedIndex, DataSource/ValueMember bindings and the game's
        /// `switch (cb.Text)` lookups keep working because the engine reads the
        /// same index as before.
        /// </summary>
        private static void LocalizeComboBoxSafe(ComboBox cb)
        {
            if (cb == null || cb.IsDisposed) return;
            if (IsProtectedDBViewerCombo(cb)) return;
            // 输入期（正被编辑/自动完成中）与下拉展开中绝不改条目：
            // 在用户按键的同栈里改 Items 会让 WinForms 重入自动完成与筛选逻辑，
            // 用户实测就是「搜索框里输入一个字符即闪退」。
            bool focused = false;
            bool dropped = false;
            try { focused = cb.Focused; } catch { }
            try { dropped = cb.DroppedDown; } catch { }
            if (focused || dropped) return;
            if (!EnterMutation("LocalizeComboBoxSafe")) return;
            try
            {
                if (cb.DataSource != null)
                {
                    TranslateComboDisplayColumn(cb);
                }
                else
                {
                    TranslateComboItems(cb);
                }
                TranslateKnownEnumCombo(cb);
            }
            catch { }
        }

        /// <summary>
        /// Enum pickers whose values are also engine colour tokens ("Blue" is both
        /// an alert level and a side colour), so they cannot live in the global
        /// dictionary.  Matched by control name instead.
        /// </summary>
        private static void TranslateKnownEnumCombo(ComboBox cb)
        {
            string n = cb.Name ?? "";
            if (n.IndexOf("AlertLevel", StringComparison.OrdinalIgnoreCase) < 0) return;
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string raw = cb.GetItemText(cb.Items[i]).Trim();
                string tr = null;
                if (raw == "Blue") tr = "蓝色警戒";
                else if (raw == "Yellow") tr = "黄色警戒";
                else if (raw == "Orange") tr = "橙色警戒";
                else if (raw == "Red") tr = "红色警戒";
                else if (raw == "Green") tr = "绿色警戒";
                else if (raw == "White") tr = "白色警戒";
                if (tr == null) continue;
                try { cb.Items[i] = tr; } catch { }
            }
        }

        private static void LocalizeListBoxSafe(ListBox lb)
        {
            if (lb == null || lb.IsDisposed) return;
            try { if (lb.Focused) return; } catch { }
            if (!EnterMutation("LocalizeListBoxSafe")) return;
            try
            {
                for (int i = 0; i < lb.Items.Count; i++)
                {
                    string raw = lb.GetItemText(lb.Items[i]);
                    if (string.IsNullOrEmpty(raw) || ContainsChinese(raw)) continue;
                    string tr = TranslateComboItemText(raw);
                    if (string.IsNullOrEmpty(tr) || tr == raw) continue;
                    try { lb.Items[i] = tr; } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Self-drawn dark text boxes keep their placeholder in WatermarkText, not in
        /// Text, so the generic control-text pass never reaches it.
        /// </summary>
        // 只读定位「加载想定」窗口；仅由 SCEN_CTRL_TREE 探针使用。
        private static Form FindLoadScenarioForm()
        {
            FormCollection forms = Application.OpenForms;
            if (forms == null) return null;
            foreach (Form f in forms)
            {
                if (f == null || f.IsDisposed) continue;
                if (f.GetType().Name.IndexOf("LoadScenario", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.Name.IndexOf("LoadScenario", StringComparison.OrdinalIgnoreCase) >= 0) return f;
            }
            return null;
        }

        /// <summary>
        /// 把一个片段的所有 size-gram 加进集合（页面翻译预过滤用）。
        /// 抽成方法还有个实际好处：方法名会进元数据，能当 DEPLOY_MARKERS 用；
        /// 局部常量名不进元数据，按 ASCII 搜二进制搜不到。
        /// </summary>
        private static void AddPageGrams(HashSet<string> grams, string text, int size)
        {
            if (grams == null || string.IsNullOrEmpty(text) || size < 1) return;
            for (int i = 0; i + size <= text.Length; i++)
            {
                grams.Add(text.Substring(i, size));
            }
        }

        /// <summary>递归找窗口里的 WebView2 控件（控件类名含 WebView2）。</summary>
        private static Control FindWebView2In(Control root)
        {
            if (root == null) return null;
            if ((root.GetType().FullName ?? "").IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0) return root;
            foreach (Control c in root.Controls)
            {
                Control found = FindWebView2In(c);
                if (found != null) return found;
            }
            return null;
        }

        private static void CollectControlTree(Control ctrl, int depth, StringBuilder sb, ref bool first)
        {
            if (ctrl == null || depth > 12) return;
            if (!first) sb.Append(",");
            first = false;
            string docInfo = "";
            try
            {
                // 单参重载 GetProperty(name) 的隐式 flags 就是 Public|Instance，此处显式写出。
                PropertyInfo piDoc = Reflect.Prop(ctrl.GetType(), "Document", BindingFlags.Public | BindingFlags.Instance);
                if (piDoc != null)
                {
                    object doc = piDoc.GetValue(ctrl, null);
                    if (doc != null)
                    {
                        PropertyInfo piBody = Reflect.Prop(doc.GetType(), "Body", BindingFlags.Public | BindingFlags.Instance);
                        object body = piBody != null ? piBody.GetValue(doc, null) : null;
                        if (body != null)
                        {
                            PropertyInfo piText = Reflect.Prop(body.GetType(), "InnerText", BindingFlags.Public | BindingFlags.Instance);
                            string t = piText != null ? piText.GetValue(body, null) as string : null;
                            docInfo = ",\"docBodyLen\":" + (t != null ? t.Length : -1);
                        }
                        else docInfo = ",\"docBodyLen\":-2";
                    }
                    else docInfo = ",\"docBodyLen\":-3";
                }
            }
            catch { }
            sb.Append("{\"d\":" + depth
                + ",\"type\":\"" + JsonEsc(ctrl.GetType().FullName) + "\""
                + ",\"name\":\"" + JsonEsc(ctrl.Name) + "\""
                + ",\"vis\":" + (ctrl.Visible ? "true" : "false")
                + ",\"bounds\":\"" + JsonEsc(ctrl.Bounds.ToString()) + "\""
                + ",\"text\":\"" + JsonEsc(ctrl.Text) + "\"" + docInfo + "}");
            foreach (Control child in ctrl.Controls)
            {
                CollectControlTree(child, depth + 1, sb, ref first);
            }
        }

        private static void TranslateWatermark(Control ctrl)
        {
            try
            {
                PropertyInfo pi = Reflect.Prop(ctrl.GetType(), "WatermarkText", BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || pi.PropertyType != typeof(string) || !pi.CanRead || !pi.CanWrite) return;
                string wm = pi.GetValue(ctrl, null) as string;
                if (string.IsNullOrEmpty(wm)) return;
                string tr;
                if (wm.StartsWith("Enter Database ID for this "))
                {
                    // The game rebuilds this placeholder from the (now Chinese) object
                    // type label, so it can be mixed-language on every re-render.
                    tr = "输入该型装备的数据库 ID...";
                }
                else
                {
                    if (ContainsChinese(wm)) return;
                    tr = TranslateString(wm);
                }
                if (!string.IsNullOrEmpty(tr) && tr != wm) pi.SetValue(ctrl, tr, null);
            }
            catch { }
        }

        private static string TranslateFuelPanelText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (!raw.Contains("kg") && !raw.Contains("fuel") && !raw.Contains("燃油") && !raw.Contains("Bingo") && !raw.Contains("Tank") && !raw.Contains("坦克")) return raw;

            string s = raw;
            // 彻底修正副油箱机翻为陆军坦克的错误
            s = s.Replace("坦克 #", "副油箱 #");
            s = Regex.Replace(s, @"\bTank\s*#", "副油箱 #", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bFuelTank\b", "燃油箱", RegexOptions.IgnoreCase);

            // 补全燃油面板中残存的英文词汇
            s = s.Replace("remaining fuel", "剩余燃油");
            s = s.Replace("fuel burn rate", "燃油消耗率");
            s = s.Replace("/ minute", "/ 分钟");
            s = s.Replace("reserve", "预留备用油量");
            s = s.Replace("total fuel", "总燃油量");
            s = s.Replace("mission fuel", "任务燃油");
            s = s.Replace("nm to base:", "海里至基地:");
            s = s.Replace("nm to tanker:", "海里至加油机:");
            s = s.Replace("nm to Bingo fuel", "海里到达返航油量(Bingo)");
            s = s.Replace("nm to Joker fuel", "海里到达警戒油量(Joker)");
            s = s.Replace("to base:", "至基地:");
            s = s.Replace("to Bingo", "到达返航油量(Bingo)");

            return s;
        }

        private static void TranslateControlTree(Control ctrl)
        {
            // 注意：本方法是「遍历器」，递归深度 = UI 树深度（正常且有限），
            // 因此不能套深度闸门（曾导致深层控件全部漏译）；闸门只放在游戏事件的
            // 回写路径上（见 HookControlTextChanged / WPF 值变更钩子）。
            if (ctrl == null) return;
            TranslateControlTreeInner(ctrl);
        }

        private static void TranslateControlTreeInner(Control ctrl)
        {
            HookControlTextChanged(ctrl);

            // Combo/item surfaces are localized at the item layer, never through
            // .Text, so they need their own pass during the scan.
            LocalizeComboBoxSafe(ctrl as ComboBox);
            ListBox lbScan = ctrl as ListBox;
            if (lbScan != null) LocalizeListBoxSafe(lbScan);

            TranslateControlText(ctrl);

            if (ctrl.Parent != null && ctrl.Parent.GetType().Name.IndexOf("FormationEditor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 解决编队编辑器按钮太窄导致文字截断为省略号 “...” 的问题
                if ((ctrl is ButtonBase || ctrl.GetType().Name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) && ctrl.Width < 70)
                {
                    ctrl.Width = 70;
                }
                // 解决 “正在排序” 标签与下拉框重叠问题
                if (ctrl.Text == "正在排序" || ctrl.Text == "Sort by:" || ctrl.Name == "c08SpHZVOS6")
                {
                    ctrl.Width = Math.Max(ctrl.Width, 65);
                }
            }

            if (ctrl is ToolStrip)
            {
                ToolStrip ts = (ToolStrip)ctrl;
                // 如果是顶部战术快捷工具栏 (DarkToolStrip2 或包含大量 ToolStripButton)
                if (ts.Name == "DarkToolStrip2" || (ts.Items.Count >= 10 && ts.Items[0] is ToolStripButton))
                {
                    LocalizeDarkToolStripButtons(ts);
                }

                foreach (ToolStripItem item in ts.Items)
                {
                    TranslateToolStripItem(item);
                }
            }
            else if (ctrl is MenuStrip)
            {
                MenuStrip ms = (MenuStrip)ctrl;
                foreach (ToolStripItem item in ms.Items)
                {
                    TranslateToolStripItem(item);
                }
            }
            else if (ctrl is StatusStrip)
            {
                StatusStrip ss = (StatusStrip)ctrl;
                foreach (ToolStripItem item in ss.Items)
                {
                    TranslateToolStripItem(item);
                }
            }
            else if (ctrl is TabControl)
            {
                TabControl tc = (TabControl)ctrl;
                foreach (TabPage tp in tc.TabPages)
                {
                    string trans = TranslateString(tp.Text);
                    if (trans != tp.Text) tp.Text = trans;
                    TranslateControlTree(tp);
                }
            }
            else if (ctrl is DataGridView)
            {
                DataGridView dgv = (DataGridView)ctrl;
                foreach (DataGridViewColumn col in dgv.Columns)
                {
                    string trans = TranslateString(col.HeaderText);
                    if (trans != col.HeaderText) col.HeaderText = trans;
                }

                // 挂载 CellFormatting 事件监听器进行无侵入单元格动态中文化 (不破坏底层数据绑定)
                int dgvHash = dgv.GetHashCode();
                if (!hookedItems.Contains(dgvHash))
                {
                    hookedItems.Add(dgvHash);
                    dgv.CellFormatting += (s, e) =>
                    {
                        try
                        {
                            string sVal = e.Value as string;
                            if (!string.IsNullOrEmpty(sVal))
                            {
                                string tr = TranslateCellText(sVal);
                                if (tr != sVal)
                                {
                                    e.Value = tr;
                                    e.FormattingApplied = true;
                                }
                            }
                        }
                        catch { }
                    };
                    dgv.Invalidate();
                }

                // 动态双语渲染数据库列表单元格中的装备名称与分类 (仅针对非编辑只读字符串单元格)
                try
                {
                    if (dgv.Rows.Count > 0 && dgv.Rows.Count <= 500)
                    {
                        foreach (DataGridViewRow row in dgv.Rows)
                        {
                            foreach (DataGridViewCell cell in row.Cells)
                            {
                                string sVal = cell.Value as string;
                                if (!string.IsNullOrEmpty(sVal) && sVal.Length > 2 && sVal.Length < 100)
                                {
                                    string tCell = TranslateString(sVal);
                                    if (tCell != sVal)
                                    {
                                        cell.ToolTipText = tCell;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            else if (ctrl is ListView)
            {
                ListView lv = (ListView)ctrl;
                foreach (ColumnHeader ch in lv.Columns)
                {
                    string trans = TranslateString(ch.Text);
                    if (trans != ch.Text) ch.Text = trans;
                }
                try
                {
                    if (lv.Items.Count > 0 && lv.Items.Count <= 500)
                    {
                        foreach (ListViewItem lvi in lv.Items)
                        {
                            string tItem = TranslateString(lvi.Text);
                            if (tItem != lvi.Text) lvi.ToolTipText = tItem;
                        }
                    }
                }
                catch { }
            }
            else if (ctrl is TreeView)
            {
                TreeView tv = (TreeView)ctrl;
                foreach (TreeNode node in tv.Nodes)
                {
                    TranslateTreeNode(node);
                }
            }
            else if (ctrl.GetType().Name == "DarkTreeView")
            {
                TranslateDarkTreeView(ctrl);
            }
            else if (ctrl.GetType().Name == "DarkListView")
            {
                TranslateDarkListView(ctrl);
            }
            else if (ctrl is ComboBox)
            {
                LocalizeDBViewerComboBox((ComboBox)ctrl);
            }
            else if (ctrl is WebBrowser)
            {
                WebBrowser wb = (WebBrowser)ctrl;
                TranslateWebBrowser(wb);
            }
            else if (ctrl.GetType().Name.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TranslateWebView2(ctrl);
            }
            else if (ctrl.GetType().Name.IndexOf("ElementHost", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    PropertyInfo piChild = ctrl.GetType().GetProperty("Child");
                    if (piChild != null)
                    {
                        object childObj = piChild.GetValue(ctrl, null);
                        if (childObj is System.Windows.DependencyObject)
                        {
                            TranslateWpfElement((System.Windows.DependencyObject)childObj);
                        }
                    }
                }
                catch { }
            }
            else if (ctrl.GetType().Name.Contains("ChromiumWebBrowser"))
            {
                TranslateChromiumWebBrowser(ctrl);
            }

            if (ctrl.ContextMenuStrip != null)
            {
                foreach (ToolStripItem item in ctrl.ContextMenuStrip.Items)
                {
                    TranslateToolStripItem(item);
                }
            }

            if (ctrl.HasChildren)
            {
                foreach (Control child in ctrl.Controls)
                {
                    TranslateControlTree(child);
                }
            }
        }


        private static readonly HashSet<int> hookedBrowsers = new HashSet<int>();
        private static void TranslateWebView2(Control ctrl)
        {
            if (ctrl == null) return;
            try
            {
                PropertyInfo propCore = ctrl.GetType().GetProperty("CoreWebView2");
                if (propCore == null) return;
                object core = propCore.GetValue(ctrl, null);
                if (core == null) return;

                MethodInfo miExec = ctrl.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                if (miExec == null) miExec = core.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                if (miExec == null) return;

                object target = (miExec.DeclaringType.IsAssignableFrom(ctrl.GetType())) ? (object)ctrl : core;

                string jsExtract = @"(function(){
                    try {
                        if (!document || !document.body || document.readyState === 'loading') return '';
                        if (!window.__cmo_nodes) window.__cmo_nodes = [];
                        var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
                        var node;
                        var items = [];
                        while ((node = walker.nextNode())) {
                            if (node.__cmo_zh) continue;
                            var p = node.parentNode ? node.parentNode.tagName : '';
                            if (p === 'SCRIPT' || p === 'STYLE') continue;
                            var val = node.nodeValue;
                            if (val && val.trim().length > 0) {
                                var idx = window.__cmo_nodes.length;
                                window.__cmo_nodes.push(node);
                                items.push(idx + '\t' + val);
                            }
                        }
                        if (items.length === 0) return '';
                        return encodeURIComponent(items.join('\n===CMO_SEP===\n'));
                    } catch(e) { return ''; }
                })()";

                System.Threading.Tasks.Task<string> task = miExec.Invoke(target, new object[] { jsExtract }) as System.Threading.Tasks.Task<string>;
                if (task == null) return;

                task.ContinueWith((t) =>
                {
                    try
                    {
                        if (t.IsFaulted || t.IsCanceled) return;
                        string rawJson = t.Result;
                        if (string.IsNullOrEmpty(rawJson) || rawJson == "\"\"" || rawJson == "null") return;

                        string encoded = rawJson.Trim('\"');
                        if (string.IsNullOrEmpty(encoded)) return;
                        string payload = Uri.UnescapeDataString(encoded);
                        if (string.IsNullOrEmpty(payload)) return;

                        string[] parts = payload.Split(new string[] { "\n===CMO_SEP===\n" }, StringSplitOptions.RemoveEmptyEntries);
                        List<string> updates = new List<string>();

                        for (int i = 0; i < parts.Length; i++)
                        {
                            string item = parts[i];
                            int tabPos = item.IndexOf('\t');
                            if (tabPos <= 0) continue;

                            string idxStr = item.Substring(0, tabPos);
                            string origText = item.Substring(tabPos + 1);
                            if (string.IsNullOrEmpty(origText.Trim())) continue;

                            int startIdx = 0;
                            while (startIdx < origText.Length && char.IsWhiteSpace(origText[startIdx])) startIdx++;
                            string leadSpace = origText.Substring(0, startIdx);

                            int endIdx = origText.Length - 1;
                            while (endIdx >= startIdx && char.IsWhiteSpace(origText[endIdx])) endIdx--;
                            string trailSpace = origText.Substring(endIdx + 1);

                            string coreText = origText.Substring(startIdx, endIdx - startIdx + 1);
                            string translated = TranslateBriefingText(coreText);

                            if (!string.IsNullOrEmpty(translated) && translated != coreText)
                            {
                                string finalText = leadSpace + translated + trailSpace;
                                updates.Add(idxStr + "\t" + finalText);
                            }
                        }

                        if (updates.Count > 0)
                        {
                            string updatePayload = Uri.EscapeDataString(string.Join("\n===CMO_SEP===\n", updates.ToArray())).Replace("'", "%27");
                            string jsApply = @"(function(){
                                try {
                                    if (!window.__cmo_nodes) return;
                                    var raw = decodeURIComponent('" + updatePayload + @"');
                                    var parts = raw.split('\n===CMO_SEP===\n');
                                    for (var i = 0; i < parts.length; i++) {
                                        var p = parts[i];
                                        var tab = p.indexOf('\t');
                                        if (tab > 0) {
                                            var idx = parseInt(p.substring(0, tab), 10);
                                            var txt = p.substring(tab + 1);
                                            var n = window.__cmo_nodes[idx];
                                            if (n) {
                                                n.nodeValue = txt;
                                                n.__cmo_zh = true;
                                            }
                                        }
                                    }
                                } catch(e) {}
                            })()";

                            if (!ctrl.IsDisposed && ctrl.IsHandleCreated)
                            {
                                ctrl.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        miExec.Invoke(target, new object[] { jsApply });
                                    }
                                    catch { }
                                }));
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>数据库描述段落标记的中文标签，与 TranslateDatabaseDescriptionText 保持一致。</summary>
        private static string TranslateDatabaseMarker(string marker)
        {
            switch (marker.ToUpperInvariant())
            {
                case "OVERVIEW": return "概况说明：";
                case "DETAIL":
                case "DETAILS": return "技术细节：";
                case "SPECIFICATION":
                case "SPECIFICATIONS": return "战术技术指标：";
                case "NOTE":
                case "NOTES": return "注意事项：";
                case "SOURCE":
                case "SOURCES": return "参考资料来源：";
                default: return marker + "：";
            }
        }

        private static string TranslateDatabaseDescriptionText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                // 1. Operators 列表国家名全量替换
                if (text.StartsWith("Operators:") || text.StartsWith("使用方") || text.StartsWith("列装国家"))
                {
                    string prefix = "列装国家与地区:";
                    int colonIdx = text.IndexOf(':');
                    string body = colonIdx >= 0 ? text.Substring(colonIdx + 1) : text;
                    string[] countries = body.Split(',');
                    for (int i = 0; i < countries.Length; i++)
                    {
                        string cTrim = countries[i].Trim();
                        if (exactMap.ContainsKey(cTrim)) countries[i] = " " + exactMap[cTrim];
                        else if (normExactMap.ContainsKey(cTrim)) countries[i] = " " + normExactMap[cTrim];
                        else countries[i] = " " + cTrim;
                    }
                    return prefix + string.Join(",", countries);
                }

                // 2. 字段前缀替换
                text = text.Replace("OVERVIEW:", "概况说明：")
                           .Replace("DETAILS:", "技术细节：")
                           .Replace("SPECIFICATIONS:", "战术技术指标：")
                           .Replace("NOTES:", "注意事项：")
                           .Replace("SOURCES:", "参考资料来源：")
                           .Replace("Fact:", "技术评述：")
                           .Replace("Diameter:", "弹径/直径:")
                           .Replace("Platforms:", "搭载平台:")
                           .Replace("Range:", "有效射程:")
                           .Replace("Maneuverability:", "机动过载能力:")
                           .Replace("IOC:", "服役起始年份 (IOC):")
                           .Replace("Wingspan:", "翼展:")
                           .Replace("Guidance:", "制导方式:")
                           .Replace("Warhead:", "战斗部:")
                           .Replace("Length:", "长度:")
                           .Replace("Weight:", "发射全重/重量:")
                           .Replace("Speed:", "最高航速/飞行速度:");

                // 3. 常见参数单位与描述中英双语优化
                if (text.Contains("beyond-visual-range") || text.Contains("fire-and-forget") || text.Contains("active radar-guided"))
                {
                    text = text.Replace("beyond-visual-range", "超视距 (BVR)")
                               .Replace("fire-and-forget", "发射后不管 (Fire-and-Forget)")
                               .Replace("active radar-guided air-to-air missile", "主动雷达制导空空导弹")
                               .Replace("air-to-air missile", "空空导弹")
                               .Replace("air-to-surface missile", "空地/空舰导弹")
                               .Replace("surface-to-air missile", "地空导弹")
                               .Replace("anti-ship missile", "反舰导弹");
                }
            }
            catch { }
            return text;
        }

        private static string TranslateBriefingText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 0. 词库整句/整行优先。数据库描述正文（DB/Descriptions 的每一行）
            //    以纯查表命中，命中即直接采用，避免被下方的启发式规则拆解。
            string trimmedTop = text.Trim();
            if (exactMap.ContainsKey(trimmedTop)) return exactMap[trimmedTop];
            string normTop = Regex.Replace(trimmedTop, @"\s+", " ");
            if (normExactMap.ContainsKey(normTop)) return normExactMap[normTop];

            // 0.1 带段落标记的整段（"OVERVIEW: <正文>"）：标记由引擎翻译，正文查词条
            Match mkTop = Regex.Match(trimmedTop, @"^(OVERVIEW|DETAIL|DETAILS|SPECIFICATIONS?|NOTES?|SOURCES?)\s*:\s*", RegexOptions.IgnoreCase);
            if (mkTop.Success)
            {
                string restTop = trimmedTop.Substring(mkTop.Length).Trim();
                if (restTop.Length > 0)
                {
                    string trRestTop = null;
                    if (exactMap.ContainsKey(restTop)) trRestTop = exactMap[restTop];
                    else
                    {
                        string normRest = Regex.Replace(restTop, @"\s+", " ");
                        if (normExactMap.ContainsKey(normRest)) trRestTop = normExactMap[normRest];
                    }
                    if (!string.IsNullOrEmpty(trRestTop))
                    {
                        return TranslateDatabaseMarker(mkTop.Groups[1].Value) + " " + trRestTop;
                    }
                }
            }

            // 0.2 数据库百科段落与关键指标标签翻译 (OVERVIEW, DETAILS, SPECIFICATIONS, Operators 等)
            if (text.StartsWith("OVERVIEW:") || text.StartsWith("DETAILS:") || text.StartsWith("SPECIFICATIONS:") ||
                text.StartsWith("NOTES:") || text.StartsWith("SOURCES:") || text.StartsWith("Fact:") ||
                text.StartsWith("Diameter:") || text.StartsWith("Platforms:") || text.StartsWith("Range:") ||
                text.StartsWith("Maneuverability:") || text.StartsWith("IOC:") || text.StartsWith("Operators:") ||
                text.StartsWith("Wingspan:") || text.StartsWith("Guidance:") || text.StartsWith("Warhead:") ||
                text.StartsWith("Length:") || text.StartsWith("Weight:") || text.StartsWith("Speed:"))
            {
                return TranslateDatabaseDescriptionText(text);
            }

            // 1. 数据库武器射程与高度列 (e.g. "200 ft AGL - 65000 ft ASL")
            if (text.Contains(" ft AGL") || text.Contains(" ft ASL") || text.Contains(" m AGL") || text.Contains(" m ASL"))
            {
                text = text.Replace(" ft AGL", " 英尺(真高AGL)")
                           .Replace(" ft ASL", " 英尺(海拔ASL)")
                           .Replace(" m AGL", " 米(真高AGL)")
                           .Replace(" m ASL", " 米(海拔ASL)");
            }

            // 数据库动力装置油门档位与高度层 (e.g. "Band 1, Loiter Speed", "145.31 kg per minute")
            if (text.Contains("Band ") || text.Contains("kg per minute"))
            {
                text = text.Replace("Band 1", "高度层 1")
                           .Replace("Band 2", "高度层 2")
                           .Replace("Band 3", "高度层 3")
                           .Replace("Band 4", "高度层 4")
                           .Replace("Loiter Speed", "盘旋待机速度")
                           .Replace("Cruise Speed", "巡航速度")
                           .Replace("Military Speed", "军用推力速度")
                           .Replace("Afterburner Speed", "加力推力速度")
                           .Replace("kg per minute", "公斤/分钟");
            }

            // 数据库战机预设挂载方案飞行剖面 (e.g. "3150 nm Strike radius, Hi-Hi-Hi profile...")
            if (text.Contains("Strike radius") || text.Contains("Recon radius") || text.Contains("Ferry Range") || text.Contains("profile.") || text.Contains("reserves.") || text.Contains("reserves"))
            {
                return TranslateFlightProfile(text);
            }

            // 2. 数据库外挂武器包线详情汇总列 (e.g. "Guided Weapon. Targets: ... 最高航速: ... Warhead: ... Can be guided by: ...")
            if (text.Contains("Targets:") || text.Contains("打击目标:") || text.Contains("Warhead:") || text.Contains("战斗部:") || text.StartsWith("Guided Weapon") || text.StartsWith("Unguided Weapon"))
            {
                return TranslateWeaponSummary(text);
            }

            // 3. 优先对完整段落进行精确或归一化匹配（防止含逗号的完整句子被逗号枚举拆分破坏）
            if (!ContainsChinese(text))
            {
                string trans = TranslateString(text);
                if (trans != text) return trans;

                string normFull = Regex.Replace(text.Trim(), @"\s+", " ");
                if (normExactMap.ContainsKey(normFull)) return normExactMap[normFull];
            }

            // 4. 复合枚举串 (如 "Late 1980s Technology, 水面搜索, Ground Search (Fixed)")，仅针对不含句号的短枚举列表
            if (!text.Contains(".") && text.Length < 160 && text.Contains(", ") && Regex.IsMatch(text, @"[A-Za-z]{3,}"))
            {
                string trComp = TranslateCommaComposite(text);
                if (trComp != text) return trComp;
            }

            if (ContainsChinese(text)) return text;

            string norm = Regex.Replace(text.Trim(), @"\s+", " ");

            // Database Viewer dynamic numeric patterns (e.g. "15 seconds Observe...", "30 seconds (Cadet)")
            if (norm.EndsWith("seconds Observe, Orient, Decide, and Act (reaction time)"))
            {
                return norm.Replace("seconds Observe, Orient, Decide, and Act (reaction time)", "秒 (观察-判断-决策-行动 OODA 反应时间)");
            }
            if (norm.EndsWith("seconds (Novice Proficiency Level)"))
            {
                return norm.Replace("seconds (Novice Proficiency Level)", "秒 (新手熟练度)");
            }
            if (norm.EndsWith("seconds (Cadet)"))
            {
                return norm.Replace("seconds (Cadet)", "秒 (学员熟练度)");
            }
            if (norm.EndsWith("seconds (Regular)"))
            {
                return norm.Replace("seconds (Regular)", "秒 (普通熟练度)");
            }
            if (norm.EndsWith("seconds (Veteran)"))
            {
                return norm.Replace("seconds (Veteran)", "秒 (老手熟练度)");
            }
            if (norm.EndsWith("seconds (Ace)"))
            {
                return norm.Replace("seconds (Ace)", "秒 (王牌熟练度)");
            }
            if (Regex.IsMatch(norm, @"^\d+(\.\d+)?\s+seconds$"))
            {
                return norm.Replace("seconds", "秒");
            }
            if (norm.StartsWith("Performance Details for "))
            {
                return "战术性能详情：" + norm.Substring("Performance Details for ".Length);
            }
            if (norm.StartsWith("Operating bands (search & track): "))
            {
                return "工作频段 (搜索与跟踪): " + norm.Substring("Operating bands (search & track): ".Length);
            }
            if (norm.StartsWith("Operating bands (FC / illumination): "))
            {
                return "工作频段 (火控与照射): " + norm.Substring("Operating bands (FC / illumination): ".Length);
            }
            if (norm.StartsWith("Operating bands: "))
            {
                return "工作频段: " + norm.Substring("Operating bands: ".Length);
            }
            if (norm.StartsWith("Max Target Illumination: "))
            {
                return "最大目标照射通道数: " + norm.Substring("Max Target Illumination: ".Length);
            }
            if (norm.StartsWith("Visual Zoom Detection/Classification: "))
            {
                return "可见光变焦探测/识别: " + norm.Substring("Visual Zoom Detection/Classification: ".Length);
            }
            if (norm.StartsWith("InfraRed Zoom Detection/Classification: "))
            {
                return "红外变焦探测/识别: " + norm.Substring("InfraRed Zoom Detection/Classification: ".Length);
            }

            // 逗号分隔的复合枚举项 (例如: "Late 1980s Technology, Ground Search (Fixed), Ground Search (Mobile)")
            if (!text.Contains(".") && text.Length < 160 && text.Contains(", "))
            {
                string trComp = TranslateCommaComposite(text);
                if (trComp != text) return trComp;
            }

            // Multi-line or multi-sentence breakdown
            if (text.Contains(". ") || text.Contains("?\n") || text.Contains("!\n") || text.Contains("\n"))
            {
                string[] sentences = Regex.Split(text, @"(?<=[.?!])\s+|\r?\n");
                if (sentences.Length > 1)
                {
                    StringBuilder sb = new StringBuilder();
                    bool anyTrans = false;
                    for (int s = 0; s < sentences.Length; s++)
                    {
                        string sent = sentences[s].Trim();
                        if (string.IsNullOrEmpty(sent)) continue;
                        string st = TranslateString(sent);
                        if (st == sent)
                        {
                            string normSent = Regex.Replace(sent, @"\s+", " ");
                            if (normExactMap.ContainsKey(normSent)) st = normExactMap[normSent];
                        }
                        if (st != sent) anyTrans = true;
                        if (sb.Length > 0) sb.Append(" ");
                        sb.Append(st);
                    }
                    if (anyTrans) return sb.ToString();
                }
            }

            return text;
        }

        private static void TranslateChromiumWebBrowser(Control ctrl)
        {
            if (ctrl == null) return;
            try
            {
                // 反射调用 ExecuteScriptAsync 进行简报 HTML 中文化
                // ChromiumWebBrowser 包含 ExecuteScriptAsync 方法
                MethodInfo execMethod = ctrl.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                if (execMethod != null)
                {
                    // 仅当包含英文且尚未标记为已翻译时注入翻译脚本
                    string js = @"(function() {
                        if (window.__cmo_translated) return;
                        if (!document.body) return;
                        window.__cmo_translated = true;
                        // 替换常见的简报关键词
                        var html = document.body.innerHTML;
                        var dict = {
                            'SITUATION': '战场态势 (SITUATION)',
                            'ENEMY FORCES': '敌方力量 (ENEMY FORCES)',
                            'FRIENDLY FORCES': '友军力量 (FRIENDLY FORCES)',
                            'MISSION': '任务简报 (MISSION)',
                            'EXECUTION': '作战执行 (EXECUTION)',
                            'COMMAND AND SIGNAL': '指挥与通信 (COMMAND & SIGNAL)',
                            'ORDER OF BATTLE': '战斗序列 (ORDER OF BATTLE)',
                            'RULES OF ENGAGEMENT': '交战规则 (ROE)',
                            'TASKS': '主要任务 (TASKS)'
                        };
                        for (var k in dict) {
                            var reg = new RegExp(k, 'g');
                            html = html.replace(reg, dict[k]);
                        }
                        document.body.innerHTML = html;
                    })();";
                    execMethod.Invoke(ctrl, new object[] { js });
                }
            }
            catch { }
        }

        private static void TranslateWebBrowser(WebBrowser wb)
        {
            if (wb == null) return;
            int hash = wb.GetHashCode();
            if (!hookedBrowsers.Contains(hash))
            {
                hookedBrowsers.Add(hash);
                wb.DocumentCompleted += (s, e) =>
                {
                    try
                    {
                        if (wb.Document != null && wb.Document.Body != null)
                        {
                            // 针对实时生成的 HTML (如数据库详情/简报)，动态替换英文字段
                            string html = wb.Document.Body.InnerHtml;
                            if (!string.IsNullOrEmpty(html) && !html.Contains("<!-- CMO_ZH_INJECTED -->"))
                            {
                                // 替换常见数据库英文字段
                                string translated = html
                                    .Replace("Aircraft size:", "机体尺寸:")
                                    .Replace("Average Climb Rate:", "平均爬升率:")
                                    .Replace("Service Ceiling:", "实用升限:")
                                    .Replace("Combat Radius:", "作战半径:")
                                    .Replace("Fuel Capacity:", "载油量:")
                                    .Replace("Damage Points:", "结构抗毁点数 (DP):")
                                    .Replace("Category:", "装备大类:")
                                    .Replace("Length:", "全长 / 舰长:")
                                    .Replace("Width:", "宽度:")
                                    .Replace("Height:", "高度:")
                                    .Replace("Weight:", "空重:")
                                    .Replace("Payload:", "有效载荷:")
                                    .Replace("Sensors:", "传感器:")
                                    .Replace("Weapons:", "武器挂载:")
                                    .Replace("Missions:", "分配任务:");

                                // 双语装备名称悬浮与替换支持
                                //
                                // 预过滤（O-01）：原来对 46,089 条词库逐条做整页 `Contains`，
                                // 是 O(词库 × 页面)。实测（同一台机、同一份词库）：
                                //   32KB 页面 677ms、100KB 页面 2,084ms —— 而这里是 UI 线程上的
                                //   DocumentCompleted 回调，等于每次导航都卡一下。
                                // 做法：先建页面的 12-gram 集合，只有某个键的前 12 字符确实在页面里
                                // 才做 Contains。纯剪枝：页面上存在的键必然命中其前 12 字符，不会漏译；
                                // 实测输出与逐字节一致，32KB 页面 677ms→135ms（≈5×）。
                                // 少于 12 字符的键无法剪枝，仍走原路径。
                                const int pageGramSize = 12;
                                HashSet<string> pageGrams = null;
                                if (translated.Length >= pageGramSize)
                                {
                                    pageGrams = new HashSet<string>(StringComparer.Ordinal);
                                    AddPageGrams(pageGrams, translated, pageGramSize);
                                }
                                foreach (var kvp in exactMap)
                                {
                                    if (kvp.Key.Length < 5) continue;
                                    if (pageGrams != null && kvp.Key.Length >= pageGramSize
                                        && !pageGrams.Contains(kvp.Key.Substring(0, pageGramSize))) continue;
                                    if (translated.Contains(kvp.Key))
                                    {
                                        string ins = kvp.Key + " 【" + kvp.Value + "】";
                                        translated = translated.Replace(kvp.Key, ins);
                                        // 插入片段可能让后续更短或其他键命中（级联语义）：
                                        // gram 集合只增不减，因此仍是保守剪枝、不漏译。
                                        if (pageGrams != null && ins.Length >= pageGramSize)
                                        {
                                            AddPageGrams(pageGrams, ins, pageGramSize);
                                        }
                                    }
                                }
                                translated += "<!-- CMO_ZH_INJECTED -->";
                                wb.Document.Body.InnerHtml = translated;
                            }
                        }
                    }
                    catch { }
                };
            }
        }

        private static string TranslateMilitaryUnitName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string s = raw;

            // 1. 如果有数量前缀如 "2x ", "6x "
            string prefix = "";
            Match mQty = Regex.Match(s, @"^(\d+x\s+)(.*)$");
            if (mQty.Success)
            {
                prefix = mQty.Groups[1].Value;
                s = mQty.Groups[2].Value;
            }

            // 2. 检查是否有整词精确翻译
            if (exactMap.ContainsKey(s))
            {
                return prefix + exactMap[s];
            }

            // 3. 通用飞机/直升机/装备类型词与中国公认军用绰号规范注入
            s = s.Replace("Tanker", "加油机")
                 .Replace("Fighter", "战斗机")
                 .Replace("Bomber", "轰炸机")
                 .Replace("Multirole (Fighter/Attack)", "多用途战机 (战斗/攻击)")
                 .Replace("Multirole", "多用途战机")
                 .Replace("Falcon", "「战隼」")
                 .Replace("Sufa", "苏法/风暴")
                 .Replace("Eagle", "「鹰」")
                 .Replace("Strike Eagle", "「攻击鹰」")
                 .Replace("Tomcat", "「雄猫」")
                 .Replace("Raptor", "「猛禽」")
                 .Replace("Lightning II", "「闪电」II")
                 .Replace("Thunderbolt II", "「雷电」II")
                 .Replace("Hornet", "「大黄蜂」")
                 .Replace("Super Hornet", "「超级大黄蜂」")
                 .Replace("Growler", "「咆哮者」")
                 .Replace("Prowler", "「徘徊者」")
                 .Replace("Hawkeye", "「鹰眼」")
                 .Replace("Sentry", "「哨兵」")
                 .Replace("Black Hawk", "「黑鹰」")
                 .Replace("Blackhawk", "「黑鹰」")
                 .Replace("Apache", "「阿帕奇」")
                 .Replace("Chinook", "「支努干」")
                 .Replace("Sea King", "「海王」")
                 .Replace("Seahawk", "「海鹰」")
                 .Replace("Guardian", "「卫士」")
                 .Replace("Longbow", "「长弓」")
                 .Replace("Osprey", "「鱼鹰」")
                 .Replace("Hercules", "「大力神」")
                 .Replace("Galaxy", "「银河」")
                 .Replace("Globemaster", "「环球霸王」")
                 .Replace("Poseidon", "「海神」")
                 .Replace("Orion", "「猎户座」")
                 .Replace("Nahshon-Eitam", "纳雄-埃塔姆")
                 .Replace("Nahshon", "纳雄")
                 .Replace("Saknayee", "萨克奈")
                 .Replace("Kohav", "科哈夫")
                 .Replace("Har Ramon", "哈尔拉蒙")
                 .Replace("Ramat David", "拉马特戴维")
                 .Replace("Wing Loong", "「翼龙」")
                 .Replace("Rainbow", "「彩虹」")
                 .Replace("Mighty Dragon", "「威龙」")
                 .Replace("Flounder", "「飞豹」")
                 .Replace("Flanker", "「侧卫」")
                 .Replace("Fullback", "「鸭嘴兽」")
                 .Replace("Fencer", "「击剑手」")
                 .Replace("Frogfoot", "「蛙足」")
                 .Replace("Fishbed", "「鱼窝」")
                 .Replace("Flogger", "「鞭挞者」")
                 .Replace("Foxbat", "「狐蝠」")
                 .Replace("Foxhound", "「捕狐犬」")
                 .Replace("Fulcrum", "「支点」")
                 .Replace("Backfire", "「逆火」")
                 .Replace("Badger", "「獾」")
                 .Replace("Bear", "「熊」")
                 .Replace("Blackjack", "「海盗旗」")
                 .Replace("Alligator", "「短吻鳄」")
                 .Replace("Helix", "「蜗牛」")
                 .Replace("Hind", "「雌鹿」")
                 .Replace("Havoc", "「浩劫」")
                 .Replace("Tyulpan", "「郁金香」")
                 .Replace("Akatsiya", "「金合欢」")
                 .Replace("Gvozdika", "「康乃馨」")
                 .Replace("Giatsint", "「风信子」")
                 .Replace("Pion", "「芍药」")
                 .Replace("Msta", "「姆斯塔河」");

            return prefix + s;
        }

        private static void TranslateTreeNode(TreeNode node)
        {
            if (node == null) return;
            string trans = TranslateString(node.Text);
            if (string.IsNullOrEmpty(trans) || trans == node.Text)
            {
                trans = TranslateMilitaryUnitName(node.Text);
            }
            if (!string.IsNullOrEmpty(trans) && trans != node.Text) node.Text = trans;
            foreach (TreeNode child in node.Nodes)
            {
                TranslateTreeNode(child);
            }
        }

        private static PropertyInfo _dtvNodesProp;
        private static PropertyInfo _dtnTextProp;
        private static PropertyInfo _dtnNodesProp;

        private static string TranslateDBComboItem(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;
            string t = rawText.Trim();

            // 通用状态与过滤
            if (t == "None") return "无 / 不限";
            if (t == "Not selected") return "未选择 (全部子类型)";
            if (t == "Active") return "已激活 (Active)";
            if (t == "Inactive") return "未激活 (Inactive)";
            if (t == "Waiting for trigger") return "等待触发 (Waiting for trigger)";
            if (t.StartsWith("Show all platforms")) return "显示全部平台 (含现实与假想)";
            if (t.StartsWith("Show only real-life")) return "仅显示现实服役平台";
            if (t.StartsWith("Show only hypothetical")) return "仅显示假想/计划平台";

            // 数据库查看器对象大类
            if (t == "Weapon") return "武器装备 (Weapon)";
            if (t == "Aircraft") return "飞机 / 航空器 (Aircraft)";
            if (t == "Ship") return "水面舰艇 (Ship)";
            if (t == "Submarine") return "水下潜艇 (Submarine)";
            if (t == "Facility") return "地面设施 (Facility)";
            if (t == "Satellite") return "人造卫星 (Satellite)";
            if (t == "Ground Unit") return "地面作战单位 (Ground Unit)";
            if (t == "Sensor") return "传感器 / 探测系统 (Sensor)";

            // 数据库查看器传感器子类型 (声呐、雷达、电子战、激光探测等)
            if (t == "BottomFixedSonar_PassiveOnly") return "海底固定声呐 [仅被动]";
            if (t == "Bow_Sonar_ActivePassive") return "舰艏声呐 [主被动]";
            if (t == "DippingSonar_ActiveOnly") return "吊放声呐 [仅主动]";
            if (t == "DippingSonar_ActivePassive") return "吊放声呐 [主被动]";
            if (t == "DippingSonar_PassiveOnly") return "吊放声呐 [仅被动]";
            if (t == "EMP_Projector") return "电磁脉冲发射器 (EMP)";
            if (t == "HullSonar_ActiveOnly") return "舰壳声呐 [仅主动]";
            if (t == "HullSonar_ActivePassive") return "舰壳声呐 [主被动]";
            if (t == "HullSonar_PassiveOnly") return "舰壳声呐 [仅被动]";
            if (t == "LaserDesignator") return "激光指示器 (Laser Designator)";
            if (t == "LaserRangefinder") return "激光测距仪 (Laser Rangefinder)";
            if (t == "LaserSpotTracker") return "激光光斑跟踪器 (LST)";
            if (t == "LIDAR") return "激光雷达 (LIDAR)";
            if (t == "Microwave_Emitter") return "微波辐射源 / 大功率微波";
            if (t == "MineNeutralization_DiverExpl") return "潜水员人工灭雷炸药";
            if (t == "MineNeutralization_Explosive") return "灭雷炸药 / 灭雷具";
            if (t == "MineNeutralization_Moored") return "定深锚雷灭雷具";
            if (t == "MineSweep_AcousticInfluenc") return "音响扫雷具";
            if (t == "MineSweep_MagneticInfluen") return "磁性扫雷具";
            if (t == "MineSweep_MechanicalCable") return "机械截割扫雷具";
            if (t == "MineSweep_MultiInfluence") return "复合物理场扫雷具";
            if (t == "MineSweep_TwoShipMagneti") return "双舰对拖磁性扫雷具";
            if (t == "NonDetectingEmitter") return "非探测辐射源 / 无源诱饵";
            if (t == "PCLS") return "潜艇通信激光系统 (PCLS)";

            // 新建任务分类 (Category)
            if (t == "Mission") return "常规任务 (Mission)";
            if (t == "Task Pool") return "兵力池 (Task Pool)";
            if (t == "Package") return "任务编组 (Package)";

            // 新建任务级别/型号 (Class)
            if (t.StartsWith("Strike")) return "突击打击 [含空空拦截] (Strike)";
            if (t == "Patrol") return "巡逻 (Patrol)";
            if (t == "Support") return "支援 (Support)";
            if (t == "Ferry") return "转场 (Ferry)";
            if (t == "Mining") return "布雷 (Mining)";
            if (t == "Mine-clearing") return "扫雷 (Mine-clearing)";
            if (t == "Cargo") return "运输 (Cargo)";

            // 任务类型 (Type - AAW/ASuW/ASW/SEAD 规范缩写保留)
            if (t == "AAW Patrol") return "防空巡逻 (AAW)";
            if (t == "ASuW Patrol (Naval)") return "对海巡逻 [水面] (ASuW)";
            if (t == "ASuW Patrol (Ground)") return "对陆巡逻 [地面] (ASuW)";
            if (t == "ASuW Patrol (Mixed)") return "海陆复合巡逻 (ASuW)";
            if (t == "ASW Patrol") return "反潜巡逻 (ASW)";
            if (t == "SEAD Patrol") return "防空压制巡逻 (SEAD)";
            if (t == "Sea Control Patrol") return "制海巡逻 (Sea Control)";
            if (t == "Naval ASuW Strike") return "对海突击打击 (ASuW Strike)";
            if (t == "Land Strike") return "对陆突击打击 (Land Strike)";
            if (t == "Air Intercept") return "空空拦截 (Air Intercept)";

            // 任务编辑器参数
            if (t == "Cruise") return "巡航 (Cruise)";
            if (t == "Loiter") return "待机/滞空 (Loiter)";
            if (t == "Full") return "全速 (Full)";
            if (t == "Flank") return "最大极速 (Flank)";
            if (t == "Unspecified") return "未指定 (Unspecified)";
            if (t == "By Class") return "按级别/型号 (By Class)";
            if (t == "Random within area") return "区域内随机巡逻";
            if (t == "Repeatable loop") return "循环巡逻";
            if (t.Contains("Tankers Refuelling Tankers"))
            {
                if (t.StartsWith("Inherited")) return "继承自上级，总是使用 [加油机相互加油除外]";
                return "总是使用 [加油机相互加油除外]";
            }
            if (t == "1x flight") return "单批编队 (1x flight)";
            if (t == "All flights") return "全部出动 (All flights)";

            // 编队编辑器队形 (Formation)
            if (t == "Line") return "一字线形编队 (Line)";
            if (t == "Column") return "单纵队形 (Column)";
            if (t == "Wedge") return "楔形编队 (Wedge)";
            if (t == "Vee") return "V字编队 (Vee)";
            if (t == "Echelon") return "梯形编队 (Echelon)";
            if (t == "Diamond") return "菱形编队 (Diamond)";
            if (t == "Circle") return "环形编队 (Circle)";

            return TranslateString(rawText);
        }

        private static readonly HashSet<int> hookedDBCombos = new HashSet<int>();

        /// <summary>
        /// True for combo boxes hosted by the Database Viewer, New Mission,
        /// Mission Editor, or Formation Editor.
        /// </summary>
        private static bool IsInTargetLocalizableForm(Control ctrl, out string formName)
        {
            formName = "";
            Control c = ctrl;
            int guard = 0;
            while (c != null && guard++ < 32)
            {
                Form f = c as Form;
                if (f != null)
                {
                    string fn = f.GetType().Name;
                    formName = fn;
                    // Every game window is localizable now; only windows whose
                    // combo items carry engine keys the game re-parses by text
                    // are excluded.
                    return fn.IndexOf("LuaConsole", StringComparison.OrdinalIgnoreCase) < 0
                        && fn.IndexOf("ScriptEditor", StringComparison.OrdinalIgnoreCase) < 0
                        && fn.IndexOf("ExpressionBuilder", StringComparison.OrdinalIgnoreCase) < 0;
                }
                c = c.Parent;
            }
            return false;
        }

        /// <summary>
        /// The database viewer dispatches on the text of its object-type picker
        /// (`switch (CB_ObjectType.Text)`).  Replacing the item strings with the
        /// Chinese labels makes every branch miss, so no detail HTML is ever
        /// generated and the WebView2 pane stays empty.  This control keeps the
        /// game's own tokens; every other combo in the window is localized.
        /// </summary>
        private static bool IsProtectedDBViewerCombo(ComboBox cb)
        {
            if (cb == null || cb.Name != "CB_ObjectType") return false;
            string formName;
            if (!IsInTargetLocalizableForm(cb, out formName)) return false;
            return formName.IndexOf("InternalDBViewer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Resolve the InternalDBViewer window.  It is created on a helper UI
        /// thread, so Application.OpenForms never lists it; fall back to the
        /// top-level window set and map the HWND back to its Control.
        /// </summary>
        private static Form FindDBViewerForm()
        {
            FormCollection forms = Application.OpenForms;
            if (forms != null)
            {
                foreach (Form f in forms)
                {
                    if (f != null && !f.IsDisposed && IsDBViewerForm(f)) return f;
                }
            }

            Form found = null;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (found != null) return false;
                    try
                    {
                        Form f = Control.FromHandle(hWnd) as Form;
                        if (f != null && !f.IsDisposed && IsDBViewerForm(f))
                        {
                            found = f;
                            return false;
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private static bool IsDBViewerForm(Form f)
        {
            return f.GetType().Name.IndexOf("InternalDBViewer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LocalizeDBViewerComboBox(ComboBox cb)
        {
            if (cb == null) return;
            try
            {
                string formName;
                if (!IsInTargetLocalizableForm(cb, out formName)) return;
                // DO NOT touch CB_ObjectType in InternalDBViewer!
                if (IsProtectedDBViewerCombo(cb)) return;

                if (!hookedDBCombos.Contains(cb.GetHashCode()))
                {
                    hookedDBCombos.Add(cb.GetHashCode());
                    Log("LocalizeTargetFormComboBox: " + cb.Name + " form=" + formName + " type=" + cb.GetType().FullName
                        + " items=" + cb.Items.Count + " bound=" + (cb.DataSource != null));
                }
                if (cb.DataSource != null) TranslateComboDisplayColumn(cb);
                else TranslateComboItems(cb);
            }
            catch { }
        }

        /// <summary>
        /// General-purpose combo item translation for game windows outside the
        /// database viewer: menu pickers, doctrine/EMCON/WRA enumerations, mission
        /// and side pickers.  Only display strings change; enum code tokens and
        /// script/schema identifiers are never rewritten.
        /// </summary>
        private static string TranslateComboItemText(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;
            string t = rawText.Trim();
            if (t.Length == 0 || ContainsChinese(t)) return rawText;
            if (IsForbiddenCodeOrEnum(t)) return rawText;

            string tr = TranslateString(t);
            if (!string.IsNullOrEmpty(tr) && tr != t) return tr;

            // 时间 / 日期占位符与提示串
            if (t == "Name") return "名称";
            return rawText;
        }

        private static void TranslateComboItems(ComboBox cb)
        {
            string formName;
            bool dbViewer = IsInTargetLocalizableForm(cb, out formName)
                            && formName.IndexOf("InternalDBViewer", StringComparison.OrdinalIgnoreCase) >= 0;
            // 先算出真正要改的项，再决定是否动用拉锯熔断。
            // 历史 bug（2026-10-17 实测）：旧实现在入口**无条件**调 AllowWrite，而 AllowWrite 按
            // 「同一 target 被重复调用超限」计数——于是**一次都没写入**的组合框只要被反复访问
            // 就会被永久静音。日志里 269 次「WRITE LOOP BLOCKED ComboBoxItems」
            // （CB_ScenOrder 28 次、Combo_AlertLevel、Combo_StateSelection 等）即由此产生，
            // 表现为下拉项大面积英文（条令/交战规则窗口的 Inherited, … 就是其中一例）。
            List<KeyValuePair<int, string>> plan = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string raw = cb.GetItemText(cb.Items[i]);
                if (string.IsNullOrEmpty(raw) || ContainsChinese(raw)) continue;
                string tr = dbViewer ? TranslateDBComboItem(raw) : TranslateComboItemText(raw);
                if (string.IsNullOrEmpty(tr) || tr == raw) continue;
                plan.Add(new KeyValuePair<int, string>(i, tr));
            }
            if (plan.Count == 0) return;   // 无需写入 → 不计数、不熔断
            // 指纹改为「本次待写入的翻译」：写成功后下次访问计划为空（不再计数）；
            // 若游戏把条目改回去（真拉锯），同一计划重现才会累加并触发熔断。
            StringBuilder fp = new StringBuilder();
            fp.Append(cb.Items.Count).Append(':');
            foreach (KeyValuePair<int, string> kv in plan)
            {
                fp.Append(kv.Key).Append('=').Append(kv.Value).Append('|');
            }
            if (!AllowWrite(cb.GetHashCode() * 31 + 7, fp.ToString(), "ComboBoxItems '" + cb.Name + "'")) return;
            bool changed = false;
            foreach (KeyValuePair<int, string> kv in plan)
            {
                try
                {
                    cb.Items[kv.Key] = kv.Value;
                    changed = true;
                }
                catch { }
            }
            if (changed) cb.Invalidate();
        }

        /// <summary>
        /// Translate the display column of a data-bound combo box. Only the display
        /// column is touched; the ValueMember column keeps the database key the game
        /// filters on.
        /// </summary>
        private static void TranslateComboDisplayColumn(ComboBox cb)
        {
            string col = cb.DisplayMember;
            if (string.IsNullOrEmpty(col)) return;

            DataTable dt = null;
            object src = cb.DataSource;
            DataView dv = src as DataView;
            if (dv != null) dt = dv.Table;
            if (dt == null) dt = src as DataTable;
            if (dt == null)
            {
                BindingSource bs = src as BindingSource;
                if (bs != null)
                {
                    dv = bs.DataSource as DataView;
                    if (dv != null) dt = dv.Table;
                    if (dt == null) dt = bs.DataSource as DataTable;
                }
            }
            if (dt == null) return;

            DataColumn dc = dt.Columns.Contains(col) ? dt.Columns[col] : null;
            if (dc == null || dc.DataType != typeof(string) || dc.ReadOnly) return;

            bool changed = false;
            try
            {
                foreach (DataRow row in dt.Rows)
                {
                    if (row.RowState == DataRowState.Deleted || row.RowState == DataRowState.Detached) continue;
                    string raw = row[dc] as string;
                    if (string.IsNullOrEmpty(raw) || ContainsChinese(raw)) continue;
                    string tr = TranslateDBComboItem(raw);
                    if (string.IsNullOrEmpty(tr) || tr == raw) continue;
                    row[dc] = tr;
                    changed = true;
                }
            }
            catch { }
            if (changed) cb.Invalidate();
        }

        private static string TranslateDBListEntry(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            string s = t;

            // 1. 替换方括号内的平台子类型 [Bomber], [Fighter], [Tanker] 等
            s = Regex.Replace(s, @"\[([A-Za-z0-9\s/\-]+)\]", m =>
            {
                string inner = m.Groups[1].Value;
                string trInner = TranslateString(inner);
                if (trInner != inner) return "[" + trInner + "]";
                return m.Value;
            });

            // 2. 替换 " -- <Country> (<Service>)" 结构
            int dashIdx = s.IndexOf(" -- ");
            if (dashIdx > 0)
            {
                string leftPart = s.Substring(0, dashIdx);
                string rightPart = s.Substring(dashIdx + 4);

                // 解析右侧: Country [Year] (Service), Year
                int parenIdx = rightPart.IndexOf(" (");
                if (parenIdx > 0)
                {
                    string countryStr = rightPart.Substring(0, parenIdx).Trim();
                    string trCountry = TranslateString(countryStr);
                    string rest = rightPart.Substring(parenIdx);

                    rest = rest.Replace("(Air Force)", "(空军)")
                               .Replace("(Navy)", "(海军)")
                               .Replace("(Army)", "(陆军)")
                               .Replace("(Marines)", "(海军陆战队)")
                               .Replace("(Marine Corps)", "(海军陆战队)")
                               .Replace("(Coast Guard)", "(海岸警卫队)")
                               .Replace("(Long Range Aviation [DA])", "(远程航空兵 [DA])")
                               .Replace("(Naval Aviation [AV-MF])", "(海军航空兵 [AV-MF])")
                               .Replace("(Air Defense Troops [PVO])", "(国土防空军 [PVO])")
                               .Replace("(Frontal Aviation [FA])", "(前线航空兵 [FA])")
                               .Replace("(Military Transport Aviation [VTA])", "(军事运输航空兵 [VTA])")
                               .Replace("(Commercial)", "(商业民航)")
                               .Replace("(Border Guard)", "(边防部队)")
                               .Replace("(Royal Air Force)", "(皇家空军)")
                               .Replace("(Royal Navy)", "(皇家海军)");

                    rightPart = trCountry + rest;
                }
                s = leftPart + " -- " + rightPart;
            }

            // 3. 著名导弹与平台代号双语化
            s = s.Replace("AMRAAM", "AMRAAM [先进中距空空导弹]")
                 .Replace("Sidewinder", "Sidewinder [响尾蛇近距弹]")
                 .Replace("Sparrow", "Sparrow [麻雀半主动弹]")
                 .Replace("Phoenix", "Phoenix [不死鸟远程弹]")
                 .Replace("Harpoon", "Harpoon [鱼叉反舰导弹]")
                 .Replace("Tomahawk", "Tomahawk [战斧巡航导弹]")
                 .Replace("Maverick", "Maverick [小牛空地导弹]")
                 .Replace("Hellfire", "Hellfire [地狱火导弹]")
                 .Replace("HARM", "HARM [哈姆反辐射导弹]")
                 .Replace("Paveway", "Paveway [铺路激光制导炸弹]")
                 .Replace("Bison B", "Bison B [野牛B]")
                 .Replace("Beluga", "Beluga [大白鲸]")
                 .Replace("Atlas", "Atlas [阿特拉斯]");

            return s;
        }

        private static void TranslateDarkListView(Control ctrl)
        {
            if (ctrl == null) return;
            try
            {
                PropertyInfo piItems = ctrl.GetType().GetProperty("Items");
                if (piItems == null) return;
                System.Collections.IList items = piItems.GetValue(ctrl, null) as System.Collections.IList;
                if (items == null) return;

                bool changed = false;
                for (int i = 0; i < items.Count && i < 3000; i++)
                {
                    object item = items[i];
                    if (item == null) continue;
                    PropertyInfo piText = item.GetType().GetProperty("Text");
                    if (piText != null)
                    {
                        string t = piText.GetValue(item, null) as string;
                        if (!string.IsNullOrEmpty(t) && !ContainsChinese(t))
                        {
                            string tr = TranslateDBListEntry(t);
                            if (tr != t)
                            {
                                piText.SetValue(item, tr, null);
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) ctrl.Invalidate();
            }
            catch { }
        }

        private static void TranslateDarkTreeView(Control ctrl)
        {
            if (ctrl == null) return;
            TranslateDarkTreeViewInner(ctrl);
        }

        private static void TranslateDarkTreeViewInner(Control ctrl)
        {
            try
            {
                if (_dtvNodesProp == null)
                {
                    _dtvNodesProp = ctrl.GetType().GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_dtvNodesProp == null) return;

                System.Collections.IEnumerable nodes = _dtvNodesProp.GetValue(ctrl, null) as System.Collections.IEnumerable;
                if (nodes == null) return;

                bool anyChanged = false;
                foreach (object node in nodes)
                {
                    if (TranslateDarkTreeNode(node))
                    {
                        anyChanged = true;
                    }
                }
                if (anyChanged)
                {
                    ctrl.Invalidate();
                }
            }
            catch { }
        }

        private static bool TranslateDarkTreeNode(object node)
        {
            if (node == null) return false;
            bool changed = false;
            try
            {
                Type t = node.GetType();
                if (_dtnTextProp == null)
                {
                    _dtnTextProp = t.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_dtnNodesProp == null)
                {
                    _dtnNodesProp = t.GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_dtnTextProp != null)
                {
                    string raw = _dtnTextProp.GetValue(node, null) as string;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        string trans = TranslateString(raw);
                        if (string.IsNullOrEmpty(trans) || trans == raw)
                        {
                            trans = TranslateMilitaryUnitName(raw);
                        }
                        if (!string.IsNullOrEmpty(trans) && trans != raw)
                        {
                            _dtnTextProp.SetValue(node, trans, null);
                            changed = true;
                        }
                    }
                }

                if (_dtnNodesProp != null)
                {
                    System.Collections.IEnumerable children = _dtnNodesProp.GetValue(node, null) as System.Collections.IEnumerable;
                    if (children != null)
                    {
                        foreach (object child in children)
                        {
                            if (TranslateDarkTreeNode(child))
                            {
                                changed = true;
                            }
                        }
                    }
                }
            }
            catch { }
            return changed;
        }

        private static readonly Dictionary<int, string> buttonIndexToImageName = new Dictionary<int, string>()
        {
            { 0, "Btn_Auto_Engage_Target.png" },
            { 1, "Btn_Manually_Engage_Target.png" },
            { 2, "Btn_Plot_Course.png" },
            { 3, "Btn_Throttle_Altitude.png" },
            { 4, "Btn_Formation_Editor.png" },
            { 5, "Btn_Magazines.png" },
            { 6, "Btn_Aircraft_Operations.png" },
            { 7, "Btn_Boat_Operations.png" },
            { 8, "Btn_Mount_Weapons.png" },
            { 9, "Btn_Sensors.png" },
            { 10, "Btn_Systems_Damage.png" },
            { 11, "Btn_Unit-Group_Doctrine.png" },
            { 12, "Btn_Mission_Editor.png" }
        };

        private static readonly Dictionary<string, System.Drawing.Image> cachedButtonImages = new Dictionary<string, System.Drawing.Image>(StringComparer.OrdinalIgnoreCase);
        private static bool buttonImagesLoaded = false;

        private static void LoadButtonImages()
        {
            if (buttonImagesLoaded) return;
            buttonImagesLoaded = true;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string menuDir = Path.Combine(baseDir, @"Symbols\Menu");
                if (Directory.Exists(menuDir))
                {
                    foreach (var kvp in buttonIndexToImageName)
                    {
                        string imgPath = Path.Combine(menuDir, kvp.Value);
                        if (File.Exists(imgPath))
                        {
                            using (var bmp = new System.Drawing.Bitmap(imgPath))
                            {
                                cachedButtonImages[kvp.Value] = new System.Drawing.Bitmap(bmp);
                            }
                        }
                    }
                    Log("Successfully pre-loaded " + cachedButtonImages.Count + " localized button images from Symbols\\Menu.");
                }
            }
            catch (Exception ex)
            {
                Log("LoadButtonImages error: " + ex.Message);
            }
        }

        private static readonly HashSet<int> processedToolStrips = new HashSet<int>();

        private static void LocalizeDarkToolStripButtons(ToolStrip ts)
        {
            if (ts == null) return;
            int hash = ts.GetHashCode();
            if (processedToolStrips.Contains(hash)) return;

            LoadButtonImages();

            int btnIndex = 0;
            for (int i = 0; i < ts.Items.Count; i++)
            {
                ToolStripItem itm = ts.Items[i];
                if (itm is ToolStripButton)
                {
                    if (buttonIndexToImageName.ContainsKey(btnIndex))
                    {
                        string imgName = buttonIndexToImageName[btnIndex];
                        if (cachedButtonImages.ContainsKey(imgName))
                        {
                            itm.Image = cachedButtonImages[imgName];
                            itm.DisplayStyle = ToolStripItemDisplayStyle.Image;
                        }
                    }
                    btnIndex++;
                }
            }
            processedToolStrips.Add(hash);
            Log("DarkToolStrip2 localized: updated " + btnIndex + " button images with Chinese bitmaps!");
        }

        
        private static void OnDropDownOpeningHandler(object sender, EventArgs e)
        {
            try
            {
                ToolStripDropDownItem ddi = sender as ToolStripDropDownItem;
                if (ddi != null && ddi.HasDropDownItems)
                {
                    foreach (ToolStripItem sub in ddi.DropDownItems)
                    {
                        TranslateToolStripItem(sub);
                    }
                    if (ddi.Owner != null)
                    {
                        ddi.Owner.BeginInvoke((MethodInvoker)delegate
                        {
                            try
                            {
                                foreach (ToolStripItem sub in ddi.DropDownItems)
                                {
                                    TranslateToolStripItem(sub);
                                }
                            }
                            catch { }
                        });
                    }
                }
            }
            catch { }
        }

        private static readonly HashSet<int> hookedItems = new HashSet<int>();
        [ThreadStatic]
        private static bool _inTextChanged;

        private static void TranslateToolStripItem(ToolStripItem item)
        {
            if (item == null) return;

            int itemHash = item.GetHashCode();
            if (!hookedItems.Contains(itemHash))
            {
                hookedItems.Add(itemHash);
                item.TextChanged += (s, e) =>
                {
                    if (_inTextChanged) return;
                    try
                    {
                        _inTextChanged = true;
                        ToolStripItem it = s as ToolStripItem;
                        if (it != null && !string.IsNullOrEmpty(it.Text) && !ContainsChinese(it.Text))
                        {
                            string tr = TranslateString(it.Text);
                            if (tr != it.Text && ContainsChinese(tr))
                            {
                                it.Text = tr;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        _inTextChanged = false;
                    }
                };
            }

            string t = item.Text;
            if (!string.IsNullOrEmpty(t))
            {
                string trans = TranslateString(t);
                if (trans != t)
                {
                    item.Text = trans;
                    translatedControlCount++;
                    if (translatedControlCount == 1 || translatedControlCount % 50 == 0)
                    {
                        Log("UI Menu/ToolStrip Translated count: " + translatedControlCount + " (Sample: " + t + " -> " + trans + ")");
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.ToolTipText))
            {
                string tipTrans = TranslateString(item.ToolTipText);
                if (tipTrans != item.ToolTipText)
                {
                    item.ToolTipText = tipTrans;
                }
            }

            if (item is ToolStripDropDownItem)
            {
                ToolStripDropDownItem ddi = (ToolStripDropDownItem)item;
                ddi.DropDownOpening -= OnDropDownOpeningHandler;
                ddi.DropDownOpening += OnDropDownOpeningHandler;
                if (ddi.HasDropDownItems)
                {
                    foreach (ToolStripItem sub in ddi.DropDownItems)
                    {
                        TranslateToolStripItem(sub);
                    }
                }
            }
        }

        private static int translatedControlCount = 0;

        private static string TranslateWeaponSummary(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = text;
            s = s.Replace("Guided Weapon. ", "制导武器。 ")
                 .Replace("Unguided Weapon. ", "非制导武器。 ")
                 .Replace("Decoy. ", "诱饵弹。 ")
                 .Replace("Sensor Pod. ", "传感器/电子战吊舱。 ")
                 .Replace("Torpedo. ", "鱼雷。 ")
                 .Replace("Naval Mine. ", "水雷。 ")
                 .Replace("Depth Charge. ", "深水炸弹。 ")
                 .Replace("Sonobuoy. ", "声呐浮标。 ")
                 .Replace("Fuel Tank. ", "外挂副油箱。 ");

            int tgtIdx = s.IndexOf("Targets: ");
            if (tgtIdx < 0) tgtIdx = s.IndexOf("打击目标: ");
            if (tgtIdx >= 0)
            {
                int prefixLen = s.IndexOf("Targets: ") >= 0 ? "Targets: ".Length : "打击目标: ".Length;
                int dotIdx = s.IndexOf(". ", tgtIdx + prefixLen);
                if (dotIdx > tgtIdx)
                {
                    string targetsPart = s.Substring(tgtIdx + prefixLen, dotIdx - (tgtIdx + prefixLen));
                    string trTargets = TranslateCommaComposite(targetsPart);
                    s = s.Substring(0, tgtIdx) + "打击目标: " + trTargets + s.Substring(dotIdx);
                }
            }

            s = s.Replace("Max Speed: ", "最高航速: ")
                 .Replace("Max Range: ", "最大射程: ")
                 .Replace(" kts. ", " 节。 ")
                 .Replace(" nm. ", " 海里。 ")
                 .Replace("Warheads: ", "战斗部: ")
                 .Replace("Warhead: ", "战斗部: ")
                 .Replace("Can be guided by: ", "制导设备: ")
                 .Replace("Can be directed by: ", "可由以下传感器引导: ")
                 .Replace("Gun. ", "航炮/火炮。 ")
                 .Replace("Deployment depth: ", "布设深度: ");

            s = s.Replace("Blast-Frag", "破片杀伤")
                 .Replace("Blast/Frag", "破片杀伤")
                 .Replace(" Frag", " 破片")
                 .Replace(" HEAT", " 破甲")
                 .Replace(" Penetrator", " 钻地侵彻")
                 .Replace(" HE.", " 高爆。")
                 .Replace(" HE ", " 高爆 ")
                 .Replace(" Nuclear", " 核战斗部")
                 .Replace(" Continuous Rod", " 连续杆")
                 .Replace(" Submunitions", " 子母弹");

            return s;
        }

        private static string TranslateFlightProfile(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace(" minutes at ", " 分钟 @ ")
                       .Replace(" min at ", " 分钟 @ ")
                       .Replace(" minutes combat in AB", " 分钟加力空战")
                       .Replace(" min combat in AB", " 分钟加力空战")
                       .Replace(" minutes in AB", " 分钟加力飞行")
                       .Replace(" nm CAP.", " 海里战斗空中巡逻 (CAP)。")
                       .Replace(" nm CAP", " 海里战斗空中巡逻 (CAP)")
                       .Replace(" nm AEW.", " 海里空中预警巡逻 (AEW)。")
                       .Replace(" nm ASW patrol.", " 海里反潜巡逻。")
                       .Replace(" nm Maritime Surveillance.", " 海里海上监视巡逻。")
                       .Replace(" nm Ferry Range.", " 海里转场航程。")
                       .Replace(" nm Strike radius, ", " 海里打击作战半径，")
                       .Replace(" nm Strike radius. ", " 海里打击作战半径。")
                       .Replace(" nm Strike radius.", " 海里打击作战半径。")
                       .Replace(" nm Recon radius, ", " 海里侦察作战半径，")
                       .Replace(" nm Recon radius. ", " 海里侦察作战半径。")
                       .Replace(" nm Recon radius.", " 海里侦察作战半径。")
                       .Replace("Cruise at optimum altitude and speed.", "按最佳高度与速度巡航。")
                       .Replace("Cruise at ", "巡航高度 ")
                       .Replace("Weapon release at max range.", "在最大射程发射武器。")
                       .Replace("No form-up time.", "无编队集结时间。")
                       .Replace(" profile.", " 飞行剖面。")
                       .Replace("Radius: ", "作战半径: ")
                       .Replace("Transit at ", "巡航转场高度 ")
                       .Replace("Ingress at ", "突防进入高度 ")
                       .Replace("Egress at ", "脱离返航高度 ")
                       .Replace(", on-station loiter", "，阵位巡逻待机")
                       .Replace(", on-station ", "，阵位滞空 ")
                       .Replace("Form-up ", "编队集结 ")
                       .Replace(" at 2k ft.", " @ 高度 2千英尺。")
                       .Replace(" at 36k ft", " @ 36千英尺")
                       .Replace("% reserves and ", "% 备份油量与 ")
                       .Replace("% reserves.", "% 备份油量。")
                       .Replace("% reserves", "% 备份油量")
                       .Replace(" min loiter", " 分钟盘旋待机")
                       .Replace("Hi-Lo-Hi", "高-低-高 (Hi-Lo-Hi)")
                       .Replace("Lo-Lo-Lo", "低-低-低 (Lo-Lo-Lo)")
                       .Replace("Hi-Hi-Hi", "高-高-高 (Hi-Hi-Hi)")
                       .Replace("low-level dash", "低空突防冲刺")
                       .Replace("k ft", "千英尺")
                       .Replace(" nm", " 海里");
        }

        private static string TranslateLoadoutValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return val;
            if (val == "-" || val == "无" || val == "无限制") return val;

            if (exactMap.ContainsKey(val)) return exactMap[val];
            string norm = Regex.Replace(val.Trim(), @"\s+", " ");
            if (normExactMap.ContainsKey(norm)) return normExactMap[norm];

            if (val.StartsWith("Shotgun:") || val.StartsWith("Winchester:"))
            {
                return val.Replace("Shotgun: ", "主武器耗尽返航 (Shotgun): ")
                          .Replace("Winchester: ", "弹尽返航 (Winchester): ")
                          .Replace("Return to base after one engagement with WVR weapons.", "在进行一次视距内交战后返航。")
                          .Replace("Return to base after one engagement with BVR weapons.", "在进行一次超视距交战后返航。")
                          .Replace("Allow targets of opportunity with air-to-air guns.", "允许使用航炮攻击机会目标。");
            }

            if (val.EndsWith(" capable") || val.EndsWith(" 能力") || val.StartsWith("Day and night") || val.StartsWith("Day-only") || val.StartsWith("Night-only"))
            {
                string cap = val.Replace(" capable", "").Replace(" 能力", "").Trim();
                return cap.Replace("Day and night", "昼夜")
                          .Replace("Day-only", "仅昼间")
                          .Replace("Night-only", "仅夜间")
                          .Replace("All-weather", "全天候")
                          + "作战能力";
            }

            if (val.Contains(" minutes at ") || val.Contains(" nm CAP") || val.Contains("Transit at ") || val.Contains("Ingress at ") || val.Contains(" profile") || val.Contains("Recon radius") || val.Contains("Strike radius") || val.Contains("reserves"))
            {
                return TranslateFlightProfile(val);
            }

            if (val.Contains(", "))
            {
                return TranslateCommaComposite(val);
            }

            string tr = TranslateString(val);
            if (tr != val) return tr;
            return val;
        }

        private static string TranslateCommaComposite(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains(",")) return TranslateString(text);
            string[] items = text.Split(new string[] { ", " }, StringSplitOptions.None);
            if (items.Length <= 1) return TranslateString(text);

            List<string> trItems = new List<string>();
            bool anyTrans = false;
            for (int i = 0; i < items.Length; i++)
            {
                string it = items[i].Trim();
                string tr = TranslateString(it);
                if (tr == it && it.EndsWith(" Technology"))
                {
                    string gen = it.Substring(0, it.Length - " Technology".Length).Trim();
                    string trGen = TranslateString(gen);
                    if (trGen != gen) tr = trGen + "技术水平";
                }
                if (tr != it) anyTrans = true;
                trItems.Add(tr);
            }
            return anyTrans ? string.Join("，", trItems.ToArray()) : text;
        }

        private static string TranslateCellText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string tr = TranslateString(text);
            if (tr != text) return tr;

            if (text.Contains("Reserve"))
            {
                text = text.Replace("(Reserve [Available])", "(后备 [可用])")
                           .Replace("Reserve [Available]", "后备 [可用]")
                           .Replace("(Reserve)", "(后备)");
            }

            if (text.Contains("Maintenance"))
            {
                text = text.Replace("(Maintenance [Unavailable])", "(维护 [不可用])")
                           .Replace("Maintenance [Unavailable]", "维护 [不可用]");
            }

            if (text.Contains("CAEW"))
            {
                text = text.Replace("Conformal Airborne Early Warning (CAEW)", "保形空中预警 (CAEW)");
            }

            if (text.Contains("Ferry"))
            {
                text = text.Replace("(Ferry)", "(转场)")
                           .Replace("Ferry Flight", "转场飞行")
                           .Replace("Ferry", "转场");
            }

            if (Regex.IsMatch(text, @"^\d+\s*(hr|小时)(\s*\d+\s*(min|分钟))?$|^\d+\s*(min|分钟)$"))
            {
                return text.Replace("hr", "小时").Replace("min", "分钟").Replace("sec", "秒");
            }

            if (text.Contains(" sorties @ ") || text.Contains(" flying time") || text.Contains("Day and night") || text.Contains("架次") || text.Contains("downtime"))
            {
                text = text.Replace("Day and night", "昼夜")
                           .Replace("Day-only", "仅昼间")
                           .Replace("Night-only", "仅夜间")
                           .Replace(" sorties @ ", " 架次 @ ")
                           .Replace(" flying time", " 飞行时间")
                           .Replace(" downtime", " 停机整备")
                           .Replace(" hr", " 小时")
                           .Replace(" min", " 分钟");
            }

            if (text.Contains("Enabled") || text.Contains("已启用") || text.Contains("Day/Night"))
            {
                text = text.Replace("Enabled, ", "已启用，")
                           .Replace("Enabled", "已启用")
                           .Replace("Day/Night:", "昼/夜:")
                           .Replace("Day/Night", "昼/夜");
            }
            if (text.StartsWith("Disabled"))
            {
                return text.Replace("Disabled (Doctrine)", "已禁用 (条令限制)").Replace("Disabled", "已禁用");
            }

            return text;
        }

        private static string TranslateLoadoutDetailText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string[] lines = text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
            StringBuilder sb = new StringBuilder();
            bool anyTrans = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trLine = line;

                if (line.StartsWith("Pre-briefed weapon state ")
                    || line.StartsWith("Effective pre-briefed weapon state ")
                    || line.StartsWith("Loadout's default weapon state ")
                    || line.StartsWith("预设武器状态")
                    || line.StartsWith("预先简报的武器状态")
                    || line.StartsWith("生效的预设武器状态")
                    || line.StartsWith("挂载方案默认武器状态"))
                {
                    string prefix = "预设武器状态: ";
                    if (line.StartsWith("Effective") || line.StartsWith("生效")) prefix = "生效的预设武器状态: ";
                    else if (line.StartsWith("Loadout's") || line.StartsWith("挂载")) prefix = "挂载方案默认武器状态: ";

                    string rest = line.Replace("Effective pre-briefed weapon state ", "")
                                      .Replace("Loadout's default weapon state ", "")
                                      .Replace("Pre-briefed weapon state ", "")
                                      .Replace("预先简报的武器状态 ", "")
                                      .Replace("生效的预设武器状态: ", "")
                                      .Replace("挂载方案默认武器状态: ", "")
                                      .Replace("预设武器状态: ", "")
                                      .Trim();
                    string trRest = exactMap.ContainsKey(rest) ? exactMap[rest] : TranslateString(rest);
                    trLine = prefix + trRest;
                    if (trLine != line) anyTrans = true;
                }
                else
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) colonIdx = line.IndexOf('：');
                    if (colonIdx > 0 && colonIdx < line.Length - 1)
                    {
                        string key = line.Substring(0, colonIdx).Trim();
                        string val = line.Substring(colonIdx + 1).Trim();
                        string trKey = exactMap.ContainsKey(key) ? exactMap[key] : (normExactMap.ContainsKey(key) ? normExactMap[key] : TranslateString(key));
                        string trVal = TranslateLoadoutValue(val);
                        trLine = trKey + ": " + trVal;
                        if (trLine != line) anyTrans = true;
                    }
                }

                if (i > 0) sb.Append("\r\n");
                sb.Append(trLine);
            }

            return anyTrans ? sb.ToString() : text;
        }

        /// <summary>
        /// Window captions.  The main window caption is rebuilt by the game on
        /// every scenario load, and the WPF message-log windows are created off
        /// the main UI thread, so neither reaches the ordinary control path.
        /// </summary>
        private static string TranslateTitleText(string fText)
        {
            if (string.IsNullOrEmpty(fText)) return fText;
            if (ContainsChinese(fText))
            {
                // 混排标题（"条令 & 交战规则(RoE) 用于推演方: Israel"）里尾部仍
                // 可能是英文阵营/想定名，单独翻译该尾串。
                int ci = fText.LastIndexOf(':');
                if (ci < 0) ci = fText.LastIndexOf('：');
                if (ci > 0 && ci < fText.Length - 1)
                {
                    string tail = fText.Substring(ci + 1).Trim();
                    if (tail.Length > 0 && !ContainsChinese(tail))
                    {
                        string trTail = TranslateString(tail);
                        if (trTail != tail) return fText.Substring(0, ci + 1) + " " + trTail;
                    }
                }
                return fText;
            }

            int cut = fText.IndexOf("- Command: Modern Operations", StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
            {
                string head = fText.Substring(0, cut).Trim().TrimEnd('-').Trim();
                string trHead = TranslateString(head);
                // 构建号每次游戏更新都会变（1900.20 → 1900.23），交给规则处理，不再写死。
                return trHead + " - " + TranslateString(fText.Substring(cut + 2).Trim());
            }
            if (fText.StartsWith("Command: Modern Operations"))
            {
                return fText.Replace("Command: Modern Operations", "现代行动：指挥")
                            .Replace("Build ", "内部版本 ");
            }
            if (fText.StartsWith("Message Log - "))
            {
                string view = fText.EndsWith("Raw text view", StringComparison.OrdinalIgnoreCase)
                    ? "原始文本视图" : "交互视图";
                return "消息日志 - " + view;
            }
            return TranslateString(fText);
        }

        private static void TranslateControlText(Control ctrl)
        {
            if (ctrl == null) return;
            TranslateControlTextInner(ctrl);
        }

        /// <summary>
        /// 文本输入控件：正文属于用户输入，只能翻水印，绝不能回写正文。
        /// DarkUI 的自定义输入框（如搜索想定框、任务名框）名字里带 TextBox/EditBox，
        /// 但不继承 TextBoxBase，旧代码把它们的正文当成普通标签翻译，直接导致闪退。
        /// </summary>
        private static bool IsTextInputControl(Control ctrl)
        {
            if (ctrl == null) return false;
            if (ctrl is TextBoxBase) return true;
            if (ctrl is ComboBox || ctrl is ListControl || ctrl is UpDownBase) return true;
            string tn = ctrl.GetType().Name;
            if (tn.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tn.IndexOf("EditBox", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tn.EndsWith("Edit", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void TranslateControlTextInner(Control ctrl)
        {
            if (ctrl is Form)
            {
                string fText = ctrl.Text;
                if (!string.IsNullOrEmpty(fText))
                {
                    // 数据库查看器标题由数据库版本号后缀动态拼成，词典无法整体命中
                    if (fText.StartsWith("Database Viewer"))
                    {
                        int parenIdx = fText.IndexOf('(');
                        ctrl.Text = "数据库查看器 " + (parenIdx >= 0 ? fText.Substring(parenIdx) : "").Trim();
                        return;
                    }
                    if (fText.StartsWith("Air Ops - "))
                    {
                        ctrl.Text = "航空作业 - " + fText.Substring("Air Ops - ".Length);
                        return;
                    }
                    if (fText.StartsWith("Docking Ops - "))
                    {
                        ctrl.Text = "船坞作业 - " + fText.Substring("Docking Ops - ".Length);
                        return;
                    }
                    if (fText.StartsWith("Mission Editor for side: "))
                    {
                        ctrl.Text = "任务编辑器 - 所属阵营: " + fText.Substring("Mission Editor for side: ".Length);
                        return;
                    }
                    if (fText.StartsWith("Formation Editor"))
                    {
                        ctrl.Text = fText.Replace("Formation Editor", "编队编辑器");
                        return;
                    }
                    if (fText.Contains("Special Messages"))
                    {
                        ctrl.Text = fText.Replace("Special Messages", "专用战情消息");
                        return;
                    }
                    if ((fText.StartsWith("就绪飞机") || fText.StartsWith("Ready Aircraft")) && fText.Contains(" at "))
                    {
                        ctrl.Text = fText.Replace("Ready Aircraft: ", "就绪飞机: ")
                                         .Replace("Ready Aircraft：", "就绪飞机：")
                                         .Replace(" at ", " @ ");
                        return;
                    }
                    // 单位命令系窗口的标题是「英文前缀 + 单位名」，词库是精确匹配、盖不住：
                    // 实测 Sensors for: Taif (King Fahd Air Base) / Damage Status for Taif (…)。
                    if (fText.StartsWith("Sensors for: "))
                    {
                        ctrl.Text = "传感器：" + fText.Substring("Sensors for: ".Length);
                        return;
                    }
                    if (fText.StartsWith("Damage Status for "))
                    {
                        ctrl.Text = "损伤与系统状态 - " + fText.Substring("Damage Status for ".Length);
                        return;
                    }
                    if (fText.StartsWith("Weapons for "))
                    {
                        ctrl.Text = "武器：" + fText.Substring("Weapons for ".Length);
                        return;
                    }
                    string trTitle = TranslateTitleText(fText);
                    if (!string.IsNullOrEmpty(trTitle) && trTitle != fText)
                    {
                        ctrl.Text = trTitle;
                        return;
                    }
                }
            }

            if (ctrl is TextBoxBase || IsTextInputControl(ctrl))
            {
                TranslateWatermark(ctrl);
            }

            if (ctrl is TextBoxBase)
            {
                TextBoxBase tb = (TextBoxBase)ctrl;
                if (!string.IsNullOrEmpty(tb.Text) && tb.Text.StartsWith("Enter Database ID for this "))
                {
                    tb.Text = "输入该型装备的数据库 ID...";
                    return;
                }
                if (tb.ReadOnly && !string.IsNullOrEmpty(tb.Text))
                {
                    string trTb = TranslateLoadoutDetailText(tb.Text);
                    if (trTb != tb.Text)
                    {
                        tb.Text = trTb;
                    }
                }
                return;
            }

            // DarkUI 等自定义输入框（名字带 TextBox/Edit 但不继承 TextBoxBase）的正文
            // 就是用户正在输入的内容，绝不可回写：
            // 实证（2026-10-16 用户实测）——在搜索框输入 m，引擎把 m 当成单位换算译成「米」
            // 写回输入框，游戏又写回 m，日志里 “UI Controls Translated count”
            // 每 2.5 秒涨 50（m -> 米）直到 5900 后进程以 0xc00000fd 栈溢出闪退。
            if (IsTextInputControl(ctrl)) return;

            // ComboBox, ListControl and UpDownBase hold internal query/code keys
            // or user inputs. Overwriting their .Text destroys SelectedIndex and breaks game logic!
            if (ctrl is ComboBox || ctrl is ListControl || ctrl is UpDownBase)
            {
                return;
            }

            // 彻底解决全局时间清空按钮 Clear 误译为天气“晴”的错误
            if ((ctrl is ButtonBase || ctrl.GetType().Name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) &&
                (ctrl.Text == "Clear" || ctrl.Text == "晴"))
            {
                ctrl.Text = "清除";
                return;
            }

            // 动态计数串与条款控件的固定英文（均位于标签/按钮等非分派控件上，改写安全）：
            // 「2 selectable sides」是 ChooseSide 面板上的 DarkLabel（宿主已用 CTRL_TREE 定位：
            // lblSideNumber）；Inherited From Doctrine: / (Parent Side) 同理属标签/按钮。
            // 注意：**不碰组合框**——同一窗口里 Combo_StateSelection 的英文值属「按文本分派」的
            // 下拉框（R8 教训：改写 .Text 会破坏 SelectedIndex 与游戏逻辑）。
            if (!(ctrl is ComboBox || ctrl is ListControl || ctrl is UpDownBase))
            {
                string ctext = ctrl.Text;
                if (!string.IsNullOrEmpty(ctext))
                {
                    Match mSel = Regex.Match(ctext, @"^(\d+)\s+selectable sides?$");
                    if (mSel.Success)
                    {
                        ctrl.Text = "可选择 " + mSel.Groups[1].Value + " 个阵营";
                        return;
                    }
                    // 推演选项窗口的刷新频率是动态值：「Every 5 seconds」/「Every 30 seconds」。
                    Match mEvery = Regex.Match(ctext, @"^Every\s+(\d+)\s+(second|seconds|minute|minutes|ms)$");
                    if (mEvery.Success)
                    {
                        int nEvery;
                        if (int.TryParse(mEvery.Groups[1].Value, out nEvery))
                        {
                            bool isMinute = mEvery.Groups[2].Value.StartsWith("minute", StringComparison.Ordinal);
                            bool isMs = mEvery.Groups[2].Value == "ms";
                            ctrl.Text = "每 " + nEvery.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + (isMs ? " 毫秒" : isMinute ? " 分钟" : " 秒");
                            return;
                        }
                    }
                    if (ctext == "Inherited From Doctrine:")
                    {
                        ctrl.Text = "继承自条令：";
                        return;
                    }
                    if (ctext == "(Parent Side)" || ctext == "(Parent Side) ")
                    {
                        ctrl.Text = "（上级阵营）";
                        return;
                    }
                }
            }

            string t = ctrl.Text;
            if (!string.IsNullOrEmpty(t))
            {
                if (t == "Spacing") { ctrl.Text = "间距"; return; }
                if (t == "Misc") { ctrl.Text = "其他设置"; return; }
                if (t == "Pick area") { ctrl.Text = "选取区域"; return; }
                if (t == "Create area") { ctrl.Text = "创建区域"; return; }
                if ((t == "Clear" || t == "晴") && ctrl.Name.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ctrl.Text = "清除";
                    return;
                }
                // 处理单行或多行挂载详情面板 (如 AirOps / ReadyAircraft 右下角的各项 Label)
                if (t.Contains("挂载") || t.Contains("作战能力") || t.Contains("飞行剖面") || t.Contains("武器状态") || t.Contains("攻击高度") ||
                    t.Contains("Loadout") || t.Contains("Pre-briefed") || t.Contains("Effective pre-briefed") || t.Contains("Unlimited") ||
                    t.StartsWith("TimeOfDay") || t.StartsWith("NumberOfLoadouts") || t.Contains("Capable") || t.Contains("capable"))
                {
                    string trMulti = TranslateLoadoutDetailText(t);
                    if (trMulti != t)
                    {
                        ctrl.Text = trMulti;
                        return;
                    }
                }
                if (t.Contains("Special Messages"))
                {
                    ctrl.Text = t.Replace("Special Messages", "专用战情消息");
                    return;
                }
                // 处理 "展开 飞机构列表" 遗留误译修正与 Occupied 动态统计
                if (t.Contains("飞机构列表"))
                {
                    ctrl.Text = t.Replace("飞机构列表", "飞机列表").Replace("展开 ", "展开");
                    return;
                }
                if (t.StartsWith("Occupied "))
                {
                    ctrl.Text = "已占用 " + t.Substring("Occupied ".Length);
                    return;
                }
                if ((t.Contains(" sorties @ ") || t.Contains("maximum airborne time") || t.Contains(" turnaround")) && !t.Contains("停航"))
                {
                    string sTr = t.Replace(" sorties @ ", " 架次 @ ")
                                  .Replace(" maximum airborne time and ", " 最长滞空时间，")
                                  .Replace(" 最长滞空时间与 ", " 最长滞空时间，")
                                  .Replace(" turnaround, with ", " 再次出动周转，")
                                  .Replace(" 再次出动准备，包含 ", " 再次出动周转，")
                                  .Replace(" standdown ready time", " 停航战备恢复时间")
                                  .Replace(" hr", " 小时")
                                  .Replace(" min", " 分钟");
                    if (sTr != t)
                    {
                        ctrl.Text = sTr;
                        return;
                    }
                }

                string trans = TranslateString(t);
                if (trans != t)
                {
                    ctrl.Text = trans;
                    translatedControlCount++;
                    if (translatedControlCount == 1 || translatedControlCount % 50 == 0)
                    {
                        Log("UI Controls Translated count: " + translatedControlCount + " (Sample: " + t + " -> " + trans + ")");
                    }
                }
            }
        }

        private static bool ContainsChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 0x4e00 && s[i] <= 0x9fa5) return true;
            }
            return false;
        }

        public static string TranslateString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            // 1. Exact match & Trimmed exact match (highest priority before path filtering)
            if (exactMap.ContainsKey(raw)) return exactMap[raw];
            string trimmedExact = raw.Trim();
            if (exactMap.ContainsKey(trimmedExact))
            {
                return raw.Replace(trimmedExact, exactMap[trimmedExact]);
            }
            // 消息日志条目以图标标记开头 ("_ 正文"、"* 正文")，标记不属于正文，
            // 直接查表会整条落空。剥离标记后翻译正文，再把标记接回去。
            char marker = raw[0];
            if ((marker == '_' || marker == '*' || marker == '+' || marker == '>' || marker == '-') && raw.Length > 2 && raw[1] == ' ')
            {
                string body = raw.Substring(2);
                string trBody = TranslateString(body);
                if (trBody != body) return marker + " " + trBody;
            }

            // 路径、扩展名与文件过滤器直接跳过，防止破坏游戏文件检索与想定树加载
            if (raw.Contains("\\") || raw.Contains("://") || raw.Contains(".scen") || raw.Contains(".save") || raw.Contains(".db3") || raw.Contains(".dll") || raw.Contains(".ini") || raw.Contains(".xml") || raw.Contains("*. ") || raw.Contains("|*."))
            {
                return raw;
            }

            // 特殊复合前缀拆解：即使前缀已经被 offsets 汉化，后续英文想定标题/地域名/年份仍需递归汉化
            if (raw.StartsWith("[未授权] ") || raw.StartsWith("[UNLICENSED] "))
            {
                string rest = raw.Substring(raw.IndexOf(']') + 1).Trim();
                string tRest = TranslateString(rest);
                return "[未授权] " + tRest;
            }
            // 想定浏览器标题行的变体：“[NOT LICENSED] Carrier Killer, 1985”。
            // 标题与年份被拼成一个整串，整串查表永远落空（用户实测：战役里剧本名还是英文）。
            // 拆成“前缀 + 标题 + 年份”分开处理，标题按普通词条翻译，年份原样保留。
            if (raw.StartsWith("[NOT LICENSED]") || raw.StartsWith("[NOTLICENSED]")
                || raw.StartsWith("[未授权]"))
            {
                int close = raw.IndexOf(']');
                string rest = close >= 0 ? raw.Substring(close + 1).Trim() : raw;
                string tail = "";
                int comma = rest.LastIndexOf(", ");
                if (comma > 0)
                {
                    string maybeYear = rest.Substring(comma + 2).Trim();
                    if (maybeYear.Length == 4 && char.IsDigit(maybeYear[0]) && char.IsDigit(maybeYear[1])
                        && char.IsDigit(maybeYear[2]) && char.IsDigit(maybeYear[3]))
                    {
                        tail = ", " + maybeYear;
                        rest = rest.Substring(0, comma).Trim();
                    }
                }
                string tTitle = TranslateString(rest);
                return "[未授权] " + tTitle + tail;
            }
            if (raw.StartsWith("作战地域:") || raw.StartsWith("作战地域：") || raw.StartsWith("位置 / 设置:"))
            {
                int colonIdx = raw.IndexOf(':');
                if (colonIdx < 0) colonIdx = raw.IndexOf('：');
                string prefix = (colonIdx >= 0) ? raw.Substring(0, colonIdx + 1).Trim() : "作战地域：";
                string body = (colonIdx >= 0) ? raw.Substring(colonIdx + 1).Trim() : raw;
                int idxY = body.IndexOf("--");
                if (idxY > 0)
                {
                    string setting = body.Substring(0, idxY).Trim();
                    string rest = body.Substring(idxY + 2).Trim();
                    rest = Regex.Replace(rest, @"\bYear\s*:\s*", "年份: ", RegexOptions.IgnoreCase);
                    string tSet = TranslateString(setting);
                    return prefix + " " + tSet + "  --  " + rest;
                }
            }
            if (raw.StartsWith("Uses: "))
            {
                return "使用数据库: " + raw.Substring(6).Trim();
            }
            // 主标题/大厅标题带构建号，构建号每次游戏更新都会变（1900.20 → 1900.23 …）。
            // 用规则处理而不是再写死一个版本串（见 docs/BUG_REGISTRY.md R31）。
            if (raw.StartsWith("Command: Modern Operations", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("Modern Operations v1.10"))
            {
                Match mTitle = Regex.Match(raw, @"^(?:Command:\s*)?Modern Operations\s+v(\d+\.\d+)\s*-\s*Build\s+([0-9][0-9.]*)$");
                if (mTitle.Success)
                {
                    NoteGameBuild(raw);
                    return "现代行动：指挥 v" + mTitle.Groups[1].Value + " - 内部版本 " + mTitle.Groups[2].Value;
                }
            }

            // 如果文本中已经含有中文，说明已经被翻译过或者是中文字符串，坚决避免重复翻译与递归替换！
            if (ContainsChinese(raw))
            {
                string bracketOnly = TranslateBracketSegments(raw);
                return bracketOnly;
            }

            // Check cache first
            lock (cacheLock)
            {
                string cached;
                if (dynamicCache.TryGetValue(raw, out cached))
                {
                    return cached;
                }
            }

            string result = DoTranslate(raw);

            // Cache successes only.  A failure cached forever would freeze an
            // English string even after the dictionary learns the phrase.
            lock (cacheLock)
            {
                if (result != raw && dynamicCache.Count < MAX_CACHE_SIZE)
                {
                    dynamicCache[raw] = result;
                }
            }

            if (result == raw && !ContainsChinese(raw))
            {
                AuditUntranslatedLive(raw);
            }

            return result;
        }

        private static readonly Dictionary<string, string> docCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string TranslateLoadDocTag(string raw)
        {
            try
            {
                Match m = Regex.Match(raw, @"\[LOADDOC\](.*?)\[/LOADDOC\]", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string htmlName = m.Groups[1].Value.Trim();
                    string cached;
                    lock (docCache)
                    {
                        if (docCache.TryGetValue(htmlName, out cached))
                        {
                            return raw.Replace(m.Value, cached);
                        }
                    }

                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scenDir = Path.Combine(baseDir, "Scenarios");
                    string summary = "特别消息: " + htmlName;
                    if (Directory.Exists(scenDir))
                    {
                        string[] matches = Directory.GetFiles(scenDir, htmlName, SearchOption.AllDirectories);
                        if (matches != null && matches.Length > 0)
                        {
                            string html = File.ReadAllText(matches[0], Encoding.UTF8);
                            string plain = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                            plain = Regex.Replace(plain, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                            plain = Regex.Replace(plain, @"<[^>]+>", " ");
                            plain = plain.Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&#39;", "'");
                            plain = Regex.Replace(plain, @"\s+", " ").Trim();
                            if (!string.IsNullOrEmpty(plain))
                            {
                                summary = plain.Length > 120 ? plain.Substring(0, 118) + "..." : plain;
                            }
                        }
                    }

                    lock (docCache) { docCache[htmlName] = summary; }
                    return raw.Replace(m.Value, summary);
                }
            }
            catch { }
            return raw;
        }

        /// <summary>
        /// 中文串里的英文括号片段（任务编辑器「可用」列表、单位名称拼装等）：
        /// 「苏法/风暴 #1 (Air Superiority BVR) (拉马特戴维)」这类整串查表必然落空，
        /// 按标点切分又因含中文被跳过，于是英文载荷名会一直显示。
        /// 这里按 () / [] 拆出纯英文片段单独翻译后原样拼回。
        /// </summary>
        private static string TranslateBracketSegments(string raw)
        {
            try
            {
                if (string.IsNullOrEmpty(raw)) return raw;
                if (!ContainsChinese(raw)) return raw;
                if (raw.IndexOf('(') < 0 && raw.IndexOf('[') < 0) return raw;
                return Regex.Replace(raw, @"([\(\[])([^\)\]\[]{2,120})([\)\]])",
                    delegate(Match m)
                    {
                        string inner = m.Groups[2].Value;
                        if (ContainsChinese(inner)) return m.Value;
                        string trInner = TranslateInnerSegment(inner);
                        if (trInner == inner) return m.Value;
                        return m.Groups[1].Value + trInner + m.Groups[3].Value;
                    });
            }
            catch { return raw; }
        }

        /// <summary>翻译括号内的英文片段：整段 → 前缀(冒号)→ 逗号分片；无命中则原样返回。</summary>
        private static string TranslateInnerSegment(string inner)
        {
            string hit = LookupPiece(inner);
            if (hit != null) return hit;

            int colon = inner.IndexOf(':');
            if (colon > 0 && colon <= 6)
            {
                string prefix = inner.Substring(0, colon + 1);
                string rest = inner.Substring(colon + 1).Trim();
                string pfx = LookupPiece(prefix);
                string restTr = TranslateCommaPieces(rest);
                if (pfx != null || restTr != rest)
                {
                    return (pfx ?? prefix) + " " + restTr;
                }
            }
            return TranslateCommaPieces(inner);
        }

        private static string TranslateCommaPieces(string s)
        {
            if (s.IndexOf(',') < 0) return s;
            string[] parts = s.Split(',');
            bool any = false;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string piece = parts[i].Trim();
                string tr = LookupPiece(piece);
                if (tr != null) { piece = tr; any = true; }
                if (i > 0) sb.Append(", ");
                sb.Append(piece);
            }
            return any ? sb.ToString() : s;
        }

        private static string LookupPiece(string piece)
        {
            if (string.IsNullOrEmpty(piece)) return null;
            if (exactMap.ContainsKey(piece)) return exactMap[piece];
            string norm = Regex.Replace(piece.Trim(), @"\s+", " ");
            if (normExactMap.ContainsKey(norm)) return normExactMap[norm];
            return null;
        }

        /// <summary>
        /// 游戏内部版本哨兵：从标题串（"Command: Modern Operations v1.10 - Build 1900.23"）读实际
        /// 构建号，与安装清单里「本汉化适配的构建号」比对；不一致即说明游戏已更新，汉化可能大面积
        /// 失效（R31 事故：构建号 1900.20→1900.23 跳变，界面与注入同时失灵而无人知晓）。
        /// </summary>
        private static void NoteGameBuild(string raw)
        {
            if (gameBuildChecked) return;
            Match m = Regex.Match(raw, @"Build\s+([0-9]+\.[0-9]+)");
            if (!m.Success) return;
            gameBuildChecked = true;
            detectedGameBuild = m.Groups[1].Value;
            try
            {
                Log("GameBuild: detected game internal build " + detectedGameBuild);
                string manifest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOChinese_Installed.json");
                if (!File.Exists(manifest)) return;
                string json = File.ReadAllText(manifest, Encoding.UTF8);
                Match tm = Regex.Match(json, "\"game_build\"\\s*:\\s*\"([0-9.]+)\"");
                string target = tm.Success ? tm.Groups[1].Value : "";
                if (target.Length == 0)
                {
                    Log("GameBuild: 安装清单未记录适配构建号（旧版安装包）——无法比对。");
                }
                else if (target != detectedGameBuild)
                {
                    Log("GameBuild: 警告！游戏已更新到 " + detectedGameBuild + "，本汉化适配的是 " + target
                        + " —— 部分界面可能退回英文或行为异常，请等待对应版本的汉化补丁。");
                }
            }
            catch (Exception ex)
            {
                Log("GameBuild check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 消息日志行识别：只处理确实是游戏日志模板拼出来的形态（R31/T5）。
        /// </summary>
        private static bool LooksLikeMessageLogLine(string s)
        {
            if (s.IndexOf("'s Endgame Report", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("now platform-classified", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("has been destroyed!", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf(" vanished", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf(" spoo", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (s.IndexOf("jamme", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Regex.IsMatch(s, @"^[Nn]ew (air|surface|submarine|weapon|ground|underwater) contact \(", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(s, @"^[Nn]ew contact!? Designated ", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 消息日志行的模板化翻译：只翻“框架”，武器/单位型号名保持原样；术语沿用词典
        /// 已有译法（终局报告 / 平台现已分类 / 新空中目标接触 / 未能欺骗 / 已被摧毁 …）。
        /// 返回 null 表示不是已知形态（调用方继续走后续通道）。
        /// </summary>
        private static string TranslateMessageLogLine(string s)
        {
            Match m;

            m = Regex.Match(s, @"^(.{1,80}?) #(\d+)'s Endgame Report (HIT|MISS)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                bool hit = m.Groups[3].Value.Equals("HIT", StringComparison.OrdinalIgnoreCase);
                return m.Groups[1].Value + " #" + m.Groups[2].Value + " 的终局报告：" + (hit ? "命中" : "脱靶");
            }

            m = Regex.Match(s, @"^(.{1,60}?) #(\d+) now platform-classified:\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value + " #" + m.Groups[2].Value + " 平台现已分类：" + m.Groups[3].Value.Trim();
            }

            m = Regex.Match(s, @"^[Nn]ew (air|surface|submarine|weapon|ground|underwater) contact \((.{1,40}?) #(\d+)\)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string kind = m.Groups[1].Value.ToLowerInvariant();
                string head = kind == "air" ? "新空中目标接触" : kind == "surface" ? "新水面目标接触"
                            : kind == "submarine" ? "新潜艇目标接触" : kind == "ground" ? "新地面目标接触"
                            : kind == "underwater" ? "新水下目标接触" : "新建武器接触";
                return head + "（" + m.Groups[2].Value + " #" + m.Groups[3].Value + "）";
            }

            m = Regex.Match(s, @"^(.{1,80}?) #(\d+) vanished$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value + " #" + m.Groups[2].Value + " 已消失";
            }

            // 诱饵/干扰：左边必是带 [] 的干扰物名，右边是导引头，形态很窄，不会吃简报长句。
            m = Regex.Match(s, @"^(.{2,60}?\])\s+(spoofed|failed to spoof)\s+(.{2,40}?)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                bool failed = m.Groups[2].Value.StartsWith("failed", StringComparison.OrdinalIgnoreCase);
                return m.Groups[1].Value + (failed ? " 未能欺骗 " : " 欺骗了 ") + m.Groups[3].Value;
            }

            m = Regex.Match(s, @"^(.{1,120}?) has been destroyed!$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value + " 已被摧毁！";
            }

            m = Regex.Match(s, @"^[Nn]ew contact!? Designated (.{1,60}?) #(\d+) - Detected by (.{1,60}?)\s*\[Sensors:\s*(.{1,60}?)\]\s*at\s*(.{1,40})$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string tail = Regex.Replace(m.Groups[5].Value.Trim(), @"(\d+(?:\.\d+)?)\s*deg", "$1°");
                return "新接触目标！标识编号：" + m.Groups[1].Value + " #" + m.Groups[2].Value
                     + " - 探测方式：" + m.Groups[3].Value.Trim()
                     + " [传感器：" + m.Groups[4].Value.Trim() + "] 方位 " + tail;
            }

            m = Regex.Match(s, @"^(.{1,60}?) jammed (.{2,40}?) successfully$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value + " 成功干扰 " + m.Groups[2].Value;
            }

            return null;
        }

        private static string DoTranslate(string raw)
        {
            if (IsForbiddenCodeOrEnum(raw)) return raw;

            // 0. 特别消息内嵌 HTML 文档标签转中文摘要: [LOADDOC]xxx.html[/LOADDOC]
            if (raw.Contains("[LOADDOC]") && raw.Contains("[/LOADDOC]"))
            {
                string trDoc = TranslateLoadDocTag(raw);
                if (!string.IsNullOrEmpty(trDoc) && trDoc != raw) return trDoc;
            }

            // 1. Exact match
            if (exactMap.ContainsKey(raw)) return exactMap[raw];

            // 2. Trimmed match
            string trimmed = raw.Trim();
            if (exactMap.ContainsKey(trimmed))
            {
                return raw.Replace(trimmed, exactMap[trimmed]);
            }

            // 2.1 Normalized whitespace exact match
            string normRaw = Regex.Replace(trimmed, @"\s+", " ");
            if (normExactMap.ContainsKey(normRaw))
            {
                return normExactMap[normRaw];
            }

            // 2.2 动态组合串：游戏运行时拼装，词典无法枚举穷尽（R31 实测：条令值 / 出动架次 / 燃油罐行）。
            //      位置在全部“整串查表”之后 —— 已有词条的整串永远优先，规则只处理未知形态。
            if (!ContainsChinese(raw))
            {
                // 条令值：“Inherited, Always Fire” / “Inherited, Bingo 60%” / “Inherited, 5%”
                if (normRaw.StartsWith("Inherited, ", StringComparison.Ordinal))
                {
                    string tailPart = normRaw.Substring(11).Trim();
                    string trTail = LookupPiece(tailPart);
                    if (trTail == null && Regex.IsMatch(tailPart, @"^[\d.,%]+$"))
                    {
                        trTail = tailPart;   // 纯数字/百分比值直接沿用
                    }
                    if (trTail == null)
                    {
                        int lastSp = tailPart.LastIndexOf(' ');
                        if (lastSp > 0)
                        {
                            string headPart = tailPart.Substring(0, lastSp).Trim();
                            string numPart = tailPart.Substring(lastSp + 1).Trim();
                            string trHead = LookupPiece(headPart);
                            if (trHead != null && Regex.IsMatch(numPart, @"^[\d.,%]+$")) trTail = trHead + " " + numPart;
                        }
                    }
                    if (!string.IsNullOrEmpty(trTail)) return "继承自上级，" + trTail;
                }

                // 出动架次：“2 Sorties (Maximum)” / “3 Sorties”
                Match mSorties = Regex.Match(normRaw, @"^(\d+)\s+Sorties?\s*(?:\(([^()]+)\))?$");
                if (mSorties.Success)
                {
                    string sub = "";
                    if (mSorties.Groups[2].Success)
                    {
                        string trSub = LookupPiece(mSorties.Groups[2].Value.Trim());
                        sub = trSub != null ? "（" + trSub + "）" : " (" + mSorties.Groups[2].Value + ")";
                    }
                    return mSorties.Groups[1].Value + " 架次" + sub;
                }

                // 燃油罐行：“AvGas (200k Liter Underground Tank)”
                Match mFuel = Regex.Match(normRaw, @"^([A-Za-z][A-Za-z0-9 .\-]{1,24}?)\s*\((\d+(?:\.\d+)?k?)\s+Liters?\s+([^()]{2,40})\)$");
                if (mFuel.Success)
                {
                    string trKind = LookupPiece(mFuel.Groups[1].Value.Trim());
                    string trTank = LookupPiece(mFuel.Groups[3].Value.Trim());
                    if (trKind != null && trTank != null)
                    {
                        return trKind + "（" + mFuel.Groups[2].Value + " 升 " + trTank + "）";
                    }
                }

                // 2.3 消息日志动态消息（游戏按模板+参数拼装；R31/T5 实测的 9 类形态）。
                //      只处理“像日志行”的文本，且形态刻意收得很紧，避免误伤简报正文长句。
                if (normRaw.Length <= 200 && LooksLikeMessageLogLine(normRaw))
                {
                    string trLog = TranslateMessageLogLine(normRaw);
                    if (trLog != null && trLog != normRaw) return trLog;
                }

                // 2.4 数据库平台详情复合字段 (WRA、自主等级、座舱世代示例)
                if (normRaw.Contains(" (WRA: ") && normRaw.EndsWith(")"))
                {
                    int wraPos = normRaw.IndexOf(" (WRA: ");
                    string catPart = normRaw.Substring(0, wraPos).Trim();
                    string wraPart = normRaw.Substring(wraPos + " (WRA: ".Length).TrimEnd(')').Trim();
                    string trCat = LookupPiece(catPart);
                    string trWra = LookupPiece(wraPart);
                    if (trCat != null && trWra != null)
                    {
                        return trCat + " (WRA: " + trWra + ")";
                    }
                }

                Match mAuto = Regex.Match(normRaw, @"^(\d+)\s*\(Autonomy Level:\s*([A-Za-z0-9_]+)\)$");
                if (mAuto.Success)
                {
                    string lvl = mAuto.Groups[2].Value.Trim();
                    string trLvl = lvl;
                    if (lvl == "Undefined") trLvl = "未定义";
                    else if (lvl == "RemotelyPiloted") trLvl = "遥控驾驶";
                    else if (lvl == "SelfRecovering") trLvl = "自主恢复";
                    else if (lvl == "ChangeableMission") trLvl = "可变更任务";
                    else if (lvl == "FaultEventAdaptive") trLvl = "故障/事件自适应";
                    else if (lvl == "MultiVehicleCoordination") trLvl = "多机协同";
                    else if (lvl == "BattlespaceCognizant") trLvl = "战场态势感知";
                    else if (lvl == "FullyAutonomous") trLvl = "全自主";
                    else
                    {
                        string lk = LookupPiece(lvl);
                        if (lk != null) trLvl = lk;
                    }
                    return mAuto.Groups[1].Value + " (自主控制等级: " + trLvl + ")";
                }

                if (normRaw.Contains(" (ex:") && normRaw.EndsWith(")"))
                {
                    int exPos = normRaw.IndexOf(" (ex:");
                    string basePart = normRaw.Substring(0, exPos).Trim();
                    string exPart = normRaw.Substring(exPos + " (ex:".Length).TrimEnd(')').Trim();
                    string trBase = LookupPiece(basePart);
                    if (trBase != null)
                    {
                        string trEx = string.IsNullOrEmpty(exPart) ? "" : LookupPiece(exPart);
                        if (string.IsNullOrEmpty(exPart)) return trBase + " (示例: )";
                        if (trEx != null) return trBase + " (示例: " + trEx + ")";
                        return trBase + " (示例: " + exPart + ")";
                    }
                }

                // 数量前缀：1x / 2x / 4x 等装备与武器物资行（如 "1x Generic Comms Jammer Pod [Advanced]"）
                Match mQty = Regex.Match(normRaw, @"^(\d+\s*[xX×]\s*)(.+)$");
                if (mQty.Success)
                {
                    string pfx = mQty.Groups[1].Value;
                    string itemPart = mQty.Groups[2].Value.Trim();
                    string trItem = LookupPiece(itemPart);
                    if (trItem != null && trItem != itemPart)
                    {
                        return pfx + trItem;
                    }
                }

                // 通用「前缀 (正文)」组合：两侧都有整串词条且都不含中文时才组译（防误译/叠译）。
                Match mPair = Regex.Match(normRaw, @"^([^()\r\n]{2,60}?)\s*\(([^()\r\n]{2,60})\)$");
                if (mPair.Success)
                {
                    string left = mPair.Groups[1].Value.Trim();
                    string right = mPair.Groups[2].Value.Trim();
                    string trLeft = LookupPiece(left);
                    string trRight = LookupPiece(right);
                    if (trLeft != null && trRight != null
                        && !IsForbiddenCodeOrEnum(trLeft) && !IsForbiddenCodeOrEnum(trRight))
                    {
                        return trLeft + "（" + trRight + "）";
                    }
                }
            }

            // 2.12 多行消息（消息日志/教程弹窗按行组织）：逐行查表后重新拼装。
            //      整段查表必然落空，按标点切分的字符占比阈值又会被长行拖低。
            if (raw.IndexOf('\n') >= 0 && !ContainsChinese(raw))
            {
                string[] lines = raw.Replace("\r\n", "\n").Split('\n');
                StringBuilder rebuilt = new StringBuilder();
                int nonEmpty = 0, hit = 0;
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    string trimmedLine = line.Trim();
                    if (li > 0) rebuilt.Append('\n');
                    if (trimmedLine.Length == 0) continue;
                    nonEmpty++;
                    string trLine = TranslateString(trimmedLine);
                    if (trLine == trimmedLine)
                    {
                        string aLine = trimmedLine.Replace('\u2019', '\'').Replace('\u2018', '\'');
                        string nLine = Regex.Replace(aLine, @"\s+", " ");
                        if (exactMap.ContainsKey(aLine)) trLine = exactMap[aLine];
                        else if (normExactMap.ContainsKey(nLine)) trLine = normExactMap[nLine];
                    }
                    if (trLine != trimmedLine) hit++;
                    int lead = line.Length - line.TrimStart().Length;
                    if (lead > 0) rebuilt.Append(line.Substring(0, lead));
                    rebuilt.Append(trLine);
                }
                if (nonEmpty > 0 && hit * 100 / nonEmpty >= 40)
                {
                    return rebuilt.ToString();
                }
            }

            // 2.15 撇号形态差异：词条用 ’ 而渲染文本用 '（或相反）时整句查不中
            string aposAscii = raw.Replace('\u2019', '\'').Replace('\u2018', '\'');
            if (aposAscii != raw)
            {
                if (exactMap.ContainsKey(aposAscii)) return exactMap[aposAscii];
                string nAscii = Regex.Replace(aposAscii.Trim(), @"\s+", " ");
                if (normExactMap.ContainsKey(nAscii)) return normExactMap[nAscii];
            }
            if (raw.IndexOf('\'') >= 0)
            {
                string aposCurly = raw.Replace("'", "\u2019");
                if (exactMap.ContainsKey(aposCurly)) return exactMap[aposCurly];
                string nCurly = Regex.Replace(aposCurly.Trim(), @"\s+", " ");
                if (normExactMap.ContainsKey(nCurly)) return normExactMap[nCurly];
            }

            string bracketRes = TranslateBracketSegments(raw);
            if (bracketRes != raw) return bracketRes;

            // 2.2 消息日志/简报正文由多个段落直接拼接，段间分隔空格被吞掉
            //     ("hit us.We expect ...armed with:AA-7")。按标点边界重新切分后
            //     逐段查表，整体命中率达到阈值才返回，避免返回半成品英文。
            if (raw.Length >= 40 && !ContainsChinese(raw))
            {
                string spaced = Regex.Replace(raw, @"([.?!])(?=[A-Z0-9])", "$1 ");
                spaced = Regex.Replace(spaced, @"([:])(?=[A-Z][a-z])", "$1 ");
                if (spaced != raw)
                {
                    string[] segs = Regex.Split(spaced, @"(?<=[.?!:])\s+");
                    if (segs.Length > 1)
                    {
                        StringBuilder rebuilt = new StringBuilder();
                        int totalLen = 0, translatedLen = 0;
                        for (int si = 0; si < segs.Length; si++)
                        {
                            string seg = segs[si].Trim();
                            if (seg.Length == 0) continue;
                            totalLen += seg.Length;
                            string trSeg = TranslateString(seg);
                            if (trSeg != seg) translatedLen += seg.Length;
                            if (rebuilt.Length > 0) rebuilt.Append(' ');
                            rebuilt.Append(trSeg);
                        }
                        if (totalLen > 0 && translatedLen * 100 / totalLen >= 25)
                        {
                            return rebuilt.ToString();
                        }
                    }
                }
            }
            if (normRaw.EndsWith(":") || normRaw.EndsWith("："))
            {
                string bare = normRaw.Substring(0, normRaw.Length - 1).Trim();
                if (normExactMap.ContainsKey(bare))
                {
                    return normExactMap[bare] + "：";
                }
            }

            // 3. Match with shortcut & prefix e.g. "&File" -> "文件(&F)" or "File (&F)"
            if (trimmed.StartsWith("&") && trimmed.Length > 1)
            {
                string noAmp = trimmed.Substring(1);
                if (exactMap.ContainsKey(noAmp))
                {
                    return exactMap[noAmp] + "(&" + trimmed[1] + ")";
                }
            }
            if (trimmed.EndsWith(")") && trimmed.Length > 4 && trimmed[trimmed.Length - 4] == '(' && trimmed[trimmed.Length - 3] == '&')
            {
                string baseText = trimmed.Substring(0, trimmed.Length - 4).Trim();
                if (exactMap.ContainsKey(baseText))
                {
                    return exactMap[baseText] + trimmed.Substring(trimmed.Length - 4);
                }
            }

            // 4. Match with trailing ellipsis e.g. "Open..." -> "打开..."
            if (trimmed.EndsWith("...") && trimmed.Length > 3)
            {
                string baseText = trimmed.Substring(0, trimmed.Length - 3).Trim();
                if (exactMap.ContainsKey(baseText))
                {
                    return exactMap[baseText] + "...";
                }
            }

            // 4.1 Match numbered prefix e.g. "1. Blue Dawn" -> "1. 蓝色拂晓"
            int dotIdx = trimmed.IndexOf('.');
            if (dotIdx > 0 && dotIdx < 5)
            {
                string numPrefix = trimmed.Substring(0, dotIdx + 1);
                string rest = trimmed.Substring(dotIdx + 1).Trim();
                if (exactMap.ContainsKey(rest))
                {
                    return numPrefix + " " + exactMap[rest];
                }
                if (exactMap.ContainsKey(trimmed))
                {
                    return exactMap[trimmed];
                }
            }

            // 5. Enclosed brackets e.g. "[Selected Unit]" -> "[当前选中单位]"
            if (trimmed.Length >= 3)
            {
                if ((trimmed.StartsWith("[") && trimmed.EndsWith("]")) ||
                    (trimmed.StartsWith("(") && trimmed.EndsWith(")")) ||
                    (trimmed.StartsWith("{") && trimmed.EndsWith("}")))
                {
                    char openChar = trimmed[0];
                    char closeChar = trimmed[trimmed.Length - 1];
                    string inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    string innerTrans = DoTranslate(inner);
                    if (innerTrans != inner)
                    {
                        return openChar + innerTrans + closeChar;
                    }
                }
            }

            // 6. Colon composite. Only for label-like keys: a long sentence that
            // happens to contain a colon must not be torn in half.
            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0 && colonIdx < trimmed.Length - 1 && colonIdx <= 40)
            {
                string left = trimmed.Substring(0, colonIdx).Trim();
                string right = trimmed.Substring(colonIdx + 1).Trim();
                if (left.Length <= 32 && right.Length <= 64)
                {
                    string transLeft = DoTranslate(left);
                    string transRight = DoTranslate(right);
                    if (transLeft != left && transRight != right)
                    {
                        return transLeft + "：" + transRight;
                    }
                    if (transLeft != left)
                    {
                        return transLeft + "：" + right;
                    }
                }
            }

            // 7. Dash composite "A - B". Same guard: never split prose.
            int dashIdx = trimmed.IndexOf(" - ");
            if (dashIdx > 0 && dashIdx < trimmed.Length - 3)
            {
                string left = trimmed.Substring(0, dashIdx).Trim();
                string right = trimmed.Substring(dashIdx + 3).Trim();
                if (left.Length <= 32 && right.Length <= 48 &&
                    left.IndexOf(' ') < 0 && right.IndexOf(' ') < 0)
                {
                    string transLeft = DoTranslate(left);
                    string transRight = DoTranslate(right);
                    if (transLeft != left || transRight != right)
                    {
                        return transLeft + " - " + transRight;
                    }
                }
            }

            // 8. No word-level composition. Word-by-word assembly produced garbled
            // mixed-language output and corrupted UI text; every display string is
            // translated as a whole through exactMap or the offset string pool.
            return raw;
        }

        private static void LoadDictionaries(string jsonPath)
        {
            try
            {
                string jsonText = File.ReadAllText(jsonPath, Encoding.UTF8);
                ParseJsonSections(jsonText);
            }
            catch (Exception ex)
            {
                Log("Error loading dictionaries: " + ex.ToString());
            }
        }

        private static void ParseJsonSections(string text)
        {
            // Section keys must be located at the TOP level of the root object.
            // A plain IndexOf would match `_meta.counts.offsets` instead and shift
            // every section boundary, silently loading zero entries.
            int idxOffsets = FindTopLevelKey(text, "offsets");
            int idxExact = FindTopLevelKey(text, "exact");
            int idxPartial = FindTopLevelKey(text, "partial");
            int idxWords = FindTopLevelKey(text, "words");

            List<SectionPos> sections = new List<SectionPos>();
            if (idxOffsets >= 0) sections.Add(new SectionPos { Name = "offsets", Pos = idxOffsets });
            if (idxExact >= 0) sections.Add(new SectionPos { Name = "exact", Pos = idxExact });
            if (idxPartial >= 0) sections.Add(new SectionPos { Name = "partial", Pos = idxPartial });
            if (idxWords >= 0) sections.Add(new SectionPos { Name = "words", Pos = idxWords });

            sections.Sort((a, b) => a.Pos.CompareTo(b.Pos));

            for (int i = 0; i < sections.Count; i++)
            {
                int start = sections[i].Pos;
                int end = (i + 1 < sections.Count) ? sections[i + 1].Pos : text.Length;
                string block = text.Substring(start, end - start);
                string name = sections[i].Name;

                ParsePairs(block, name == "offsets", name == "exact", name == "partial", name == "words");
            }
        }

        private class SectionPos
        {
            public string Name;
            public int Pos;
        }

        /// <summary>
        /// Return the index of a quoted key that sits directly inside the root
        /// object (depth 1), or -1. Nested containers such as `_meta.counts` are
        /// at depth 2 and therefore never match.
        /// </summary>
        private static int FindTopLevelKey(string text, string key)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    int j = i + 1;
                    bool matches = j + key.Length < text.Length
                        && text[j + key.Length] == '"'
                        && string.CompareOrdinal(text, j, key, 0, key.Length) == 0;
                    if (matches && depth == 1)
                    {
                        return i;
                    }
                    // Skip the whole string literal, honouring escapes.
                    i = j;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '"') break;
                        i++;
                    }
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return -1;
        }

        private static void ParsePairs(string block, bool isOffset, bool isExact, bool isPartial, bool isWords)
        {
            int p = 0;
            while (p < block.Length)
            {
                // Find start quote of key
                int keyStart = -1;
                while (p < block.Length)
                {
                    if (block[p] == '\"') { keyStart = p + 1; p++; break; }
                    p++;
                }
                if (keyStart < 0 || p >= block.Length) break;

                // Find closing quote of key (handling escaped quotes)
                StringBuilder sbKey = new StringBuilder();
                while (p < block.Length)
                {
                    if (block[p] == '\\' && p + 1 < block.Length)
                    {
                        char c = block[p + 1];
                        if (c == '"') sbKey.Append('"');
                        else if (c == '\\') sbKey.Append('\\');
                        else if (c == '/') sbKey.Append('/');
                        else if (c == 'b') sbKey.Append('\b');
                        else if (c == 'f') sbKey.Append('\f');
                        else if (c == 'n') sbKey.Append('\n');
                        else if (c == 'r') sbKey.Append('\r');
                        else if (c == 't') sbKey.Append('\t');
                        else if (c == 'u' && p + 5 < block.Length)
                        {
                            int code;
                            if (int.TryParse(block.Substring(p + 2, 4),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sbKey.Append((char)code);
                                p += 6;
                                continue;
                            }
                            sbKey.Append('\\'); sbKey.Append(c);
                        }
                        else { sbKey.Append('\\'); sbKey.Append(c); }
                        p += 2;
                    }
                    else if (block[p] == '\"')
                    {
                        p++;
                        break;
                    }
                    else
                    {
                        sbKey.Append(block[p]);
                        p++;
                    }
                }
                string key = sbKey.ToString();

                // Skip until ':'
                while (p < block.Length && block[p] != ':') p++;
                if (p >= block.Length) break;
                p++; // skip ':'

                // Skip whitespace until value quote or open brace/bracket
                while (p < block.Length && block[p] != '\"' && block[p] != '{' && block[p] != '[') p++;
                if (p >= block.Length || block[p] != '\"') continue;
                p++; // skip open quote of value

                // Find closing quote of value
                StringBuilder sbVal = new StringBuilder();
                while (p < block.Length)
                {
                    if (block[p] == '\\' && p + 1 < block.Length)
                    {
                        char c = block[p + 1];
                        if (c == '"') sbVal.Append('"');
                        else if (c == '\\') sbVal.Append('\\');
                        else if (c == '/') sbVal.Append('/');
                        else if (c == 'b') sbVal.Append('\b');
                        else if (c == 'f') sbVal.Append('\f');
                        else if (c == 'n') sbVal.Append('\n');
                        else if (c == 'r') sbVal.Append('\r');
                        else if (c == 't') sbVal.Append('\t');
                        else if (c == 'u' && p + 5 < block.Length)
                        {
                            int code;
                            if (int.TryParse(block.Substring(p + 2, 4),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sbVal.Append((char)code);
                                p += 6;
                                continue;
                            }
                            sbVal.Append('\\'); sbVal.Append(c);
                        }
                        else { sbVal.Append('\\'); sbVal.Append(c); }
                        p += 2;
                    }
                    else if (block[p] == '\"')
                    {
                        p++;
                        break;
                    }
                    else
                    {
                        sbVal.Append(block[p]);
                        p++;
                    }
                }
                string val = sbVal.ToString();

                if (key == "offsets" || key == "exact" || key == "partial" || key == "words") continue;

                if (isOffset)
                {
                    int off;
                    if (int.TryParse(key, out off))
                    {
                        offsetMap[off] = val;
                    }
                }
                else if (isExact)
                {
                    exactMap[key] = val;
                    string nKey = Regex.Replace(key.Trim(), @"\s+", " ");
                    if (nKey.Length > 0 && !normExactMap.ContainsKey(nKey))
                    {
                        normExactMap[nKey] = val;
                    }
                }
                else if (isPartial)
                {
                    partialMap[key] = val;
                }
                else if (isWords)
                {
                    wordsMap[key] = val;
                }
            }
        }

        private static readonly HashSet<string> liveAuditSeen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object auditLock = new object();
        private static string auditLogPath = "";
        // 游戏内部版本哨兵（见 NoteGameBuild）：只在首次拿到标题串时比对一次。
        private static string detectedGameBuild = "";
        private static bool gameBuildChecked = false;

        private static void AuditUntranslatedLive(string raw)
        {
            try
            {
                if (string.IsNullOrEmpty(raw) || raw.Length < 2 || raw.Length > 300) return;
                if (ContainsChinese(raw)) return;
                if (raw.Contains("\\") || raw.Contains("://") || raw.Contains(".dll") || raw.Contains(".exe") || raw.Contains(".db3") || raw.Contains(".scen") || raw.Contains(".png")) return;

                bool hasAlpha = false;
                for (int i = 0; i < raw.Length; i++)
                {
                    if ((raw[i] >= 'A' && raw[i] <= 'Z') || (raw[i] >= 'a' && raw[i] <= 'z'))
                    {
                        hasAlpha = true;
                        break;
                    }
                }
                if (!hasAlpha) return;

                lock (auditLock)
                {
                    if (liveAuditSeen.Add(raw))
                    {
                        if (string.IsNullOrEmpty(auditLogPath))
                        {
                            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                            auditLogPath = Path.Combine(baseDir, "CMOLocalizer_LiveAudit.log");
                        }
                        File.AppendAllText(auditLogPath, raw.Replace("\r", "\\r").Replace("\n", "\\n") + "\r\n", Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        private static string JsonEsc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static MenuStrip FindMainMenuStrip(out Form ownerForm)
        {
            ownerForm = null;
            FormCollection forms = Application.OpenForms;
            if (forms == null) return null;
            foreach (Form frm in forms)
            {
                if (frm == null || frm.IsDisposed) continue;
                if (frm.MainMenuStrip != null)
                {
                    ownerForm = frm;
                    return frm.MainMenuStrip;
                }
                foreach (Control c in frm.Controls)
                {
                    if (c is MenuStrip)
                    {
                        ownerForm = frm;
                        return (MenuStrip)c;
                    }
                }
            }
            return null;
        }

        private static void DumpMenuItemJson(ToolStripItem item, int idx, StringBuilder sb)
        {
            sb.Append("{\"index\":").Append(idx)
              .Append(",\"name\":\"").Append(JsonEsc(item.Name))
              .Append("\",\"text\":\"").Append(JsonEsc(item.Text))
              .Append("\",\"type\":\"").Append(JsonEsc(item.GetType().Name))
              .Append("\",\"enabled\":").Append(item.Enabled ? "true" : "false")
              .Append(",\"visible\":").Append(item.Visible ? "true" : "false");

            ToolStripDropDownItem ddi = item as ToolStripDropDownItem;
            if (ddi != null && ddi.DropDownItems != null && ddi.DropDownItems.Count > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < ddi.DropDownItems.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    DumpMenuItemJson(ddi.DropDownItems[i], i, sb);
                }
                sb.Append("]");
            }
            sb.Append("}");
        }

        private static ToolStripItem FindMenuItemByText(ToolStripItemCollection items, string target)
        {
            if (items == null) return null;
            foreach (ToolStripItem item in items)
            {
                if (item == null) continue;
                string cleanText = (item.Text ?? "").Replace("&", "").Trim();
                if (string.Equals(cleanText, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
                ToolStripDropDownItem ddi = item as ToolStripDropDownItem;
                if (ddi != null && ddi.HasDropDownItems)
                {
                    ToolStripItem sub = FindMenuItemByText(ddi.DropDownItems, target);
                    if (sub != null) return sub;
                }
            }
            foreach (ToolStripItem item in items)
            {
                if (item == null) continue;
                string cleanText = (item.Text ?? "").Replace("&", "").Trim();
                if (cleanText.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return item;
                }
                ToolStripDropDownItem ddi = item as ToolStripDropDownItem;
                if (ddi != null && ddi.HasDropDownItems)
                {
                    ToolStripItem sub = FindMenuItemByText(ddi.DropDownItems, target);
                    if (sub != null) return sub;
                }
            }
            return null;
        }

        private static void CollectControlTexts(Control ctrl, List<string> list)
        {
            if (ctrl == null || ctrl.IsDisposed) return;
            if (!string.IsNullOrEmpty(ctrl.Text))
            {
                list.Add(ctrl.GetType().Name + ":" + ctrl.Name + "=" + ctrl.Text);
            }
            if (ctrl is TabControl)
            {
                foreach (TabPage tp in ((TabControl)ctrl).TabPages)
                {
                    list.Add("TabPage:" + tp.Name + "=" + tp.Text);
                    CollectControlTexts(tp, list);
                }
            }
            else if (ctrl is DataGridView)
            {
                foreach (DataGridViewColumn col in ((DataGridView)ctrl).Columns)
                {
                    list.Add("DGVCol:" + col.Name + "=" + col.HeaderText);
                }
            }
            else if (ctrl is ListView)
            {
                foreach (ColumnHeader ch in ((ListView)ctrl).Columns)
                {
                    list.Add("LVCol:" + ch.Text);
                }
            }
            else if (ctrl is ToolStrip)
            {
                foreach (ToolStripItem tsi in ((ToolStrip)ctrl).Items)
                {
                    list.Add("TSItem:" + tsi.Name + "=" + tsi.Text);
                }
            }
            foreach (Control child in ctrl.Controls)
            {
                CollectControlTexts(child, list);
            }
        }

        private static void ProcessTestCommands()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cmdFile = Path.Combine(baseDir, "CMOLocalizer_Cmd.txt");
            if (!File.Exists(cmdFile)) return;

            string rawCmd = "";
            try
            {
                rawCmd = File.ReadAllText(cmdFile, Encoding.UTF8).Trim();
                File.Delete(cmdFile);
            }
            catch (Exception ex)
            {
                Log("ProcessTestCommands read error: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(rawCmd)) return;
            string resFile = Path.Combine(baseDir, "CMOLocalizer_CmdResult.json");

            try
            {
                string response = ExecuteTestCommand(rawCmd);
                File.WriteAllText(resFile, response, Encoding.UTF8);
                Log("ProcessTestCommands executed: " + rawCmd);
            }
            catch (Exception ex)
            {
                string err = "{\"status\":\"error\",\"message\":\"" + JsonEsc(ex.Message) + "\"}";
                try { File.WriteAllText(resFile, err, Encoding.UTF8); } catch { }
                Log("ProcessTestCommands exception: " + ex.ToString());
            }
        }

        private static string ExecuteTestCommand(string rawCmd)
        {
            string cmd = rawCmd;
            string arg = "";
            int sp = rawCmd.IndexOf(' ');
            if (sp > 0)
            {
                cmd = rawCmd.Substring(0, sp).Trim().ToUpperInvariant();
                arg = rawCmd.Substring(sp + 1).Trim();
            }
            else
            {
                cmd = rawCmd.Trim().ToUpperInvariant();
            }

            if (cmd == "RELOAD_DICT")
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dictPath = Path.Combine(baseDir, "CMOLocalizer.json");
                if (File.Exists(dictPath))
                {
                    exactMap.Clear();
                    normExactMap.Clear();
                    lock (cacheLock) { dynamicCache.Clear(); }
                    LoadDictionaries(dictPath);
                    return "{\"status\":\"ok\",\"exact\":" + exactMap.Count + ",\"offsets\":" + offsetMap.Count + "}";
                }
                return "{\"status\":\"error\",\"message\":\"CMOLocalizer.json not found\"}";
            }

            Form mainFrm;
            MenuStrip ms = FindMainMenuStrip(out mainFrm);

            if (cmd == "DUMP_MENUS")
            {
                if (ms == null) return "{\"status\":\"error\",\"message\":\"MenuStrip not found\"}";
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"status\":\"ok\",\"menus\":[");
                for (int i = 0; i < ms.Items.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    DumpMenuItemJson(ms.Items[i], i, sb);
                }
                sb.Append("]}");
                return sb.ToString();
            }

            if (cmd == "SHOW_MENU")
            {
                if (ms == null || mainFrm == null) return "{\"status\":\"error\",\"message\":\"MenuStrip not found\"}";
                int idx = int.Parse(arg);
                string menuText = "";
                mainFrm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        foreach (ToolStripItem it in ms.Items)
                        {
                            ToolStripDropDownItem d = it as ToolStripDropDownItem;
                            if (d != null) d.HideDropDown();
                        }
                        if (idx >= 0 && idx < ms.Items.Count)
                        {
                            ToolStripDropDownItem target = ms.Items[idx] as ToolStripDropDownItem;
                            if (target != null)
                            {
                                TranslateToolStripItem(target);
                                target.ShowDropDown();
                            }
                        }
                    }
                    catch { }
                });
                if (idx >= 0 && idx < ms.Items.Count) menuText = ms.Items[idx].Text;
                return "{\"status\":\"ok\",\"action\":\"SHOW_MENU\",\"index\":" + idx + ",\"text\":\"" + JsonEsc(menuText) + "\"}";
            }

            if (cmd == "SHOW_SUBMENU")
            {
                if (ms == null || mainFrm == null) return "{\"status\":\"error\",\"message\":\"MenuStrip not found\"}";
                string[] parts = arg.Split(':');
                int pIdx = int.Parse(parts[0]);
                int cIdx = int.Parse(parts[1]);
                string subText = "";
                if (pIdx >= 0 && pIdx < ms.Items.Count)
                {
                    ToolStripDropDownItem pItem = ms.Items[pIdx] as ToolStripDropDownItem;
                    if (pItem != null && cIdx >= 0 && cIdx < pItem.DropDownItems.Count)
                    {
                        subText = pItem.DropDownItems[cIdx].Text;
                    }
                }
                mainFrm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        foreach (ToolStripItem it in ms.Items)
                        {
                            ToolStripDropDownItem d = it as ToolStripDropDownItem;
                            if (d != null) d.HideDropDown();
                        }
                        if (pIdx >= 0 && pIdx < ms.Items.Count)
                        {
                            ToolStripDropDownItem pItem = ms.Items[pIdx] as ToolStripDropDownItem;
                            if (pItem != null)
                            {
                                TranslateToolStripItem(pItem);
                                pItem.ShowDropDown();
                                if (cIdx >= 0 && cIdx < pItem.DropDownItems.Count)
                                {
                                    ToolStripDropDownItem cItem = pItem.DropDownItems[cIdx] as ToolStripDropDownItem;
                                    if (cItem != null)
                                    {
                                        TranslateToolStripItem(cItem);
                                        cItem.Select();
                                        cItem.ShowDropDown();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                });
                return "{\"status\":\"ok\",\"action\":\"SHOW_SUBMENU\",\"parent\":" + pIdx + ",\"child\":" + cIdx + ",\"text\":\"" + JsonEsc(subText) + "\"}";
            }

            if (cmd == "HIDE_MENUS")
            {
                if (ms != null && mainFrm != null)
                {
                    mainFrm.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            foreach (ToolStripItem it in ms.Items)
                            {
                                ToolStripDropDownItem d = it as ToolStripDropDownItem;
                                if (d != null) d.HideDropDown();
                            }
                        }
                        catch { }
                    });
                }
                return "{\"status\":\"ok\",\"action\":\"HIDE_MENUS\"}";
            }

            if (cmd == "CLICK_MENU")
            {
                if (ms == null || mainFrm == null) return "{\"status\":\"error\",\"message\":\"MenuStrip not found\"}";
                ToolStripItem found = FindMenuItemByText(ms.Items, arg);
                if (found == null) return "{\"status\":\"error\",\"message\":\"Menu item not found: " + JsonEsc(arg) + "\"}";
                string foundText = found.Text;
                string foundName = found.Name;
                mainFrm.BeginInvoke((MethodInvoker)delegate
                {
                    try { found.PerformClick(); } catch { }
                });
                return "{\"status\":\"ok\",\"action\":\"CLICK_MENU\",\"name\":\"" + JsonEsc(foundName) + "\",\"text\":\"" + JsonEsc(foundText) + "\"}";
            }

            if (cmd == "DUMP_OPEN_FORMS")
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"status\":\"ok\",\"forms\":[");
                FormCollection forms = Application.OpenForms;
                bool first = true;
                if (forms != null)
                {
                    foreach (Form f in forms)
                    {
                        if (f == null || f.IsDisposed) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        List<string> ctrls = new List<string>();
                        try { CollectControlTexts(f, ctrls); } catch { }
                        sb.Append("{\"name\":\"").Append(JsonEsc(f.Name))
                          .Append("\",\"type\":\"").Append(JsonEsc(f.GetType().FullName))
                          .Append("\",\"text\":\"").Append(JsonEsc(f.Text))
                          .Append("\",\"visible\":").Append(f.Visible ? "true" : "false")
                          .Append(",\"hwnd\":").Append(f.Handle.ToInt64())
                          .Append(",\"bounds\":\"").Append(f.Bounds.ToString())
                          .Append("\",\"controls\":[");
                        for (int i = 0; i < ctrls.Count && i < 200; i++)
                        {
                            if (i > 0) sb.Append(",");
                            sb.Append("\"").Append(JsonEsc(ctrls[i])).Append("\"");
                        }
                        sb.Append("]}");
                    }
                }
                sb.Append("]}");
                return sb.ToString();
            }

            if (cmd == "WPF_TEXTLOG")
            {
                return DumpWpfLog();
            }

            if (cmd.StartsWith("CTRL_TREE"))
            {
                // 必须传原始命令：过滤词在第二个 token 里，而 cmd 只有首 token（大写）——
                // 传 cmd 会让 filter 永远是空串，「CTRL_TREE <窗体过滤>」静默退化成全量导出。
                return HandleCtrlTree(rawCmd);
            }

            if (cmd == "CLOSE_FORM")
            {
                FormCollection forms = Application.OpenForms;
                int closedCount = 0;
                if (forms != null)
                {
                    for (int i = forms.Count - 1; i >= 0; i--)
                    {
                        Form f = forms[i];
                        if (f == null || f.IsDisposed || f == mainFrm) continue;
                        if (arg == "ALL" || f.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 || f.Text.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                f.BeginInvoke((MethodInvoker)delegate { try { f.Close(); } catch { } });
                                closedCount++;
                            }
                            catch { }
                        }
                    }
                }
                return "{\"status\":\"ok\",\"closed\":" + closedCount + "}";
            }

            if (cmd == "CLICK_BUTTON")
            {
                FormCollection forms = Application.OpenForms;
                string clickedInfo = null;
                if (forms != null)
                {
                    foreach (Form f in forms)
                    {
                        if (f == null || f.IsDisposed || !f.Visible) continue;
                        Button btn = FindButtonByTextOrName(f, arg);
                        if (btn != null)
                        {
                            string bName = btn.Name;
                            string bText = btn.Text;
                            f.BeginInvoke((MethodInvoker)delegate
                            {
                                try { btn.PerformClick(); } catch { }
                            });
                            clickedInfo = f.Name + "." + bName + "(" + bText + ")";
                            break;
                        }
                        ToolStripItem tsi = FindToolStripItemInControl(f, arg);
                        if (tsi != null)
                        {
                            string tName = tsi.Name;
                            string tText = tsi.Text;
                            f.BeginInvoke((MethodInvoker)delegate
                            {
                                try { tsi.PerformClick(); } catch { }
                            });
                            clickedInfo = f.Name + ".ToolStripItem." + tName + "(" + tText + ")";
                            break;
                        }
                    }
                }
                if (clickedInfo != null)
                {
                    return "{\"status\":\"ok\",\"clicked\":\"" + JsonEsc(clickedInfo) + "\"}";
                }
                return "{\"status\":\"error\",\"message\":\"Button not found: " + JsonEsc(arg) + "\"}";
            }

            if (cmd == "SELECT_TAB")
            {
                // SELECT_TAB <窗体过滤> <索引或页签标题片段>：只读切页签（UI 线程），
                // 用于逐页签采控件文本（不点确定/应用，不写任何设置）。
                string filter = arg;
                string want = "";
                int tabSp = arg.LastIndexOf(' ');
                if (tabSp > 0)
                {
                    filter = arg.Substring(0, tabSp).Trim();
                    want = arg.Substring(tabSp + 1).Trim();
                }
                Form tabForm = null;
                TabControl tabCtl = null;
                FormCollection tabForms = Application.OpenForms;
                if (tabForms != null)
                {
                    foreach (Form f in tabForms)
                    {
                        if (f == null || f.IsDisposed) continue;
                        if (filter.Length > 0 &&
                            (f.Text ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            (f.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        TabControl tc = FindTabControlIn(f);
                        if (tc == null) continue;
                        tabForm = f;
                        tabCtl = tc;
                        break;
                    }
                }
                if (tabCtl == null)
                {
                    return "{\"status\":\"error\",\"message\":\"TabControl not found: " + JsonEsc(filter) + "\"}";
                }
                int wantIdx = -1;
                StringBuilder tabNames = new StringBuilder();
                for (int i = 0; i < tabCtl.TabPages.Count; i++)
                {
                    string tn = tabCtl.TabPages[i].Text ?? "";
                    if (i > 0) tabNames.Append(",");
                    tabNames.Append("\"").Append(JsonEsc(tn)).Append("\"");
                    if (wantIdx < 0 && want.Length > 0 &&
                        tn.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) wantIdx = i;
                }
                if (wantIdx < 0)
                {
                    int parsedIdx;
                    if (int.TryParse(want, out parsedIdx)) wantIdx = parsedIdx;
                }
                if (wantIdx < 0 || wantIdx >= tabCtl.TabPages.Count)
                {
                    return "{\"status\":\"error\",\"message\":\"tab not found: " + JsonEsc(want) + "\",\"tabs\":[" + tabNames + "]}";
                }
                Form targetForm = tabForm;
                TabControl targetTab = tabCtl;
                int targetIdx = wantIdx;
                ManualResetEvent tabDone = new ManualResetEvent(false);
                try
                {
                    targetForm.BeginInvoke((MethodInvoker)delegate
                    {
                        try { targetTab.SelectedIndex = targetIdx; } catch { }
                        try { tabDone.Set(); } catch { }
                    });
                }
                catch { }
                tabDone.WaitOne(3000);
                return "{\"status\":\"ok\",\"action\":\"SELECT_TAB\",\"form\":\"" + JsonEsc(targetForm.Name)
                     + "\",\"index\":" + wantIdx + ",\"text\":\"" + JsonEsc(tabCtl.TabPages[wantIdx].Text ?? "")
                     + "\",\"tabs\":[" + tabNames + "]}";
            }

            if (cmd == "RUN_LUA")
            {
                try
                {
                    Type tLua = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        tLua = a.GetType("Command_Core.Lua.LuaSandBox");
                        if (tLua != null) break;
                    }
                    if (tLua != null)
                    {
                        MethodInfo mSing = tLua.GetMethod("Singleton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        object sandBox = mSing != null ? mSing.Invoke(null, null) : null;
                        if (sandBox != null)
                        {
                            MethodInfo mRun = tLua.GetMethod("RunScript", new Type[] { typeof(string), typeof(bool), typeof(string), typeof(string) });
                            if (mRun != null)
                            {
                                object res = mRun.Invoke(sandBox, new object[] { arg, true, "", "" });
                                return "{\"status\":\"ok\",\"action\":\"RUN_LUA\"}";
                            }
                        }
                    }
                    return "{\"status\":\"error\",\"message\":\"LuaSandBox not available\"}";
                }
                catch (Exception ex)
                {
                    return "{\"status\":\"error\",\"message\":\"" + JsonEsc(ex.Message) + "\"}";
                }
            }

            if (cmd == "CLICK_TOOLBAR")
            {
                int targetIdx = 0;
                int.TryParse(arg, out targetIdx);
                ToolStripItem tsi = FindToolbarButtonByIndex(mainFrm, targetIdx);
                if (tsi != null && mainFrm != null)
                {
                    mainFrm.BeginInvoke((MethodInvoker)delegate
                    {
                        try { tsi.PerformClick(); } catch { }
                    });
                    return "{\"status\":\"ok\",\"action\":\"CLICK_TOOLBAR\",\"index\":" + targetIdx + "}";
                }
                return "{\"status\":\"error\",\"message\":\"Toolbar button not found: " + arg + "\"}";
            }

            if (cmd == "DB_INSPECT" || cmd == "DB_SELECT_ROW")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";

                FieldInfo fHtml = dbForm.GetType().GetField("RenderedHTMLString", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fId = dbForm.GetType().GetField("SelectedObjectID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fName = dbForm.GetType().GetField("SelectedObjectName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                // 混淆字段名每次游戏更新都会变（R31）：名字查不到时按「带 SelectedIndices 的控件字段」结构兜底。
                FieldInfo fList = dbForm.GetType().GetField("dXkHcikCGK6", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fList == null)
                {
                    foreach (FieldInfo fi in dbForm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (!typeof(Control).IsAssignableFrom(fi.FieldType)) continue;
                        object ctlVal = null;
                        try { ctlVal = fi.GetValue(dbForm); }
                        catch (Exception) { }
                        if (ctlVal != null && ctlVal.GetType().GetProperty("SelectedIndices") != null)
                        {
                            fList = fi;
                            break;
                        }
                    }
                    Log("DB_SELECT_ROW: list field by name not found; structural fallback -> " + (fList != null ? fList.Name : "(none)"));
                }
                PropertyInfo piSOT = dbForm.GetType().GetProperty("SelectedObjectType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                string sotVal = piSOT != null ? piSOT.GetValue(dbForm, null) as string : "(null)";
                MethodInfo mDisplay = dbForm.GetType().GetMethod("DisplaySelection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (cmd == "DB_SELECT_ROW")
                {
                    int row = 0;
                    int.TryParse(arg, out row);
                    dbForm.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (fList != null)
                            {
                                object lv = fList.GetValue(dbForm);
                                if (lv != null)
                                {
                                    PropertyInfo piSel = lv.GetType().GetProperty("SelectedIndices");
                                    if (piSel != null)
                                    {
                                        var selList = piSel.GetValue(lv, null) as System.Collections.IList;
                                        if (selList != null)
                                        {
                                            selList.Clear();
                                            selList.Add(row);
                                        }
                                    }
                                }
                            }
                            if (mDisplay != null)
                            {
                                var pars = mDisplay.GetParameters();
                                if (pars.Length == 0) mDisplay.Invoke(dbForm, null);
                                else if (pars.Length == 2) mDisplay.Invoke(dbForm, new object[] { false, false });
                            }
                        }
                        catch (Exception ex) { Log("DB_SELECT_ROW error: " + ex.Message); }
                    });
                    return "{\"status\":\"ok\",\"action\":\"DB_SELECT_ROW\",\"row\":" + row + ",\"sot\":\"" + JsonEsc(sotVal) + "\"}";
                }

                string html = fHtml != null ? fHtml.GetValue(dbForm) as string : "";
                int objId = fId != null ? (int)fId.GetValue(dbForm) : -1;
                string objName = fName != null ? fName.GetValue(dbForm) as string : "";
                return "{\"status\":\"ok\",\"id\":" + objId + ",\"name\":\"" + JsonEsc(objName) + "\",\"sot\":\"" + JsonEsc(sotVal) + "\",\"html_len\":" + (html != null ? html.Length : 0) + ",\"html_sample\":\"" + JsonEsc(html != null ? html.Substring(0, Math.Min(350, html.Length)) : "") + "\"}";
            }

            if (cmd == "DB_NAVIGATE_ID")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";

                string[] parts = arg.Split(' ');
                string objType = parts.Length > 0 ? parts[0] : "Sensor";
                string objIdStr = parts.Length > 1 ? parts[1] : "1438";

                dbForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        PropertyInfo piCb = dbForm.GetType().GetProperty("CB_ObjectType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (piCb != null)
                        {
                            ComboBox cb = piCb.GetValue(dbForm, null) as ComboBox;
                            if (cb != null)
                            {
                                for (int i = 0; i < cb.Items.Count; i++)
                                {
                                    if (cb.Items[i].ToString().IndexOf(objType, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        cb.SelectedIndex = i;
                                        break;
                                    }
                                }
                            }
                        }
                        PropertyInfo piTb = dbForm.GetType().GetProperty("DBIDGOTO", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (piTb != null)
                        {
                            Control tb = piTb.GetValue(dbForm, null) as Control;
                            if (tb != null) tb.Text = objIdStr;
                        }
                        PropertyInfo piBtn = dbForm.GetType().GetProperty("SearchDBIDButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (piBtn != null)
                        {
                            Button btn = piBtn.GetValue(dbForm, null) as Button;
                            if (btn != null) btn.PerformClick();
                        }
                    }
                    catch (Exception ex) { Log("DB_NAVIGATE_ID error: " + ex.Message); }
                });
                return "{\"status\":\"ok\",\"action\":\"DB_NAVIGATE_ID\",\"type\":\"" + objType + "\",\"id\":\"" + objIdStr + "\"}";
            }

            if (cmd == "DB_COMBO_DIAG" || cmd == "DB_HTML_DUMP")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";

                if (cmd == "DB_HTML_DUMP")
                {
                    FieldInfo fHtmlDump = dbForm.GetType().GetField("RenderedHTMLString", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    string h = fHtmlDump != null ? fHtmlDump.GetValue(dbForm) as string : "";
                    string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer_DBHTML.html");
                    try { File.WriteAllText(outPath, h ?? "", Encoding.UTF8); } catch { }
                    return "{\"status\":\"ok\",\"action\":\"DB_HTML_DUMP\",\"len\":" + (h != null ? h.Length : 0) + "}";
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"status\":\"ok\",\"combos\":[");
                List<ComboBox> combos = new List<ComboBox>();
                foreach (FieldInfo fi in dbForm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(ComboBox).IsAssignableFrom(fi.FieldType)) continue;
                    ComboBox cbf = fi.GetValue(dbForm) as ComboBox;
                    if (cbf != null && !combos.Contains(cbf)) combos.Add(cbf);
                }
                bool first = true;
                foreach (ComboBox c in combos)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"name\":\"" + JsonEsc(c.Name) + "\",\"type\":\"" + JsonEsc(c.GetType().FullName) + "\",\"dropDownStyle\":\"" + c.DropDownStyle + "\",\"drawMode\":\"" + c.DrawMode + "\",\"bound\":" + (c.DataSource != null ? "true" : "false") + ",\"displayMember\":\"" + JsonEsc(c.DisplayMember) + "\",\"valueMember\":\"" + JsonEsc(c.ValueMember) + "\",\"hooked\":" + (hookedDBCombos.Contains(c.GetHashCode()) ? "true" : "false") + ",\"text\":\"" + JsonEsc(c.Text) + "\",\"translatedText\":\"" + JsonEsc(TranslateDBComboItem(c.Text)) + "\",\"selectedIndex\":" + c.SelectedIndex + ",\"itemCount\":" + c.Items.Count + ",\"items\":[");
                    int n = Math.Min(c.Items.Count, 120);
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sb.Append(",");
                        object it = c.Items[i];
                        sb.Append("{\"idx\":" + i + ",\"cls\":\"" + JsonEsc(it != null ? it.GetType().FullName : "null") + "\",\"toString\":\"" + JsonEsc(it != null ? it.ToString() : "") + "\",\"itemText\":\"" + JsonEsc(c.GetItemText(it)) + "\",\"translated\":\"" + JsonEsc(TranslateDBComboItem(c.GetItemText(it))) + "\"}");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                return sb.ToString();
            }

            if (cmd == "DB_DOM_DUMP")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";

                Control wv = null;
                foreach (FieldInfo fi in dbForm.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (fi.FieldType.Name.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    wv = fi.GetValue(dbForm) as Control;
                    if (wv != null) break;
                }
                if (wv == null) return "{\"status\":\"error\",\"message\":\"WebView2 not found\"}";

                string outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer_DBDOM.json");
                Control wvTarget = wv;
                dbForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        PropertyInfo pc = wvTarget.GetType().GetProperty("CoreWebView2");
                        object core = pc != null ? pc.GetValue(wvTarget, null) : null;
                        if (core == null) { File.WriteAllText(outFile, "CoreWebView2 null", Encoding.UTF8); return; }
                        MethodInfo exec = core.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                        if (exec == null) { File.WriteAllText(outFile, "ExecuteScriptAsync not found", Encoding.UTF8); return; }

                        string jsDump = @"(function(){
                            try {
                                var r = {rs: document.readyState, hasBody: !!document.body, htmlLen: -1, textLen: -1, nodes: 0, sample: ''};
                                if (document.body) {
                                    r.htmlLen = document.body.innerHTML.length;
                                    try { r.textLen = document.body.innerText ? document.body.innerText.length : -1; } catch(e2) { r.textLen = -9; }
                                    var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
                                    var n, c = 0, s = [];
                                    while ((n = w.nextNode())) {
                                        var p = n.parentNode ? n.parentNode.tagName : '';
                                        if (p === 'SCRIPT' || p === 'STYLE') continue;
                                        var v = n.nodeValue;
                                        if (v && v.trim().length > 0) { c++; if (s.length < 60) s.push(v.substring(0, 160)); }
                                    }
                                    r.nodes = c;
                                    r.sample = s.join(' @@ ');
                                }
                                return JSON.stringify(r);
                            } catch(e) { return 'ERR:' + e; }
                        })()";;

                        System.Threading.Tasks.Task<string> task = exec.Invoke(core, new object[] { jsDump }) as System.Threading.Tasks.Task<string>;
                        if (task == null) { File.WriteAllText(outFile, "no task", Encoding.UTF8); return; }
                        task.ContinueWith((t) =>
                        {
                            try
                            {
                                string raw = t.IsFaulted || t.IsCanceled
                                    ? ("TASKFAIL:" + (t.Exception != null ? t.Exception.Message : ""))
                                    : t.Result;
                                if (!string.IsNullOrEmpty(raw)) raw = raw.Trim('"');
                                File.WriteAllText(outFile, raw ?? "", Encoding.UTF8);
                            }
                            catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(outFile, "EX:" + ex.ToString(), Encoding.UTF8); } catch { }
                    }
                });
                return "{\"status\":\"ok\",\"action\":\"DB_DOM_DUMP\",\"file\":\"CMOLocalizer_DBDOM.json\"}";
            }

            // 只读探针：导出任意窗口里 WebView2 的文本 / 执行一段 JS（默认只读）。
            // 为什么需要它：阵营选择面板（ChooseSide）的「进入想定」按钮是 WebView2 里的 HTML 元素，
            // CTRL_TREE / WPF_DUMP 都看不到它，也没有 Win32 几何信息可供按坐标点击；
            // 引擎本身就在游戏进程里，直接在进程内操控该 WebView2 才是最可靠的路径。
            if (cmd == "WEBVIEW_TEXT" || cmd == "WEBVIEW_JS")
            {
                string[] wparts = arg.Split(new char[] { ' ' }, 2);
                string wfilter = wparts.Length > 0 ? wparts[0].Trim() : "";
                string wscript = cmd == "WEBVIEW_TEXT"
                    ? "document.body ? document.body.innerText : ''"
                    : (wparts.Length > 1 ? wparts[1] : "");
                if (wscript.Length == 0) return "{\"status\":\"error\",\"message\":\"no script\"}";
                Form whost = null;
                FormCollection wforms = Application.OpenForms;
                if (wforms != null)
                {
                    foreach (Form f in wforms)
                    {
                        if (f == null || f.IsDisposed) continue;
                        if (wfilter.Length > 0
                            && (f.Text ?? "").IndexOf(wfilter, StringComparison.OrdinalIgnoreCase) < 0
                            && (f.Name ?? "").IndexOf(wfilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        whost = f;
                        break;
                    }
                }
                // 注意：这里只选窗体（读 Form.Text/Name 在后台线程是被容的）；
                // WebView2 控件本身的搜索也必须放到 UI 线程（见下），
                // 否则在后台读它的 Controls 同样会抛 TargetInvocationException。
                if (whost == null) return "{\"status\":\"error\",\"message\":\"form not found\"}";
                // WebView2 的控件属性与 ExecuteScriptAsync 都必须在 UI 线程上碰；
                // 在后台线程读 CoreWebView2 会抛 TargetInvocationException（已实测），
                // 所以整段（找子控件 / 取 CoreWebView2 / 发起脚本）都放进 BeginInvoke，
                // 只在后台等 Task 结果（UI 线程在跑消息泵，所以不会死锁）。
                object started = null;
                string werr = null;
                whost.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        Control wvv = FindWebView2In(whost);
                        if (wvv == null) { werr = "WebView2 not found in form"; return; }
                        PropertyInfo pc = wvv.GetType().GetProperty("CoreWebView2");
                        object cr = pc != null ? pc.GetValue(wvv, null) : null;
                        if (cr == null) { werr = "CoreWebView2 null"; return; }
                        MethodInfo me = wvv.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                        if (me == null) me = cr.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                        if (me == null) { werr = "ExecuteScriptAsync not found"; return; }
                        object tg = me.DeclaringType.IsAssignableFrom(wvv.GetType()) ? (object)wvv : cr;
                        started = me.Invoke(tg, new object[] { wscript });
                    }
                    catch (Exception ex) { werr = ex.GetBaseException().Message; }
                });
                for (int wi = 0; wi < 100 && started == null && werr == null; wi++) System.Threading.Thread.Sleep(50);
                if (werr != null) return "{\"status\":\"error\",\"message\":\"" + JsonEsc(werr) + "\"}";
                System.Threading.Tasks.Task<string> wtask = started as System.Threading.Tasks.Task<string>;
                if (wtask == null) return "{\"status\":\"error\",\"message\":\"ExecuteScriptAsync did not start\"}";
                try { wtask.Wait(8000); } catch { }
                if (wtask.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                    return "{\"status\":\"error\",\"message\":\"script failed or timed out\"}";
                string wres = wtask.Result ?? "";
                if (wres.Length > 20000) wres = wres.Substring(0, 20000);
                return "{\"status\":\"ok\",\"action\":\"" + cmd + "\",\"form\":\"" + JsonEsc(whost.Name)
                    + "\",\"result\":\"" + JsonEsc(wres) + "\"}";
            }

            if (cmd == "DB_CTRL_TREE")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"status\":\"ok\",\"tree\":[");
                bool firstNode = true;
                CollectControlTree(dbForm, 0, sb, ref firstNode);
                sb.Append("]}");
                return sb.ToString();
            }

            // 只读探针：导出「加载想定」窗口控件树（输入保护回归用）。不得改写任何控件。
            if (cmd == "SCEN_CTRL_TREE")
            {
                Form scenForm = FindLoadScenarioForm();
                if (scenForm == null) return "{\"status\":\"error\",\"message\":\"LoadScenario window not open\"}";
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"status\":\"ok\",\"tree\":[");
                bool firstNode = true;
                CollectControlTree(scenForm, 0, sb, ref firstNode);
                sb.Append("]}");
                return sb.ToString();
            }

            if (cmd == "DB_SET_FILTER")
            {
                Form dbForm = FindDBViewerForm();
                if (dbForm == null) return "{\"status\":\"error\",\"message\":\"InternalDBViewer not open\"}";
                string[] parts = arg.Split(' ');
                string cName = parts[0];
                string val = parts.Length > 1 ? parts[1] : "";
                dbForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        Action<Control> searchAndSet = null;
                        searchAndSet = (root) =>
                        {
                            foreach (Control c in root.Controls)
                            {
                                ComboBox cb = c as ComboBox;
                                if (cb != null && cb.Name.IndexOf(cName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    for (int i = 0; i < cb.Items.Count; i++)
                                    {
                                        string itText = cb.GetItemText(cb.Items[i]);
                                        if (itText.IndexOf(val, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            cb.SelectedIndex = i;
                                            return;
                                        }
                                    }
                                }
                                if (c.HasChildren) searchAndSet(c);
                            }
                        };
                        searchAndSet(dbForm);
                    }
                    catch (Exception ex) { Log("DB_SET_FILTER error: " + ex.Message); }
                });
                return "{\"status\":\"ok\",\"action\":\"DB_SET_FILTER\",\"target\":\"" + cName + "\",\"val\":\"" + val + "\"}";
            }

            // XLATE <text> — exercise the real translation chain for one arbitrary string.
            if (cmd == "XLATE")
            {
                string s = arg;
                string viaString = TranslateString(s);
                string viaBriefing = TranslateBriefingText(s);
                return "{\"status\":\"ok\",\"len\":" + s.Length
                    + ",\"string\":\"" + JsonEsc(viaString) + "\""
                    + ",\"briefing\":\"" + JsonEsc(viaBriefing) + "\"}";
            }

            if (cmd == "SCEN_EXPAND_ALL" || cmd == "SCEN_SELECT" || cmd == "SCEN_LOAD")
            {
                Form loadScenForm = null;
                Control dtvCtrl = null;
                Button btnLoad = null;

                FormCollection forms = Application.OpenForms;
                if (forms != null)
                {
                    foreach (Form f in forms)
                    {
                        if (f == null || f.IsDisposed) continue;
                        if (f.GetType().Name.IndexOf("LoadScenario", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("LoadScenario", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            loadScenForm = f;
                            dtvCtrl = FindControlByTypeOrName(f, "DarkTreeView");
                            btnLoad = FindControlByTypeOrName(f, "Button2") as Button;
                            break;
                        }
                    }
                }

                if (loadScenForm == null) return "{\"status\":\"error\",\"message\":\"LoadScenario window not open\"}";

                if (cmd == "SCEN_LOAD")
                {
                    if (btnLoad != null)
                    {
                        loadScenForm.BeginInvoke((MethodInvoker)delegate
                        {
                            try { btnLoad.PerformClick(); } catch { }
                        });
                        return "{\"status\":\"ok\",\"action\":\"SCEN_LOAD\"}";
                    }
                    return "{\"status\":\"error\",\"message\":\"Load button not found\"}";
                }

                if (dtvCtrl == null) return "{\"status\":\"error\",\"message\":\"DarkTreeView not found in LoadScenario\"}";

                string matchFound = null;
                loadScenForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        TranslateDarkTreeView(dtvCtrl);
                        PropertyInfo piNodes = dtvCtrl.GetType().GetProperty("Nodes");
                        if (piNodes != null)
                        {
                            var nodes = piNodes.GetValue(dtvCtrl, null) as System.Collections.IEnumerable;
                            if (nodes != null)
                            {
                                if (cmd == "SCEN_EXPAND_ALL")
                                {
                                    ExpandAllDarkNodes(nodes);
                                    TranslateDarkTreeView(dtvCtrl);
                                    dtvCtrl.Invalidate();
                                }
                                else if (cmd == "SCEN_SELECT")
                                {
                                    matchFound = SelectDarkNodeRecursive(dtvCtrl, nodes, arg);
                                    TranslateDarkTreeView(dtvCtrl);
                                    dtvCtrl.Invalidate();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("SCEN command error: " + ex.Message);
                    }
                });

                return "{\"status\":\"ok\",\"action\":\"" + cmd + "\",\"query\":\"" + JsonEsc(arg) + "\"}";
            }

            if (cmd == "DROP_DOWN")
            {
                string want = arg.Trim();
                ComboBox hit = null;
                string ownerName = "";
                FormCollection fc2 = Application.OpenForms;
                if (fc2 != null)
                {
                    foreach (Form f3 in fc2)
                    {
                        if (f3 == null || f3.IsDisposed || !f3.Visible) continue;
                        foreach (Control c3 in AllControls(f3))
                        {
                            ComboBox c3b = c3 as ComboBox;
                            if (c3b == null) continue;
                            if (want.Length == 0 || c3b.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                hit = c3b;
                                ownerName = f3.GetType().Name;
                                break;
                            }
                        }
                        if (hit != null) break;
                    }
                }
                if (hit == null) return "{\"status\":\"error\",\"message\":\"combo not found: " + JsonEsc(want) + "\"}";
                ComboBox target = hit;
                Form ownerForm = target.FindForm();
                if (ownerForm != null)
                {
                    ownerForm.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            LocalizeComboBoxSafe(target);
                            target.DroppedDown = true;
                            target.Invalidate();
                        }
                        catch { }
                    });
                }
                return "{\"status\":\"ok\",\"action\":\"DROP_DOWN\",\"form\":\"" + JsonEsc(ownerName)
                    + "\",\"combo\":\"" + JsonEsc(target.Name) + "\",\"text\":\"" + JsonEsc(target.Text) + "\"}";
            }

            if (cmd == "POOL_DUMP")
            {
                return HandlePoolDump();
            }

            if (cmd == "WPF_DUMP" || cmd == "DUMP_COMBOS" || cmd == "DUMP_LISTS" || cmd == "XLATE_BATCH")
            {
                if (cmd == "XLATE_BATCH")
                {
                    List<string> inputs = new List<string>();
                    try
                    {
                        string raw = File.ReadAllText(arg, Encoding.UTF8);
                        int p = 0;
                        while (true)
                        {
                            int q1 = raw.IndexOf('"', p);
                            if (q1 < 0) break;
                            StringBuilder item = new StringBuilder();
                            p = q1 + 1;
                            while (p < raw.Length)
                            {
                                char ch = raw[p];
                                if (ch == '\\' && p + 1 < raw.Length)
                                {
                                    char nx = raw[p + 1];
                                    if (nx == 'n') item.Append('\n');
                                    else if (nx == 'r') item.Append('\r');
                                    else if (nx == 't') item.Append('\t');
                                    else if (nx == 'u' && p + 5 < raw.Length)
                                    {
                                        int code;
                                        if (int.TryParse(raw.Substring(p + 2, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                                        {
                                            item.Append((char)code);
                                            p += 4;
                                        }
                                    }
                                    else item.Append(nx);
                                    p += 2;
                                    continue;
                                }
                                if (ch == '"') { p++; break; }
                                item.Append(ch);
                                p++;
                            }
                            inputs.Add(item.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        return "{\"status\":\"error\",\"message\":\"" + JsonEsc(ex.Message) + "\"}";
                    }

                    StringBuilder jb = new StringBuilder();
                    jb.Append("{\"status\":\"ok\",\"count\":" + inputs.Count + ",\"items\":[");
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        if (i > 0) jb.Append(",");
                        string src = inputs[i];
                        jb.Append("{\"in\":\"" + JsonEsc(src) + "\"");
                        jb.Append(",\"string\":\"" + JsonEsc(TranslateString(src)) + "\"");
                        jb.Append(",\"briefing\":\"" + JsonEsc(TranslateBriefingText(src)) + "\"}");
                    }
                    jb.Append("]}");
                    return jb.ToString();
                }

                if (cmd == "DUMP_COMBOS")
                {
                    StringBuilder jb = new StringBuilder();
                    jb.Append("{\"status\":\"ok\",\"combos\":[");
                    bool first = true;
                    List<Form> forms = new List<Form>();
                    FormCollection fc = Application.OpenForms;
                    if (fc != null) foreach (Form f in fc) if (f != null && !f.IsDisposed) forms.Add(f);
                    foreach (Form f2 in forms)
                    {
                        try
                        {
                            CollectCombos(f2, jb, ref first);
                        }
                        catch { }
                    }
                    jb.Append("]}");
                    return jb.ToString();
                }

                // DUMP_LISTS：导出所有 ListBox / CheckedListBox / ListView 的条目（只读）。
                // 为什么需要：DUMP_COMBOS 只盖下拉框，而「想定特性与设置」的特性列表、事件编辑器、
                // 参考点管理器等是列表控件；它们的条目不在控件 .Text 里，不导出就会漏查。
                if (cmd == "DUMP_LISTS")
                {
                    StringBuilder lb2 = new StringBuilder();
                    lb2.Append("{\"status\":\"ok\",\"lists\":[");
                    bool lbFirst = true;
                    FormCollection lfc = Application.OpenForms;
                    if (lfc != null)
                    {
                        foreach (Form lf in lfc)
                        {
                            if (lf == null || lf.IsDisposed) continue;
                            try { CollectLists(lf, lb2, ref lbFirst); } catch { }
                        }
                    }
                    lb2.Append("]}");
                    return lb2.ToString();
                }

                // WPF_DUMP: visual tree of every WPF window owned by this process.
                StringBuilder wpfJson = new StringBuilder();
                wpfJson.Append("{\"status\":\"ok\",\"windows\":[");
                bool wpfFirst = true;
                if (currentPid == 0) currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        uint pid2;
                        GetWindowThreadProcessId(hWnd, out pid2);
                        if (pid2 != currentPid) return true;
                        StringBuilder sb = new StringBuilder(256);
                        GetClassName(hWnd, sb, sb.Capacity);
                        if (!sb.ToString().StartsWith("HwndWrapper")) return true;
                        System.Windows.Interop.HwndSource src2 = System.Windows.Interop.HwndSource.FromHwnd(hWnd);
                        if (src2 == null || src2.Dispatcher == null || src2.RootVisual == null) return true;
                        StringBuilder tree = new StringBuilder();
                        bool tFirst = true;
                        src2.Dispatcher.Invoke(new Action(delegate
                        {
                            DumpWpfTree(src2.RootVisual, 0, tree, ref tFirst);
                        }));
                        if (!wpfFirst) wpfJson.Append(",");
                        wpfFirst = false;
                        StringBuilder wt = new StringBuilder(256);
                        GetWindowText(hWnd, wt, wt.Capacity);
                        wpfJson.Append("{\"hwnd\":\"" + hWnd.ToString() + "\",\"title\":\"" + JsonEsc(wt.ToString()) + "\",\"nodes\":[" + tree + "]}");
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                wpfJson.Append("]}");
                return wpfJson.ToString();
            }

            if (cmd == "CLICK_WPF")
            {
                if (currentPid == 0) currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                string want = arg.Trim();
                int clicked = 0;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        uint pid3;
                        GetWindowThreadProcessId(hWnd, out pid3);
                        if (pid3 != currentPid) return true;
                        StringBuilder sb = new StringBuilder(256);
                        GetClassName(hWnd, sb, sb.Capacity);
                        if (!sb.ToString().StartsWith("HwndWrapper")) return true;
                        System.Windows.Interop.HwndSource src3 = System.Windows.Interop.HwndSource.FromHwnd(hWnd);
                        if (src3 == null || src3.Dispatcher == null || src3.RootVisual == null) return true;
                        src3.Dispatcher.Invoke(new Action(delegate
                        {
                            try
                            {
                                clicked += ClickWpfButton(src3.RootVisual, want);
                            }
                            catch { }
                        }));
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                return "{\"status\":\"ok\",\"action\":\"CLICK_WPF\",\"clicked\":" + clicked + ",\"query\":\"" + JsonEsc(want) + "\"}";
            }

            return "{\"status\":\"error\",\"message\":\"Unknown command: " + JsonEsc(cmd) + "\"}";
        }

        private static int ClickWpfButton(System.Windows.DependencyObject dep, string want)
        {
            int n = 0;
            if (dep == null) return 0;
            try
            {
                System.Windows.Controls.Button btn = dep as System.Windows.Controls.Button;
                if (btn != null)
                {
                    string content = btn.Content as string;
                    if (!string.IsNullOrEmpty(content) && content.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        btn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        n++;
                    }
                }
            }
            catch { }
            int count = 0;
            try { count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep); } catch { count = 0; }
            for (int i = 0; i < count; i++)
            {
                try { n += ClickWpfButton(System.Windows.Media.VisualTreeHelper.GetChild(dep, i), want); } catch { }
            }
            return n;
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            if (root == null) yield break;
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control sub in AllControls(c)) yield return sub;
            }
        }

        private static void CollectCombos(Control root, StringBuilder sb, ref bool first)
        {
            if (root == null || root.IsDisposed) return;
            ComboBox cb = root as ComboBox;
            if (cb != null)
            {
                if (!first) sb.Append(",");
                first = false;
                Form owner = null;
                try { owner = cb.FindForm(); } catch { }
                sb.Append("{\"form\":\"" + JsonEsc(owner != null ? owner.GetType().Name : "")
                    + "\",\"name\":\"" + JsonEsc(cb.Name)
                    + "\",\"type\":\"" + JsonEsc(cb.GetType().FullName)
                    + "\",\"text\":\"" + JsonEsc(cb.Text)
                    + "\",\"sel\":" + cb.SelectedIndex
                    + ",\"bound\":" + (cb.DataSource != null ? "true" : "false")
                    + ",\"items\":[");
                for (int i = 0; i < cb.Items.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"" + JsonEsc(cb.GetItemText(cb.Items[i])) + "\"");
                }
                sb.Append("]}");
            }
            foreach (Control child in root.Controls) CollectCombos(child, sb, ref first);
        }

        /// <summary>导出 ListBox / CheckedListBox / ListView 的条目（只读，供回归扫描用）。</summary>
        private static void CollectLists(Control root, StringBuilder sb, ref bool first)
        {
            if (root == null || root.IsDisposed) return;
            // 每个控件单独兜异常：一个控件读取失败不得连带丢掉同窗体其余列表
            // （历史：整棵子树被一个异常静默中止，输出 0 条 → 被误读成「窗口里没有列表」）。
            try { AppendListInfo(root, sb, ref first); } catch { }
            foreach (Control child in root.Controls)
            {
                try { CollectLists(child, sb, ref first); } catch { }
            }
        }

        private static void AppendListInfo(Control root, StringBuilder sb, ref bool first)
        {
            Form owner = null;
            try { owner = root.FindForm(); } catch { }
            string formName = owner != null ? owner.GetType().Name : "";
            ListBox lb = root as ListBox;
            if (lb != null)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"form\":\"").Append(JsonEsc(formName))
                  .Append("\",\"name\":\"").Append(JsonEsc(lb.Name))
                  .Append("\",\"type\":\"").Append(JsonEsc(lb.GetType().Name))
                  .Append("\",\"sel\":").Append(lb.SelectedIndex)
                  .Append(",\"items\":[");
                for (int i = 0; i < lb.Items.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(JsonEsc(Convert.ToString(lb.Items[i]))).Append("\"");
                }
                sb.Append("]}");
                return;
            }
            ListView lv = root as ListView;
            if (lv != null)
            {
                int selIdx = -1;
                try { if (lv.SelectedIndices.Count > 0) selIdx = lv.SelectedIndices[0]; } catch { }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"form\":\"").Append(JsonEsc(formName))
                  .Append("\",\"name\":\"").Append(JsonEsc(lv.Name))
                  .Append("\",\"type\":\"ListView\",\"sel\":").Append(selIdx)
                  .Append(",\"items\":[");
                for (int i = 0; i < lv.Items.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(JsonEsc(lv.Items[i].Text ?? "")).Append("\"");
                }
                sb.Append("]}");
                return;
            }
            // 名字像列表、但不是 WinForms ListBox/ListView（游戏自绘控件）也要如实登记，
            // 否则「0 条」会被当成「没有列表」而错过整块界面。
            string tn = root.GetType().Name;
            if (tn.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!first) sb.Append(",");
            first = false;
            string baseName = "";
            try { baseName = root.GetType().BaseType != null ? root.GetType().BaseType.Name : ""; } catch { }
            sb.Append("{\"form\":\"").Append(JsonEsc(formName))
              .Append("\",\"name\":\"").Append(JsonEsc(root.Name))
              .Append("\",\"type\":\"").Append(JsonEsc(tn))
              .Append("\",\"kind\":\"非 WinForms 列表（基类 ").Append(JsonEsc(baseName)).Append("）\",\"items\":[]}");
        }

        private static string GetWpfElementName(System.Windows.DependencyObject dep)
        {
            try
            {
                System.Windows.FrameworkElement fe = dep as System.Windows.FrameworkElement;
                if (fe != null && !string.IsNullOrEmpty(fe.Name)) return fe.Name;
                System.Windows.FrameworkContentElement fce = dep as System.Windows.FrameworkContentElement;
                if (fce != null && !string.IsNullOrEmpty(fce.Name)) return fce.Name;
            }
            catch { }
            return "";
        }

        private static void DumpWpfTree(System.Windows.DependencyObject dep, int depth, StringBuilder sb, ref bool first)
        {
            if (dep == null || depth > 24) return;
            string typeName = dep.GetType().Name;
            string text = "";
            try
            {
                System.Windows.Controls.TextBlock tb = dep as System.Windows.Controls.TextBlock;
                if (tb != null) text = tb.Text;
                System.Windows.Controls.ContentControl cc = dep as System.Windows.Controls.ContentControl;
                if (cc != null && cc.Content is string) text = (string)cc.Content;
                System.Windows.Documents.Run run = dep as System.Windows.Documents.Run;
                if (run != null) text = run.Text;
                System.Windows.Controls.RichTextBox rtb = dep as System.Windows.Controls.RichTextBox;
                if (rtb != null) text = "[FlowDocument paragraphs=" + rtb.Document.Blocks.Count + "]";
            }
            catch { }
            if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"d\":" + depth + ",\"type\":\"" + JsonEsc(typeName) + "\",\"name\":\"" + JsonEsc(GetWpfElementName(dep)) + "\",\"text\":\"" + JsonEsc(text.Length > 160 ? text.Substring(0, 160) : text) + "\"}");
            }
            else if (depth <= 18)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"d\":" + depth + ",\"type\":\"" + JsonEsc(typeName) + "\",\"name\":\"" + JsonEsc(GetWpfElementName(dep)) + "\",\"text\":\"\"}");
            }
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep); } catch { n = 0; }
            for (int i = 0; i < n; i++)
            {
                try { DumpWpfTree(System.Windows.Media.VisualTreeHelper.GetChild(dep, i), depth + 1, sb, ref first); } catch { }
            }
            if (n == 0)
            {
                // Documents are not Visual children; descend their logical tree.
                System.Windows.Documents.FlowDocument fd = null;
                try
                {
                    PropertyInfo piDoc = dep.GetType().GetProperty("Document", BindingFlags.Public | BindingFlags.Instance);
                    if (piDoc != null && piDoc.PropertyType == typeof(System.Windows.Documents.FlowDocument))
                        fd = piDoc.GetValue(dep, null) as System.Windows.Documents.FlowDocument;
                }
                catch { }
                if (fd != null)
                {
                    foreach (System.Windows.Documents.Block b in fd.Blocks)
                    {
                        System.Windows.Documents.Paragraph para = b as System.Windows.Documents.Paragraph;
                        if (para == null) continue;
                        foreach (System.Windows.Documents.Inline inl in para.Inlines)
                        {
                            System.Windows.Documents.Run r = inl as System.Windows.Documents.Run;
                            if (r == null) continue;
                            if (!first) sb.Append(",");
                            first = false;
                            string rt = r.Text;
                            sb.Append("{\"d\":" + (depth + 1) + ",\"type\":\"Run\",\"text\":\"" + JsonEsc(rt.Length > 160 ? rt.Substring(0, 160) : rt) + "\"}");
                        }
                    }
                }
            }
        }

        private static Control FindControlByTypeOrName(Control root, string target)
        {
            if (root == null || root.IsDisposed) return null;
            if (string.Equals(root.GetType().Name, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(root.Name, target, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            foreach (Control child in root.Controls)
            {
                Control found = FindControlByTypeOrName(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private static TabControl FindTabControlIn(Control root)
        {
            if (root == null || root.IsDisposed) return null;
            TabControl tc = root as TabControl;
            if (tc != null) return tc;
            foreach (Control child in root.Controls)
            {
                TabControl found = FindTabControlIn(child);
                if (found != null) return found;
            }
            return null;
        }

        private static Button FindButtonByTextOrName(Control root, string target)
        {
            if (root == null || root.IsDisposed) return null;
            if (root is Button)
            {
                Button b = (Button)root;
                if ((b.Text != null && b.Text.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (b.Name != null && b.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return b;
                }
            }
            foreach (Control child in root.Controls)
            {
                Button found = FindButtonByTextOrName(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private static ToolStripItem FindToolStripItemInControl(Control root, string target)
        {
            if (root == null || root.IsDisposed) return null;
            if (root is ToolStrip)
            {
                ToolStrip ts = (ToolStrip)root;
                ToolStripItem found = FindMenuItemByText(ts.Items, target);
                if (found != null) return found;
            }
            foreach (Control child in root.Controls)
            {
                ToolStripItem found = FindToolStripItemInControl(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private static ToolStripItem FindToolbarButtonByIndex(Control root, int targetBtnIndex)
        {
            if (root == null || root.IsDisposed) return null;
            if (root is ToolStrip && !(root is MenuStrip) && !(root is StatusStrip))
            {
                ToolStrip ts = (ToolStrip)root;
                int btnIdx = 0;
                for (int i = 0; i < ts.Items.Count; i++)
                {
                    if (ts.Items[i] is ToolStripButton)
                    {
                        if (btnIdx == targetBtnIndex) return ts.Items[i];
                        btnIdx++;
                    }
                }
            }
            foreach (Control child in root.Controls)
            {
                ToolStripItem found = FindToolbarButtonByIndex(child, targetBtnIndex);
                if (found != null) return found;
            }
            return null;
        }

        private static void ExpandAllDarkNodes(System.Collections.IEnumerable nodes)
        {
            if (nodes == null) return;
            foreach (object n in nodes)
            {
                if (n == null) continue;
                PropertyInfo piExp = n.GetType().GetProperty("Expanded");
                if (piExp != null) piExp.SetValue(n, true, null);
                PropertyInfo piCh = n.GetType().GetProperty("Nodes");
                if (piCh != null) ExpandAllDarkNodes(piCh.GetValue(n, null) as System.Collections.IEnumerable);
            }
        }

        private static string SelectDarkNodeRecursive(Control dtv, System.Collections.IEnumerable nodes, string target)
        {
            if (nodes == null || string.IsNullOrEmpty(target)) return null;
            foreach (object n in nodes)
            {
                if (n == null) continue;
                PropertyInfo piTxt = n.GetType().GetProperty("Text");
                string txt = piTxt != null ? piTxt.GetValue(n, null) as string : "";
                PropertyInfo piTag = n.GetType().GetProperty("Tag");
                string tag = piTag != null && piTag.GetValue(n, null) != null ? piTag.GetValue(n, null).ToString() : "";

                bool isMatch = (txt != null && txt.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (tag != null && tag.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);

                if (isMatch)
                {
                    // Expand all parents
                    object parent = n;
                    while (parent != null)
                    {
                        PropertyInfo piP = parent.GetType().GetProperty("ParentNode");
                        parent = piP != null ? piP.GetValue(parent, null) : null;
                        if (parent != null)
                        {
                            PropertyInfo piExp = parent.GetType().GetProperty("Expanded");
                            if (piExp != null) piExp.SetValue(parent, true, null);
                        }
                    }

                    // Select this node
                    PropertyInfo piSel = dtv.GetType().GetProperty("SelectedNodes");
                    if (piSel != null)
                    {
                        object selList = piSel.GetValue(dtv, null);
                        if (selList != null)
                        {
                            MethodInfo miClear = selList.GetType().GetMethod("Clear");
                            MethodInfo miAdd = selList.GetType().GetMethod("Add");
                            if (miClear != null) miClear.Invoke(selList, null);
                            if (miAdd != null) miAdd.Invoke(selList, new object[] { n });
                        }
                    }
                    return txt;
                }

                PropertyInfo piCh = n.GetType().GetProperty("Nodes");
                if (piCh != null)
                {
                    string subMatch = SelectDarkNodeRecursive(dtv, piCh.GetValue(n, null) as System.Collections.IEnumerable, target);
                    if (subMatch != null)
                    {
                        PropertyInfo piExp = n.GetType().GetProperty("Expanded");
                        if (piExp != null) piExp.SetValue(n, true, null);
                        return subMatch;
                    }
                }
            }
            return null;
        }
    }
}
