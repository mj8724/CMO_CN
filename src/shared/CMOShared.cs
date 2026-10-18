// CMO 汉化补丁共享状态层（安装器 CMO_Chinese_Setup.exe 与更新助手 CMO_Updater.exe 共同编译）
//
// 职责：①部署实证（游戏目录内的安装清单）②设置持久化（%APPDATA%\CMOChinese\settings.json）
//      ③安装包缓存与回退 ④版本门控（是否提示可回退）
//
// 设计依据：docs/AUTO_UPDATE_DESIGN.md（方案 v2）、docs/BUG_REGISTRY.md R13–R18。
// 约束：状态只以「清单 + 文件哈希」为准（注册表 Version 仅作迁移期回退）；回退单位是整个安装包，
//      不做资产级快照；设置放 Roaming，避免被旧版 %LOCALAPPDATA% 清理逻辑波及。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CMOShared
{
    /// <summary>
    /// 发布常量单一来源：安装器与更新助手都从这里取值，禁止再各自复制字面量。
    /// 历史事故：版本号曾在安装器标题、安装器常量、助手常量、词典 _meta 四处各存一份，
    /// 打包时漏同步就会造成「资产自称的版本与安装包名不符」。
    /// 改动约束：RegPath / RunKeyName 是已安装用户的注册表身份，改了会丢设置与自启，禁止改值。
    /// </summary>
    public static class ReleaseConstants
    {
        public const string Version = "4.2.1";
        /// <summary>
        /// 本汉化适配的游戏内部版本（构建号，例如 1900.23）。它与 Version 一起必须出现在
        /// 安装器标题、更新助手提示、发版说明与 README 里；游戏更新（构建号变化）时我们的
        /// Version 加 0.1。单一来源：仓库根目录 GAME_BUILD（verify_all 校验两者一致）。
        /// </summary>
        public const string GameBuild = "1900.23";
        public const string RegPath = @"Software\CMOChinese";
        public const string RunKeyName = "CMOChineseUpdater";
        public const string RepoApi = "https://api.github.com/repos/mj8724/CMO_CN/releases/latest";
        public const string Mirror = "https://ghproxy.net/";
    }

    public static class CmoPaths
    {
        /// <summary>
        /// 把压缩包条目名解析为目标根目录内的绝对路径；越界（绝对路径、.. 逃逸、
        /// 盘符/UNC 前缀）时返回 false 且 dest 为 null。
        /// 解压落盘前必须过这一关：安装器是提权运行的，一个构造过的压缩包可以靠
        /// `..\..\` 条目往游戏目录之外写文件（.orig 备份还会再生成第二个越界文件）。
        /// </summary>
        public static bool TryResolveInside(string root, string entryName, out string dest)
        {
            dest = null;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(entryName)) return false;
            try
            {
                string rel = entryName.Replace('/', Path.DirectorySeparatorChar)
                                      .Replace('\\', Path.DirectorySeparatorChar);
                if (rel.Length == 0 || Path.IsPathRooted(rel)) return false;
                if (rel.IndexOf(':') >= 0) return false;          // 盘符或备用数据流前缀
                string rootFull = Path.GetFullPath(root);
                if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    rootFull += Path.DirectorySeparatorChar;
                }
                string candidate = Path.GetFullPath(Path.Combine(rootFull, rel));
                if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
                dest = candidate;
                return true;
            }
            catch { return false; }
        }

        public const string ManifestName = "CMOChinese_Installed.json";
        public static string RegistryPathOverride;   // 自测用：避免碰真实 HKCU\Software\CMOChinese
        public static string RoamingDirOverride;     // 自测用：临时目录
        public static string LocalDirOverride;

        public static string RegistryPath
        {
            get { return RegistryPathOverride ?? @"Software\CMOChinese"; }
        }

        public static string RoamingDir
        {
            get
            {
                if (RoamingDirOverride != null) return RoamingDirOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CMOChinese");
            }
        }

        public static string LocalDir
        {
            get
            {
                if (LocalDirOverride != null) return LocalDirOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMOChinese");
            }
        }

        public static string SettingsFile { get { return Path.Combine(RoamingDir, "settings.json"); } }
        public static string PackagesDir { get { return Path.Combine(LocalDir, "packages"); } }
        public static string LogFile { get { return Path.Combine(LocalDir, "shared.log"); } }

        public static string ManifestPath(string gamePath)
        {
            return Path.Combine(gamePath, ManifestName);
        }

        public static void EnsureDir(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }

    public static class CmoLog
    {
        private static readonly object Gate = new object();

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    CmoPaths.EnsureDir(CmoPaths.LocalDir);
                    string p = CmoPaths.LogFile;
                    if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024)
                    {
                        string old = p + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + " [" + level + "] " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>原子文件写入：先写 .tmp，再替换目标（目标不存在时用 Move）。</summary>
    public static class AtomicFile
    {
        public static void WriteText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            CmoPaths.EnsureDir(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            Replace(tmp, path);
        }

        public static void Replace(string tmp, string path)
        {
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch (Exception ex) { CmoLog.Warn("File.Replace 失败，改用覆盖: " + ex.Message); }
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }

    /// <summary>扁平 JSON 读写（只处理本模块自己的两种文件，保持零依赖）。</summary>
    internal static class FlatJson
    {
        public static string GetString(string json, string key, string def)
        {
            if (json == null) return def;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : def;
        }

        public static bool GetBool(string json, string key, bool def)
        {
            if (json == null) return def;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return def;
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static long GetNumber(string json, string key, long def)
        {
            if (json == null) return def;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            long v;
            return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Unescape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    int cp;
                    if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                    {
                        sb.Append((char)cp);
                        i += 4;
                    }
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }
    }

    /// <summary>用户设置（设置永不随「还原」丢失；只有显式 Purge 才清除）。</summary>
    public sealed class CmoSettings
    {
        public const int SchemaVersion = 1;

        public bool AutoCheck = true;
        public int CheckIntervalHours = 6;
        public bool AutoStart = true;
        public int NotifyMode = 1;              // 0=弹窗 1=托盘气泡 2=静默
        public bool AutoInstall = true;
        public string Mirror = "https://ghproxy.net/";
        public bool TrayOnClose = true;         // 关闭更新助手窗口时最小化到托盘
        public int SnapshotKeep = 2;            // 保留最近几个版本的安装包缓存
        public string LastCheckUtc = "";
        public string SkippedVersion = "";
        public string LastNotifiedVersion = "";
        public string GamePath = "";
        public string PendingInstallVersion = "";    // 升级中标记：目标版本（安装后校验失败则用它回滚）
        public string PendingPreviousVersion = "";   // 升级前版本（回滚目标）

        public static CmoSettings Load()
        {
            CmoSettings s = new CmoSettings();
            string path = CmoPaths.SettingsFile;
            if (File.Exists(path))
            {
                string json = null;
                try { json = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex) { CmoLog.Warn("读取设置文件失败: " + ex.Message); }
                // 只有像本模块写出的设置文件才接受；其它一律按损坏处理（备份 + 默认值），
                // 避免把被截断/被其它程序覆盖的文件当成"全默认设置"静默接受。
                if (json == null || !json.TrimStart().StartsWith("{")
                    || json.IndexOf("\"schema\"", StringComparison.Ordinal) < 0)
                {
                    CmoLog.Warn("设置文件无法识别，已备份为 settings.json.bad 并使用默认值");
                    try { File.Copy(path, path + ".bad", true); } catch { }
                }
                else
                {
                    try { s.Read(json); }
                    catch (Exception ex)
                    {
                        CmoLog.Warn("设置文件解析失败，改用默认值: " + ex.Message);
                        try { File.Copy(path, path + ".bad", true); } catch { }
                        s = new CmoSettings();
                    }
                }
            }
            else
            {
                if (s.MigrateFromRegistry()) s.Save();
            }
            if (s.CheckIntervalHours < 1) s.CheckIntervalHours = 6;
            if (s.SnapshotKeep < 1) s.SnapshotKeep = 2;
            if (string.IsNullOrEmpty(s.Mirror)) s.Mirror = "https://ghproxy.net/";
            return s;
        }

        private void Read(string json)
        {
            AutoCheck = FlatJson.GetBool(json, "auto_check", AutoCheck);
            CheckIntervalHours = (int)FlatJson.GetNumber(json, "check_interval_hours", CheckIntervalHours);
            AutoStart = FlatJson.GetBool(json, "auto_start", AutoStart);
            NotifyMode = (int)FlatJson.GetNumber(json, "notify_mode", NotifyMode);
            AutoInstall = FlatJson.GetBool(json, "auto_install", AutoInstall);
            Mirror = FlatJson.GetString(json, "mirror", Mirror);
            TrayOnClose = FlatJson.GetBool(json, "tray_on_close", TrayOnClose);
            SnapshotKeep = (int)FlatJson.GetNumber(json, "snapshot_keep", SnapshotKeep);
            LastCheckUtc = FlatJson.GetString(json, "last_check_utc", LastCheckUtc);
            SkippedVersion = FlatJson.GetString(json, "skipped_version", SkippedVersion);
            LastNotifiedVersion = FlatJson.GetString(json, "last_notified_version", LastNotifiedVersion);
            GamePath = FlatJson.GetString(json, "game_path", GamePath);
            PendingInstallVersion = FlatJson.GetString(json, "pending_install_version", PendingInstallVersion);
            PendingPreviousVersion = FlatJson.GetString(json, "pending_previous_version", PendingPreviousVersion);
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"auto_check\": ").Append(AutoCheck ? "true" : "false").Append(",\n");
            sb.Append("  \"check_interval_hours\": ").Append(CheckIntervalHours).Append(",\n");
            sb.Append("  \"auto_start\": ").Append(AutoStart ? "true" : "false").Append(",\n");
            sb.Append("  \"notify_mode\": ").Append(NotifyMode).Append(",\n");
            sb.Append("  \"auto_install\": ").Append(AutoInstall ? "true" : "false").Append(",\n");
            sb.Append("  \"tray_on_close\": ").Append(TrayOnClose ? "true" : "false").Append(",\n");
            sb.Append("  \"snapshot_keep\": ").Append(SnapshotKeep).Append(",\n");
            sb.Append("  \"mirror\": \"").Append(FlatJson.Escape(Mirror)).Append("\",\n");
            sb.Append("  \"last_check_utc\": \"").Append(FlatJson.Escape(LastCheckUtc)).Append("\",\n");
            sb.Append("  \"skipped_version\": \"").Append(FlatJson.Escape(SkippedVersion)).Append("\",\n");
            sb.Append("  \"last_notified_version\": \"").Append(FlatJson.Escape(LastNotifiedVersion)).Append("\",\n");
            sb.Append("  \"game_path\": \"").Append(FlatJson.Escape(GamePath)).Append("\",\n");
            sb.Append("  \"pending_install_version\": \"").Append(FlatJson.Escape(PendingInstallVersion)).Append("\",\n");
            sb.Append("  \"pending_previous_version\": \"").Append(FlatJson.Escape(PendingPreviousVersion)).Append("\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public void Save()
        {
            try { AtomicFile.WriteText(CmoPaths.SettingsFile, ToJson()); }
            catch (Exception ex) { CmoLog.Warn("保存设置失败: " + ex.Message); }
        }

        /// <summary>旧版把设置放在 HKCU\Software\CMOChinese；一次性迁移到 settings.json。</summary>
        private bool MigrateFromRegistry()
        {
            bool any = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CmoPaths.RegistryPath))
                {
                    if (k == null) return false;
                    object v;
                    v = k.GetValue("AutoCheck"); if (v != null) { AutoCheck = Convert.ToInt64(v) != 0; any = true; }
                    v = k.GetValue("CheckIntervalHours"); if (v != null) { CheckIntervalHours = (int)Convert.ToInt64(v); any = true; }
                    v = k.GetValue("AutoStart"); if (v != null) { AutoStart = Convert.ToInt64(v) != 0; any = true; }
                    v = k.GetValue("NotifyMode"); if (v != null) { NotifyMode = (int)Convert.ToInt64(v); any = true; }
                    v = k.GetValue("AutoInstall"); if (v != null) { AutoInstall = Convert.ToInt64(v) != 0; any = true; }
                    v = k.GetValue("Mirror"); if (v != null && v.ToString().Length > 0) { Mirror = v.ToString(); any = true; }
                    v = k.GetValue("GamePath"); if (v != null) { GamePath = v.ToString(); any = true; }
                    v = k.GetValue("LastCheckUtc"); if (v != null) { LastCheckUtc = v.ToString(); any = true; }
                    v = k.GetValue("SkippedVersion"); if (v != null) { SkippedVersion = v.ToString(); any = true; }
                    v = k.GetValue("LastNotifiedVersion"); if (v != null) { LastNotifiedVersion = v.ToString(); any = true; }
                }
            }
            catch (Exception ex) { CmoLog.Warn("迁移旧注册表设置失败: " + ex.Message); }
            if (any) CmoLog.Info("已把 HKCU\\" + CmoPaths.RegistryPath + " 的设置迁移到 settings.json");
            return any;
        }

        /// <summary>显式「清除我的全部设置」：删除设置文件与兼容用注册表项。</summary>
        public static void Purge()
        {
            try { if (File.Exists(CmoPaths.SettingsFile)) File.Delete(CmoPaths.SettingsFile); }
            catch (Exception ex) { CmoLog.Warn("删除设置文件失败: " + ex.Message); }
            try { Registry.CurrentUser.DeleteSubKeyTree(CmoPaths.RegistryPath, false); }
            catch (Exception ex) { CmoLog.Warn("删除注册表设置失败: " + ex.Message); }
        }

        /// <summary>迁移期回退：旧版写入的已安装版本号（新逻辑不再依赖它作状态来源）。</summary>
        public static string RegistryInstalledVersion()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CmoPaths.RegistryPath))
                {
                    if (k == null) return "";
                    object v = k.GetValue("Version");
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }
    }

    public enum ManifestState { NotInstalled, Ok, Modified }

    /// <summary>安装清单：游戏目录级「部署实证」，是「装了什么版本」的唯一真相。</summary>
    public sealed class InstallManifest
    {
        public string Version = "";
        /// <summary>安装时本汉化适配的游戏内部版本（构建号）；引擎启动时拿它与游戏实际构建号比对。</summary>
        public string GameBuild = "";
        public string InstalledAt = "";
        public string Source = "";
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] EngineFiles = { "CMOLocalizer.dll", "CMOLocalizer.json" };

        public static string Sha256OfFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static Dictionary<string, string> HashEngineFiles(string gamePath)
        {
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in EngineFiles)
            {
                string p = Path.Combine(gamePath, name);
                if (File.Exists(p)) files[name] = Sha256OfFile(p);
            }
            return files;
        }

        public static bool Write(string gamePath, string version, string source, Dictionary<string, string> files)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"schema\": 1,\n");
                sb.Append("  \"version\": \"").Append(FlatJson.Escape(version ?? "")).Append("\",\n");
                sb.Append("  \"game_build\": \"").Append(FlatJson.Escape(ReleaseConstants.GameBuild)).Append("\",\n");
                sb.Append("  \"installed_at\": \"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
                sb.Append("  \"source\": \"").Append(FlatJson.Escape(source ?? "")).Append("\",\n");
                sb.Append("  \"files\": {\n");
                int i = 0;
                foreach (KeyValuePair<string, string> kv in files)
                {
                    sb.Append("    \"").Append(FlatJson.Escape(kv.Key)).Append("\": \"").Append(FlatJson.Escape(kv.Value)).Append("\"");
                    sb.Append(++i < files.Count ? ",\n" : "\n");
                }
                sb.Append("  }\n}\n");
                AtomicFile.WriteText(CmoPaths.ManifestPath(gamePath), sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                CmoLog.Error("写入安装清单失败: " + ex.Message);
                return false;
            }
        }

        public static InstallManifest Read(string gamePath)
        {
            try
            {
                string p = CmoPaths.ManifestPath(gamePath);
                if (!File.Exists(p)) return null;
                string json = File.ReadAllText(p, Encoding.UTF8);
                InstallManifest m = new InstallManifest();
                m.Version = FlatJson.GetString(json, "version", "");
                m.GameBuild = FlatJson.GetString(json, "game_build", "");
                m.InstalledAt = FlatJson.GetString(json, "installed_at", "");
                m.Source = FlatJson.GetString(json, "source", "");
                int i = json.IndexOf("\"files\"", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int open = json.IndexOf('{', i);
                    int close = json.IndexOf('}', open < 0 ? 0 : open);
                    if (open >= 0 && close > open)
                    {
                        string block = json.Substring(open + 1, close - open - 1);
                        foreach (Match mm in Regex.Matches(block, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"([0-9a-fA-F]{8,})\""))
                            m.Files[FlatJson.Unescape(mm.Groups[1].Value)] = mm.Groups[2].Value.ToLowerInvariant();
                    }
                }
                return m;
            }
            catch (Exception ex)
            {
                CmoLog.Warn("读取安装清单失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>只对清单记录过的引擎文件判定，避免把用户无关改动报成问题。</summary>
        public static ManifestState Verify(string gamePath, out string detail)
        {
            detail = "";
            InstallManifest m = Read(gamePath);
            if (m == null)
            {
                detail = "无安装清单";
                return ManifestState.NotInstalled;
            }
            foreach (KeyValuePair<string, string> kv in m.Files)
            {
                string p = Path.Combine(gamePath, kv.Key);
                if (!File.Exists(p)) { detail = kv.Key + " 缺失"; return ManifestState.Modified; }
                string actual;
                try { actual = Sha256OfFile(p); }
                catch (Exception ex) { detail = kv.Key + " 无法读取: " + ex.Message; return ManifestState.Modified; }
                if (!string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    detail = kv.Key + " 与清单不一致";
                    return ManifestState.Modified;
                }
            }
            detail = "清单校验通过 v" + m.Version;
            return ManifestState.Ok;
        }

        public static void Clear(string gamePath)
        {
            try
            {
                string p = CmoPaths.ManifestPath(gamePath);
                if (File.Exists(p)) File.Delete(p);
                CmoLog.Info("已清除安装清单: " + p);
            }
            catch (Exception ex) { CmoLog.Warn("清除安装清单失败: " + ex.Message); }
        }

        /// <summary>部署在游戏目录里的资产自带版本戳（CMOLocalizer.json 的 _meta.version）。</summary>
        public static string DeployedAssetVersion(string gamePath)
        {
            try
            {
                string p = Path.Combine(gamePath, "CMOLocalizer.json");
                if (!File.Exists(p)) return null;
                using (FileStream fs = File.OpenRead(p))
                {
                    byte[] head = new byte[1024];
                    int n = fs.Read(head, 0, head.Length);
                    if (n <= 0) return null;
                    Match m = Regex.Match(Encoding.UTF8.GetString(head, 0, n), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value.Trim() : null;
                }
            }
            catch (Exception ex)
            {
                CmoLog.Warn("读取已部署资产版本失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>自愈：引擎文件在但没有清单（存量 4.1.x 用户 / 绿色包用户）→ 按当前文件登记。</summary>
        public static bool Adopt(string gamePath, string fallbackVersion)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath)) return false;
            foreach (string name in EngineFiles)
                if (!File.Exists(Path.Combine(gamePath, name))) return false;
            Dictionary<string, string> files = HashEngineFiles(gamePath);
            if (files.Count != EngineFiles.Length) return false;
            // 版本回退链：调用方给的（注册表）→ 已部署资产自带戳 → unknown
            string v = fallbackVersion;
            if (string.IsNullOrEmpty(v)) v = DeployedAssetVersion(gamePath);
            if (string.IsNullOrEmpty(v)) v = "unknown";
            v = v.Trim();
            bool ok = Write(gamePath, v, "adopted", files);
            if (ok) CmoLog.Info("已登记当前安装状态（adopt）: v" + v);
            return ok;
        }
    }

    /// <summary>安装包缓存：回退以整个安装包为单位（权威、自带全部资产，避免混合态）。</summary>
    public static class VersionStore
    {
        private static readonly Regex PkgRe = new Regex(@"CMO_Chinese_Setup_v?([0-9][0-9.]*)\.exe$", RegexOptions.IgnoreCase);

        /// <summary>文件名里的版本号（仅作回退，优先用 PackageVersion 的资产版本戳）。</summary>
        public static string VersionOfPackage(string path)
        {
            Match m = PkgRe.Match(Path.GetFileName(path ?? ""));
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// 安装包版本号：**以文件名为准**（发布资产的名字来自 VERSION，是发布流程的权威命名），
        /// 同时用内嵌词典的 _meta.version 交叉校验（不一致只记日志）。
        /// 没有版本号的文件名（如用户自己改过名）才回退到内嵌版本戳。
        /// </summary>
        // 记忆化：同一次安装/回退流程里同一个包会被反复问版本（CachedVersions/Prune/Previous），
        // 每次都要解一次 14MB 包的嵌入资源流。键含 size + mtime，换过的包不会被当成旧的。
        private static readonly object _verLock = new object();
        private static readonly Dictionary<string, string> _verCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string PackageKey(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                return path + "|" + fi.Length.ToString(CultureInfo.InvariantCulture)
                    + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        public static string PackageVersion(string path)
        {
            string key = PackageKey(path);
            if (key != null)
            {
                lock (_verLock)
                {
                    string hit;
                    if (_verCache.TryGetValue(key, out hit)) return hit;
                }
            }
            string result = ComputePackageVersion(path);
            if (key != null)
            {
                lock (_verLock)
                {
                    if (_verCache.Count > 64) _verCache.Clear();
                    _verCache[key] = result;
                }
            }
            return result;
        }

        private static string ComputePackageVersion(string path)
        {
            string byName = VersionOfPackage(path);
            string byContent = EmbeddedAssetVersion(path);
            if (!string.IsNullOrEmpty(byName) && !string.IsNullOrEmpty(byContent)
                && !string.Equals(byName, byContent, StringComparison.OrdinalIgnoreCase))
            {
                CmoLog.Warn("安装包文件名版本（v" + byName + "）与内嵌词典 _meta.version（v" + byContent
                    + "）不一致：以文件名为准（版本戳忘同步时会出现，已由 verify_all 版本门禁拦截）");
            }
            if (!string.IsNullOrEmpty(byName)) return byName;
            return byContent;
        }

        private static string EmbeddedAssetVersion(string packagePath)
        {
            try
            {
                using (Stream s = PackageAssets.EmbeddedResourceStream(packagePath, "CMOLocalizer.json"))
                {
                    if (s == null) return null;
                    // _meta 块在文件顶部，读前 1KB 足够，避免整读 11MB 词典
                    byte[] buf = new byte[1024];
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) return null;
                    string head = Encoding.UTF8.GetString(buf, 0, n);
                    Match m = Regex.Match(head, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value.Trim() : null;
                }
            }
            catch (Exception ex)
            {
                CmoLog.Warn("读取安装包内嵌版本失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>把安装包复制进缓存目录（按资产版本命名）；返回缓存路径，失败返回 null。</summary>
        public static string Cache(string setupPath)
        {
            try
            {
                if (string.IsNullOrEmpty(setupPath) || !File.Exists(setupPath)) return null;
                string ver = PackageVersion(setupPath);
                if (string.IsNullOrEmpty(ver))
                {
                    CmoLog.Warn("安装包版本无法识别，未缓存: " + setupPath);
                    return null;
                }
                CmoPaths.EnsureDir(CmoPaths.PackagesDir);
                string dest = Path.Combine(CmoPaths.PackagesDir, "CMO_Chinese_Setup_v" + ver + ".exe");
                bool samePlace = string.Equals(Path.GetFullPath(dest), Path.GetFullPath(setupPath), StringComparison.OrdinalIgnoreCase);
                if (!samePlace)
                {
                    if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(setupPath).Length)
                    {
                        File.Copy(setupPath, dest, true);
                        CmoLog.Info("已缓存安装包: " + dest);
                    }
                }
                return dest;
            }
            catch (Exception ex)
            {
                CmoLog.Warn("缓存安装包失败: " + ex.Message);
                return null;
            }
        }

        public static List<string> CachedVersions()
        {
            List<string> vs = new List<string>();
            try
            {
                if (!Directory.Exists(CmoPaths.PackagesDir)) return vs;
                foreach (string f in Directory.GetFiles(CmoPaths.PackagesDir, "*.exe"))
                {
                    string v = PackageVersion(f);
                    if (!string.IsNullOrEmpty(v) && !vs.Contains(v)) vs.Add(v);
                }
            }
            catch (Exception ex) { CmoLog.Warn("枚举安装包缓存失败: " + ex.Message); }
            vs.Sort(delegate(string a, string b) { return ReleaseGate.Compare(b, a); });
            return vs;
        }

        public static string CachedPackage(string version)
        {
            if (string.IsNullOrEmpty(version)) return null;
            try
            {
                if (!Directory.Exists(CmoPaths.PackagesDir)) return null;
                string want = version.TrimStart('v', 'V');
                foreach (string f in Directory.GetFiles(CmoPaths.PackagesDir, "*.exe"))
                {
                    string v = PackageVersion(f);
                    if (v != null && string.Equals(v, want, StringComparison.OrdinalIgnoreCase)) return f;
                }
            }
            catch (Exception ex) { CmoLog.Warn("查找缓存安装包失败: " + ex.Message); }
            return null;
        }

        /// <summary>缓存中比 localVersion 更旧的最大版本；localVersion 为空则取最新缓存。</summary>
        public static string Previous(string localVersion)
        {
            List<string> vs = CachedVersions();
            if (vs.Count == 0) return null;
            if (string.IsNullOrEmpty(localVersion)) return vs[0];
            string best = null;
            foreach (string v in vs)
            {
                if (ReleaseGate.Compare(v, localVersion) < 0)
                {
                    if (best == null || ReleaseGate.Compare(v, best) > 0) best = v;
                }
            }
            return best;
        }

        public static void Prune(int keep)
        {
            try
            {
                if (keep < 1) keep = 1;
                List<string> vs = CachedVersions();
                for (int i = keep; i < vs.Count; i++)
                {
                    string p = CachedPackage(vs[i]);
                    if (p != null && File.Exists(p))
                    {
                        File.Delete(p);
                        CmoLog.Info("清理旧安装包缓存: " + p);
                    }
                }
            }
            catch (Exception ex) { CmoLog.Warn("清理安装包缓存失败: " + ex.Message); }
        }
    }

    /// <summary>读取安装包内嵌资源（csc -resource: 写入的清单资源），用于回退后的一致性校验。</summary>
    public static class PackageAssets
    {
        private static readonly object _pkgAsmLock = new object();
        private static string _pkgAsmPath;
        private static long _pkgAsmSize = -1;
        private static DateTime _pkgAsmMtime;
        private static Assembly _pkgAsm;

        /// <summary>
        /// 同一个安装包（路径 + 大小 + mtime 均未变）只读一次。
        /// 包是 14MB 级 EXE，安装/回退流程会多次要资源名与摘要；没缓存时每次都 ReadAllBytes 一遍，
        /// 白白吃掉几十 MB 内存与磁盘 IO。缓存键必须含 size+mtime，否则换包后会读到旧程序集。
        /// </summary>
        private static Assembly Load(string packagePath)
        {
            FileInfo fi = new FileInfo(packagePath);
            long size = fi.Length;
            DateTime mtime = fi.LastWriteTimeUtc;
            lock (_pkgAsmLock)
            {
                if (_pkgAsm != null && string.Equals(_pkgAsmPath, packagePath, StringComparison.OrdinalIgnoreCase)
                    && _pkgAsmSize == size && _pkgAsmMtime == mtime)
                {
                    return _pkgAsm;
                }
                Assembly asm = Assembly.Load(File.ReadAllBytes(packagePath));
                _pkgAsmPath = packagePath;
                _pkgAsmSize = size;
                _pkgAsmMtime = mtime;
                _pkgAsm = asm;
                return asm;
            }
        }

        public static string[] EmbeddedResourceNames(string packagePath)
        {
            try { return Load(packagePath).GetManifestResourceNames(); }
            catch (Exception ex)
            {
                CmoLog.Warn("读取安装包资源失败: " + ex.Message);
                return new string[0];
            }
        }

        public static string EmbeddedSha256(string packagePath, string resourceName)
        {
            try
            {
                using (Stream s = EmbeddedResourceStream(packagePath, resourceName))
                {
                    if (s == null) return null;
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] h = sha.ComputeHash(s);
                        StringBuilder sb = new StringBuilder(h.Length * 2);
                        foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                        return sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                CmoLog.Warn("读取安装包内嵌 " + resourceName + " 失败: " + ex.Message);
                return null;
            }
        }

        public static Stream EmbeddedResourceStream(string packagePath, string resourceName)
        {
            return Load(packagePath).GetManifestResourceStream(resourceName);
        }
    }

    /// <summary>检查频率下拉的取值映射（安装器与更新助手共用同一套语义）。</summary>
    public static class IntervalPresets
    {
        public static int ToIndex(int hours)
        {
            if (hours == 0) return 0;
            if (hours == 1) return 1;
            if (hours == 3) return 2;
            if (hours == 12) return 4;
            if (hours == 24) return 5;
            return 3;
        }

        public static int ToHours(int index)
        {
            switch (index)
            {
                case 0: return 24;   // 仅开机检查（开机一次 + 24h 间隔）
                case 1: return 1;
                case 2: return 3;
                case 4: return 12;
                case 5: return 24;
                default: return 6;
            }
        }

        public static string Text(int hours)
        {
            if (hours <= 0) return "每次开机";
            return "每 " + hours + " 小时";
        }
    }

    /// <summary>版本门控：只有在「远端更旧且本地确实缓存了该版本安装包」时才提示回退。</summary>
    public static class ReleaseGate
    {
        public static string Clean(string v)
        {
            if (v == null) return "0.0";
            v = v.Trim().TrimStart('v', 'V');
            int dash = v.IndexOf('-');
            if (dash > 0) v = v.Substring(0, dash);
            return v.Length == 0 ? "0.0" : v;
        }

        public static int Compare(string a, string b)
        {
            try { return new Version(Clean(a)).CompareTo(new Version(Clean(b))); }
            catch { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public static bool ShouldOfferRollback(string localVersion, string remoteVersion, bool hasCachedRemote)
        {
            if (string.IsNullOrEmpty(remoteVersion) || string.IsNullOrEmpty(localVersion)) return false;
            return Compare(remoteVersion, localVersion) < 0 && hasCachedRemote;
        }
    }

    /// <summary>回退编排：安装器 CLI 与更新助手共用这一处实现。</summary>
    public static class Rollback
    {
        public static bool Run(string gamePath, string targetVersion, bool silent, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            {
                error = "游戏目录不存在: " + gamePath;
                return false;
            }
            string pkg = VersionStore.CachedPackage(targetVersion);
            if (pkg == null)
            {
                error = "本地没有 v" + targetVersion + " 的安装包缓存，无法离线回退（可下载该版本安装包后重试）。";
                return false;
            }
            // 缓存内容必须能被认定为目标版本
            string pkgVer = VersionStore.PackageVersion(pkg);
            if (!string.Equals(pkgVer, targetVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
            {
                error = "缓存安装包版本（v" + pkgVer + "）与目标版本 v" + targetVersion + " 不一致，已中止回退。";
                CmoLog.Error(error);
                return false;
            }

            KillProcesses();
            string expectDll = PackageAssets.EmbeddedSha256(pkg, "CMOLocalizer.dll");

            string args = "--install \"" + gamePath + "\" --silent";
            int code = RunProcess(pkg, args, 600000);
            if (code != 0)
            {
                error = "运行安装包失败（退出码 " + code + "）: " + pkg;
                CmoLog.Error(error);
                return false;
            }

            Dictionary<string, string> files = InstallManifest.HashEngineFiles(gamePath);
            if (files.Count != InstallManifest.EngineFiles.Length)
            {
                error = "回退后引擎文件不完整（期望 dll+json）";
                CmoLog.Error(error);
                return false;
            }
            if (!string.IsNullOrEmpty(expectDll)
                && !string.Equals(files["CMOLocalizer.dll"], expectDll, StringComparison.OrdinalIgnoreCase))
            {
                error = "回退后 CMOLocalizer.dll 与该安装包内嵌版本不一致，已中止登记（避免混合态）。";
                CmoLog.Error(error);
                return false;
            }
            if (!InstallManifest.Write(gamePath, targetVersion, pkg, files))
            {
                error = "回退成功但写入安装清单失败，请重新安装以修复状态。";
                return false;
            }
            CmoLog.Info("已回退到 v" + targetVersion + "（" + pkg + "）");
            return true;
        }

        public static void KillProcesses()
        {
            foreach (string name in new string[] { "Command", "CMO_Updater" })
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.CloseMainWindow(); } catch { }
                    }
                    for (int i = 0; i < 10; i++)
                    {
                        if (Process.GetProcessesByName(name).Length == 0) break;
                        System.Threading.Thread.Sleep(500);
                    }
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch (Exception ex) { CmoLog.Warn("结束进程 " + name + " 失败: " + ex.Message); }
            }
            System.Threading.Thread.Sleep(700);
        }

        public static int RunProcess(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process p = Process.Start(psi);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }
    }
}
