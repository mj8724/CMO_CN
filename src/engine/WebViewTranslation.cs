using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private sealed class WebTranslationState
        {
            public Control Owner;
            public object Core;
            public MethodInfo Execute;
            public EventInfo Initialization;
            public EventInfo Messages;
            public Delegate InitializationHandler;
            public Delegate MessageHandler;
            public Task<string> Registration;
            public string ScriptId;
            public bool Disposed;
            public bool SourceWarningLogged;
            public bool CapabilityWarningLogged;
            public bool Fallback;
            public bool FallbackInFlight;
            public int FallbackVersion;
            public int FallbackDrainCount;
            public System.Windows.Forms.Timer FallbackTimer;
        }

        private static readonly ConditionalWeakTable<Control, WebTranslationState> webTranslations =
            new ConditionalWeakTable<Control, WebTranslationState>();
        private static readonly object webScriptLock = new object();
        private static string webTranslationScript;

        // Called by discovery, but does no DOM work after this particular control is attached.
        private static void TranslateWebViewEarly(Control ctrl)
        {
            if (ctrl == null || ctrl.IsDisposed) return;
            if (ctrl.InvokeRequired)
            {
                try { ctrl.BeginInvoke(new Action(() => TranslateWebViewEarly(ctrl))); }
                catch (InvalidOperationException) { } // The control can close between discovery and dispatch.
                return;
            }
            WebTranslationState state = webTranslations.GetValue(ctrl, CreateWebTranslationState);
            AttachWebTranslation(state);
        }

        private static WebTranslationState CreateWebTranslationState(Control owner)
        {
            var state = new WebTranslationState { Owner = owner };
            state.Initialization = owner.GetType().GetEvent("CoreWebView2InitializationCompleted");
            if (state.Initialization != null)
            {
                state.InitializationHandler = WebEventDelegate(state.Initialization, (sender, args) => AttachWebTranslation(state));
                state.Initialization.AddEventHandler(owner, state.InitializationHandler);
            }
            owner.Disposed += (sender, args) =>
            {
                state.Disposed = true;
                if (state.FallbackTimer != null) state.FallbackTimer.Dispose();
                DetachWebMessages(state);
                if (state.InitializationHandler != null)
                    state.Initialization.RemoveEventHandler(owner, state.InitializationHandler);
                webTranslations.Remove(owner);
            };
            owner.HandleDestroyed += (sender, args) =>
            {
                state.FallbackVersion++;
                state.FallbackInFlight = false;
                if (state.FallbackTimer != null) state.FallbackTimer.Stop();
            };
            owner.HandleCreated += (sender, args) =>
            {
                if (state.Fallback) { state.FallbackTimer.Start(); PollWebFallback(state, true); }
            };
            return state;
        }

        private static Delegate WebEventDelegate(EventInfo info, Action<object, object> callback)
        {
            ParameterInfo[] parameters = info.EventHandlerType.GetMethod("Invoke").GetParameters();
            ParameterExpression sender = Expression.Parameter(parameters[0].ParameterType, "sender");
            ParameterExpression args = Expression.Parameter(parameters[1].ParameterType, "args");
            return Expression.Lambda(info.EventHandlerType,
                Expression.Invoke(Expression.Constant(callback), Expression.Convert(sender, typeof(object)),
                    Expression.Convert(args, typeof(object))), sender, args).Compile();
        }

        private static void DetachWebMessages(WebTranslationState state)
        {
            try
            {
                if (state.Messages != null && state.MessageHandler != null)
                    state.Messages.RemoveEventHandler(state.Core, state.MessageHandler);
            }
            catch (Exception ex) { Log("Web translation detach: " + ex.GetBaseException().Message); }
            state.MessageHandler = null;
            state.Messages = null;
        }

        private static void AttachWebTranslation(WebTranslationState state)
        {
            if (state.Disposed) return;
            try
            {
                PropertyInfo property = state.Owner.GetType().GetProperty("CoreWebView2");
                object core = property == null ? null : property.GetValue(state.Owner, null);
                if (core == null) return;
                if (ReferenceEquals(state.Core, core))
                {
                    if (!state.Fallback && !WebMessagesEnabled(core)) EnableWebFallback(state);
                    return;
                }
                DetachWebMessages(state);
                if (state.FallbackTimer != null) state.FallbackTimer.Stop();
                state.Fallback = false;
                state.FallbackInFlight = false;
                state.FallbackVersion++;
                state.Registration = null;
                state.ScriptId = null;
                state.CapabilityWarningLogged = false;
                state.Core = core;
                state.Execute = core.GetType().GetMethod("ExecuteScriptAsync", new Type[] { typeof(string) });
                MethodInfo register = core.GetType().GetMethod("AddScriptToExecuteOnDocumentCreatedAsync", new Type[] { typeof(string) });
                state.Messages = core.GetType().GetEvent("WebMessageReceived");
                if (state.Execute == null)
                {
                    WarnWebCapability(state, "script execution API unavailable");
                    return;
                }
                if (state.Messages != null)
                {
                    state.MessageHandler = WebEventDelegate(state.Messages, (sender, args) => ReceiveWebTranslation(state, args));
                    state.Messages.AddEventHandler(core, state.MessageHandler);
                }
                bool messagesEnabled = state.Messages != null && WebMessagesEnabled(core);
                string script = GetWebTranslationScript(messagesEnabled);
                if (register == null)
                {
                    WarnWebCapability(state, "document-created API unavailable; using incremental script fallback");
                    EnableWebFallback(state);
                    return;
                }
                Task<string> registration = (Task<string>)register.Invoke(core, new object[] { script });
                state.Registration = registration;
                registration.ContinueWith(task =>
                {
                    string failure = task.IsFaulted ? task.Exception.GetBaseException().Message : task.IsCanceled ? "canceled" : null;
                    // Registration covers future navigations only. Catch up the current document
                    // after it completes, without waiting on the WebView's UI message pump.
                    PostWebTranslation(state, () =>
                    {
                        if (!ReferenceEquals(state.Core, core)) return;
                        if (failure != null)
                        {
                            WarnWebCapability(state, "registration failed; using incremental script fallback: " + failure);
                            EnableWebFallback(state);
                        }
                        else
                        {
                            state.ScriptId = task.Result;
                            if (messagesEnabled && !state.Fallback) ExecuteWebTranslation(state, script);
                            else EnableWebFallback(state);
                        }
                    });
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                WarnWebCapability(state, "attach failed; using incremental script fallback: " + ex.GetBaseException().Message);
                if (state.Execute != null) EnableWebFallback(state);
            }
        }

        private static bool WebMessagesEnabled(object core)
        {
            PropertyInfo settingsProperty = core.GetType().GetProperty("Settings");
            object settings = settingsProperty == null ? null : settingsProperty.GetValue(core, null);
            PropertyInfo enabled = settings == null ? null : settings.GetType().GetProperty("IsWebMessageEnabled");
            return enabled == null || (bool)enabled.GetValue(settings, null);
        }

        private static void WarnWebCapability(WebTranslationState state, string message)
        {
            if (state.CapabilityWarningLogged) return;
            state.CapabilityWarningLogged = true;
            Log("Web translation: " + message);
        }

        private static void EnableWebFallback(WebTranslationState state)
        {
            if (state.Disposed || state.Execute == null || state.Fallback) return;
            state.Fallback = true;
            if (state.FallbackTimer == null)
            {
                state.FallbackTimer = new System.Windows.Forms.Timer { Interval = 250 };
                state.FallbackTimer.Tick += (sender, args) =>
                {
                    if (state.FallbackInFlight) return;
                    state.FallbackDrainCount = 0;
                    PollWebFallback(state, false);
                };
            }
            state.FallbackTimer.Start();
            PollWebFallback(state, true);
        }

        private static void PollWebFallback(WebTranslationState state, bool install)
        {
            if (state.Disposed || state.FallbackInFlight || !state.Owner.IsHandleCreated) return;
            object core = state.Core;
            string source;
            try { source = WebNativeSource(core); if (!TrustedWebSource(source)) return; }
            catch (Exception) { return; }
            // The observer retains only changed text. A normal tick reads one bounded
            // batch, never the body. Missing registration installs once per document.
            string script = (install ? GetWebTranslationScript(false) + "\n;" : "")
                + "window.__cmoWebTranslation ? window.__cmoWebTranslation.take() : 'CMO_WEB_INSTALL'";
            int version = state.FallbackVersion;
            state.FallbackInFlight = true;
            try
            {
                Task<string> task = (Task<string>)state.Execute.Invoke(core, new object[] { script });
                task.ContinueWith(completed =>
                {
                    // Observe failures even if disposal cancels the UI callback.
                    string failure = completed.IsFaulted ? completed.Exception.GetBaseException().Message
                        : completed.IsCanceled ? "canceled" : null;
                    PostWebTranslation(state, () =>
                    {
                        if (!ReferenceEquals(state.Core, core) || version != state.FallbackVersion) return;
                        state.FallbackInFlight = false;
                        if (failure != null)
                        {
                            WarnWebCapability(state, "fallback script unavailable");
                            return;
                        }
                        if (!string.Equals(source, WebNativeSource(core), StringComparison.Ordinal)
                            || !TrustedWebSource(WebNativeSource(core))) return;
                        string result = completed.Result;
                        if (result == "\"CMO_WEB_INSTALL\"") { PollWebFallback(state, true); return; }
                        // take() returns URI-encoded ASCII, so no general JSON parser is
                        // needed for ExecuteScriptAsync's JSON-quoted string result.
                        if (string.IsNullOrEmpty(result) || result.Length < 2 || result.Length > 1048576 || result[0] != '"'
                            || result[result.Length - 1] != '"' || result.IndexOf('\\') >= 0) return;
                        string message = Uri.UnescapeDataString(result.Substring(1, result.Length - 2));
                        ProcessWebTranslationMessage(state, message, source);
                        if (message.StartsWith("CMO_WEB_V1\t", StringComparison.Ordinal) && ++state.FallbackDrainCount < 8)
                            PollWebFallback(state, false);
                    });
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                state.FallbackInFlight = false;
                WarnWebCapability(state, "fallback script unavailable: " + ex.GetBaseException().Message);
            }
        }

        private static void PostWebTranslation(WebTranslationState state, Action action)
        {
            if (state.Disposed || state.Owner.IsDisposed || !state.Owner.IsHandleCreated) return;
            try
            {
                state.Owner.BeginInvoke(new Action(() =>
                {
                    if (state.Disposed) return;
                    try { action(); }
                    catch (Exception ex) { Log("Web translation UI callback: " + ex.GetBaseException().Message); }
                }));
            }
            catch (InvalidOperationException) { } // Control disposal can race a background reply.
        }

        private static void ExecuteWebTranslation(WebTranslationState state, string script)
        {
            if (state.Disposed) return;
            try
            {
                Task<string> task = (Task<string>)state.Execute.Invoke(state.Core, new object[] { script });
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted) Log("Web translation script: " + t.Exception.GetBaseException().Message);
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { Log("Web translation execute: " + ex.GetBaseException().Message); }
        }

        private static void ReceiveWebTranslation(WebTranslationState state, object args)
        {
            if (state.Disposed) return;
            try
            {
                string message = args.GetType().GetMethod("TryGetWebMessageAsString").Invoke(args, null) as string;
                PropertyInfo sourceProperty = args.GetType().GetProperty("Source");
                string eventSource = sourceProperty == null ? null : sourceProperty.GetValue(args, null) as string;
                ProcessWebTranslationMessage(state, message, eventSource);
            }
            catch (Exception) { } // Other game messages may have an unrelated shape.
        }

        private static void ProcessWebTranslationMessage(WebTranslationState state, string message, string eventSource)
        {
            string nonce;
            var items = new List<KeyValuePair<int, string>>();
            try
            {
                if (message == null || !message.StartsWith("CMO_WEB_V1\t", StringComparison.Ordinal) || message.Length > 524288) return;
                string nativeSource = WebNativeSource(state.Core);
                if (!TrustedWebSource(nativeSource) || (eventSource != null && !TrustedWebSource(eventSource)))
                {
                    if (!state.SourceWarningLogged) Log("Web translation: untrusted document origin; native bridge ignored");
                    state.SourceWarningLogged = true;
                    return;
                }
                string[] lines = message.Split('\n');
                if (lines.Length < 2 || lines.Length > 65) return;
                nonce = lines[0].Substring(11);
                if (nonce.Length == 0 || nonce.Length > 80) return;
                for (int i = 1; i < lines.Length; i++)
                {
                    int tab = lines[i].IndexOf('\t');
                    int id;
                    if (tab < 1 || !int.TryParse(lines[i].Substring(0, tab), out id) || id < 1) return;
                    string source = Uri.UnescapeDataString(lines[i].Substring(tab + 1));
                    if (source.Length > 16384 || source.Trim().Length == 0) return;
                    items.Add(new KeyValuePair<int, string>(id, source));
                }
            }
            catch (Exception) { return; } // Native event boundaries must also tolerate unrelated/malformed page messages.
            object core = state.Core;
            ThreadPool.QueueUserWorkItem(ignored =>
            {
                try
                {
                    var reply = new StringBuilder("window.__cmoWebTranslation && window.__cmoWebTranslation.receive(");
                    reply.Append(WebJsString(nonce)).Append(",[");
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) reply.Append(',');
                        string source = items[i].Value;
                        string key = source.Trim();
                        // This bridge translates text only: it must never resolve game document commands.
                        string translated = key.IndexOf("[LOADDOC]", StringComparison.OrdinalIgnoreCase) >= 0 ? key : TranslateBriefingText(key);
                        int offset = source.IndexOf(key, StringComparison.Ordinal);
                        string target = source.Substring(0, offset) + translated + source.Substring(offset + key.Length);
                        reply.Append('[').Append(items[i].Key).Append(',').Append(WebJsString(source))
                            .Append(',').Append(WebJsString(target)).Append(']');
                    }
                    reply.Append("]);");
                    PostWebTranslation(state, () =>
                    {
                        if (ReferenceEquals(state.Core, core) && TrustedWebSource(WebNativeSource(core)))
                            ExecuteWebTranslation(state, reply.ToString());
                    });
                }
                catch (Exception ex) { Log("Web translation batch: " + ex.GetBaseException().Message); }
            });
        }

        private static string WebNativeSource(object core)
        {
            PropertyInfo property = core.GetType().GetProperty("Source");
            return property == null ? null : property.GetValue(core, null) as string;
        }

        private static bool TrustedWebSource(string source)
        {
            Uri uri;
            if (string.IsNullOrEmpty(source) || !Uri.TryCreate(source, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme == "about") return uri.AbsolutePath == "blank" || uri.AbsolutePath == "srcdoc";
            if ((uri.Scheme == "http" || uri.Scheme == "https") && uri.IsLoopback) return true;
            if (!uri.IsFile) return false;
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(uri.LocalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string WebJsString(string value)
        {
            var result = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c < ' ' || c == '\u2028' || c == '\u2029') result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }

        private static string GetWebTranslationScript(bool messagesEnabled)
        {
            lock (webScriptLock)
            {
                if (webTranslationScript != null)
                    return webTranslationScript.Replace("__CMO_WEB_MESSAGES_ENABLED__", messagesEnabled ? "true" : "false");
                // A small, deterministic set of first-screen labels, not the multi-MB dictionary.
                string[] keys = { "SITUATION", "MISSION", "EXECUTION", "ENEMY FORCES", "FRIENDLY FORCES",
                    "COMMAND AND SIGNAL", "ORDER OF BATTLE", "RULES OF ENGAGEMENT", "TASKS", "OVERVIEW",
                    "DETAILS", "SPECIFICATIONS", "NOTES", "SOURCES", "Description", "General", "Sensors",
                    "Weapons", "Loadouts", "Magazines", "Communications", "Propulsion", "Fuel", "Category",
                    "Aircraft size:", "Average Climb Rate:", "Service Ceiling:", "Combat Radius:", "Fuel Capacity:",
                    "Damage Points:", "Category:", "Length:", "Width:", "Height:", "Weight:", "Payload:",
                    "Sensors:", "Weapons:", "Missions:", "Load", "Cancel", "OK", "Side", "Briefing",
                    "Select Side", "Start", "Resume", "Close", "General Data", "Capabilities", "Properties" };
                var labels = new StringBuilder("{");
                foreach (string key in keys)
                {
                    string value;
                    if (!exactMap.TryGetValue(key, out value) || string.IsNullOrEmpty(value) || value == key) continue;
                    if (labels.Length > 1) labels.Append(',');
                    labels.Append(WebJsString(key)).Append(':').Append(WebJsString(value));
                }
                labels.Append('}');
                using (Stream resource = typeof(CustomAppDomainManager).Assembly.GetManifestResourceStream("CMOLocalizer.WebViewTranslation.js"))
                {
                    if (resource == null) throw new InvalidOperationException("Missing WebViewTranslation.js embedded resource");
                    using (var reader = new StreamReader(resource, Encoding.UTF8))
                        webTranslationScript = reader.ReadToEnd().Replace("__CMO_WEB_LABELS__", labels.ToString())
                            .Replace("__CMO_WEB_FILE_ROOT__", WebJsString(new Uri(AppDomain.CurrentDomain.BaseDirectory).AbsoluteUri));
                }
                return webTranslationScript.Replace("__CMO_WEB_MESSAGES_ENABLED__", messagesEnabled ? "true" : "false");
            }
        }
    }
}
