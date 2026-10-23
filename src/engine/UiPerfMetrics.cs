using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly object uiPerfLock = new object();
        private static readonly Dictionary<string, UiPerfMetric> uiPerfMetrics =
            new Dictionary<string, UiPerfMetric>(StringComparer.Ordinal);
        private static int uiPerfEpoch;

        private sealed class UiPerfMetric
        {
            internal long Posted, Started, Completed, Pending, PeakPending, Active;
            internal long TotalTicks, MaxTicks, MaxQueueTicks, OverFrame;
        }

        // A queued scope does not count as completed work until the owning UI
        // thread actually executes it. Disposing before Start cancels the ticket.
        private sealed class UiPerfScope : IDisposable
        {
            private readonly UiPerfMetric metric;
            private readonly long queuedAt;
            private long startedAt;
            private bool finished;

            internal UiPerfScope(string kind, bool queued)
            {
                lock (uiPerfLock)
                {
                    if (!uiPerfMetrics.TryGetValue(kind, out metric))
                    {
                        metric = new UiPerfMetric();
                        uiPerfMetrics.Add(kind, metric);
                    }
                    if (queued)
                    {
                        queuedAt = Stopwatch.GetTimestamp();
                        metric.Posted++;
                        metric.Pending++;
                        if (metric.Pending > metric.PeakPending) metric.PeakPending = metric.Pending;
                    }
                }
                if (!queued) Start();
            }

            internal void Start()
            {
                lock (uiPerfLock)
                {
                    if (finished || startedAt != 0) return;
                    startedAt = Stopwatch.GetTimestamp();
                    if (queuedAt != 0)
                    {
                        metric.Pending--;
                        long wait = startedAt - queuedAt;
                        if (wait > metric.MaxQueueTicks) metric.MaxQueueTicks = wait;
                    }
                    metric.Started++;
                    metric.Active++;
                }
            }

            public void Dispose()
            {
                lock (uiPerfLock)
                {
                    if (finished) return;
                    finished = true;
                    if (startedAt == 0)
                    {
                        if (queuedAt != 0) metric.Pending--;
                        return;
                    }
                    long elapsed = Stopwatch.GetTimestamp() - startedAt;
                    metric.Active--;
                    metric.Completed++;
                    metric.TotalTicks += elapsed;
                    if (elapsed > metric.MaxTicks) metric.MaxTicks = elapsed;
                    if (elapsed * 1000.0 / Stopwatch.Frequency > 16.0) metric.OverFrame++;
                }
            }
        }

        private static bool ResetUiPerfMetrics()
        {
            lock (uiPerfLock)
            {
                foreach (UiPerfMetric metric in uiPerfMetrics.Values)
                    if (metric.Pending != 0 || metric.Active != 0) return false;
                uiPerfMetrics.Clear();
                uiPerfEpoch++;
                return true;
            }
        }

        private static void AppendUiPerfMetrics(StringBuilder output)
        {
            lock (uiPerfLock)
            {
                output.Append(",\"uiPerfVersion\":1,\"uiPerfEpoch\":").Append(uiPerfEpoch);
                output.Append(",\"uiPerf\":{");
                bool first = true;
                foreach (KeyValuePair<string, UiPerfMetric> item in uiPerfMetrics)
                {
                    if (!first) output.Append(',');
                    first = false;
                    UiPerfMetric metric = item.Value;
                    output.Append('"').Append(item.Key).Append("\":{");
                    output.Append("\"posted\":").Append(metric.Posted);
                    output.Append(",\"started\":").Append(metric.Started);
                    output.Append(",\"completed\":").Append(metric.Completed);
                    output.Append(",\"pending\":").Append(metric.Pending);
                    output.Append(",\"peakPending\":").Append(metric.PeakPending);
                    output.Append(",\"active\":").Append(metric.Active);
                    output.Append(",\"totalMs\":").Append(UiPerfMilliseconds(metric.TotalTicks));
                    output.Append(",\"maxMs\":").Append(UiPerfMilliseconds(metric.MaxTicks));
                    output.Append(",\"maxQueueMs\":").Append(UiPerfMilliseconds(metric.MaxQueueTicks));
                    output.Append(",\"over16ms\":").Append(metric.OverFrame).Append('}');
                }
                output.Append('}');
            }
        }

        private static string UiPerfMilliseconds(long ticks)
        {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
