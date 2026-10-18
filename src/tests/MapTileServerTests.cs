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
            idle.ReceiveTimeout = 1000;
            idle.Connect(IPAddress.Loopback, port);
            idle.GetStream().WriteByte((byte)'G');
            Thread.Sleep(100);
            CheckPng(Request("GET " + tilePath + " HTTP/1.1", 4000), true);
            try { Require(idle.GetStream().ReadByte() == -1, "Idle connection was not closed"); }
            catch (IOException) { throw new InvalidOperationException("idle_client_timeout: incomplete request remains open"); }
        }
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
            Require(args.Length == 3, "Usage: MapTileServerTests DLL fixtureDir starvation|protocol|idle|batch");
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
            else throw new InvalidOperationException("Unknown test mode");
            Console.WriteLine("PASS " + args[2]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        finally { if (listener != null) listener.Stop(); }
    }
}
