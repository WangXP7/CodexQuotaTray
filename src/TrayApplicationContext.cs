using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Control _dispatcher;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _pinItem;
        private readonly ToolStripMenuItem _refreshItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly QuotaPopupForm _popup;
        private readonly QuotaMonitor _monitor;
        private readonly Timer _freshnessTimer;
        private Icon _currentIcon;
        private QuotaSnapshot _lastSnapshot;
        private string _connectionStatus = "正在启动…";
        private string _lastAlertKey;
        private DateTime _lastLowQuotaAlertAtUtc = DateTime.MinValue;
        private bool _closing;

        public TrayApplicationContext(bool forcePopup)
        {
            _dispatcher = new Control();
            IntPtr dispatcherHandle = _dispatcher.Handle;

            _popup = new QuotaPopupForm();
            _popup.SetPinned(UserSettings.PopupPinned);

            _statusItem = new ToolStripMenuItem("正在读取 Codex 额度…");
            _statusItem.Enabled = false;

            _showItem = new ToolStripMenuItem("显示额度卡片");
            _showItem.Click += delegate { TogglePopup(); };

            _pinItem = new ToolStripMenuItem("固定悬浮窗");
            _pinItem.CheckOnClick = true;
            _pinItem.Checked = UserSettings.PopupPinned;
            _pinItem.Click += delegate
            {
                bool pinned = _pinItem.Checked;
                UserSettings.PopupPinned = pinned;
                _popup.SetPinned(pinned);
                if (pinned)
                {
                    _popup.ShowNearTaskbar();
                }
            };

            _refreshItem = new ToolStripMenuItem("立即刷新");
            _refreshItem.Click += delegate
            {
                _refreshItem.Text = "正在刷新…";
                _monitor.RefreshNow();
                Timer resetTextTimer = new Timer();
                resetTextTimer.Interval = 1800;
                resetTextTimer.Tick += delegate
                {
                    resetTextTimer.Stop();
                    resetTextTimer.Dispose();
                    _refreshItem.Text = "立即刷新";
                };
                resetTextTimer.Start();
            };

            ToolStripMenuItem copyItem = new ToolStripMenuItem("复制额度摘要");
            copyItem.Click += delegate { CopySummary(); };

            _autoStartItem = new ToolStripMenuItem("随 Windows 启动");
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = UserSettings.IsAutoStartEnabled();
            _autoStartItem.Click += delegate { SetAutoStart(_autoStartItem.Checked); };

            ToolStripMenuItem openDataItem = new ToolStripMenuItem("打开 Codex 数据目录");
            openDataItem.Click += delegate { OpenCodexDataDirectory(); };

            ToolStripMenuItem docsItem = new ToolStripMenuItem("数据来源说明");
            docsItem.Click += delegate { ShowDataSourceInfo(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitThread(); };

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_showItem);
            _menu.Items.Add(_pinItem);
            _menu.Items.Add(_refreshItem);
            _menu.Items.Add(copyItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(openDataItem);
            _menu.Items.Add(docsItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _currentIcon = IconRenderer.Create(null, false);
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _currentIcon;
            _notifyIcon.Text = "Codex 额度：正在读取";
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;
            _notifyIcon.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                {
                    TogglePopup();
                }
            };

            _monitor = new QuotaMonitor();
            _monitor.SnapshotChanged += OnSnapshotChanged;
            _monitor.StatusChanged += OnStatusChanged;
            _monitor.Start();

            _freshnessTimer = new Timer();
            _freshnessTimer.Interval = 60000;
            _freshnessTimer.Tick += delegate { RefreshFreshnessPresentation(); };
            _freshnessTimer.Start();

            if (forcePopup || UserSettings.PopupPinned)
            {
                _popup.ShowNearTaskbar();
            }
        }

        private void OnSnapshotChanged(QuotaSnapshot snapshot)
        {
            RunOnUi(delegate { ApplySnapshot(snapshot); });
        }

        private void OnStatusChanged(string status)
        {
            RunOnUi(delegate
            {
                _connectionStatus = status;
                UpdateStatusItem();
                if (_lastSnapshot != null)
                {
                    _notifyIcon.Text = BuildTooltip(_lastSnapshot);
                }
            });
        }

        private void ApplySnapshot(QuotaSnapshot snapshot)
        {
            if (_closing || snapshot == null)
            {
                return;
            }

            _lastSnapshot = snapshot;
            _popup.UpdateSnapshot(snapshot);

            UpdateIcon(snapshot);

            _notifyIcon.Text = BuildTooltip(snapshot);
            UpdateStatusItem();
            ShowLowQuotaAlert(snapshot);
        }

        private void UpdateStatusItem()
        {
            if (_lastSnapshot == null)
            {
                _statusItem.Text = Truncate(_connectionStatus, 52);
                return;
            }

            string value;
            if (_lastSnapshot.IsUnlimited)
            {
                value = "无限额度";
            }
            else if (_lastSnapshot.DisplayRemainingPercent.HasValue)
            {
                value = "剩余 " + _lastSnapshot.DisplayRemainingPercent.Value.ToString() + "%";
            }
            else
            {
                value = "额度未知";
            }

            string source;
            if (_lastSnapshot.IsOlderThan(TimeSpan.FromMinutes(10)))
            {
                source = "数据较旧";
            }
            else if (_lastSnapshot.IsFallback)
            {
                source = "本地缓存";
            }
            else if (RealtimeIsUnavailable())
            {
                source = "最近值";
            }
            else
            {
                source = "实时";
            }
            _statusItem.Text = value + " · " + source;
        }

        private string BuildTooltip(QuotaSnapshot snapshot)
        {
            StringBuilder text = new StringBuilder("Codex ");
            if (snapshot.IsUnlimited)
            {
                text.Append("无限额度");
            }
            else if (snapshot.DisplayRemainingPercent.HasValue)
            {
                text.Append("剩余 ");
                text.Append(snapshot.DisplayRemainingPercent.Value.ToString());
                text.Append("%");
            }
            else
            {
                text.Append("额度未知");
            }

            if (snapshot.Primary != null)
            {
                text.Append(" · ");
                text.Append(QuotaFormatting.FormatWindow(snapshot.Primary.WindowMinutes).Replace(" ", ""));
                text.Append(" ");
                text.Append(snapshot.Primary.RemainingPercent.ToString());
                text.Append("%");
            }

            if (snapshot.Secondary != null)
            {
                text.Append(" / ");
                text.Append(QuotaFormatting.FormatWindow(snapshot.Secondary.WindowMinutes).Replace(" ", ""));
                text.Append(" ");
                text.Append(snapshot.Secondary.RemainingPercent.ToString());
                text.Append("%");
            }

            if (snapshot.IsOlderThan(TimeSpan.FromMinutes(10)))
            {
                text.Append(" · 数据较旧");
            }
            else if (snapshot.IsFallback)
            {
                text.Append(" · 缓存");
            }
            else if (RealtimeIsUnavailable())
            {
                text.Append(" · 最近值");
            }

            return Truncate(text.ToString(), 63);
        }

        private void RefreshFreshnessPresentation()
        {
            if (_closing || _lastSnapshot == null)
            {
                return;
            }

            _popup.UpdateSnapshot(_lastSnapshot);
            UpdateIcon(_lastSnapshot);
            _notifyIcon.Text = BuildTooltip(_lastSnapshot);
            UpdateStatusItem();
        }

        private void UpdateIcon(QuotaSnapshot snapshot)
        {
            bool stale = snapshot.IsOlderThan(TimeSpan.FromMinutes(10));
            Icon nextIcon = IconRenderer.Create(
                stale ? (int?)null : snapshot.DisplayRemainingPercent,
                !stale && snapshot.IsUnlimited);
            Icon previous = _currentIcon;
            _currentIcon = nextIcon;
            _notifyIcon.Icon = nextIcon;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private bool RealtimeIsUnavailable()
        {
            return !String.IsNullOrEmpty(_connectionStatus) &&
                (_connectionStatus.IndexOf("不可用", StringComparison.Ordinal) >= 0 ||
                 _connectionStatus.IndexOf("失败", StringComparison.Ordinal) >= 0 ||
                 _connectionStatus.IndexOf("未找到", StringComparison.Ordinal) >= 0 ||
                 _connectionStatus.IndexOf("保留最近", StringComparison.Ordinal) >= 0);
        }

        private void ShowLowQuotaAlert(QuotaSnapshot snapshot)
        {
            int? remaining = snapshot.DisplayRemainingPercent;
            if (!remaining.HasValue || remaining.Value > 20 || snapshot.IsOlderThan(TimeSpan.FromMinutes(10)))
            {
                if (remaining.HasValue && remaining.Value > 20)
                {
                    _lastAlertKey = null;
                }

                return;
            }

            int threshold = remaining.Value <= 5 ? 5 : (remaining.Value <= 10 ? 10 : 20);
            string resetKey = (snapshot.Primary == null ? "" : snapshot.Primary.ResetsAtUnix.ToString()) + ":" +
                (snapshot.Secondary == null ? "" : snapshot.Secondary.ResetsAtUnix.ToString());
            string alertKey = resetKey + ":" + threshold.ToString();
            DateTime now = DateTime.UtcNow;
            if (String.Equals(alertKey, _lastAlertKey, StringComparison.Ordinal) ||
                now - _lastLowQuotaAlertAtUtc < TimeSpan.FromMinutes(10))
            {
                return;
            }

            _lastAlertKey = alertKey;
            _lastLowQuotaAlertAtUtc = now;
            _notifyIcon.BalloonTipTitle = "Codex 额度提醒";
            _notifyIcon.BalloonTipText = "剩余额度 " + remaining.Value.ToString() + "%";
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
            _notifyIcon.ShowBalloonTip(5000);
        }

        private void TogglePopup()
        {
            if (_popup.Visible && !_popup.Pinned)
            {
                _popup.Hide();
            }
            else
            {
                _popup.UpdateSnapshot(_lastSnapshot);
                _popup.ShowNearTaskbar();
            }
        }

        private void CopySummary()
        {
            if (_lastSnapshot == null)
            {
                _notifyIcon.BalloonTipTitle = "Codex 额度";
                _notifyIcon.BalloonTipText = "尚未读取到额度数据。";
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(2500);
                return;
            }

            try
            {
                Clipboard.SetText(_lastSnapshot.BuildSummary());
                _notifyIcon.BalloonTipTitle = "已复制";
                _notifyIcon.BalloonTipText = "额度摘要已复制到剪贴板。";
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(2000);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                UserSettings.SetAutoStart(enabled);
                _autoStartItem.Checked = UserSettings.IsAutoStartEnabled();
            }
            catch (Exception exception)
            {
                _autoStartItem.Checked = UserSettings.IsAutoStartEnabled();
                MessageBox.Show(exception.Message, "设置启动项失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenCodexDataDirectory()
        {
            string directory = CodexLocator.ResolveCodexHome();
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                MessageBox.Show("未找到 Codex 数据目录。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + directory + "\"");
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "打开目录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void ShowDataSourceInfo()
        {
            MessageBox.Show(
                "优先通过 Codex App Server 读取账户的 rateLimits；连接不可用时，只读取本机 Codex 会话中的额度缓存。\r\n\r\n" +
                "本工具不会读取、复制或保存 auth.json 中的登录令牌。托盘数字是 100 - usedPercent，并取多个额度窗口中的较低值。",
                "Codex 额度 · 数据来源",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void RunOnUi(MethodInvoker action)
        {
            if (_closing || _dispatcher.IsDisposed)
            {
                return;
            }

            try
            {
                if (_dispatcher.InvokeRequired)
                {
                    _dispatcher.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string Truncate(string value, int maximumLength)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value;
            }

            return value.Substring(0, maximumLength);
        }

        protected override void ExitThreadCore()
        {
            if (!_closing)
            {
                _closing = true;
                _freshnessTimer.Stop();
                _freshnessTimer.Dispose();
                _notifyIcon.Visible = false;
                _monitor.Dispose();
                _popup.Close();
                _notifyIcon.Dispose();
                _menu.Dispose();
                if (_currentIcon != null)
                {
                    _currentIcon.Dispose();
                    _currentIcon = null;
                }

                _dispatcher.Dispose();
            }

            base.ExitThreadCore();
        }
    }
}
