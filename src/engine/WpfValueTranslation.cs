using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private sealed class WeaponsDiagnosticId { internal int Value; }
        private static int weaponsDiagnosticNextId;
        private static readonly ConditionalWeakTable<object, WeaponsDiagnosticId> weaponsDiagnosticIds =
            new ConditionalWeakTable<object, WeaponsDiagnosticId>();
        [ThreadStatic] private static WeaponsDiagnosticTrace weaponsDiagnosticTrace;

        private sealed class WeaponsDiagnosticTrace
        {
            internal WeakReference Root;
            internal long Started;
            internal int Frames;
            internal int TargetFrames;
            internal int Bytes;
            internal bool Armed;
            internal bool Overflow;
            internal string Error;
            internal readonly List<string> Events = new List<string>();
        }

        private sealed class WeaponsDiagnosticNode
        {
            internal DependencyObject Node;
            internal string Path;
        }

        private static int WeaponsId(object value)
        {
            if (value == null) return 0;
            return weaponsDiagnosticIds.GetValue(value, delegate(object ignored)
            {
                return new WeaponsDiagnosticId { Value = System.Threading.Interlocked.Increment(ref weaponsDiagnosticNextId) };
            }).Value;
        }

        private static string WeaponsQuote(string value) { return "\"" + JsonEsc(value ?? "") + "\""; }

        private static DependencyObject WeaponsParent(DependencyObject value)
        {
            System.Windows.Documents.TextElement text = value as System.Windows.Documents.TextElement;
            if (text != null) return text.Parent;
            DependencyObject parent = LogicalTreeHelper.GetParent(value);
            if (parent == null && value is System.Windows.Media.Visual)
                parent = System.Windows.Media.VisualTreeHelper.GetParent(value);
            return parent;
        }

        private static List<DependencyObject> WeaponsChildren(DependencyObject value)
        {
            List<DependencyObject> children = new List<DependencyObject>();
            if (value is System.Windows.Media.Visual)
            {
                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(value);
                if (count > 4096) throw new InvalidOperationException("weapons visual child limit");
                for (int i = 0; i < count; i++) children.Add(System.Windows.Media.VisualTreeHelper.GetChild(value, i));
            }
            System.Windows.Documents.InlineCollection inlines = null;
            TextBlock text = value as TextBlock;
            System.Windows.Documents.Span span = value as System.Windows.Documents.Span;
            if (text != null) inlines = text.Inlines;
            else if (span != null) inlines = span.Inlines;
            if (inlines != null)
                for (System.Windows.Documents.Inline inline = inlines.FirstInline; inline != null; inline = inline.NextInline)
                {
                    if (children.Count >= 4096) throw new InvalidOperationException("weapons inline child limit");
                    children.Add(inline);
                }
            return children;
        }

        private static DependencyObject FindWeaponsDiagnosticRoot(DependencyObject root)
        {
            Stack<DependencyObject> pending = new Stack<DependencyObject>();
            if (root != null) pending.Push(root);
            int visited = 0;
            while (pending.Count != 0)
            {
                if (++visited > 4096) throw new InvalidOperationException("weapons discovery node limit");
                DependencyObject node = pending.Pop();
                if (node.GetType().Name == "UnitWeapons_WPF") return node;
                List<DependencyObject> children = WeaponsChildren(node);
                for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
            return null;
        }

        private static string WeaponsSetting(object value)
        {
            if (value == null) return "null";
            if (object.ReferenceEquals(value, DependencyProperty.UnsetValue)) return "\"UnsetValue\"";
            if (object.ReferenceEquals(value, System.Windows.Data.Binding.DoNothing)) return "\"DoNothing\"";
            string text = value as string;
            if (text != null) return WeaponsQuote(text);
            return "{\"type\":" + WeaponsQuote(value.GetType().FullName) + "}";
        }

        private static string WeaponsBinding(System.Windows.Data.BindingBase binding, int depth)
        {
            if (binding == null) return "{\"kind\":\"none\",\"id\":0}";
            if (depth > 4) throw new InvalidOperationException("weapons binding depth limit");
            StringBuilder json = new StringBuilder();
            json.Append("{\"kind\":").Append(WeaponsQuote(binding.GetType().Name))
                .Append(",\"id\":").Append(WeaponsId(binding))
                .Append(",\"stringFormat\":").Append(WeaponsSetting(binding.StringFormat))
                .Append(",\"fallbackValue\":").Append(WeaponsSetting(binding.FallbackValue))
                .Append(",\"targetNullValue\":").Append(WeaponsSetting(binding.TargetNullValue));
            System.Windows.Data.Binding single = binding as System.Windows.Data.Binding;
            if (single != null)
            {
                json.Append(",\"path\":").Append(WeaponsQuote(single.Path == null ? null : single.Path.Path))
                    .Append(",\"sourceType\":").Append(WeaponsQuote(single.Source == null ? null : single.Source.GetType().FullName))
                    .Append(",\"elementName\":").Append(WeaponsQuote(single.ElementName))
                    .Append(",\"relativeSourceMode\":").Append(WeaponsQuote(single.RelativeSource == null ? null : single.RelativeSource.Mode.ToString()))
                    .Append(",\"mode\":").Append(WeaponsQuote(single.Mode.ToString()))
                    .Append(",\"updateSourceTrigger\":").Append(WeaponsQuote(single.UpdateSourceTrigger.ToString()))
                    .Append(",\"converter\":").Append(WeaponsQuote(single.Converter == null ? null : single.Converter.GetType().FullName))
                    .Append(",\"converterParameter\":").Append(WeaponsSetting(single.ConverterParameter))
                    .Append(",\"culture\":").Append(WeaponsQuote(single.ConverterCulture == null ? null : single.ConverterCulture.Name));
            }
            System.Windows.Data.MultiBinding multi = binding as System.Windows.Data.MultiBinding;
            System.Windows.Data.PriorityBinding priority = binding as System.Windows.Data.PriorityBinding;
            if (multi != null)
                json.Append(",\"mode\":").Append(WeaponsQuote(multi.Mode.ToString()))
                    .Append(",\"converter\":").Append(WeaponsQuote(multi.Converter == null ? null : multi.Converter.GetType().FullName))
                    .Append(",\"converterParameter\":").Append(WeaponsSetting(multi.ConverterParameter))
                    .Append(",\"culture\":").Append(WeaponsQuote(multi.ConverterCulture == null ? null : multi.ConverterCulture.Name));
            IList<System.Windows.Data.BindingBase> bindings = multi != null ? multi.Bindings : priority != null ? priority.Bindings : null;
            if (bindings != null)
            {
                if (bindings.Count > 16) throw new InvalidOperationException("weapons binding child limit");
                json.Append(",\"bindings\":[");
                for (int i = 0; i < bindings.Count; i++)
                {
                    if (i != 0) json.Append(',');
                    json.Append(WeaponsBinding(bindings[i], depth + 1));
                }
                json.Append(']');
            }
            return json.Append('}').ToString();
        }

        private static string DescribeWeaponsNode(DependencyObject node, string path, bool detail)
        {
            FrameworkElement element = node as FrameworkElement;
            FrameworkContentElement content = node as FrameworkContentElement;
            DependencyProperty property = node is TextBlock ? TextBlock.TextProperty
                : node is System.Windows.Documents.Run ? System.Windows.Documents.Run.TextProperty : null;
            System.Windows.Data.BindingBase binding = property == null ? null : System.Windows.Data.BindingOperations.GetBindingBase(node, property);
            StringBuilder json = new StringBuilder();
            json.Append("{\"id\":").Append(WeaponsId(node)).Append(",\"parentId\":").Append(WeaponsId(WeaponsParent(node)))
                .Append(",\"path\":").Append(WeaponsQuote(path)).Append(",\"type\":").Append(WeaponsQuote(node.GetType().FullName))
                .Append(",\"name\":").Append(WeaponsQuote(GetWpfElementName(node)))
                .Append(",\"text\":").Append(WeaponsQuote(ReadWpfText(node)))
                .Append(",\"loaded\":").Append(element != null ? (element.IsLoaded ? "true" : "false") : content != null && content.IsLoaded ? "true" : "false")
                .Append(",\"connected\":").Append(PresentationSource.FromDependencyObject(node) != null ? "true" : "false")
                .Append(",\"bindingId\":").Append(WeaponsId(binding));
            if (detail)
            {
                json.Append(",\"property\":").Append(WeaponsQuote(property == null ? null : property.Name))
                    .Append(",\"binding\":").Append(WeaponsBinding(binding, 0));
                object context = element != null ? element.DataContext : content != null ? content.DataContext : null;
                json.Append(",\"dataContextType\":").Append(WeaponsQuote(context == null ? null : context.GetType().FullName));
                if (property != null)
                {
                    object local = node.ReadLocalValue(property);
                    json.Append(",\"localValueType\":").Append(WeaponsQuote(local == null ? null : local.GetType().FullName))
                        .Append(",\"valueSource\":").Append(WeaponsQuote(DependencyPropertyHelper.GetValueSource(node, property).BaseValueSource.ToString()));
                }
                json.Append(",\"visible\":").Append(element == null ? "null" : element.IsVisible ? "true" : "false")
                    .Append(",\"bounds\":");
                if (element != null && PresentationSource.FromDependencyObject(element) != null)
                {
                    Point first = element.PointToScreen(new Point(0, 0));
                    Point last = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                    json.Append("{\"space\":\"screen_physical\",\"x\":").Append(Math.Min(first.X, last.X).ToString("R", CultureInfo.InvariantCulture))
                        .Append(",\"y\":").Append(Math.Min(first.Y, last.Y).ToString("R", CultureInfo.InvariantCulture))
                        .Append(",\"width\":").Append(Math.Abs(last.X - first.X).ToString("R", CultureInfo.InvariantCulture))
                        .Append(",\"height\":").Append(Math.Abs(last.Y - first.Y).ToString("R", CultureInfo.InvariantCulture)).Append('}');
                }
                else json.Append("null");
            }
            return json.Append('}').ToString();
        }

        private static string CaptureWeaponsDiagnostic(DependencyObject root, int limit, bool detail)
        {
            if (root == null || !root.Dispatcher.CheckAccess()) return "{\"status\":\"error\",\"reason\":\"missing root or wrong Dispatcher\"}";
            DependencyObject target = FindWeaponsDiagnosticRoot(root);
            if (target == null) return "{\"status\":\"not_found\",\"targetCount\":0}";
            Stack<WeaponsDiagnosticNode> pending = new Stack<WeaponsDiagnosticNode>();
            HashSet<DependencyObject> visited = new HashSet<DependencyObject>();
            pending.Push(new WeaponsDiagnosticNode { Node = target, Path = "weapon" });
            StringBuilder nodes = new StringBuilder();
            while (pending.Count != 0)
            {
                if (visited.Count >= limit) return "{\"status\":\"overflow\",\"reason\":\"node limit\"}";
                WeaponsDiagnosticNode next = pending.Pop();
                if (!visited.Add(next.Node)) continue;
                if (nodes.Length != 0) nodes.Append(',');
                nodes.Append(DescribeWeaponsNode(next.Node, next.Path, detail));
                if (nodes.Length > 131072) return "{\"status\":\"overflow\",\"reason\":\"byte limit\"}";
                List<DependencyObject> children = WeaponsChildren(next.Node);
                for (int i = children.Count - 1; i >= 0; i--)
                    pending.Push(new WeaponsDiagnosticNode { Node = children[i], Path = next.Path + "/" + i });
            }
            return "{\"status\":\"ok\",\"diagnosticOnly\":true,\"targetCount\":1,\"hostRootId\":" + WeaponsId(root)
                + ",\"hostType\":" + WeaponsQuote(root.GetType().FullName) + ",\"nodeCount\":" + visited.Count
                + ",\"targetId\":" + WeaponsId(target) + ",\"nodes\":[" + nodes + "]}";
        }

        private static bool WeaponsTraceActive()
        {
            WeaponsDiagnosticTrace trace = weaponsDiagnosticTrace;
            if (trace == null || !trace.Armed) return false;
            if (Stopwatch.GetTimestamp() - trace.Started >= Stopwatch.Frequency * 8 || trace.Frames >= 240)
            { trace.Armed = false; return false; }
            return true;
        }

        private static void AppendWeaponsDiagnosticEvent(string phase, string data)
        {
            WeaponsDiagnosticTrace trace = weaponsDiagnosticTrace;
            string entry = "{\"phase\":" + WeaponsQuote(phase) + ",\"elapsedTicks\":"
                + (Stopwatch.GetTimestamp() - trace.Started) + ",\"data\":" + data + "}";
            if (trace.Events.Count >= 1024 || trace.Bytes + entry.Length > 1048576)
            { trace.Overflow = true; trace.Armed = false; return; }
            trace.Events.Add(entry);
            trace.Bytes += entry.Length;
        }

        private static void RecordWeaponsDiagnosticNode(DependencyObject node, string phase)
        {
            if (!WeaponsTraceActive() || node == null) return;
            try
            {
                DependencyObject root = weaponsDiagnosticTrace.Root.Target as DependencyObject;
                bool weapon = false;
                for (DependencyObject parent = node; parent != null; parent = WeaponsParent(parent))
                {
                    if (parent.GetType().Name == "UnitWeapons_WPF") weapon = true;
                    if (object.ReferenceEquals(parent, root))
                    {
                        if (weapon) AppendWeaponsDiagnosticEvent(phase, DescribeWeaponsNode(node, "event", true));
                        return;
                    }
                }
            }
            catch (Exception error) { weaponsDiagnosticTrace.Error = error.Message; weaponsDiagnosticTrace.Armed = false; }
        }

        private static void RecordWeaponsDiagnosticFrame(string phase)
        {
            if (!WeaponsTraceActive()) return;
            try
            {
                if (phase == "render_before_translation") weaponsDiagnosticTrace.Frames++;
                DependencyObject root = weaponsDiagnosticTrace.Root.Target as DependencyObject;
                string data = CaptureWeaponsDiagnostic(root, 256, false);
                if (data.IndexOf("\"status\":\"ok\"", StringComparison.Ordinal) >= 0)
                { if (phase == "render_before_translation") weaponsDiagnosticTrace.TargetFrames++; }
                else { weaponsDiagnosticTrace.Error = "frame target missing or capture incomplete"; weaponsDiagnosticTrace.Armed = false; }
                AppendWeaponsDiagnosticEvent(phase, data);
            }
            catch (Exception error) { weaponsDiagnosticTrace.Error = error.Message; weaponsDiagnosticTrace.Armed = false; }
        }

        private static string RunWeaponsDiagnostic(DependencyObject root, string action)
        {
            if (action == "SNAPSHOT") return CaptureWeaponsDiagnostic(root, 512, true);
            if (action == "START")
            {
                weaponsDiagnosticTrace = new WeaponsDiagnosticTrace { Root = new WeakReference(root), Started = Stopwatch.GetTimestamp(), Armed = true };
                return "{\"status\":\"armed\",\"durationMs\":8000,\"engineMvid\":" + WeaponsQuote(typeof(CustomAppDomainManager).Module.ModuleVersionId.ToString())
                    + ",\"pid\":" + Process.GetCurrentProcess().Id + "}";
            }
            if (action == "STOP")
            {
                WeaponsDiagnosticTrace trace = weaponsDiagnosticTrace;
                if (trace == null) return "{\"status\":\"incomplete\",\"reason\":\"not armed\"}";
                trace.Armed = false;
                return "{\"status\":" + WeaponsQuote(trace.Frames > 0 && trace.TargetFrames > 0 && trace.Error == null && !trace.Overflow ? "ok" : "incomplete")
                    + ",\"diagnosticOnly\":true,\"engineMvid\":" + WeaponsQuote(typeof(CustomAppDomainManager).Module.ModuleVersionId.ToString())
                    + ",\"pid\":" + Process.GetCurrentProcess().Id + ",\"timestampFrequency\":" + Stopwatch.Frequency
                    + ",\"frames\":" + trace.Frames + ",\"targetFrames\":" + trace.TargetFrames
                    + ",\"overflow\":" + (trace.Overflow ? "true" : "false") + ",\"error\":" + WeaponsSetting(trace.Error)
                    + ",\"coverage\":\"binding identities sampled at Loaded, existing value hook and Rendering; no SetBinding interception or pixel evidence\",\"events\":[" + string.Join(",", trace.Events.ToArray()) + "]}";
            }
            return "{\"status\":\"error\",\"reason\":\"expected SNAPSHOT, START or STOP\"}";
        }

        private static string ExecuteWeaponsDiagnostic(string argument)
        {
            string action = argument.ToUpperInvariant();
            if (action != "SNAPSHOT" && action != "START" && action != "STOP")
                return "{\"status\":\"error\",\"reason\":\"expected SNAPSHOT, START or STOP\"}";
            List<DependencyObject> roots = new List<DependencyObject>();
            try
            {
                foreach (System.Windows.Forms.Form form in OpenFormsSnapshot())
                {
                    if (form == null || form.IsDisposed || !form.IsHandleCreated || !form.Visible) continue;
                    System.Threading.Tasks.TaskCompletionSource<List<DependencyObject>> completion = new System.Threading.Tasks.TaskCompletionSource<List<DependencyObject>>();
                    System.Windows.Forms.MethodInvoker discover = delegate
                    {
                        try
                        {
                            List<DependencyObject> found = new List<DependencyObject>();
                            foreach (System.Windows.Forms.Control host in CollectElementHosts(form))
                            {
                                PropertyInfo child = host.GetType().GetProperty("Child");
                                DependencyObject root = child == null ? null : child.GetValue(host, null) as DependencyObject;
                                if (root != null && (root.GetType().Name == "RightColumnWPF" || FindWeaponsDiagnosticRoot(root) != null)) found.Add(root);
                            }
                            completion.TrySetResult(found);
                        }
                        catch (Exception error) { completion.TrySetException(error); }
                    };
                    if (form.InvokeRequired) form.BeginInvoke(discover); else discover();
                    if (!completion.Task.Wait(2000)) return "{\"status\":\"error\",\"reason\":\"UI discovery timeout\"}";
                    foreach (DependencyObject root in completion.Task.Result) if (!roots.Contains(root)) roots.Add(root);
                }
                if (roots.Count != 1) return "{\"status\":\"error\",\"reason\":\"expected one weapons host\",\"hostCount\":" + roots.Count + "}";
                string result = null;
                Action inspect = delegate { result = RunWeaponsDiagnostic(roots[0], action); };
                if (roots[0].Dispatcher.CheckAccess()) inspect();
                else roots[0].Dispatcher.Invoke(inspect, System.Windows.Threading.DispatcherPriority.Send,
                    System.Threading.CancellationToken.None, TimeSpan.FromSeconds(2));
                return result;
            }
            catch (Exception error) { return "{\"status\":\"error\",\"reason\":" + WeaponsQuote(error.GetBaseException().Message) + "}"; }
        }

        /// <summary>
        /// R67：武器行绑定的显示翻译（由 WeaponsTextTranslationConverter 在求值链上调用）。
        ///
        /// 与原 MultiBinding 的语义保持一致：StringFormat 先由本方法拼出最终串，再整串翻译；
        /// 任一分量为 UnsetValue/DoNothing 时直接返回原分量，交给 WPF 走原哨兵语义；
        /// 不读取也不写回业务源。
        /// </summary>
        internal static object TranslateWeaponsBoundText(object[] values, object parameter, CultureInfo culture)
        {
            try
            {
                if (values == null || values.Length == 0) return DependencyProperty.UnsetValue;
                for (int i = 0; i < values.Length; i++)
                {
                    object v = values[i];
                    if (v == null || object.ReferenceEquals(v, DependencyProperty.UnsetValue)
                        || object.ReferenceEquals(v, System.Windows.Data.Binding.DoNothing))
                        return DependencyProperty.UnsetValue;
                }
                string format = parameter as string;
                string raw = format == null ? string.Join(" ", ToTextArray(values, culture))
                                            : string.Format(culture ?? CultureInfo.CurrentCulture, format, values);
                if (string.IsNullOrEmpty(raw) || !HasAsciiLetter(raw)) return raw;
                string translated = TranslateString(raw);
                if (string.IsNullOrEmpty(translated) || translated == raw) translated = TranslateMilitaryUnitName(raw);
                if (string.IsNullOrEmpty(translated)) return raw;
                return translated;
            }
            catch { return DependencyProperty.UnsetValue; }
        }

        private static string[] ToTextArray(object[] values, CultureInfo culture)
        {
            string[] text = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                text[i] = values[i] == null ? "" : System.Convert.ToString(values[i], culture ?? CultureInfo.CurrentCulture);
            return text;
        }

        /// <summary>
        /// R67：把武器行 Run 的 MultiBinding/Single Binding 换成带翻译的等价绑定。
        /// 保留子绑定、StringFormat、Mode 与 Culture，只加一层显示翻译；
        /// 已在投影下的绑定不重复包装。
        /// </summary>
        private static void ProjectWeaponsBoundRun(System.Windows.Documents.Run run)
        {
            if (run == null || !IsWeaponsDisplayNode(run)) return;
            try
            {
                BindingBase current = BindingOperations.GetBindingBase(run, System.Windows.Documents.Run.TextProperty);
                if (current == null) return;
                MultiBinding currentMulti = current as MultiBinding;
                Binding currentSingle = current as Binding;
                if ((currentMulti != null && object.ReferenceEquals(currentMulti.Converter, weaponsProjectionMulti))
                    || (currentSingle != null && object.ReferenceEquals(currentSingle.Converter, weaponsProjectionSingle))) return;
                MultiBinding multi = current as MultiBinding;
                if (multi != null)
                {
                    MultiBinding copy = new MultiBinding();
                    copy.Converter = weaponsProjectionMulti;
                    copy.ConverterParameter = multi.StringFormat;
                    copy.ConverterCulture = multi.ConverterCulture;
                    copy.StringFormat = null;
                    copy.Mode = multi.Mode;
                    copy.NotifyOnSourceUpdated = multi.NotifyOnSourceUpdated;
                    copy.NotifyOnTargetUpdated = multi.NotifyOnTargetUpdated;
                    for (int i = 0; i < multi.Bindings.Count; i++) copy.Bindings.Add(multi.Bindings[i]);
                    BindingOperations.SetBinding(run, System.Windows.Documents.Run.TextProperty, copy);
                    return;
                }
                Binding single = current as Binding;
                if (single != null)
                {
                    Binding copy = new Binding();
                    copy.Path = single.Path;
                    copy.Source = single.Source;
                    copy.ElementName = single.ElementName;
                    copy.RelativeSource = single.RelativeSource;
                    copy.XPath = single.XPath;
                    copy.Mode = single.Mode;
                    copy.UpdateSourceTrigger = single.UpdateSourceTrigger;
                    copy.NotifyOnSourceUpdated = single.NotifyOnSourceUpdated;
                    copy.NotifyOnTargetUpdated = single.NotifyOnTargetUpdated;
                    copy.Converter = weaponsProjectionSingle;
                    copy.ConverterParameter = single.StringFormat;
                    copy.ConverterCulture = single.ConverterCulture;
                    copy.StringFormat = null;
                    copy.FallbackValue = single.FallbackValue;
                    copy.TargetNullValue = single.TargetNullValue;
                    BindingOperations.SetBinding(run, System.Windows.Documents.Run.TextProperty, copy);
                }
            }
            catch { }
        }

        private static readonly WeaponsTextTranslationConverter weaponsProjectionMulti = new WeaponsTextTranslationConverter();
        private static readonly WeaponsTextTranslationConverterSingle weaponsProjectionSingle = new WeaponsTextTranslationConverterSingle();

        /// <summary>
        /// R68：把翻译投影挂到**稳定的容器**上，而不是会被游戏反复重建的 Run。
        ///
        /// 实测（WEAPONS_DIAG 连续采样）：`UnitWeapons_WPF` 与内部 `ItemsControl` 的 id 始终是 1 / 5，
        /// 而每行的 `Run` 每次都换新 id（2832 → 2892 → 2952 …）。
        /// 所以任何“挂到 Run 上”的钩子都会随重建丢失，只能等到下一拍补扫，
        /// 屏幕上就是阶梯式回闪。挂在容器上则每次重建后都能立刻重新投影。
        /// </summary>
        private static void AttachWeaponsContainerProjection(ItemsControl items)
        {
            if (items == null || !IsWeaponsDisplayNode(items)) return;
            WeaponsContainerHook hook = weaponsContainerHooks.GetValue(items,
                delegate(ItemsControl owner) { return new WeaponsContainerHook(owner); });
            hook.AttachIfLoaded();
            hook.ProjectAll();
        }

        private static readonly ConditionalWeakTable<ItemsControl, WeaponsContainerHook> weaponsContainerHooks =
            new ConditionalWeakTable<ItemsControl, WeaponsContainerHook>();

        private sealed class WeaponsContainerHook
        {
            private readonly ItemsControl owner;
            private bool attached;

            internal WeaponsContainerHook(ItemsControl owner)
            {
                this.owner = owner;
                owner.Loaded += OnLoaded;
                owner.Unloaded += OnDetached;
            }

            internal void AttachIfLoaded()
            {
                if (attached || !owner.IsLoaded || owner.Dispatcher.HasShutdownStarted) return;
                attached = true;
                owner.LayoutUpdated += OnLayout;
                owner.Dispatcher.ShutdownStarted += OnDetached;
            }

            private void OnLoaded(object sender, RoutedEventArgs args) { AttachIfLoaded(); ProjectAll(); }

            private void OnDetached(object sender, EventArgs args)
            {
                if (!attached) return;
                attached = false;
                owner.LayoutUpdated -= OnLayout;
                owner.Dispatcher.ShutdownStarted -= OnDetached;
            }

            private void OnLayout(object sender, EventArgs args) { ProjectAll(); }

            /// <summary>
            /// 把当前所有已生成的容器内的绑定 Run 重新投影。
            /// 元素是新建的，所以不能按对象身份去重，只能看绑定上是否已是我们自己的转换器。
            /// </summary>
            internal void ProjectAll()
            {
                try
                {
                    if (owner.Dispatcher.HasShutdownStarted) return;
                    int count = owner.Items.Count;
                    for (int i = 0; i < count; i++)
                    {
                        DependencyObject container = null;
                        try { container = owner.ItemContainerGenerator.ContainerFromIndex(i); }
                        catch { }
                        if (container == null) continue;
                        ProjectRunsIn(container, 0);
                    }
                }
                catch { }
            }

            private static void ProjectRunsIn(DependencyObject node, int depth)
            {
                if (node == null || depth > 24) return;
                System.Windows.Documents.Run run = node as System.Windows.Documents.Run;
                if (run != null) { ProjectWeaponsBoundRun(run); return; }
                int count = 0;
                try { count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); }
                catch { count = 0; }
                for (int i = 0; i < count; i++)
                {
                    try { ProjectRunsIn(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1); }
                    catch { }
                }
                System.Windows.Documents.InlineCollection inlines = null;
                TextBlock text = node as TextBlock;
                System.Windows.Documents.Span span = node as System.Windows.Documents.Span;
                if (text != null) inlines = text.Inlines;
                else if (span != null) inlines = span.Inlines;
                if (inlines == null) return;
                for (System.Windows.Documents.Inline inline = inlines.FirstInline; inline != null; inline = inline.NextInline)
                {
                    try { ProjectRunsIn(inline, depth + 1); }
                    catch { }
                }
            }
        }

        private static readonly ConditionalWeakTable<FrameworkElement, WpfValueTranslationState> wpfValueTranslations =
            new ConditionalWeakTable<FrameworkElement, WpfValueTranslationState>();
        // R66：武器行真正带绑定的是 Hyperlink 里的 Run（实测 bindingId=22），不是外层 TextBlock。
        // Run 是 FrameworkContentElement，不能放进上面只收 FrameworkElement 的表。
        private static readonly ConditionalWeakTable<System.Windows.Documents.Run, WpfRunValueTranslationState> wpfRunValueTranslations =
            new ConditionalWeakTable<System.Windows.Documents.Run, WpfRunValueTranslationState>();
        private static readonly object wpfValueSubscriptionLock = new object();

        /// <summary>
        /// R66：为带绑定的 Run 安装同步值钩子。
        ///
        /// 实测（build/weapons-flicker/deploy/snap_now.json）：武器行结构是
        /// `TextBlock → Hyperlink → Run(bindingId=22)`，外层 TextBlock 没有绑定。
        /// 游戏通过绑定把英文推给 Run 时，此前没有任何同步钩子，只能等下一轮补扫，
        /// 于是英文必定先上屏一帧（这就是“还在频闪”）。
        /// 这里只对武器子树内的 Run 挂钩，其他文档 Run 仍由队列处理。
        /// </summary>
        private static void AttachWeaponsRunTranslation(System.Windows.Documents.Run run)
        {
            if (run == null || !IsWeaponsDisplayNode(run)) return;
            // R67：先装绑定投影——文字由绑定求值产出，光改 Text 必被下一次求值覆盖。
            ProjectWeaponsBoundRun(run);
            WpfRunValueTranslationState state = wpfRunValueTranslations.GetValue(run,
                delegate(System.Windows.Documents.Run owner) { return new WpfRunValueTranslationState(owner); });
            state.AttachIfLoaded();
        }

        private sealed class WpfRunValueTranslationState
        {
            private readonly System.Windows.Documents.Run owner;
            private readonly DependencyPropertyDescriptor descriptor;
            private bool attached;

            internal WpfRunValueTranslationState(System.Windows.Documents.Run owner)
            {
                this.owner = owner;
                descriptor = DependencyPropertyDescriptor.FromProperty(
                    System.Windows.Documents.Run.TextProperty, typeof(System.Windows.Documents.Run));
                owner.Loaded += OnLoaded;
                owner.Unloaded += OnDetached;
            }

            internal void AttachIfLoaded()
            {
                if (attached || !owner.IsLoaded || owner.Dispatcher.HasShutdownStarted) return;
                lock (wpfValueSubscriptionLock) descriptor.AddValueChanged(owner, OnValueChanged);
                attached = true;
                owner.Dispatcher.ShutdownStarted += OnDetached;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                AttachIfLoaded();
                TranslateFlowInline(owner);
            }

            private void OnDetached(object sender, EventArgs args)
            {
                if (!attached) return;
                owner.Dispatcher.ShutdownStarted -= OnDetached;
                lock (wpfValueSubscriptionLock) descriptor.RemoveValueChanged(owner, OnValueChanged);
                attached = false;
            }

            private void OnValueChanged(object sender, EventArgs args)
            {
                if (!owner.IsLoaded || IsEditableWpfTarget(owner) || _inWpfTextChanged) return;
                string current = owner.Text;
                if (string.IsNullOrEmpty(current) || !HasAsciiLetter(current)) return;
                if (!EnterMutation("WpfRunValueHook")) return;
                try
                {
                    _inWpfTextChanged = true;
                    // TranslateFlowInline 内部同样走 SetRunTextKeepBinding 与幂等短路；
                    // 先复核当前值未被游戏再次改写，避免把旧快照写回。
                    if (owner.Text == current) TranslateFlowInline(owner);
                }
                catch { }
                finally { _inWpfTextChanged = false; ExitMutation(); }
            }
        }

        private static void AttachWpfValueTranslation(FrameworkElement target)
        {
            // The table uses object identity and does not root an unloaded element.
            // The element and all callbacks remain on its owning Dispatcher.
            WpfValueTranslationState state = wpfValueTranslations.GetValue(target,
                delegate(FrameworkElement owner) { return new WpfValueTranslationState(owner); });
            state.AttachIfLoaded();
        }

        private sealed class WpfValueTranslationState
        {
            private readonly FrameworkElement owner;
            private readonly DependencyPropertyDescriptor descriptor;
            private bool attached;

            internal WpfValueTranslationState(FrameworkElement owner)
            {
                this.owner = owner;
                descriptor = owner is TextBlock
                    ? DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                    : DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
                owner.Loaded += OnLoaded;
                owner.Unloaded += OnDetached;
            }

            internal void AttachIfLoaded()
            {
                // DPD itself retains its component strongly. A weak callback alone
                // would still leak controls translated before they ever load.
                if (attached || !owner.IsLoaded || owner.Dispatcher.HasShutdownStarted) return;
                // WPF caches the descriptor across Dispatchers, but its tracker
                // dictionary does not synchronize Add/RemoveValueChanged.
                lock (wpfValueSubscriptionLock) descriptor.AddValueChanged(owner, OnValueChanged);
                attached = true;
                owner.Dispatcher.ShutdownStarted += OnDetached;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                AttachIfLoaded();
                TranslateWpfElement(owner);
            }

            private void OnDetached(object sender, EventArgs args)
            {
                // Dispatcher shutdown does not guarantee an Unloaded event.
                Detach();
            }

            private void Detach()
            {
                if (!attached) return;
                owner.Dispatcher.ShutdownStarted -= OnDetached;
                lock (wpfValueSubscriptionLock) descriptor.RemoveValueChanged(owner, OnValueChanged);
                attached = false;
            }

            private void OnValueChanged(object sender, EventArgs args)
            {
                RecordWeaponsDiagnosticNode(owner, "value_hook_before_translation");
                if (!owner.IsLoaded || IsEditableWpfTarget(owner) || _inWpfTextChanged) return;
                if (!EnterMutation("WpfValueHook")) return;
                try
                {
                    _inWpfTextChanged = true;
                    TextBlock text = owner as TextBlock;
                    if (text != null)
                    {
                        string current = text.Text;
                        if (string.IsNullOrEmpty(current)) return;
                        string mixed = FixMixedEnglishPrefix(current);
                        if (mixed != null) { SetTextKeepBinding(text, mixed); return; }
                        if (current.Contains("kg") && (current.Contains("fuel") || current.Contains("燃油")
                            || current.Contains("Tank") || current.Contains("坦克") || current.Contains("reserve")))
                        {
                            string fuel = TranslateFuelPanelText(current);
                            if (fuel != current) { SetTextKeepBinding(text, fuel); return; }
                        }
                        if (!HasAsciiLetter(current)) return;
                        string translated = TranslateString(current);
                        if (string.IsNullOrEmpty(translated) || translated == current)
                            translated = TranslateMilitaryUnitName(current);
                        // R64c 诊断：这条同步回写路径历史上**不记日志**，于是出现
                        // 「屏幕在闪、WPF_TEXTLOG 却 0 增量」的观测盲区（抓屏实测
                        // 英文态 60~70ms、每秒 2 次，日志无任何记录）。
                        // 记入同一个环形缓冲，才能把「谁在打」变成可度量事实。
                        if (translated != current)
                        {
                            SetTextKeepBinding(text, translated);
                            LogWpfWrite("Hook", current, translated);
                        }
                    }
                    else
                    {
                        ContentControl content = (ContentControl)owner;
                        string current = content.Content as string;
                        if (string.IsNullOrEmpty(current)) return;
                        string mixed = FixMixedEnglishPrefix(current);
                        if (mixed != null) { SetContentKeepBinding(content, mixed); return; }
                        if (!HasAsciiLetter(current)) return;
                        string translated = TranslateString(current);
                        if (translated != current)
                        {
                            SetContentKeepBinding(content, translated);
                            LogWpfWrite("Hook", current, translated);
                        }
                    }
                }
                catch { }
                finally { _inWpfTextChanged = false; ExitMutation(); }
            }
        }
    }
}
