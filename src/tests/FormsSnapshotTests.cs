using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// R59 回归：WinForms 的 OpenForms 是活集合，遍历时必须先取快照。
// 负向能力：把快照换成直接 foreach，本用例必须失败（见 --legacy 模式）。
class FormsSnapshotTests
{
    static Type engine;
    static int failures;

    static object Call(string name)
    {
        MethodInfo method = engine.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("missing " + name);
        return method.Invoke(null, null);
    }

    static int DirectEnumeration(Form[] ignore)
    {
        int seen = 0;
        foreach (Form form in Application.OpenForms) seen++;
        return seen;
    }

    [STAThread]
    static int Main(string[] args)
    {
        engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
        bool legacy = args.Length > 1 && args[1] == "--legacy";
        int exceptions = 0;
        int samples = 0;
        int zeroLength = 0;

        // Churn the collection on the UI thread while a worker snapshots it.
        ManualResetEvent stop = new ManualResetEvent(false);
        Thread churn = new Thread(delegate()
        {
            int n = 0;
            while (!stop.WaitOne(15))
            {
                try
                {
                    Form form = new Form { Width = 200, Height = 120, ShowInTaskbar = false };
                    form.Text = "snapshot fixture " + (n++);
                    form.Show();
                    Application.DoEvents();
                    form.Close();
                    form.Dispose();
                    Application.DoEvents();
                }
                catch { }
            }
        });
        churn.SetApartmentState(ApartmentState.STA);
        churn.IsBackground = true;
        churn.Start();

        Thread worker = new Thread(delegate()
        {
            for (int i = 0; i < 400; i++)
            {
                try
                {
                    if (legacy) { DirectEnumeration(null); }
                    else
                    {
                        Form[] snapshot = (Form[])Call("OpenFormsSnapshot");
                        if (snapshot == null) { exceptions++; }
                        else if (snapshot.Length == 0) zeroLength++;
                        else if (Array.IndexOf(snapshot, null) >= 0) exceptions++;
                    }
                    samples++;
                }
                catch (Exception) { exceptions++; }
                Thread.Sleep(5);
            }
        });
        worker.IsBackground = true;
        worker.Start();
        worker.Join(20000);
        stop.Set();
        churn.Join(5000);

        Console.WriteLine("samples=" + samples + " exceptions=" + exceptions
            + " empty_snapshots=" + zeroLength + " legacy=" + legacy);
        if (legacy)
        {
            // The old code path must be able to fail, proving the test can catch it.
            Console.WriteLine(exceptions > 0
                ? "PASS negative_control_direct_enumeration_threw"
                : "FAIL negative_control_direct_enumeration_never_threw");
            return exceptions > 0 ? 0 : 1;
        }
        if (exceptions != 0) { Console.WriteLine("FAIL snapshot_raised_or_nul"); return 1; }
        if (samples != 400) { Console.WriteLine("FAIL worker_incomplete"); return 1; }
        Console.WriteLine("PASS snapshot_never_throws_under_churn");
        Console.WriteLine("ALL PASS checks=1");
        return 0;
    }
}
