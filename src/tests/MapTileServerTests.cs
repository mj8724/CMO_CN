using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

// Runs in its own Framework process. It invokes only the local map service,
// never InitializeNewDomain, the game, an installer, or the updater.
class MapTileServerTests
{
    private static int port;
    private static string tilePath;

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static byte[] Request(string request, int timeout)
    {
        using (TcpClient client = new TcpClient())
        {
            client.ReceiveTimeout = timeout;
            client.SendTimeout = timeout;
            client.Connect(IPAddress.Loopback, port);
            using (NetworkStream stream = client.GetStream())
            using (MemoryStream result = new MemoryStream())
            {
                byte[] body = Encoding.ASCII.GetBytes(request + "\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                stream.Write(body, 0, body.Length);
                byte[] buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) result.Write(buffer, 0, read);
                return result.ToArray();
            }
        }
    }

    private static void CheckPng(byte[] response, bool requireInk)
    {
        string text = Encoding.ASCII.GetString(response);
        int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Require(end > 0 && text.StartsWith("HTTP/1.1 200 OK\r\n"), "Expected HTTP 200 PNG");
        int start = end + 4;
        Require(text.Substring(0, end).Contains("Content-Length: " + (response.Length - start)), "Content-Length differs");
        Require(response.Length > start + 8 && response[start] == 137 && response[start + 1] == 80 && response[start + 2] == 78 && response[start + 3] == 71, "PNG signature missing");
        using (MemoryStream bytes = new MemoryStream(response, start, response.Length - start))
        using (Bitmap image = new Bitmap(bytes))
        {
            Require(image.Width == 256 && image.Height == 256, "Tile dimensions differ");
            bool ink = false;
            if (requireInk)
            {
                for (int y = 0; y < 256 && !ink; y++)
                    for (int x = 0; x < 256 && !ink; x++) ink = image.GetPixel(x, y).A != 0;
                Require(ink, "Known Chinese landmark tile is entirely transparent");
            }
        }
    }

    private static string KnownTile(string baseDir, string set, int zoom)
    {
        using (BinaryReader reader = new BinaryReader(File.OpenRead(Path.Combine(baseDir, "WW/PlacenameSets/" + set + "/0000_0000.wwp")), Encoding.UTF8))
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                double lat = reader.ReadSingle(), lon = reader.ReadSingle();
                reader.ReadUInt32();
                if (Math.Abs(lat) > 70 || Math.Abs(lon) > 175 || name.Length == 0 || name[0] < 0x3400) continue;
                double n = Math.Pow(2, zoom), radians = lat * Math.PI / 180;
                int x = (int)Math.Floor((lon + 180) / 360 * n);
                int y = (int)Math.Floor((1 - Math.Log(Math.Tan(radians) + 1 / Math.Cos(radians)) / Math.PI) / 2 * n);
                Console.WriteLine("LANDMARK " + name + " z=" + zoom + " x=" + x + " y=" + y);
                return "/MapTiles/Labels/" + zoom + "/" + x + "/" + y + ".png";
            }
        }
        throw new InvalidOperationException("No suitable Chinese landmark fixture");
    }

    private static void Starvation()
    {
        CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        int minWorker, minIo, maxWorker, maxIo;
        ThreadPool.GetMinThreads(out minWorker, out minIo);
        ThreadPool.GetMaxThreads(out maxWorker, out maxIo);
        int workers = Environment.ProcessorCount;
        Require(ThreadPool.SetMinThreads(workers, minIo), "SetMinThreads failed");
        Require(ThreadPool.SetMaxThreads(workers, maxIo), "SetMaxThreads failed");
        ThreadPool.GetMaxThreads(out maxWorker, out maxIo);
        Require(maxWorker == workers, "Worker maximum did not take effect");
        using (ManualResetEvent release = new ManualResetEvent(false))
        using (CountdownEvent entered = new CountdownEvent(workers))
        using (CountdownEvent exited = new CountdownEvent(workers))
        {
            for (int i = 0; i < workers; i++) ThreadPool.QueueUserWorkItem(delegate
            {
                entered.Signal();
                release.WaitOne();
                exited.Signal();
            });
            bool responded = false;
            try
            {
                Require(entered.Wait(10000), "Not all pool blockers entered");
                int availableWorker, availableIo;
                ThreadPool.GetAvailableThreads(out availableWorker, out availableIo);
                Console.WriteLine("POOL max=" + maxWorker + " entered=" + workers + " available=" + availableWorker + " IOCPavailable=" + availableIo);
                Require(availableWorker == 0, "ThreadPool was not exhausted");
                try
                {
                    CheckPng(Request("GET " + tilePath + " HTTP/1.1", 1500), true);
                    responded = true;
                }
                catch (IOException ex) { Console.WriteLine("STARVED_READ " + ex.Message); }
            }
            finally
            {
                release.Set();
                Require(exited.Wait(10000), "Pool blockers did not exit");
            }
            CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
            Console.WriteLine("RECOVERY_AFTER_RELEASE 200 PNG");
            Require(responded, "starvation_http_response: timed out while worker pool was exhausted");
        }
    }

    private static void Protocol(string baseDir)
    {
        foreach (int zoom in new int[] { 3, 7, 9, 22 })
            CheckPng(Request("GET " + KnownTile(baseDir, zoom == 3 ? "Synoptic" : "Cities", zoom) + " HTTP/1.1", 3000), true);
        string[] invalid = { "/MapTiles/Labels/3/8/0.png", "/MapTiles/Labels/3/0/8.png", "/MapTiles/Labels/-1/0/0.png", "/MapTiles/Labels/23/0/0.png", "/MapTiles/Labels/999999999999/0/0.png", "/wrong/3/0/0.png", "/MapTiles/Labels/3/0/0.png/extra" };
        foreach (string path in invalid)
        {
            string response = Encoding.ASCII.GetString(Request("GET " + path + " HTTP/1.1", 3000));
            Require(response.StartsWith("HTTP/1.1 400 "), "invalid_request_accepted: " + path);
        }
        Require(Encoding.ASCII.GetString(Request("POST " + tilePath + " HTTP/1.1", 3000)).StartsWith("HTTP/1.1 400 "), "Non-GET request accepted");
    }

    private static void IdleClient()
    {
        using (TcpClient idle = new TcpClient())
        {
            idle.ReceiveTimeout = 2500;
            idle.Connect(IPAddress.Loopback, port);
            idle.GetStream().WriteByte((byte)'G');
            Thread.Sleep(100);
            CheckPng(Request("GET " + tilePath + " HTTP/1.1", 4000), true);
            try { Require(idle.GetStream().ReadByte() == -1, "Idle connection was not closed"); }
            catch (IOException) { throw new InvalidOperationException("idle_client_timeout: incomplete request remains open"); }
        }
    }

    private static void MultipleIdleClients()
    {
        CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        TcpClient[] idle = new TcpClient[3];
        try
        {
            for (int i = 0; i < idle.Length; i++)
            {
                idle[i] = new TcpClient();
                idle[i].Connect(IPAddress.Loopback, port);
                idle[i].GetStream().WriteByte((byte)'G');
            }
            Thread.Sleep(100);
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            try { CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true); }
            catch (IOException ex) { throw new InvalidOperationException("multi_idle_head_of_line: healthy GET blocked behind three incomplete requests", ex); }
            Console.WriteLine("MULTI_IDLE three incomplete clients, healthy GET ms=" + elapsed.ElapsedMilliseconds);
        }
        finally { foreach (TcpClient client in idle) if (client != null) client.Close(); }
    }

    private static void TrickleDeadline()
    {
        CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        using (TcpClient slow = new TcpClient())
        using (ManualResetEvent stop = new ManualResetEvent(false))
        {
            slow.ReceiveTimeout = 2300;
            slow.SendTimeout = 1000;
            slow.Connect(IPAddress.Loopback, port);
            NetworkStream stream = slow.GetStream();
            stream.WriteByte((byte)'G');
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            Thread sender = new Thread(delegate()
            {
                try { while (!stop.WaitOne(250)) stream.WriteByte((byte)'G'); }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            sender.IsBackground = true;
            sender.Start();
            try
            {
                try { Require(stream.ReadByte() == -1, "Unexpected response to incomplete trickle request"); }
                catch (IOException ex)
                {
                    SocketException socket = ex.InnerException as SocketException;
                    Require(socket != null && (socket.SocketErrorCode == SocketError.ConnectionReset || socket.SocketErrorCode == SocketError.ConnectionAborted),
                        "trickle_deadline: connection stayed open while bytes refreshed the per-read timeout");
                }
                Require(elapsed.ElapsedMilliseconds < 2300, "trickle_deadline: total first-line budget exceeded");
                Console.WriteLine("TRICKLE one byte every 250ms, closed ms=" + elapsed.ElapsedMilliseconds);
            }
            finally
            {
                stop.Set();
                slow.Close();
                Require(sender.Join(1500), "Trickle sender did not exit");
            }
        }
        CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
    }

    private static bool Closed(TcpClient client)
    {
        try { return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0; }
        catch (SocketException) { return true; }
    }

    private static void Pressure()
    {
        CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        int threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        TcpClient[] clients = new TcpClient[96];
        int rejected = 0;
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i] = new TcpClient();
                try
                {
                    clients[i].Connect(IPAddress.Loopback, port);
                    clients[i].GetStream().WriteByte((byte)'G');
                }
                catch (SocketException) { clients[i].Close(); clients[i] = null; rejected++; }
                catch (IOException) { clients[i].Close(); clients[i] = null; rejected++; }
            }
            Require(elapsed.ElapsedMilliseconds < 1000, "Pressure setup was too slow to distinguish queue rejection from deadline expiry");
            Thread.Sleep(400);
            int closedEarly = rejected;
            foreach (TcpClient client in clients) if (client != null && Closed(client)) closedEarly++;
            Require(closedEarly >= 28, "pressure_queue_bound: 96 idle connections were not bounded to four workers plus 64 queued clients");
            int threadsDuring = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
            Require(threadsDuring <= threadsBefore + 2, "Pressure created unbounded service threads");
            Thread.Sleep(1800);
            int closedTotal = rejected;
            foreach (TcpClient client in clients) if (client != null && Closed(client)) closedTotal++;
            Require(closedTotal == clients.Length, "pressure_queue_deadline: queued idle connections received a fresh timeout instead of expiring from accept time");
            Console.WriteLine("PRESSURE clients=96 closedEarly=" + closedEarly + " closedTotal=" + closedTotal
                + " threadsBefore=" + threadsBefore + " threadsDuring=" + threadsDuring + " ms=" + elapsed.ElapsedMilliseconds);
            CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        }
        finally { foreach (TcpClient client in clients) if (client != null) client.Close(); }
    }

    private static void Batch(string baseDir)
    {
        Exception[] errors = new Exception[8];
        using (ManualResetEvent start = new ManualResetEvent(false))
        using (CountdownEvent done = new CountdownEvent(errors.Length))
        {
            for (int i = 0; i < errors.Length; i++)
            {
                int index = i;
                Thread thread = new Thread(delegate()
                {
                    try { start.WaitOne(); CheckPng(Request("GET " + tilePath + " HTTP/1.1", 4000), true); }
                    catch (Exception ex) { errors[index] = ex; }
                    finally { done.Signal(); }
                });
                thread.IsBackground = true;
                thread.Start();
            }
            start.Set();
            Require(done.Wait(10000), "Concurrent map requests did not finish");
        }
        foreach (Exception error in errors) Require(error == null, "Concurrent request failed: " + error);
        for (int i = 0; i < 20; i++) CheckPng(Request("GET " + tilePath + " HTTP/1.1", 3000), true);
        // The response can arrive just before its diagnostic is appended.
        Thread.Sleep(100);
        int entries = 0;
        foreach (string line in File.ReadAllLines(Path.Combine(baseDir, "map_service.log")))
            if (line.Contains("] LocalTile ")) entries++;
        Require(entries == 16, "request_log_limit: expected 16 sampled requests, got " + entries);
        Console.WriteLine("BATCH 8 simultaneous + 20 sequential, sampled logs=" + entries);
    }

    public static int Main(string[] args)
    {
        TcpListener listener = null;
        try
        {
            Require(args.Length == 3, "Usage: MapTileServerTests DLL fixtureDir starvation|protocol|idle|batch|multi-idle|trickle|pressure");
            Type type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            type.GetField("logPath", flags).SetValue(null, Path.Combine(args[1], "map_service.log"));
            type.GetMethod("StartLocalTileServer", flags).Invoke(null, new object[] { args[1] });
            listener = (TcpListener)type.GetField("tileListener", flags).GetValue(null);
            Require(listener != null, "Tile service did not start");
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            tilePath = KnownTile(args[1], "Synoptic", 3);
            if (args[2] == "starvation") Starvation();
            else if (args[2] == "protocol") Protocol(args[1]);
            else if (args[2] == "idle") IdleClient();
            else if (args[2] == "batch") Batch(args[1]);
            else if (args[2] == "multi-idle") MultipleIdleClients();
            else if (args[2] == "trickle") TrickleDeadline();
            else if (args[2] == "pressure") Pressure();
            else throw new InvalidOperationException("Unknown test mode");
            Console.WriteLine("PASS " + args[2]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        finally { if (listener != null) listener.Stop(); }
    }
}
