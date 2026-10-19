using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Mirrors the public metadata of Command's DarkUI types, without loading or
// executing Command.exe. The real collection's clear event is EventHandler<object>.
public sealed class ObservableListModified<T> : EventArgs
{
    public IEnumerable<T> Items { get; private set; }
    public ObservableListModified(IEnumerable<T> items) { Items = items; }
}

public sealed class ObservableList<T> : List<T>, System.Collections.IEnumerable
{
    public event EventHandler<ObservableListModified<T>> ItemsAdded;
    public event EventHandler<ObservableListModified<T>> ItemsRemoved;
    public event EventHandler<object> ItemsCleared;
    public static int InMutation;
    public int Enumerations;
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        Enumerations++;
        return base.GetEnumerator();
    }
    public int Subscribers
    {
        get { return (ItemsAdded == null ? 0 : ItemsAdded.GetInvocationList().Length)
            + (ItemsRemoved == null ? 0 : ItemsRemoved.GetInvocationList().Length)
            + (ItemsCleared == null ? 0 : ItemsCleared.GetInvocationList().Length); }
    }
    public new void Add(T item) { AddRange(new T[] { item }); }
    public new void AddRange(IEnumerable<T> items)
    {
        List<T> added = new List<T>(items);
        base.AddRange(added);
        InMutation++;
        try { if (ItemsAdded != null) ItemsAdded(this, new ObservableListModified<T>(added)); }
        finally { InMutation--; }
    }
    public new bool Remove(T item)
    {
        if (!base.Remove(item)) return false;
        InMutation++;
        try { if (ItemsRemoved != null) ItemsRemoved(this, new ObservableListModified<T>(new T[] { item })); }
        finally { InMutation--; }
        return true;
    }
    public new void Clear()
    {
        base.Clear();
        InMutation++;
        try { if (ItemsCleared != null) ItemsCleared(this, null); }
        finally { InMutation--; }
    }
}

public sealed class DarkTreeNode
{
    private string text;
    private bool expanded = true;
    public static int TextReads;
    public event EventHandler TextChanged;
    public event EventHandler NodeExpanded;
    public event EventHandler NodeCollapsed;
    public int ExpansionSubscribers { get { return (NodeExpanded == null ? 0 : NodeExpanded.GetInvocationList().Length)
        + (NodeCollapsed == null ? 0 : NodeCollapsed.GetInvocationList().Length); } }
    public Rectangle FullArea { get; set; }
    public bool Expanded
    {
        get { return expanded; }
        set
        {
            bool changed = expanded != value;
            expanded = value;
            if (changed && value && NodeExpanded != null) NodeExpanded(this, EventArgs.Empty);
            if (changed && !value && NodeCollapsed != null) NodeCollapsed(this, EventArgs.Empty);
        }
    }
    public int TextSubscribers { get { return TextChanged == null ? 0 : TextChanged.GetInvocationList().Length; } }
    public string Text
    {
        get { TextReads++; return text; }
        set
        {
            if (text == value) return;
            text = value;
            if (TextChanged != null) TextChanged(this, EventArgs.Empty);
        }
    }
    public ObservableList<DarkTreeNode> Nodes { get; set; }
    public DarkTreeNode ParentNode { get; set; }
    public object Tag { get; set; }
    public DarkTreeNode(string text)
    {
        this.text = text;
        Nodes = new ObservableList<DarkTreeNode>();
        Tag = "Scenarios/English-business-key.scen";
    }
}

public class DarkTreeView : Control
{
    private ObservableList<DarkTreeNode> nodes = new ObservableList<DarkTreeNode>();
    private Rectangle viewport = new Rectangle(0, 0, 320, 180);
    public event EventHandler ViewportChanged;
    public Rectangle Viewport
    {
        get { return viewport; }
        set { viewport = value; if (ViewportChanged != null) ViewportChanged(this, EventArgs.Empty); }
    }
    public ObservableList<DarkTreeNode> Nodes
    {
        get { return nodes; }
        set { nodes = value; Invalidate(); }
    }
    public void RecreateForTest() { RecreateHandle(); }
}

namespace DarkUI.Controls
{
    // Real public DarkUI's node TextChanged handler calls UpdateNodes(), which
    // traverses every expanded row and measures text through CreateGraphics.
    public sealed class DarkTreeView : global::DarkTreeView
    {
        public int LayoutCalls;
        public int Measurements;
        public int LayoutDelayMilliseconds;
        public void AttachLayout(global::DarkTreeNode node) { node.TextChanged += Nodes_TextChanged; }
        public void DetachLayout(global::DarkTreeNode node) { node.TextChanged -= Nodes_TextChanged; }
        private void Nodes_TextChanged(object sender, EventArgs args) { UpdateNodes(); }
        public void UpdateNodes()
        {
            LayoutCalls++;
            if (LayoutDelayMilliseconds > 0) System.Threading.Thread.Sleep(LayoutDelayMilliseconds);
            foreach (global::DarkTreeNode node in Nodes)
                using (Graphics graphics = CreateGraphics())
                {
                    graphics.MeasureString(node.Text, Font);
                    Measurements++;
                }
        }
    }
}

class DarkTreeTranslationTests
{
    private static Type engine;
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static void Call(string method, params object[] args) { engine.GetMethod(method, PrivateStatic).Invoke(null, args); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); }
    }
    private static double Until(Func<bool> ready, int milliseconds = 250)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.ElapsedMilliseconds < milliseconds) Pump(1);
        return elapsed.Elapsed.TotalMilliseconds;
    }
    private static Form Host(DarkTreeView tree)
    {
        Form form = new Form { Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual,
            Size = new Size(320, 180), ShowInTaskbar = false };
        form.Controls.Add(tree);
        // Create only hidden HWNDs. These tests never show or activate windows.
        IntPtr ownerHandle = form.Handle;
        IntPtr treeHandle = tree.Handle;
        return form;
    }
    private static void Search()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            tree.Nodes.Add(new DarkTreeNode("Tree fixture original"));
            Call("TranslateDarkTreeView", tree);
            DarkTreeNode added = new DarkTreeNode("Tree fixture changed");
            TextBox input = new TextBox();
            form.Controls.Add(input);
            input.TextChanged += delegate { tree.Nodes.Clear(); tree.Nodes.Add(added); };
            input.Text = "m";
            Require(added.Text == "Tree fixture changed", "Node text changed inside the game's rebuild stack");
            double latency = Until(delegate { return added.Text == "目录更新"; });
            Require(added.Text == "目录更新", "Search rebuild remained English without a polling scan");
            Require(input.Text == "m", "Search input was rewritten");
            Console.WriteLine("METRIC search_rebuild_ms=" + latency.ToString("F3"));
        }
    }
    private static void Nested()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode parent = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(parent);
            Call("TranslateDarkTreeView", tree);
            DarkTreeNode child = new DarkTreeNode("Tree fixture changed");
            DarkTreeNode leaf = new DarkTreeNode("Tree fixture original");
            child.Nodes.Add(leaf);
            parent.Nodes.Add(child);
            Until(delegate { return leaf.Text == "目录原文"; });
            Require(child.Text == "目录更新" && leaf.Text == "目录原文", "A deep added subtree was not translated");
            leaf.Text = "Tree fixture changed";
            Until(delegate { return leaf.Text == "目录更新"; });
            Require(leaf.Text == "目录更新", "A tracked node TextChanged was not translated");
        }
    }
    private static void Reentrant()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            Call("TranslateDarkTreeView", tree);
            DarkTreeNode first = new DarkTreeNode("Tree fixture original");
            DarkTreeNode replacement = new DarkTreeNode("Tree fixture changed");
            first.TextChanged += delegate
            {
                Require(ObservableList<DarkTreeNode>.InMutation == 0, "Translation reentered a collection mutation");
                if (first.Text == "目录原文") { tree.Nodes.Clear(); tree.Nodes.Add(replacement); }
            };
            tree.Nodes.Add(first);
            Until(delegate { return replacement.Text == "目录更新"; });
            Require(replacement.Text == "目录更新", "A game callback rebuilding the tree lost its replacement");
            Require((string)replacement.Tag == "Scenarios/English-business-key.scen", "A business path/token was rewritten");
        }
    }

    private static void CollectionReentry(string mode)
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode original = new DarkTreeNode("Tree fixture original");
            DarkTreeNode replacement = new DarkTreeNode("Tree fixture changed");
            bool active = false;
            // Game subscriptions already exist before the engine first attaches.
            if (mode == "collection-clear-refill")
                tree.Nodes.ItemsCleared += delegate { if (active) tree.Nodes.Add(replacement); };
            else if (mode == "collection-remove-readd")
                tree.Nodes.ItemsRemoved += delegate
                {
                    if (!active) return;
                    original.Text = "Tree fixture changed";
                    tree.Nodes.Add(original);
                };
            else
                tree.Nodes.ItemsAdded += delegate { if (active) tree.Nodes.Remove(replacement); };
            tree.Nodes.Add(original);
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return original.Text == "目录原文"; });
            active = true;
            if (mode == "collection-clear-refill") tree.Nodes.Clear();
            else if (mode == "collection-remove-readd") tree.Nodes.Remove(original);
            else tree.Nodes.Add(replacement);
            Pump(30);
            // A periodic refresh must not leave the live node permanently untracked.
            for (int i = 0; i < 3; i++) { Call("TranslateDarkTreeView", tree); Pump(10); }
            DarkTreeNode target = mode == "collection-remove-readd" ? original : replacement;
            if (mode == "collection-add-remove")
            {
                Require(tree.Nodes.Count == 1 && object.ReferenceEquals(tree.Nodes[0], original), "Reverted add changed the collection");
                Require(target.Text == "Tree fixture changed" && target.TextSubscribers == 0,
                    "A stale ItemsAdded payload translated or retained a node no longer in the collection");
            }
            else
            {
                Require(tree.Nodes.Count == 1 && object.ReferenceEquals(tree.Nodes[0], target), "Reentrant rebuild lost node identity");
                Require(target.Text == "目录更新" && target.TextSubscribers == 1,
                    "Reentrant clear/remove detached the current live node, even after periodic refresh");
                target.Text = "Tree fixture original";
                Until(delegate { return target.Text == "目录原文"; });
                Require(target.Text == "目录原文", "Reconciled node stopped observing subsequent text changes");
            }
            Require((string)target.Tag == "Scenarios/English-business-key.scen", "Reentrant collection handling changed a business token");
            Console.WriteLine("COLLECTION mode=" + mode + " count=" + tree.Nodes.Count
                + " text_subscribers=" + target.TextSubscribers);
        }
    }

    private static void CollectionMove()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode first = new DarkTreeNode("Tree fixture original");
            DarkTreeNode second = new DarkTreeNode("Tree fixture original");
            DarkTreeNode moved = new DarkTreeNode("Tree fixture original");
            first.Nodes.Add(moved);
            tree.Nodes.AddRange(new DarkTreeNode[] { first, second });
            bool active = false;
            first.Nodes.ItemsRemoved += delegate
            {
                if (!active) return;
                moved.Text = "Tree fixture changed";
                second.Nodes.Add(moved);
            };
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return moved.Text == "目录原文"; });
            active = true;
            first.Nodes.Remove(moved);
            Until(delegate { return moved.Text == "目录更新"; });
            Require(first.Nodes.Count == 0 && second.Nodes.Count == 1 && object.ReferenceEquals(second.Nodes[0], moved),
                "Move changed the live node's identity or owner");
            Require(moved.Text == "目录更新" && moved.TextSubscribers == 1 && moved.ExpansionSubscribers == 2,
                "Move lost or duplicated engine subscriptions");
            second.Nodes.Remove(moved);
            first.Nodes.Add(moved);
            moved.Text = "Tree fixture original";
            Until(delegate { return moved.Text == "目录原文"; });
            Require(object.ReferenceEquals(first.Nodes[0], moved) && moved.TextSubscribers == 1 && moved.Text == "目录原文",
                "Moving a node back lost its current text notification");
        }
    }

    private static void CollectionBurst()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode changed = new DarkTreeNode("Tree fixture original");
            DarkTreeNode untouched = new DarkTreeNode("Tree fixture original");
            untouched.Nodes.Add(new DarkTreeNode("Tree fixture original"));
            tree.Nodes.AddRange(new DarkTreeNode[] { changed, untouched });
            Call("TranslateDarkTreeView", tree);
            Pump(20);
            tree.Nodes.Enumerations = changed.Nodes.Enumerations = untouched.Nodes.Enumerations = 0;
            List<DarkTreeNode> added = new List<DarkTreeNode>();
            for (int i = 0; i < 16; i++)
            {
                DarkTreeNode node = new DarkTreeNode("Tree fixture changed");
                added.Add(node);
                changed.Nodes.Add(node);
            }
            Require(changed.Nodes.Enumerations == 0, "Each add synchronously scanned the changed collection");
            Until(delegate { return added.TrueForAll(delegate(DarkTreeNode node) { return node.Text == "目录更新"; }); });
            Require(added.TrueForAll(delegate(DarkTreeNode node) { return node.Text == "目录更新"; }), "Coalesced additions did not finish");
            Require(changed.Nodes.Enumerations == 1, "An add burst was not reconciled in one membership scan");
            Require(tree.Nodes.Enumerations == 0 && untouched.Nodes.Enumerations == 0,
                "Reconciling one changed collection scanned unrelated existing collections");
            Console.WriteLine("COLLECTION burst=16 changed_enumerations=" + changed.Nodes.Enumerations
                + " unrelated_enumerations=" + (tree.Nodes.Enumerations + untouched.Nodes.Enumerations));
        }
    }

    private static void CollectionRemovalDuringWrite()
    {
        // Warm the ordinary translation path before testing callback ordering.
        using (DarkTreeView warm = new DarkTreeView())
        using (Form host = Host(warm))
        {
            warm.Nodes.Add(new DarkTreeNode("Tree fixture original"));
            Call("TranslateDarkTreeView", warm);
        }
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            int removedWrites = 0;
            bool rebuilt = false;
            DarkTreeNode replacement = new DarkTreeNode("Tree fixture changed");
            for (int i = 0; i < 16; i++)
            {
                DarkTreeNode node = new DarkTreeNode("Tree fixture original");
                node.TextChanged += delegate
                {
                    if (node.Text != "目录原文") return;
                    removedWrites++;
                    if (rebuilt) return;
                    rebuilt = true;
                    tree.Nodes.Clear();
                    tree.Nodes.Add(replacement);
                };
                tree.Nodes.Add(node);
            }
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return replacement.Text == "目录更新"; });
            Require(replacement.Text == "目录更新", "A callback replacement was not translated");
            Require(removedWrites == 1, "The same translation slice rewrote nodes removed by an earlier callback: " + removedWrites);
            Require(tree.Nodes.Count == 1 && object.ReferenceEquals(tree.Nodes[0], replacement), "Rebuild lost its live replacement");
        }
    }
    private static void Replace()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode old = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(old);
            Call("TranslateDarkTreeView", tree);
            ObservableList<DarkTreeNode> oldRoots = tree.Nodes;
            DarkTreeNode root = new DarkTreeNode("Tree fixture changed");
            tree.Nodes = new ObservableList<DarkTreeNode> { root };
            Until(delegate { return root.Text == "目录更新"; });
            Require(root.Text == "目录更新", "Root collection replacement was not discovered on invalidation");
            Require(oldRoots.Subscribers == 0 && old.TextSubscribers == 0, "Old root collection subscriptions leaked");
            ObservableList<DarkTreeNode> oldChildren = root.Nodes;
            DarkTreeNode leaf = new DarkTreeNode("Tree fixture original");
            root.Nodes = new ObservableList<DarkTreeNode> { leaf };
            // Real Nodes setters are silent. The retained periodic entry point
            // must safely reconcile a wholesale child collection replacement.
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return leaf.Text == "目录原文"; });
            Require(leaf.Text == "目录原文" && oldChildren.Subscribers == 0, "Silent child collection replacement was not reconciled");
            leaf.Text = "Tree fixture changed";
            tree.RecreateForTest();
            Until(delegate { return leaf.Text == "目录更新"; });
            Require(leaf.Text == "目录更新", "Handle recreation lost queued text translation");
        }
    }
    private static void DisposeAndRemove()
    {
        DarkTreeView tree = new DarkTreeView();
        using (Form form = Host(tree))
        {
            DarkTreeNode root = new DarkTreeNode("Tree fixture original");
            DarkTreeNode child = new DarkTreeNode("Tree fixture original");
            root.Nodes.Add(child);
            tree.Nodes.Add(root);
            Call("TranslateDarkTreeView", tree);
            Require(root.TextSubscribers > 0 && root.Nodes.Subscribers > 0, "Fixture did not attach node notifications");
            tree.Nodes.Remove(root);
            Until(delegate { return root.TextSubscribers == 0; });
            Require(root.TextSubscribers == 0 && child.TextSubscribers == 0 && root.ExpansionSubscribers == 0
                && child.ExpansionSubscribers == 0 && root.Nodes.Subscribers == 0,
                "Removed subtree retained engine subscriptions");
            child.Text = "Tree fixture changed";
            Pump(20);
            Require(child.Text == "Tree fixture changed", "Detached node was still rewritten");
            tree.Nodes.Add(root);
            ObservableList<DarkTreeNode> roots = tree.Nodes;
            tree.Dispose();
            Require(roots.Subscribers == 0 && root.TextSubscribers == 0 && child.TextSubscribers == 0
                && root.ExpansionSubscribers == 0 && child.ExpansionSubscribers == 0,
                "Disposal did not unsubscribe the root and all nodes");
            Pump(20);
        }
    }
    private static void BeforeHandle()
    {
        using (DarkTreeView tree = new DarkTreeView())
        {
            Call("TranslateDarkTreeView", tree);
            DarkTreeNode node = new DarkTreeNode("Tree fixture changed");
            tree.Nodes.Add(node);
            Require(!tree.IsHandleCreated, "Translation forced an early native handle");
            using (Form form = Host(tree))
            {
                Until(delegate { return node.Text == "目录更新"; });
                Require(node.Text == "目录更新", "Queued pre-handle work was lost after HandleCreated");
            }
        }
    }
    private static void Benchmark()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode parent = new DarkTreeNode("Tree fixture original");
            for (int i = 0; i < 1000; i++) parent.Nodes.Add(new DarkTreeNode("Tree fixture original"));
            tree.Nodes.Add(parent);
            int initialTranslated = 0;
            parent.TextChanged += delegate { initialTranslated++; };
            foreach (DarkTreeNode node in parent.Nodes) node.TextChanged += delegate { initialTranslated++; };
            DarkTreeNode.TextReads = 0;
            Stopwatch attach = Stopwatch.StartNew();
            Call("TranslateDarkTreeView", tree);
            double firstSliceMs = attach.Elapsed.TotalMilliseconds;
            Until(delegate { return initialTranslated == 1001; }, 3000);
            attach.Stop();
            Require(initialTranslated == 1001, "Deferred initial tree translation never completed");
            Console.WriteLine("METRIC initial_nodes=1001 attach_ms=" + attach.Elapsed.TotalMilliseconds.ToString("F3")
                + " first_slice_ms=" + firstSliceMs.ToString("F3") + " initial_text_reads=" + DarkTreeNode.TextReads);
            Pump(20);
            DarkTreeNode.TextReads = 0;
            List<DarkTreeNode> added = new List<DarkTreeNode>();
            for (int i = 0; i < 20; i++) added.Add(new DarkTreeNode("Tree fixture changed"));
            Stopwatch elapsed = Stopwatch.StartNew();
            int translated = 0;
            double translationMs = -1;
            foreach (DarkTreeNode node in added) node.TextChanged += delegate
            {
                if (++translated == 20) translationMs = elapsed.Elapsed.TotalMilliseconds;
            };
            parent.Nodes.AddRange(added);
            Until(delegate { return translated == 20; });
            int reads = DarkTreeNode.TextReads;
            foreach (DarkTreeNode node in added) Require(node.Text == "目录更新", "Benchmark additions stayed English");
            Require(reads <= 60, "Incremental batch scanned unrelated old nodes: reads=" + reads);
            Console.WriteLine("METRIC old_nodes=1000 added_nodes=20 incremental_text_reads=" + reads
                + " batch_translation_ms=" + translationMs.ToString("F3"));
            DarkTreeNode.TextReads = 0;
            parent.Text = "Tree fixture changed";
            Pump(30);
            int renameReads = DarkTreeNode.TextReads;
            Require(parent.Text == "目录更新" && renameReads == 1,
                "Renaming a parent walked its unchanged children: reads=" + renameReads);
            Console.WriteLine("METRIC parent_rename_text_reads=" + renameReads);
        }
    }
    private static void LayoutBatch()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            List<string> businessOrder = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                DarkTreeNode node = new DarkTreeNode("Tree fixture original");
                tree.AttachLayout(node);
                node.TextChanged += delegate { businessOrder.Add("A"); };
                node.TextChanged += delegate { businessOrder.Add("B"); };
                tree.Nodes.Add(node);
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return businessOrder.Count == 128; }, 5000);
            Console.WriteLine("METRIC layout_rows=64 layout_calls=" + tree.LayoutCalls + " measurements=" + tree.Measurements
                + " business_callbacks=" + businessOrder.Count + " translation_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3"));
            Require(businessOrder.Count == 128, "Layout batching suppressed business TextChanged subscribers");
            for (int i = 0; i < businessOrder.Count; i++)
                Require(businessOrder[i] == (i % 2 == 0 ? "A" : "B"), "Business TextChanged order changed");
            Require(tree.LayoutCalls == 64 && tree.Measurements == 4096,
                "Translation altered the tree's normal synchronous layout events");
        }
    }
    private static void LayoutUnsubscribe(bool rehook)
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode node = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(node);
            tree.AttachLayout(node);
            bool changed = false;
            node.TextChanged += delegate
            {
                if (changed) return;
                changed = true;
                tree.DetachLayout(node);
                if (rehook) tree.AttachLayout(node);
            };
            Call("TranslateDarkTreeView", tree);
            int listeners = 0;
            foreach (Delegate handler in Handlers(node)) if (object.ReferenceEquals(handler.Target, tree)) listeners++;
            Require(listeners == (rehook ? 1 : 0), "A tree subscriber removed during translation was restored/duplicated: " + listeners);
            int layouts = tree.LayoutCalls;
            node.Text = "Unmapped fixture text";
            Require(tree.LayoutCalls - layouts == (rehook ? 1 : 0), "Subsequent changes invoke an incorrectly restored layout listener");
        }
    }
    private static void ExceptionIsolation()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode failing = new DarkTreeNode("Tree fixture original");
            DarkTreeNode sibling = new DarkTreeNode("Tree fixture changed");
            failing.TextChanged += delegate
            {
                if (failing.Text != "目录原文") return;
                failing.Text = "Tree fixture original";
                throw new InvalidOperationException("Persistent fixture business callback failure");
            };
            tree.Nodes.Add(failing);
            tree.Nodes.Add(sibling);
            for (int i = 0; i < 3; i++) Call("TranslateDarkTreeView", tree);
            Until(delegate { return sibling.Text == "目录更新"; });
            Require(sibling.Text == "目录更新", "A failing node prevented its sibling from translating");
        }
    }
    private static void ExceptionWithChildReplacement()
    {
        using (DarkTreeView tree = new DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode parent = new DarkTreeNode("Tree fixture original");
            DarkTreeNode leaf = new DarkTreeNode("Tree fixture changed");
            ObservableList<DarkTreeNode> oldChildren = parent.Nodes;
            parent.TextChanged += delegate
            {
                if (parent.Text != "目录原文") return;
                parent.Nodes = new ObservableList<DarkTreeNode> { leaf };
                parent.Text = "Tree fixture original";
                throw new InvalidOperationException("Parent callback failed after silently replacing children");
            };
            tree.Nodes.Add(parent);
            for (int i = 0; i < 3; i++) Call("TranslateDarkTreeView", tree);
            Until(delegate { return leaf.Text == "目录更新"; }, 1000);
            Require(leaf.Text == "目录更新" && oldChildren.Subscribers == 0,
                "A failing parent hid its silently replaced healthy subtree");
        }
    }
    private static void VisiblePriority()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            tree.LayoutDelayMilliseconds = 2;
            tree.Viewport = new Rectangle(0, 0, 320, 40);
            List<int> translated = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                int index = i;
                DarkTreeNode node = new DarkTreeNode("Tree fixture original");
                node.FullArea = new Rectangle(0, i == 50 ? 0 : i == 51 ? 20 : 1000 + i * 20, 320, 20);
                tree.AttachLayout(node);
                node.TextChanged += delegate { translated.Add(index); };
                tree.Nodes.Add(node);
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            Call("TranslateDarkTreeView", tree);
            double firstSliceMs = elapsed.Elapsed.TotalMilliseconds;
            Require(translated.Count > 0 && translated[0] == 50, "An offscreen row was translated before the current viewport");
            Require(translated.Count < 64 && firstSliceMs < 80, "Initial translation blocked on the whole large tree");
            int firstCount = translated.Count;
            Application.DoEvents();
            Require(translated.Count < 64, "BeginInvoke continuations drained the whole backlog without yielding");
            Until(delegate { return translated.Count == 64; }, 5000);
            Require(translated.Count == 64, "Deferred offscreen rows never completed");
            Console.WriteLine("METRIC visible_priority_first_slice_ms=" + firstSliceMs.ToString("F3")
                + " first_slice_nodes=" + firstCount + " completed_nodes=" + translated.Count);
        }
    }
    private static void ExpandAndScroll()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            tree.LayoutDelayMilliseconds = 2;
            tree.Viewport = new Rectangle(0, 0, 320, 40);
            DarkTreeNode parent = new DarkTreeNode("Tree fixture original") { Expanded = false,
                FullArea = new Rectangle(0, 1000, 320, 20) };
            tree.Nodes.Add(parent);
            List<DarkTreeNode> children = new List<DarkTreeNode>();
            List<DarkTreeNode> translated = new List<DarkTreeNode>();
            for (int i = 0; i < 24; i++)
            {
                DarkTreeNode child = new DarkTreeNode("Tree fixture original")
                    { FullArea = new Rectangle(0, 1000 + i * 20, 320, 20) };
                children.Add(child);
                parent.Nodes.Add(child);
                tree.AttachLayout(child);
                DarkTreeNode captured = child;
                child.TextChanged += delegate { translated.Add(captured); };
            }
            for (int i = 0; i < 32; i++)
            {
                DarkTreeNode node = new DarkTreeNode("Tree fixture original")
                    { FullArea = new Rectangle(0, 2000 + i * 20, 320, 20) };
                tree.AttachLayout(node);
                tree.Nodes.Add(node);
            }
            Call("TranslateDarkTreeView", tree);
            DarkTreeNode exposed = children[23];
            Require(exposed.Text == "Tree fixture original", "Fixture did not leave a folded backlog");
            parent.Expanded = true;
            tree.Viewport = exposed.FullArea;
            tree.RecreateForTest();
            Until(delegate { return exposed.Text == "目录原文"; }, 1000);
            Require(exposed.Text == "目录原文" && translated.IndexOf(exposed) < 8,
                "Expanded/scrolled-into-view row stayed behind folded/offscreen work");
            // Collapse again while deferred work remains. Folded nodes must
            // still finish eventually, and reopening must retain translations.
            parent.Expanded = false;
            Until(delegate { return translated.Count == 24; }, 5000);
            Require(translated.Count == 24, "Folded nodes were starved permanently");
            parent.Expanded = true;
            Require(children[0].Text == "目录原文" && children[23].Text == "目录原文", "Reopening lost translated child text");

            DarkTreeNode queued = new DarkTreeNode("Tree fixture changed");
            parent.Nodes.Add(queued);
            int beforeDispose = tree.LayoutCalls;
            tree.Dispose();
            Pump(50);
            Require(queued.Text == "Tree fixture changed" && tree.LayoutCalls == beforeDispose,
                "A disposed tree ran deferred translation/layout");
            Require(queued.TextSubscribers == 0 && queued.ExpansionSubscribers == 0,
                "Disposal retained deferred node subscribers");
        }
    }
    private static void CollapsePriority()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            tree.LayoutDelayMilliseconds = 8;
            tree.Viewport = new Rectangle(0, 0, 320, 40);
            DarkTreeNode oldBranch = new DarkTreeNode("Tree fixture original") { FullArea = new Rectangle(0, 1000, 320, 20) };
            DarkTreeNode newBranch = new DarkTreeNode("Tree fixture original") { Expanded = false,
                FullArea = new Rectangle(0, 1000, 320, 20) };
            List<string> changes = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                DarkTreeNode oldChild = new DarkTreeNode("Tree fixture original") { FullArea = new Rectangle(0, i * 20, 320, 20) };
                oldChild.TextChanged += delegate { changes.Add("old"); };
                oldBranch.Nodes.Add(oldChild);
                tree.AttachLayout(oldChild);
                DarkTreeNode newChild = new DarkTreeNode("Tree fixture original") { FullArea = oldChild.FullArea };
                newChild.TextChanged += delegate { changes.Add("new"); };
                newBranch.Nodes.Add(newChild);
                tree.AttachLayout(newChild);
            }
            tree.Nodes.Add(oldBranch);
            tree.Nodes.Add(newBranch);
            Call("TranslateDarkTreeView", tree);
            Require(changes.Count == 1 && changes[0] == "old", "Fixture did not leave a visible queued row");
            oldBranch.Expanded = false;
            newBranch.Expanded = true; // Same viewport; no ViewportChanged event.
            Until(delegate { return changes.Count > 1; }, 1000);
            Require(changes.Count > 1 && changes[1] == "new", "A collapsed branch retained stale visible priority");
            Until(delegate { return changes.Count == 4; }, 3000);
            Require(changes.Count == 4, "Reclassified folded rows never completed");
        }
    }
    private static void CallbackScrollPriority()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            tree.LayoutDelayMilliseconds = 8;
            tree.Viewport = new Rectangle(0, 0, 320, 40);
            List<int> changes = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                DarkTreeNode node = new DarkTreeNode("Tree fixture original")
                    { FullArea = new Rectangle(0, i < 2 ? i * 20 : 1000 + (i - 2) * 20, 320, 20) };
                tree.AttachLayout(node);
                node.TextChanged += delegate
                {
                    changes.Add(index);
                    if (index == 0) tree.Viewport = new Rectangle(0, 1000, 320, 40);
                };
                tree.Nodes.Add(node);
            }
            Call("TranslateDarkTreeView", tree);
            Until(delegate { return changes.Count > 1; }, 1000);
            Require(changes.Count > 1 && changes[0] == 0 && changes[1] == 2,
                "A business callback's viewport scroll was ignored during translation");
            Until(delegate { return changes.Count == 4; }, 3000);
            Require(changes.Count == 4, "Viewport reclassification lost deferred rows");
        }
    }
    private static Delegate[] Handlers(DarkTreeNode node)
    {
        Delegate handlers = (Delegate)typeof(DarkTreeNode).GetField("TextChanged",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(node);
        return handlers == null ? new Delegate[0] : handlers.GetInvocationList();
    }
    private static void RequireHandlers(DarkTreeNode node, params Delegate[] expected)
    {
        Delegate[] actual = Handlers(node);
        Require(actual.Length == expected.Length, "Subscriber count changed: " + actual.Length + " != " + expected.Length);
        for (int i = 0; i < actual.Length; i++)
            Require(actual[i].Equals(expected[i]), "Subscriber identity/order changed at index " + i);
    }
    private static void LayoutSubscriptions()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode node = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(node);
            List<string> order = new List<string>();
            EventHandler removed = delegate { order.Add("B"); };
            EventHandler added = delegate { order.Add("C"); };
            bool first = true;
            EventHandler before = delegate
            {
                order.Add("A");
                if (!first) return;
                first = false;
                node.TextChanged -= removed;
                node.TextChanged += added;
            };
            node.TextChanged += before;
            tree.AttachLayout(node);
            EventHandler layout = (EventHandler)Handlers(node)[1];
            node.TextChanged += removed;
            EventHandler boundsReader = delegate { Require(tree.LayoutCalls == 1, "Business callback observed artificially delayed layout"); };
            node.TextChanged += boundsReader;
            Call("TranslateDarkTreeView", tree);
            Require(string.Join("", order.ToArray()) == "AB", "Business subscription changes were lost or reordered");
            Delegate engineHandler = Handlers(node)[3];
            RequireHandlers(node, before, layout, boundsReader, engineHandler, added);
            node.TextChanged -= boundsReader;
            order.Clear();
            node.Text = "outside";
            Require(tree.LayoutCalls == 2 && string.Join("", order.ToArray()) == "AC", "Normal events or business subscriptions changed");
        }
    }
    private static void LayoutRemoval()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode node = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(node);
            tree.AttachLayout(node);
            int business = 0;
            EventHandler listener = delegate { business++; };
            node.TextChanged += listener;
            Call("TranslateDarkTreeView", tree);
            tree.Nodes.Remove(node);
            tree.DetachLayout(node);
            int priorBusiness = business;
            node.Text = "Tree fixture changed";
            Until(delegate { return Handlers(node).Length == 1; });
            RequireHandlers(node, listener);
            Require(node.Text == "Tree fixture changed" && business == priorBusiness + 1,
                "A pending removed node was translated before membership reconciliation");
            int layouts = tree.LayoutCalls;
            node.Text = "detached";
            Require(tree.LayoutCalls == layouts && business == priorBusiness + 2, "Removed node retained a tree callback or lost business events");

            tree.Nodes.Add(node);
            tree.AttachLayout(node);
            Call("TranslateDarkTreeView", tree);
            Require(Handlers(node).Length == 3, "Reattachment duplicated or removed a subscriber");
            layouts = tree.LayoutCalls;
            node.Text = "after reattach";
            Require(tree.LayoutCalls == layouts + 1, "Fresh layout listener did not survive removal of the old proxy");
        }
    }
    private static void LayoutExit()
    {
        using (DarkUI.Controls.DarkTreeView tree = new DarkUI.Controls.DarkTreeView())
        using (Form form = Host(tree))
        {
            DarkTreeNode node = new DarkTreeNode("Tree fixture original");
            tree.Nodes.Add(node);
            tree.AttachLayout(node);
            Delegate layout = Handlers(node)[0];
            EventHandler throwing = delegate { throw new InvalidOperationException("fixture callback failure"); };
            node.TextChanged += throwing;
            Call("TranslateDarkTreeView", tree);
            Delegate engineHandler = Handlers(node)[2];
            RequireHandlers(node, layout, throwing, engineHandler);
            Require(node.Text == "目录原文" && tree.LayoutCalls == 1, "Exceptional node write lost normal layout");
            node.TextChanged -= throwing;
            EventHandler disposing = delegate { tree.Dispose(); };
            node.TextChanged += disposing;
            node.Text = "dispose";
            RequireHandlers(node, layout, disposing);
            int layouts = tree.LayoutCalls;
            Pump(30);
            Require(tree.LayoutCalls == layouts, "Disposed tree ran queued translation");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Usage: DarkTreeTranslationTests <dll> <dict> <mode>");
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            Call("LoadDictionaries", args[1]);
            Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
            exact["Tree fixture original"] = "目录原文";
            exact["Tree fixture changed"] = "目录更新";
            Control.CheckForIllegalCrossThreadCalls = true;
            string mode = args[2];
            if (mode == "search") Search();
            else if (mode == "nested") Nested();
            else if (mode == "reentrant") Reentrant();
            else if (mode == "collection-clear-refill" || mode == "collection-remove-readd" || mode == "collection-add-remove") CollectionReentry(mode);
            else if (mode == "collection-move") CollectionMove();
            else if (mode == "collection-burst") CollectionBurst();
            else if (mode == "collection-during-write") CollectionRemovalDuringWrite();
            else if (mode == "replace") Replace();
            else if (mode == "dispose") DisposeAndRemove();
            else if (mode == "prehandle") BeforeHandle();
            else if (mode == "benchmark") Benchmark();
            else if (mode == "layout") LayoutBatch();
            else if (mode == "layout-subscriptions") LayoutSubscriptions();
            else if (mode == "layout-removal") LayoutRemoval();
            else if (mode == "layout-exit") LayoutExit();
            else if (mode == "layout-unsubscribe") LayoutUnsubscribe(false);
            else if (mode == "layout-rehook") LayoutUnsubscribe(true);
            else if (mode == "exception") ExceptionIsolation();
            else if (mode == "exception-replace") ExceptionWithChildReplacement();
            else if (mode == "visible-priority") VisiblePriority();
            else if (mode == "expand-scroll") ExpandAndScroll();
            else if (mode == "collapse-priority") CollapsePriority();
            else if (mode == "callback-scroll") CallbackScrollPriority();
            else throw new ArgumentException("Unknown mode " + mode);
            Console.WriteLine("PASS " + mode);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
