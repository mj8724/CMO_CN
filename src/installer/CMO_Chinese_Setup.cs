using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using CMOShared;
using Microsoft.Win32;

namespace CMOLocalizerInstaller
{
    public class InstallerForm : Form
    {
        private TextBox txtPath;
        private Button btnBrowse;
        private Button btnInstall;
        private Button btnUninstall;
        private Button btnDonate;
        private TextBox txtLog;
        private Label lblStatus;
        private Label lblUpdateState;
        private Label lblUpdateNext;
        private CheckBox chkAutoStart;
        private Button btnUpgrade;
        private Button btnCheckUpdate;
        private Button btnNeverRemind;
        private ComboBox cbInterval;
        private ComboBox cbNotify;
        private Button btnRollback;
        private CheckBox chkPurgeSettings;
        private bool suppressAutoStartEvent;
        private bool suppressUpdateUiEvent;
        private readonly ToolTip toolTip = new ToolTip();

        public InstallerForm(string initialPath)
        {
            this.Text = "Command: Modern Operations (CMO) 完整中文汉化一键安装工具 v4.2.1（适配游戏内部版本 " + ReleaseConstants.GameBuild + "）";
            this.Size = new Size(700, 770);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            Label lblTitle = new Label();
            lblTitle.Text = "Command: Modern Operations 完整中文汉化补丁";
            lblTitle.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(20, 60, 120);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(20, 18);
            this.Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = "采用 CLR AppDomainManager 零侵入内存级双重本地化引擎 | 不修改原版 EXE | 支持一键还原";
            lblSub.Text += "\n适配游戏内部版本 " + ReleaseConstants.GameBuild + "（游戏标题里的 Build 号；游戏更新后需等待对应版本的补丁）";
            lblSub.ForeColor = Color.DimGray;
            lblSub.AutoSize = true;
            lblSub.Location = new Point(22, 50);
            this.Controls.Add(lblSub);

            Label lblPath = new Label();
            lblPath.Text = "CMO 游戏根目录:";
            lblPath.AutoSize = true;
            lblPath.Location = new Point(22, 90);
            this.Controls.Add(lblPath);

            txtPath = new TextBox();
            txtPath.Location = new Point(22, 115);
            txtPath.Size = new Size(520, 26);
            txtPath.Text = initialPath;
            this.Controls.Add(txtPath);

            btnBrowse = new Button();
            btnBrowse.Text = "浏览目录...";
            btnBrowse.Location = new Point(552, 113);
            btnBrowse.Size = new Size(95, 28);
            btnBrowse.Click += new EventHandler(OnBrowseClick);
            this.Controls.Add(btnBrowse);

            lblStatus = new Label();
            lblStatus.Text = "当前状态: 正在检测...";
            lblStatus.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(22, 155);
            this.Controls.Add(lblStatus);

            btnInstall = new Button();
            btnInstall.Text = "一键安装中文汉化补丁";
            btnInstall.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            btnInstall.BackColor = Color.FromArgb(30, 110, 200);
            btnInstall.ForeColor = Color.White;
            btnInstall.FlatStyle = FlatStyle.Flat;
            btnInstall.Location = new Point(22, 190);
            btnInstall.Size = new Size(300, 46);
            btnInstall.Click += new EventHandler(OnInstallClick);
            this.Controls.Add(btnInstall);

            btnUninstall = new Button();
            btnUninstall.Text = "一键还原原版英文";
            btnUninstall.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
            btnUninstall.Location = new Point(347, 190);
            btnUninstall.Size = new Size(300, 46);
            btnUninstall.Click += new EventHandler(OnUninstallClick);
            this.Controls.Add(btnUninstall);

            chkPurgeSettings = new CheckBox();
            chkPurgeSettings.Text = "还原时同时清除我的全部设置（更新频率/提醒方式等）";
            chkPurgeSettings.AutoSize = true;
            chkPurgeSettings.Checked = false;
            chkPurgeSettings.ForeColor = Color.DimGray;
            chkPurgeSettings.Location = new Point(347, 240);
            this.Controls.Add(chkPurgeSettings);

            btnDonate = new Button();
            btnDonate.Text = "☕ 觉得好用？请作者喝杯咖啡 / 可乐";
            btnDonate.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            btnDonate.BackColor = Color.FromArgb(254, 248, 237);
            btnDonate.ForeColor = Color.FromArgb(180, 95, 20);
            btnDonate.FlatStyle = FlatStyle.Flat;
            btnDonate.FlatAppearance.BorderColor = Color.FromArgb(235, 195, 135);
            btnDonate.Location = new Point(22, 276);
            btnDonate.Size = new Size(625, 34);
            btnDonate.Cursor = Cursors.Hand;
            btnDonate.Click += new EventHandler(OnDonateClick);
            this.Controls.Add(btnDonate);

            // ── 版本更新（自动升级）分组 ─────────────────────────────
            GroupBox grpUpdate = new GroupBox();
            grpUpdate.Text = "版本更新";
            grpUpdate.Location = new Point(22, 320);
            grpUpdate.Size = new Size(645, 230);
            this.Controls.Add(grpUpdate);

            lblUpdateState = new Label();
            lblUpdateState.AutoSize = true;
            lblUpdateState.Location = new Point(14, 26);
            grpUpdate.Controls.Add(lblUpdateState);

            lblUpdateNext = new Label();
            lblUpdateNext.AutoSize = true;
            lblUpdateNext.ForeColor = Color.DimGray;
            lblUpdateNext.Location = new Point(14, 50);
            grpUpdate.Controls.Add(lblUpdateNext);

            // 设置改为直接在此修改（旧版只有一行只读文字，用户反馈「更新频率也没有了」）
            Label lblInterval = new Label();
            lblInterval.Text = "自动检查频率:";
            lblInterval.AutoSize = true;
            lblInterval.Location = new Point(14, 79);
            grpUpdate.Controls.Add(lblInterval);

            cbInterval = new ComboBox();
            cbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cbInterval.Location = new Point(120, 75);
            cbInterval.Size = new Size(190, 26);
            cbInterval.Items.AddRange(new object[] { "每次开机检查一次", "每 1 小时", "每 3 小时", "每 6 小时（推荐）", "每 12 小时", "每 24 小时" });
            cbInterval.SelectedIndexChanged += new EventHandler(OnIntervalChanged);
            grpUpdate.Controls.Add(cbInterval);

            Label lblNotify = new Label();
            lblNotify.Text = "提醒方式:";
            lblNotify.AutoSize = true;
            lblNotify.Location = new Point(330, 79);
            grpUpdate.Controls.Add(lblNotify);

            cbNotify = new ComboBox();
            cbNotify.DropDownStyle = ComboBoxStyle.DropDownList;
            cbNotify.Location = new Point(408, 75);
            cbNotify.Size = new Size(160, 26);
            cbNotify.Items.AddRange(new object[] { "弹窗提醒", "仅托盘气泡", "不提醒" });
            cbNotify.SelectedIndexChanged += new EventHandler(OnNotifyChanged);
            grpUpdate.Controls.Add(cbNotify);

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "随 Windows 启动并在后台自动检查更新";
            chkAutoStart.AutoSize = true;
            chkAutoStart.Location = new Point(14, 112);
            chkAutoStart.CheckedChanged += new EventHandler(OnAutoStartChanged);
            grpUpdate.Controls.Add(chkAutoStart);

            btnUpgrade = new Button();
            btnUpgrade.Text = "立即升级";
            btnUpgrade.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            btnUpgrade.BackColor = Color.FromArgb(30, 110, 200);
            btnUpgrade.ForeColor = Color.White;
            btnUpgrade.FlatStyle = FlatStyle.Flat;
            btnUpgrade.Location = new Point(14, 146);
            btnUpgrade.Size = new Size(150, 30);
            btnUpgrade.Click += new EventHandler(OnUpgradeClick);
            grpUpdate.Controls.Add(btnUpgrade);

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "检查升级";
            btnCheckUpdate.Location = new Point(174, 146);
            btnCheckUpdate.Size = new Size(100, 30);
            btnCheckUpdate.Click += new EventHandler(OnCheckUpdateClick);
            grpUpdate.Controls.Add(btnCheckUpdate);

            btnNeverRemind = new Button();
            btnNeverRemind.Text = "不再提醒";
            btnNeverRemind.Location = new Point(284, 146);
            btnNeverRemind.Size = new Size(100, 30);
            btnNeverRemind.Click += new EventHandler(OnNeverRemindClick);
            grpUpdate.Controls.Add(btnNeverRemind);
            btnRollback = new Button();
            btnRollback.Text = "回退到上一版本";
            btnRollback.Location = new Point(394, 146);
            btnRollback.Size = new Size(180, 30);
            btnRollback.Click += new EventHandler(OnRollbackClick);
            grpUpdate.Controls.Add(btnRollback);

            Label lblPolicy = new Label();
            lblPolicy.AutoSize = true;
            lblPolicy.ForeColor = Color.DimGray;
            lblPolicy.Location = new Point(14, 186);
            lblPolicy.Text = "注：「一键还原原版英文」不会删除你的更新设置；回退需要本地缓存过上一版本的安装包。";
            grpUpdate.Controls.Add(lblPolicy);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(245, 247, 250);
            txtLog.Location = new Point(22, 564);
            txtLog.Size = new Size(645, 150);
            this.Controls.Add(txtLog);

            Log("欢迎使用 Command: Modern Operations (CMO) 完整中文汉化安装程序。");
            Log("本汉化涵盖：主界面、菜单、作战条令(RoE)、任务编辑、航空作业、全部45个教程及全球4608处地名。");
            RefreshStatus();
            RefreshUpdateState();
        }

        private void OnDonateClick(object sender, EventArgs e)
        {
            using (DonateForm df = new DonateForm())
            {
                df.ShowDialog(this);
            }
        }

        private void Log(string msg)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "请选择 Command: Modern Operations 游戏安装根目录 (包含 Command.exe)";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    txtPath.Text = fbd.SelectedPath;
                    RefreshStatus();
                }
            }
        }

        private void RefreshStatus()
        {
            string gp = txtPath.Text.Trim();
            if (string.IsNullOrEmpty(gp) || !File.Exists(Path.Combine(gp, "Command.exe")))
            {
                lblStatus.Text = "当前状态: 未找到 Command.exe，请点击[浏览目录]选择正确的游戏根目录";
                lblStatus.ForeColor = Color.Red;
                return;
            }

            // 状态以游戏目录内的安装清单（部署实证）为准，不再单看注册表 Version：
            // 注册表值会被任意安装包覆盖、会被旧版还原清空，从而“说谎”。
            string detail;
            ManifestState state = InstallManifest.Verify(gp, out detail);
            if (state == ManifestState.Ok)
            {
                InstallManifest m = InstallManifest.Read(gp);
                lblStatus.Text = "当前状态: 已安装完整中文汉化补丁 v" + m.Version
                    + "（" + InstallerCore.LocalTimeText(m.InstalledAt) + " 安装）";
                lblStatus.ForeColor = Color.Green;
            }
            else if (state == ManifestState.Modified)
            {
                lblStatus.Text = "当前状态: 检测到" + detail + "，建议重新点击[一键安装中文汉化补丁]修复";
                lblStatus.ForeColor = Color.DarkOrange;
            }
            else if (InstallerCore.CheckInstalled(gp))
            {
                // 存量 4.1.x 用户 / 绿色包用户：有文件无清单 → 自愈登记，不再误报“未安装”
                InstallManifest.Adopt(gp, CmoSettings.RegistryInstalledVersion());
                InstallManifest adopted = InstallManifest.Read(gp);
                string shown = (adopted != null && adopted.Version.Length > 0) ? adopted.Version : "未知";
                lblStatus.Text = "当前状态: 已安装完整中文汉化补丁（已登记当前状态，v" + shown + "）";
                lblStatus.ForeColor = Color.Green;
            }
            else
            {
                lblStatus.Text = "当前状态: 原版英文环境 (未安装汉化补丁)";
                lblStatus.ForeColor = Color.DarkOrange;
            }
        }

        private void RefreshUpdateState()
        {
            CmoSettings st = CmoSettings.Load();
            suppressUpdateUiEvent = true;
            cbInterval.SelectedIndex = IntervalPresets.ToIndex(st.CheckIntervalHours);
            cbNotify.SelectedIndex = st.NotifyMode < 0 || st.NotifyMode > 2 ? 1 : st.NotifyMode;
            suppressUpdateUiEvent = false;
            suppressAutoStartEvent = true;
            chkAutoStart.Checked = InstallerCore.GetAutoStart();
            suppressAutoStartEvent = false;
            btnNeverRemind.Text = st.AutoCheck ? "不再提醒" : "开启提醒";
            lblUpdateState.Text = st.AutoCheck
                ? ("自动检查已开启：开机后自动检查一次，之后" + IntervalPresets.Text(st.CheckIntervalHours) + "检查一次")
                : "已关闭自动检查（点击「开启提醒」可随时恢复）";

            string installed = InstallerCore.StateVersion(txtPath.Text.Trim());
            lblUpdateNext.Text = installed.Length > 0
                ? ("当前已安装版本 v" + installed + "　·　上次检查：" + InstallerCore.LastCheckText() + "　·　下次：" + InstallerCore.NextCheckText())
                : "尚未安装汉化补丁 —— 安装后即可使用开机自检与自动升级";

            string prev = VersionStore.Previous(installed);
            btnRollback.Text = prev != null ? ("回退到上一版本 v" + prev) : "回退到上一版本";
            btnRollback.Enabled = prev != null;
            toolTip.SetToolTip(btnRollback, prev != null
                ? ("用本地缓存的 v" + prev + " 安装包重新安装，回到上一版本")
                : "本地没有更旧版本的安装包缓存（安装/升级时会自动缓存安装包）");
        }

        private void OnIntervalChanged(object sender, EventArgs e)
        {
            if (suppressUpdateUiEvent) return;
            CmoSettings st = CmoSettings.Load();
            st.CheckIntervalHours = IntervalPresets.ToHours(cbInterval.SelectedIndex);
            st.Save();
            Log("已把自动检查频率改为" + IntervalPresets.Text(st.CheckIntervalHours) + "一次。");
            RefreshUpdateState();
        }

        private void OnNotifyChanged(object sender, EventArgs e)
        {
            if (suppressUpdateUiEvent) return;
            CmoSettings st = CmoSettings.Load();
            st.NotifyMode = cbNotify.SelectedIndex < 0 ? 1 : cbNotify.SelectedIndex;
            st.Save();
            Log("已把提醒方式改为「" + cbNotify.Text + "」。");
            RefreshUpdateState();
        }

        private void OnRollbackClick(object sender, EventArgs e)
        {
            string gp = txtPath.Text.Trim();
            string installed = InstallerCore.StateVersion(gp);
            string prev = VersionStore.Previous(installed);
            if (prev == null)
            {
                MessageBox.Show("本地没有更旧版本的安装包缓存，无法离线回退。\n\n可在发布页下载对应版本安装包后手动安装。",
                    "回退到上一版本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("将用本地缓存的 v" + prev + " 安装包重新安装，把补丁回退到 v" + prev + "。\n\n"
                + "（如果游戏正在运行会先关闭游戏；回退后请重新启动游戏）\n\n确定继续？",
                "回退到上一版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Log("开始回退到 v" + prev + " ...");
            string err;
            if (Rollback.Run(gp, prev, false, out err))
            {
                Log("已回退到 v" + prev + "，安装状态已更新为 v" + prev + "。");
                MessageBox.Show("已回退到 v" + prev + "。", "回退成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("[错误] 回退失败: " + err);
                MessageBox.Show("回退失败:\n" + err, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshStatus();
            RefreshUpdateState();
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (suppressAutoStartEvent) return;
            InstallerCore.SetAutoStart(chkAutoStart.Checked);
            Log(chkAutoStart.Checked
                ? "已开启开机自动检查：随 Windows 启动并在后台每 6 小时检查一次更新。"
                : "已关闭开机自动检查（仍可从更新助手/安装器手动检查）。");
            RefreshUpdateState();
        }

        private void OnCheckUpdateClick(object sender, EventArgs e)
        {
            RunUpdater("检查升级");
        }

        private void OnUpgradeClick(object sender, EventArgs e)
        {
            RunUpdater("立即升级");
        }

        private void RunUpdater(string what)
        {
            try
            {
                string gp = txtPath.Text.Trim();
                if (Directory.Exists(gp)) InstallerCore.RememberGamePath(gp);
                string err;
                string exe = InstallerCore.LaunchUpdater(gp, out err);
                if (exe == null)
                {
                    Log("[" + what + "] 启动更新助手失败: " + err);
                    MessageBox.Show("无法启动更新助手:\n" + err, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Log("[" + what + "] 已启动更新助手，请在弹出的窗口中确认升级。");
                RefreshUpdateState();
            }
            catch (Exception ex)
            {
                Log("[" + what + "] 异常: " + ex.Message);
            }
        }

        private void OnNeverRemindClick(object sender, EventArgs e)
        {
            bool nowAuto = InstallerCore.ToggleNeverRemind();
            btnNeverRemind.Text = nowAuto ? "不再提醒" : "开启提醒";
            if (nowAuto)
            {
                Log("已重新开启自动更新检查（每 6 小时）。");
                MessageBox.Show("已重新开启自动更新检查。", "自动升级", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("已选择不再提醒：自动检查已关闭，并记住了当前版本；手动「检查升级」仍可使用。");
                MessageBox.Show("已关闭自动更新提醒。\n\n以后需要升级时，随时点击「检查升级」即可。", "自动升级",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            RefreshUpdateState();
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            string gp = txtPath.Text.Trim();
            string err;
            if (InstallerCore.Install(gp, out err))
            {
                Log("Successfully Installed! 已成功复制 CMOLocalizer.dll 与 CMOLocalizer.json。");
                Log("已自动备份原版配置为 Command.exe.config.cmo_bak 并启用本地化引擎。");
                RefreshStatus();
                MessageBox.Show("CMO 完整中文汉化补丁安装成功！\n现在直接从 Steam 或运行 Command.exe 启动游戏即可显示中文。\n\n已同时启用自动升级：开机后自动检查一次，之后每 6 小时检查一次。", "安装成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("[错误] 安装失败: " + err);
                MessageBox.Show("安装失败:\n" + err, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnUninstallClick(object sender, EventArgs e)
        {
            string gp = txtPath.Text.Trim();
            string err;
            if (InstallerCore.Uninstall(gp, out err))
            {
                bool purge = chkPurgeSettings.Checked;
                InstallManifest.Clear(gp);
                if (purge) CmoSettings.Purge();
                Log(purge
                    ? "已成功卸载中文汉化补丁并恢复原版 Command.exe.config 配置（已按你的选择清除全部更新设置）。"
                    : "已成功卸载中文汉化补丁并恢复原版 Command.exe.config 配置（你的更新设置已保留，重装后继续生效）。");
                RefreshStatus();
                RefreshUpdateState();
                MessageBox.Show(purge
                    ? "已无损还原为原版英文环境！（更新设置也已按你的选择清除）"
                    : "已无损还原为原版英文环境！\n\n你的更新设置（检查频率/提醒方式等）已保留，下次安装会继续使用。",
                    "还原成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("[错误] 还原失败: " + err);
                MessageBox.Show("还原失败:\n" + err, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class DonateForm : Form
    {
        public DonateForm()
        {
            this.Text = "☕ 请作者喝杯咖啡 / 可乐 - 感谢您的支持与鼓励";
            this.Size = new Size(580, 525);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            this.BackColor = Color.White;

            Label lblTitle = new Label();
            lblTitle.Text = "❤️ 感谢支持 CMO 中文本地化项目";
            lblTitle.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(180, 85, 20);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(20, 16);
            this.Controls.Add(lblTitle);

            Label lblDesc = new Label();
            lblDesc.Text = "本汉化补丁完全免费开源，由爱好者利用业余时间持续维护更新。\n如果您觉得汉化体验还不错，欢迎扫码请作者喝杯咖啡或可乐，感谢您的支持！";
            lblDesc.ForeColor = Color.FromArgb(80, 80, 80);
            lblDesc.AutoSize = true;
            lblDesc.Location = new Point(22, 48);
            this.Controls.Add(lblDesc);

            // 微信支付卡片
            Panel pnlWeChat = new Panel();
            pnlWeChat.Location = new Point(25, 100);
            pnlWeChat.Size = new Size(245, 335);
            pnlWeChat.BorderStyle = BorderStyle.FixedSingle;
            pnlWeChat.BackColor = Color.FromArgb(250, 254, 250);
            this.Controls.Add(pnlWeChat);

            Label lblWeChat = new Label();
            lblWeChat.Text = "微信支付";
            lblWeChat.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblWeChat.ForeColor = Color.FromArgb(7, 193, 96);
            lblWeChat.Dock = DockStyle.Top;
            lblWeChat.Height = 32;
            lblWeChat.TextAlign = ContentAlignment.MiddleCenter;
            pnlWeChat.Controls.Add(lblWeChat);

            PictureBox pbWeChat = new PictureBox();
            pbWeChat.Dock = DockStyle.Fill;
            pbWeChat.SizeMode = PictureBoxSizeMode.Zoom;
            pbWeChat.Image = LoadEmbeddedImage("QR_WeChat");
            pnlWeChat.Controls.Add(pbWeChat);
            pbWeChat.BringToFront();

            // 支付宝卡片
            Panel pnlAlipay = new Panel();
            pnlAlipay.Location = new Point(295, 100);
            pnlAlipay.Size = new Size(245, 335);
            pnlAlipay.BorderStyle = BorderStyle.FixedSingle;
            pnlAlipay.BackColor = Color.FromArgb(250, 252, 255);
            this.Controls.Add(pnlAlipay);

            Label lblAlipay = new Label();
            lblAlipay.Text = "支付宝";
            lblAlipay.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblAlipay.ForeColor = Color.FromArgb(22, 119, 255);
            lblAlipay.Dock = DockStyle.Top;
            lblAlipay.Height = 32;
            lblAlipay.TextAlign = ContentAlignment.MiddleCenter;
            pnlAlipay.Controls.Add(lblAlipay);

            PictureBox pbAlipay = new PictureBox();
            pbAlipay.Dock = DockStyle.Fill;
            pbAlipay.SizeMode = PictureBoxSizeMode.Zoom;
            pbAlipay.Image = LoadEmbeddedImage("QR_Alipay");
            pnlAlipay.Controls.Add(pbAlipay);
            pbAlipay.BringToFront();

            Button btnClose = new Button();
            btnClose.Text = "关 闭";
            btnClose.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            btnClose.Location = new Point(230, 448);
            btnClose.Size = new Size(110, 32);
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);
        }

        private Image LoadEmbeddedImage(string resName)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] allRes = asm.GetManifestResourceNames();
                foreach (string name in allRes)
                {
                    if (name.EndsWith(resName, StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream stream = asm.GetManifestResourceNames() != null ? asm.GetManifestResourceStream(name) : null)
                        {
                            if (stream != null)
                            {
                                using (Image tmp = Image.FromStream(stream))
                                {
                                    return new Bitmap(tmp);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    public static class InstallerCore
    {
        public static string DetectGamePath()
        {
            // 1. 如果安装程序直接放在游戏根目录下运行
            string cur = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(cur, "Command.exe"))) return cur;

            // 2. 通过注册表检测 Steam 游戏安装路径 (支持 32/64 位及 CurrentUser)
            string[] uninstallKeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1076160",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1076160"
            };

            foreach (string sub in uninstallKeys)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(sub))
                    {
                        if (key != null)
                        {
                            object loc = key.GetValue("InstallLocation");
                            if (loc != null && File.Exists(Path.Combine(loc.ToString(), "Command.exe")))
                                return loc.ToString();
                        }
                    }
                }
                catch { }

                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(sub))
                    {
                        if (key != null)
                        {
                            object loc = key.GetValue("InstallLocation");
                            if (loc != null && File.Exists(Path.Combine(loc.ToString(), "Command.exe")))
                                return loc.ToString();
                        }
                    }
                }
                catch { }
            }

            // 3. 读取 Steam 主目录并解析所有 Steam 库文件夹 (libraryfolders.vdf)
            string steamMainDir = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        object sp = key.GetValue("SteamPath");
                        if (sp != null) steamMainDir = sp.ToString().Replace('/', '\\');
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(steamMainDir))
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        if (key != null)
                        {
                            object ip = key.GetValue("InstallPath");
                            if (ip != null) steamMainDir = ip.ToString().Replace('/', '\\');
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(steamMainDir) && Directory.Exists(steamMainDir))
            {
                // 检查 Steam 默认主库
                string mainCmo = Path.Combine(steamMainDir, @"steamapps\common\Command - Modern Operations");
                if (File.Exists(Path.Combine(mainCmo, "Command.exe"))) return mainCmo;

                // 解析 libraryfolders.vdf 获取用户的所有自定义多盘符库
                string vdfPath = Path.Combine(steamMainDir, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    try
                    {
                        string vdfText = File.ReadAllText(vdfPath);
                        var matches = System.Text.RegularExpressions.Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\"");
                        foreach (System.Text.RegularExpressions.Match m in matches)
                        {
                            if (m.Success && m.Groups.Count > 1)
                            {
                                string libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                                string cmoInLib = Path.Combine(libPath, @"steamapps\common\Command - Modern Operations");
                                if (File.Exists(Path.Combine(cmoInLib, "Command.exe"))) return cmoInLib;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 4. 自动全盘驱动器遍历扫描 (涵盖常见安装盘符与自建库)
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                string[] checkRel = new string[]
                {
                    @"soft\steam\steamapps\common\Command - Modern Operations",
                    @"Steam\steamapps\common\Command - Modern Operations",
                    @"SteamLibrary\steamapps\common\Command - Modern Operations",
                    @"Program Files (x86)\Steam\steamapps\common\Command - Modern Operations",
                    @"Program Files\Steam\steamapps\common\Command - Modern Operations",
                    @"Games\Steam\steamapps\common\Command - Modern Operations",
                    @"Games\SteamLibrary\steamapps\common\Command - Modern Operations"
                };

                foreach (DriveInfo d in drives)
                {
                    if (!d.IsReady) continue;
                    foreach (string rel in checkRel)
                    {
                        string p = Path.Combine(d.RootDirectory.FullName, rel);
                        if (File.Exists(Path.Combine(p, "Command.exe"))) return p;
                    }
                }
            }
            catch { }

            return "";
        }

        public static bool CheckInstalled(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath)) return false;
            string cfgPath = Path.Combine(gamePath, "Command.exe.config");
            string dllDest = Path.Combine(gamePath, "CMOLocalizer.dll");
            string jsonDest = Path.Combine(gamePath, "CMOLocalizer.json");
            if (!File.Exists(cfgPath) || !File.Exists(dllDest) || !File.Exists(jsonDest)) return false;
            try
            {
                string txt = File.ReadAllText(cfgPath);
                return txt.Contains("CMOLocalizer.LocalizerDomainManager");
            }
            catch
            {
                return false;
            }
        }

        public static bool Install(string gamePath, out string errorMsg)
        {
            errorMsg = "";
            if (string.IsNullOrEmpty(gamePath) || !File.Exists(Path.Combine(gamePath, "Command.exe")))
            {
                errorMsg = "未在目标路径找到 Command.exe，请确认游戏目录是否正确。";
                return false;
            }

            string curDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllSrc = Path.Combine(curDir, "CMOLocalizer.dll");
            string jsonSrc = Path.Combine(curDir, "CMOLocalizer.json");

            if (!File.Exists(dllSrc))
            {
                dllSrc = Path.Combine(curDir, "patch_files", "CMOLocalizer.dll");
                jsonSrc = Path.Combine(curDir, "patch_files", "CMOLocalizer.json");
            }

            try
            {
                string dllDest = Path.Combine(gamePath, "CMOLocalizer.dll");
                string jsonDest = Path.Combine(gamePath, "CMOLocalizer.json");

                if (File.Exists(dllSrc))
                {
                    if (!string.Equals(Path.GetFullPath(dllSrc), Path.GetFullPath(dllDest), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(dllSrc, dllDest, true);
                    }
                }
                else
                {
                    if (!ExtractEmbeddedResource("CMOLocalizer.dll", dllDest))
                    {
                        errorMsg = "安装包内未找到 CMOLocalizer.dll 资源文件！";
                        return false;
                    }
                }

                if (File.Exists(jsonSrc))
                {
                    if (!string.Equals(Path.GetFullPath(jsonSrc), Path.GetFullPath(jsonDest), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(jsonSrc, jsonDest, true);
                    }
                }
                else
                {
                    if (!ExtractEmbeddedResource("CMOLocalizer.json", jsonDest))
                    {
                        errorMsg = "安装包内未找到 CMOLocalizer.json 资源文件！";
                        return false;
                    }
                }

                string cfgPath = Path.Combine(gamePath, "Command.exe.config");
                string bakPath = Path.Combine(gamePath, "Command.exe.config.cmo_bak");
                if (!File.Exists(bakPath) && File.Exists(cfgPath))
                {
                    File.Copy(cfgPath, bakPath, true);
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(cfgPath);

                XmlNode runtime = doc.SelectSingleNode("//configuration/runtime");
                if (runtime == null)
                {
                    XmlNode config = doc.SelectSingleNode("//configuration");
                    runtime = doc.CreateElement("runtime");
                    config.AppendChild(runtime);
                }

                XmlNode typeNode = runtime.SelectSingleNode("appDomainManagerType");
                if (typeNode == null)
                {
                    typeNode = doc.CreateElement("appDomainManagerType");
                    XmlAttribute attr = doc.CreateAttribute("value");
                    attr.Value = "CMOLocalizer.LocalizerDomainManager";
                    typeNode.Attributes.Append(attr);
                    runtime.AppendChild(typeNode);
                }
                else
                {
                    typeNode.Attributes["value"].Value = "CMOLocalizer.LocalizerDomainManager";
                }

                XmlNode asmNode = runtime.SelectSingleNode("appDomainManagerAssembly");
                if (asmNode == null)
                {
                    asmNode = doc.CreateElement("appDomainManagerAssembly");
                    XmlAttribute attr = doc.CreateAttribute("value");
                    attr.Value = "CMOLocalizer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                    asmNode.Attributes.Append(attr);
                    runtime.AppendChild(asmNode);
                }
                else
                {
                    asmNode.Attributes["value"].Value = "CMOLocalizer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                }

                doc.Save(cfgPath);

                // 释放汉化版武器数据库 HTML 模板 (DB/Templates)
                string tplDir = Path.Combine(gamePath, "DB", "Templates");
                string tplBakDir = Path.Combine(gamePath, "DB", "Templates_Original_Backup");
                if (Directory.Exists(tplDir))
                {
                    if (!Directory.Exists(tplBakDir))
                    {
                        Directory.CreateDirectory(tplBakDir);
                        foreach (string f in Directory.GetFiles(tplDir, "*.html"))
                        {
                            File.Copy(f, Path.Combine(tplBakDir, Path.GetFileName(f)), true);
                        }
                    }
                    string[] tplFiles = new string[] {
                        "Aircraft.html", "Facility.html", "GroundUnit.html", "Platform.html",
                        "Satellite.html", "Ship.html", "Submarine.html", "Weapon.html"
                    };
                    foreach (string tf in tplFiles)
                    {
                        ExtractEmbeddedResource("TPL_" + tf, Path.Combine(tplDir, tf));
                    }
                }

                // 部署已全量汉化的 Scenarios HTML 简报/教程正文（内嵌 zip，带 .orig 备份）
                DeployScenarioHtml(gamePath);

                // 部署汉化地名集（地图矢量地名层的数据源，必须先于标签瓦片重绘）
                DeployPlacenames(gamePath);

                // 部署 Symbols\Menu 中文按钮图与启动器中文资源
                DeploySymbolsAndLauncher(gamePath);

                // 部署自动升级组件并写入设置（开机自启 / 每 6 小时检查）
                DeployUpdater(gamePath);
                WriteRegistrySettings(gamePath);

                // 兜底：对 zip 未覆盖的 HTML 做标题级替换 (带 .orig 备份)
                LocalizeScenarioBriefings(Path.Combine(gamePath, "Scenarios"));

                // 自动汉化 World Wind 标注图层瓦片 (WW/Cache/Stamen Terrain - Labels)
                LocalizeMapLabelTiles(gamePath);

                // 最后一步：写「安装清单」（部署实证）—— 它是「装了什么版本」的唯一真相，
                // 也是「一键还原后立即显示原版英文环境」的依据；失败不得留下半份清单。
                // 先做一次版本戳一致性自检：安装包内嵌词典 _meta.version 应与 CurrentVersion 一致，
                // 不一致说明打包时忘了同步版本戳（历史上出现过，会让“已装版本”与实际资产不符）。
                string bundleVer = VersionStore.PackageVersion(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(bundleVer) && bundleVer != CurrentVersion)
                {
                    CmoLog.Warn("版本戳不一致：安装包内嵌词典 _meta.version=" + bundleVer
                        + "，安装器 CurrentVersion=" + CurrentVersion);
                }
                Dictionary<string, string> manifestFiles = InstallManifest.HashEngineFiles(gamePath);
                if (!InstallManifest.Write(gamePath, CurrentVersion, Application.ExecutablePath, manifestFiles))
                {
                    errorMsg = "汉化文件已部署，但写入安装清单失败（状态可能显示不准，重新安装即可修复）。";
                    return false;
                }
                // 缓存自身安装包：回退以整个安装包为单位（权威、自带全部资产）
                VersionStore.Cache(Application.ExecutablePath);

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }



        /// <summary>
        /// 部署汉化后的 World Wind 地名集（WW\PlacenameSets\*\0000_0000.wwp）。
        /// 地图上的国名/城市名以及 Stamen 标签瓦片重绘都以它为数据源；
        /// 不部署它则用户装完包地图仍是英文。覆盖前写 .orig 备份，卸载可还原。
        /// </summary>
        /// <summary>
        /// 部署 Symbols\Menu 中文按钮图（引擎启动时预加载，遗漏会导致按钮图仍是英文）
        /// 与启动器中文资源（Launcher\Localization\stringsChinese.resx）。
        /// 覆盖前写 .orig 备份，卸载可还原。
        /// </summary>
        private static void DeploySymbolsAndLauncher(string gamePath)
        {
            try
            {
                string menuDir = Path.Combine(gamePath, @"Symbols\Menu");
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("SYMBOLS_MENU_ZIP"))
                {
                    if (s != null && Directory.Exists(menuDir))
                    {
                        using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                        {
                            foreach (ZipArchiveEntry e in zip.Entries)
                            {
                                if (e.Name.Length == 0) continue;
                                string dest;
                                if (!CmoPaths.TryResolveInside(menuDir, e.FullName, out dest))
                                {
                                    CmoLog.Warn("跳过越界 zip 条目: " + e.FullName);
                                    continue;
                                }
                                string dir = Path.GetDirectoryName(dest);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                string bak = dest + ".orig";
                                if (File.Exists(dest) && !File.Exists(bak)) File.Copy(dest, bak, true);
                                using (Stream es = e.Open())
                                using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
                                {
                                    es.CopyTo(fs);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                string locDir = Path.Combine(gamePath, @"Launcher\Localization");
                if (Directory.Exists(locDir))
                {
                    string dest = Path.Combine(locDir, "stringsChinese.resx");
                    string bak = dest + ".orig";
                    if (File.Exists(dest) && !File.Exists(bak)) File.Copy(dest, bak, true);
                    ExtractEmbeddedResource("LAUNCHER_ZH", dest);
                }
            }
            catch { }
        }

        /// <summary>卸载：还原 Symbols\Menu 与启动器中文资源。</summary>
        private static void RestoreSymbolsAndLauncher(string gamePath)
        {
            try
            {
                string menuDir = Path.Combine(gamePath, @"Symbols\Menu");
                if (Directory.Exists(menuDir))
                {
                    foreach (string f in Directory.GetFiles(menuDir, "*.orig", SearchOption.AllDirectories))
                    {
                        string target = f.Substring(0, f.Length - ".orig".Length);
                        File.Copy(f, target, true);
                        File.Delete(f);
                    }
                }
            }
            catch { }
            try
            {
                string orig = Path.Combine(gamePath, @"Launcher\Localization\stringsChinese.resx.orig");
                string res = Path.Combine(gamePath, @"Launcher\Localization\stringsChinese.resx");
                if (File.Exists(orig))
                {
                    File.Copy(orig, res, true);
                    File.Delete(orig);
                }
            }
            catch { }
        }

        private static void DeployPlacenames(string gamePath)
        {
            string[][] sets = new string[][] {
                new string[] { "PLACE_SYNOPTIC", @"WW\PlacenameSets\Synoptic" },
                new string[] { "PLACE_CITIES",   @"WW\PlacenameSets\Cities" }
            };
            foreach (string[] s in sets)
            {
                try
                {
                    string dir = Path.Combine(gamePath, s[1]);
                    if (!Directory.Exists(dir)) continue;
                    string target = Path.Combine(dir, "0000_0000.wwp");
                    string backup = target + ".orig";
                    if (File.Exists(target) && !File.Exists(backup)) File.Copy(target, backup, true);
                    ExtractEmbeddedResource(s[0], target);
                }
                catch { }
            }
        }

        /// <summary>卸载：还原原始英文地名集。</summary>
        private static void RestorePlacenames(string gamePath)
        {
            string[] dirs = new string[] { @"WW\PlacenameSets\Synoptic", @"WW\PlacenameSets\Cities" };
            foreach (string d in dirs)
            {
                try
                {
                    string target = Path.Combine(gamePath, d, "0000_0000.wwp");
                    string backup = target + ".orig";
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, target, true);
                        File.Delete(backup);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 部署随包分发的已汉化 Scenarios HTML（内嵌 SCEN_HTML_ZIP）。
        /// 覆盖前先写 .orig 原始英文备份；已存在的 .orig 永不覆盖，保证卸载可 100% 还原。
        /// </summary>
        private static void DeployScenarioHtml(string gamePath)
        {
            try
            {
                string scenDir = Path.Combine(gamePath, "Scenarios");
                if (!Directory.Exists(scenDir)) return;
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("SCEN_HTML_ZIP"))
                {
                    if (s == null) return;
                    using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            if (e.Length == 0) continue;
                            string dest;
                            if (!CmoPaths.TryResolveInside(gamePath, e.FullName, out dest))
                            {
                                CmoLog.Warn("跳过越界 zip 条目: " + e.FullName);
                                continue;
                            }
                            string dir = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            if (dest.EndsWith(".orig", StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(dest)) continue;
                            }
                            else
                            {
                                string orig = dest + ".orig";
                                if (!File.Exists(orig) && File.Exists(dest)) File.Copy(dest, orig, true);
                            }
                            using (Stream es = e.Open())
                            using (FileStream fs = File.Create(dest))
                            {
                                es.CopyTo(fs);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static readonly System.Text.RegularExpressions.Regex HanRegex =
            new System.Text.RegularExpressions.Regex("[\u4e00-\u9fff]");

        private static void LocalizeScenarioBriefings(string scenDir)
        {
            if (!Directory.Exists(scenDir)) return;
            try
            {
                string[] htmls = Directory.GetFiles(scenDir, "*.htm*", SearchOption.AllDirectories);
                foreach (string h in htmls)
                {
                    if (h.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)) continue;
                    // 已由 zip 全量汉化的文件不得再用标题级替换覆盖（否则会降级成中英混排）
                    if (HanRegex.IsMatch(File.ReadAllText(h, Encoding.UTF8))) continue;
                    string orig = h + ".orig";
                    if (!File.Exists(orig))
                    {
                        File.Copy(h, orig, true);
                    }
                    string text = File.ReadAllText(orig, Encoding.UTF8);
                    string trans = text
                        .Replace("<h2>Briefing</h2>", "<h2>作战任务简报 (Briefing)</h2>")
                        .Replace("<h1>Introduction</h1>", "<h1>战役背景导言 (Introduction)</h1>")
                        .Replace("<h3>Situation</h3>", "<h3>战场态势 (Situation)</h3>")
                        .Replace("<h3>Timeline:</h3>", "<h3>行动时间线:</h3>")
                        .Replace("<h3>Threat:</h3>", "<h3>敌情威胁评估:</h3>")
                        .Replace("<h3>Assets:</h3>", "<h3>我方兵力配属:</h3>")
                        .Replace("<h3>Task:</h3>", "<h3>作战任务指标:</h3>")
                        .Replace("<h3>Execution</h3>", "<h3>作战实施决心 (Execution)</h3>")
                        .Replace("<h3>Friendly Forces</h3>", "<h3>我方友邻部队 (Friendly Forces)</h3>")
                        .Replace("<h3>Scenario Notes:</h3>", "<h3>想定设计说明:</h3>")
                        .Replace("<B>Original Author:</B>", "<B>想定原作者:</B>")
                        .Replace("<B>Location:</B>", "<B>战区位置:</B>")
                        .Replace("<B>Playable Sides:</B>", "<B>可扮演参战方:</B>")
                        .Replace("You are NOT, REPEAT NOT authorized to use nuclear weapons.", "你严禁（重复：严禁）动用任何核武器。");
                    if (trans != text)
                    {
                        File.WriteAllText(h, trans, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        private static void RestoreScenarioBriefings(string scenDir)
        {
            if (!Directory.Exists(scenDir)) return;
            try
            {
                string[] origs = Directory.GetFiles(scenDir, "*.orig", SearchOption.AllDirectories);
                foreach (string orig in origs)
                {
                    string target = orig.Substring(0, orig.Length - 5);
                    File.Copy(orig, target, true);
                    File.Delete(orig);
                }
            }
            catch { }
        }

        private class TilePlace
        {
            public string Name;
            public double Lat;
            public double Lon;
            public int Level;
        }

        private static void LocalizeMapLabelTiles(string gamePath)
        {
            try
            {
                string cacheDir = Path.Combine(gamePath, @"WW\Cache\Stamen Terrain - Labels");
                string backupDir = Path.Combine(gamePath, @"WW\Cache\Stamen Terrain - Labels.orig");
                if (!Directory.Exists(cacheDir)) return;

                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                    foreach (string f in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(cacheDir.Length).TrimStart('\\', '/');
                        string dest = Path.Combine(backupDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, true);
                    }
                }

                List<TilePlace> list = new List<TilePlace>();
                string synWwp = Path.Combine(gamePath, @"WW\PlacenameSets\Synoptic\0000_0000.wwp");
                string citWwp = Path.Combine(gamePath, @"WW\PlacenameSets\Cities\0000_0000.wwp");

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
                                list.Add(new TilePlace { Name = name, Lat = lat, Lon = lon, Level = lvl });
                            }
                        }
                    }
                    catch { }
                };

                loadWwp(synWwp, 0);
                loadWwp(citWwp, 1);
                if (list.Count == 0) return;

                string[] pngs = Directory.GetFiles(cacheDir, "*.png", SearchOption.AllDirectories);
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                Parallel.ForEach(pngs, pngPath =>
                {
                    try
                    {
                        string norm = pngPath.Replace('\\', '/');
                        string[] parts = norm.Split('/');
                        int z = int.Parse(parts[parts.Length - 3]);
                        int x = int.Parse(parts[parts.Length - 2]);
                        string fName = parts[parts.Length - 1];
                        int y = int.Parse(fName.Substring(0, fName.IndexOf('.')));

                        double n = Math.Pow(2.0, z);
                        double minLon = x / n * 360.0 - 180.0;
                        double maxLon = (x + 1) / n * 360.0 - 180.0;
                        double maxLat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
                        double minLat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * (y + 1) / n))) * 180.0 / Math.PI;

                        List<TilePlace> inTile = new List<TilePlace>();
                        foreach (var p in list)
                        {
                            if (p.Lon >= minLon && p.Lon <= maxLon && p.Lat >= minLat && p.Lat <= maxLat)
                            {
                                if (z <= 5) { if (p.Level == 0) inTile.Add(p); }
                                else if (z <= 7) { inTile.Add(p); }
                                else { if (p.Level == 1) inTile.Add(p); }
                            }
                        }

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

                                    List<RectangleF> occupied = new List<RectangleF>();
                                    foreach (var p in inTile)
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
                                            foreach (var occ in occupied)
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
                                            }
                                        }
                                    }
                                }
                            }
                            bmp.Save(pngPath, ImageFormat.Png);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static void RestoreMapLabelTiles(string gamePath)
        {
            try
            {
                string cacheDir = Path.Combine(gamePath, @"WW\Cache\Stamen Terrain - Labels");
                string backupDir = Path.Combine(gamePath, @"WW\Cache\Stamen Terrain - Labels.orig");
                if (Directory.Exists(backupDir))
                {
                    if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
                    Directory.Move(backupDir, cacheDir);
                }
            }
            catch { }
        }

        private static bool ExtractEmbeddedResource(string resName, string destPath)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] allRes = asm.GetManifestResourceNames();
                string match = null;
                foreach (string name in allRes)
                {
                    if (name.EndsWith(resName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = name;
                        break;
                    }
                }
                if (match == null) return false;

                using (Stream stream = asm.GetManifestResourceStream(match))
                {
                    if (stream == null) return false;
                    using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  自动升级（更新助手 CMO_Updater.exe）集成
        // ──────────────────────────────────────────────────────────────
        // 发布常量的唯一来源是 CMOShared.ReleaseConstants（禁止在本文件再写字面量副本）。
        public const string CurrentVersion = ReleaseConstants.Version;
        private const string RegPath = ReleaseConstants.RegPath;
        private const string RunKeyName = ReleaseConstants.RunKeyName;

        private static object RegGet(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    return k == null ? null : k.GetValue(name);
                }
            }
            catch { return null; }
        }

        private static void RegSet(string name, object value, RegistryValueKind kind)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    if (k != null) k.SetValue(name, value, kind);
                }
            }
            catch { }
        }

        public static string InstalledVersion()
        {
            object v = RegGet("Version");
            return v == null ? "" : v.ToString();
        }

        /// <summary>状态显示用的已安装版本：清单优先，其次（迁移期）注册表。</summary>
        public static string StateVersion(string gamePath)
        {
            if (!string.IsNullOrEmpty(gamePath) && Directory.Exists(gamePath))
            {
                InstallManifest m = InstallManifest.Read(gamePath);
                if (m != null && m.Version.Length > 0 && m.Version != "unknown") return m.Version;
            }
            string reg = CmoSettings.RegistryInstalledVersion();
            return reg == null ? "" : reg;
        }

        public static string LocalTimeText(string iso)
        {
            DateTime d;
            if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return "未知时间";
        }

        public static void RememberGamePath(string gamePath)
        {
            if (!string.IsNullOrEmpty(gamePath)) RegSet("GamePath", gamePath, RegistryValueKind.String);
        }

        public static bool GetAutoCheck()
        {
            return CmoSettings.Load().AutoCheck;
        }

        public static void SetAutoCheck(bool on)
        {
            CmoSettings st = CmoSettings.Load();
            st.AutoCheck = on;
            st.Save();
            RegSet("AutoCheck", on ? 1 : 0, RegistryValueKind.DWord);   // 兼容旧版读取
        }

        /// <summary>「不再提醒」开关：关闭自动检查 + 记住当前版本；返回新的自动检查状态。</summary>
        public static bool ToggleNeverRemind()
        {
            CmoSettings st = CmoSettings.Load();
            st.AutoCheck = !st.AutoCheck;
            if (!st.AutoCheck)
            {
                string ver = StateVersion(CmoSettings.Load().GamePath);
                st.SkippedVersion = ver.Length > 0 ? ver : CurrentVersion;
            }
            else
            {
                st.SkippedVersion = "";
            }
            st.Save();
            RegSet("AutoCheck", st.AutoCheck ? 1 : 0, RegistryValueKind.DWord);
            return st.AutoCheck;
        }

        public static bool GetAutoStart()
        {
            return CmoSettings.Load().AutoStart;
        }

        public static string GetGamePath()
        {
            object v = RegGet("GamePath");
            if (v != null && Directory.Exists(v.ToString())) return v.ToString();
            return DetectGamePath();
        }

        public static void SetAutoStart(bool on)
        {
            CmoSettings st = CmoSettings.Load();
            st.AutoStart = on;
            st.Save();
            RegSet("AutoStart", on ? 1 : 0, RegistryValueKind.DWord);   // 兼容旧版读取
            try
            {
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (run == null) return;
                    string exe = Path.Combine(GetGamePath(), "CMO_Updater.exe");
                    if (on && File.Exists(exe))
                        run.SetValue(RunKeyName, "\"" + exe + "\" --boot", RegistryValueKind.String);
                    else if (!on)
                        run.DeleteValue(RunKeyName, false);
                }
            }
            catch { }
        }

        public static string LastCheckText()
        {
            string v = CmoSettings.Load().LastCheckUtc;
            if (string.IsNullOrEmpty(v)) return "尚未检查";
            DateTime d;
            if (!DateTime.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d))
                return "尚未检查";
            return d.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        public static string NextCheckText()
        {
            CmoSettings st = CmoSettings.Load();
            if (!st.AutoCheck) return "已关闭";
            string v = st.LastCheckUtc;
            DateTime d;
            if (string.IsNullOrEmpty(v) || !DateTime.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d))
                return "开机后自动检查";
            int hours = st.CheckIntervalHours;
            if (hours < 1) hours = 6;
            DateTime next = d.ToLocalTime().AddHours(hours);
            if (next <= DateTime.Now) return "即将检查";
            if (next.Date == DateTime.Today) return "今天 " + next.ToString("HH:mm");
            return next.ToString("yyyy-MM-dd HH:mm");
        }

        /// <summary>部署更新助手到游戏目录（内嵌资源 UPDATER_EXE）。</summary>
        public static bool DeployUpdater(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath)) return false;
            return ExtractEmbeddedResource("UPDATER_EXE", Path.Combine(gamePath, "CMO_Updater.exe"));
        }

        /// <summary>写入/补齐设置项；不改动用户已有的选择。</summary>
        public static void WriteRegistrySettings(string gamePath)
        {
            RegSet("GamePath", gamePath, RegistryValueKind.String);
            RegSet("Version", CurrentVersion, RegistryValueKind.String);
            if (RegGet("AutoCheck") == null) RegSet("AutoCheck", 1, RegistryValueKind.DWord);
            if (RegGet("CheckIntervalHours") == null) RegSet("CheckIntervalHours", 6, RegistryValueKind.DWord);
            if (RegGet("AutoStart") == null) RegSet("AutoStart", 1, RegistryValueKind.DWord);
            if (RegGet("NotifyMode") == null) RegSet("NotifyMode", 1, RegistryValueKind.DWord);
            if (RegGet("AutoInstall") == null) RegSet("AutoInstall", 1, RegistryValueKind.DWord);
            if (RegGet("Mirror") == null) RegSet("Mirror", ReleaseConstants.Mirror, RegistryValueKind.String);
            // 设置正文存 %APPDATA%\CMOChinese\settings.json（首次从上面的注册表值一次性迁移）
            CmoSettings st = CmoSettings.Load();
            if (string.IsNullOrEmpty(st.GamePath)) { st.GamePath = gamePath; st.Save(); }
            // 按用户的开机自启偏好重建 Run 键（还原时会被删掉，重装后需恢复）
            SetAutoStart(st.AutoStart);
        }

        /// <summary>静默安装之后把更新助手重新拉起（供 --restart-updater 使用）。</summary>
        public static void StartUpdater(string gamePath)
        {
            try
            {
                string exe = Path.Combine(gamePath, "CMO_Updater.exe");
                if (!File.Exists(exe) || !GetAutoStart()) return;
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--restarted");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        /// <summary>启动更新助手做一次检查（游戏目录没有时用临时释放的内嵌副本）。</summary>
        public static string LaunchUpdater(string gamePath, out string error)
        {
            error = "";
            try
            {
                string exe = string.IsNullOrEmpty(gamePath) ? "" : Path.Combine(gamePath, "CMO_Updater.exe");
                if (exe.Length == 0 || !File.Exists(exe))
                {
                    exe = Path.Combine(Path.GetTempPath(), "CMO_Updater.exe");
                    if (!ExtractEmbeddedResource("UPDATER_EXE", exe))
                    {
                        error = "当前安装包未内嵌更新助手（UPDATER_EXE 资源缺失），请使用 v4.1.4 及以上版本安装包。";
                        return null;
                    }
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--check");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
                return exe;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>卸载：结束更新助手、清理开机启动、注册表设置与程序本体。</summary>
        public static void CleanupUpdater(string gamePath)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("CMO_Updater"))
                {
                    try { p.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(700);
            }
            catch { }
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run != null) run.DeleteValue(RunKeyName, false);
                }
            }
            catch { }
            try
            {
                // 只清「安装态」的注册表版本号；更新频率/提醒方式/自启偏好等设置必须保留
                //（用户明确反馈过：还原后设置全没了）
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (k != null) k.DeleteValue("Version", false);
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(gamePath))
                {
                    string exe = Path.Combine(gamePath, "CMO_Updater.exe");
                    if (File.Exists(exe)) File.Delete(exe);
                }
            }
            catch { }
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "CMO_Updater.exe");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }

        public static bool Uninstall(string gamePath, out string errorMsg)
        {
            errorMsg = "";
            if (string.IsNullOrEmpty(gamePath) || !File.Exists(Path.Combine(gamePath, "Command.exe")))
            {
                errorMsg = "未在目标路径找到 Command.exe。";
                return false;
            }

            try
            {
                string cfgPath = Path.Combine(gamePath, "Command.exe.config");
                string bakPath = Path.Combine(gamePath, "Command.exe.config.cmo_bak");

                if (File.Exists(bakPath))
                {
                    File.Copy(bakPath, cfgPath, true);
                    File.Delete(bakPath);
                }
                else if (File.Exists(cfgPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(cfgPath);
                    XmlNode runtime = doc.SelectSingleNode("//configuration/runtime");
                    if (runtime != null)
                    {
                        XmlNode typeNode = runtime.SelectSingleNode("appDomainManagerType");
                        if (typeNode != null) runtime.RemoveChild(typeNode);
                        XmlNode asmNode = runtime.SelectSingleNode("appDomainManagerAssembly");
                        if (asmNode != null) runtime.RemoveChild(asmNode);
                        doc.Save(cfgPath);
                    }
                }

                string dllDest = Path.Combine(gamePath, "CMOLocalizer.dll");
                string jsonDest = Path.Combine(gamePath, "CMOLocalizer.json");
                string curDir = AppDomain.CurrentDomain.BaseDirectory;
                // Only delete if installer is not running from the game folder itself, or leave backup copy
                if (!string.Equals(Path.GetFullPath(curDir).TrimEnd('\\'), Path.GetFullPath(gamePath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(dllDest)) File.Delete(dllDest);
                    if (File.Exists(jsonDest)) File.Delete(jsonDest);
                }
                // 还原武器数据库 HTML 模板
                string tplDir = Path.Combine(gamePath, "DB", "Templates");
                string tplBakDir = Path.Combine(gamePath, "DB", "Templates_Original_Backup");
                if (Directory.Exists(tplBakDir) && Directory.Exists(tplDir))
                {
                    foreach (string f in Directory.GetFiles(tplBakDir, "*.html"))
                    {
                        File.Copy(f, Path.Combine(tplDir, Path.GetFileName(f)), true);
                    }
                }

                // 还原 Scenarios 下的 .orig 简报备份
                RestoreScenarioBriefings(Path.Combine(gamePath, "Scenarios"));

                // 还原 World Wind 标注图层瓦片
                RestoreMapLabelTiles(gamePath);

                // 还原原始英文地名集
                RestorePlacenames(gamePath);

                // 还原 Symbols\Menu 与启动器资源
                RestoreSymbolsAndLauncher(gamePath);

                // 清理自动升级：结束助手进程、删除开机启动项、注册表设置与助手本体
                CleanupUpdater(gamePath);

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string gp = DetectGamePath();

            if (args != null && args.Length > 0)
            {
                string cmd = null;
                string gpArg = null;
                int waitPid = 0;
                bool restartUpdater = false;
                bool purgeSettings = false;

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a == "--install" || a == "/install") cmd = "install";
                    else if (a == "--uninstall" || a == "/uninstall") cmd = "uninstall";
                    else if (a == "--rollback" || a == "/rollback") cmd = "rollback";
                    else if (a == "--adopt" || a == "/adopt") cmd = "adopt";
                    else if (a == "--list-versions" || a == "/list-versions") cmd = "list";
                    else if (a == "--purge-settings" || a == "/purge-settings") purgeSettings = true;
                    else if (a == "--silent" || a == "/silent") { }
                    else if (a == "--restart-updater") restartUpdater = true;
                    else if (a == "--wait-pid" && i + 1 < args.Length)
                    {
                        int.TryParse(args[++i], out waitPid);
                    }
                    else if (Directory.Exists(a)) gpArg = a;
                }

                if (gpArg != null) gp = gpArg;

                // 自更新：等待旧更新助手退出后再写文件，避免文件占用导致覆盖失败
                if (waitPid > 0)
                {
                    try
                    {
                        Process old = Process.GetProcessById(waitPid);
                        if (!old.WaitForExit(120000))
                        {
                            try { old.Kill(); } catch { }
                            System.Threading.Thread.Sleep(1500);
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(1200);
                }

                string err;
                if (cmd == "install")
                {
                    bool ok = Install(gp, out err);
                    Console.WriteLine(ok ? "[SUCCESS] Installed to " + gp : "[ERROR] " + err);
                    if (ok && restartUpdater) StartUpdater(gp);
                    return ok ? 0 : 1;
                }
                else if (cmd == "uninstall")
                {
                    bool ok = Uninstall(gp, out err);
                    if (ok)
                    {
                        InstallManifest.Clear(gp);
                        if (purgeSettings) CmoSettings.Purge();
                    }
                    Console.WriteLine(ok
                        ? "[SUCCESS] Uninstalled from " + gp + (purgeSettings ? " (settings purged)" : " (settings kept)")
                        : "[ERROR] " + err);
                    return ok ? 0 : 1;
                }
                else if (cmd == "rollback")
                {
                    string installed = StateVersion(gp);
                    string prev = VersionStore.Previous(installed);
                    if (prev == null)
                    {
                        Console.WriteLine("[ERROR] 本地没有比 v" + installed + " 更旧的安装包缓存");
                        return 1;
                    }
                    bool ok = Rollback.Run(gp, prev, true, out err);
                    Console.WriteLine(ok ? "[SUCCESS] Rolled back to v" + prev : "[ERROR] " + err);
                    return ok ? 0 : 1;
                }
                else if (cmd == "adopt")
                {
                    bool ok = InstallManifest.Adopt(gp, CmoSettings.RegistryInstalledVersion());
                    InstallManifest m = InstallManifest.Read(gp);
                    Console.WriteLine(ok
                        ? "[SUCCESS] Adopted v" + (m == null ? "?" : m.Version) + " from " + gp
                        : "[ERROR] 未找到完整引擎文件，无法登记安装状态");
                    return ok ? 0 : 1;
                }
                else if (cmd == "list")
                {
                    InstallManifest m = InstallManifest.Read(gp);
                    Console.WriteLine("game=" + gp);
                    Console.WriteLine("installed=" + (m == null ? "none" : "v" + m.Version + " (" + m.Source + ")"));
                    Console.WriteLine("cached=" + string.Join(",", VersionStore.CachedVersions().ToArray()));
                    return 0;
                }

                if (purgeSettings)
                {
                    CmoSettings.Purge();
                    Console.WriteLine("[SUCCESS] 已清除全部更新设置");
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm(gp));
            return 0;
        }
    }
}
