using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal sealed class QuotaPopupForm : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

        private readonly Label _title;
        private readonly Label _plan;
        private readonly Label _percent;
        private readonly Label _caption;
        private readonly Label _primaryTitle;
        private readonly Label _primaryValue;
        private readonly Label _secondaryTitle;
        private readonly Label _secondaryValue;
        private readonly Label _extraTitle;
        private readonly Label _extraValue;
        private readonly Label _footer;
        private readonly Label _tasksTitle;
        private readonly Label _tasksCount;
        private readonly FlowLayoutPanel _tasksPanel;
        private QuotaSnapshot _snapshot;
        private DateTime _ignoreDeactivateUntilUtc;

        public bool Pinned { get; private set; }

        public QuotaPopupForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(27, 30, 35);
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Codex 额度";
            ClientSize = new Size(366, 520);
            MinimumSize = ClientSize;
            MaximumSize = ClientSize;
            KeyPreview = true;
            DoubleBuffered = true;

            _title = MakeLabel("Codex 额度", 18, 14, 180, 24, 12f, FontStyle.Bold, Color.White);
            _plan = MakeLabel("", 228, 17, 120, 21, 9f, FontStyle.Regular, Color.FromArgb(148, 159, 173));
            _plan.TextAlign = ContentAlignment.MiddleRight;
            _percent = MakeLabel("--", 17, 42, 128, 62, 35f, FontStyle.Bold, Color.FromArgb(105, 113, 125));
            _caption = MakeLabel("正在读取额度…", 146, 56, 202, 42, 10f, FontStyle.Regular, Color.FromArgb(194, 201, 211));

            _primaryTitle = MakeLabel("主要窗口", 18, 120, 84, 24, 9f, FontStyle.Regular, Color.FromArgb(139, 150, 165));
            _primaryValue = MakeLabel("—", 106, 120, 242, 24, 10f, FontStyle.Regular, Color.FromArgb(232, 235, 240));
            _secondaryTitle = MakeLabel("其它窗口", 18, 151, 84, 24, 9f, FontStyle.Regular, Color.FromArgb(139, 150, 165));
            _secondaryValue = MakeLabel("—", 106, 151, 242, 24, 10f, FontStyle.Regular, Color.FromArgb(232, 235, 240));
            _extraTitle = MakeLabel("Credits", 18, 182, 84, 24, 9f, FontStyle.Regular, Color.FromArgb(139, 150, 165));
            _extraValue = MakeLabel("—", 106, 182, 242, 24, 10f, FontStyle.Regular, Color.FromArgb(232, 235, 240));
            _extraValue.AutoEllipsis = true;
            _footer = MakeLabel("等待数据", 18, 220, 330, 20, 8.5f, FontStyle.Regular, Color.FromArgb(120, 132, 148));
            _tasksTitle = MakeLabel("正在执行的任务", 18, 258, 190, 22, 10f, FontStyle.Bold, Color.White);
            _tasksCount = MakeLabel("0 个", 224, 258, 124, 22, 8.5f, FontStyle.Regular, Color.FromArgb(139, 150, 165));
            _tasksCount.TextAlign = ContentAlignment.MiddleRight;

            _tasksPanel = new BufferedFlowLayoutPanel();
            _tasksPanel.Location = new Point(14, 286);
            _tasksPanel.Size = new Size(338, 216);
            _tasksPanel.AutoScroll = false;
            _tasksPanel.WrapContents = false;
            _tasksPanel.FlowDirection = FlowDirection.TopDown;
            _tasksPanel.BackColor = Color.FromArgb(31, 35, 41);
            _tasksPanel.Padding = new Padding(4);

            Controls.AddRange(new Control[]
            {
                _title, _plan, _percent, _caption,
                _primaryTitle, _primaryValue,
                _secondaryTitle, _secondaryValue, _extraTitle, _extraValue,
                _footer, _tasksTitle, _tasksCount, _tasksPanel
            });
            UpdateTasks(new List<CodexTaskInfo>());

            _title.MouseDown += BeginDrag;
            MouseDown += BeginDrag;
            Deactivate += delegate
            {
                if (!Pinned && DateTime.UtcNow >= _ignoreDeactivateUntilUtc)
                {
                    Hide();
                }
            };
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Escape && !Pinned)
                {
                    Hide();
                }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CS_DROPSHADOW;
                return parameters;
            }
        }

        public void SetPinned(bool pinned)
        {
            Pinned = pinned;
            if (pinned && !Visible)
            {
                ShowNearTaskbar();
            }
        }

        public void UpdateSnapshot(QuotaSnapshot snapshot)
        {
            _snapshot = snapshot;
            if (snapshot == null)
            {
                return;
            }

            int? remaining = snapshot.DisplayRemainingPercent;
            if (snapshot.IsUnlimited)
            {
                _percent.Text = "∞";
                _caption.Text = "Credits 无上限";
            }
            else if (remaining.HasValue)
            {
                _percent.Text = remaining.Value.ToString() + "%";
                _caption.Text = "可用额度\r\n取各窗口中的较低值";
            }
            else
            {
                _percent.Text = "--";
                _caption.Text = "当前账户未返回百分比额度";
            }

            _percent.ForeColor = IconRenderer.GetColor(remaining, snapshot.IsUnlimited);
            _plan.Text = String.IsNullOrEmpty(snapshot.PlanType)
                ? String.Empty
                : QuotaFormatting.FormatPlan(snapshot.PlanType);

            SetWindowRow(_primaryTitle, _primaryValue, snapshot.Primary, "主要窗口");
            SetWindowRow(_secondaryTitle, _secondaryValue, snapshot.Secondary, "其它窗口");

            if (snapshot.AdditionalBuckets != null && snapshot.AdditionalBuckets.Count > 0)
            {
                _extraTitle.Text = "其它额度";
                _extraValue.Text = FormatAdditionalBuckets(snapshot);
            }
            else if (snapshot.IndividualLimit != null)
            {
                _extraTitle.Text = "个人限额";
                _extraValue.Text = "剩余 " + snapshot.IndividualLimit.RemainingPercent.ToString() + "%";
            }
            else if (snapshot.ResetCreditCount.HasValue && snapshot.ResetCreditCount.Value > 0)
            {
                _extraTitle.Text = "额度重置";
                _extraValue.Text = snapshot.ResetCreditCount.Value.ToString() + " 次可用";
            }
            else
            {
                _extraTitle.Text = "Credits";
                _extraValue.Text = snapshot.CreditsUnlimited
                    ? "无限"
                    : (String.IsNullOrEmpty(snapshot.CreditBalance) ? "—" : snapshot.CreditBalance);
            }

            string freshness = snapshot.IsOlderThan(TimeSpan.FromMinutes(10)) ? " · 数据较旧" : String.Empty;
            _footer.Text = snapshot.SourceName + " · " + snapshot.FetchedAtUtc.ToLocalTime().ToString("HH:mm:ss") + freshness;
            Invalidate();
        }

        public void UpdateTasks(IList<CodexTaskInfo> tasks)
        {
            _tasksPanel.SuspendLayout();
            while (_tasksPanel.Controls.Count > 0)
            {
                Control control = _tasksPanel.Controls[0];
                _tasksPanel.Controls.RemoveAt(0);
                control.Dispose();
            }

            int count = tasks == null ? 0 : tasks.Count;
            ResizeTaskArea(count);
            _tasksCount.Text = count <= 1
                ? count.ToString() + " 个"
                : count.ToString() + " 个并行";

            if (count == 0)
            {
                Label empty = MakeLabel(
                    "当前没有正在执行的任务",
                    0, 0, 310, _tasksPanel.Height - 8, 9f, FontStyle.Regular,
                    Color.FromArgb(120, 132, 148));
                empty.TextAlign = ContentAlignment.MiddleCenter;
                empty.Margin = new Padding(0);
                _tasksPanel.Controls.Add(empty);
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                foreach (CodexTaskInfo task in tasks)
                {
                    _tasksPanel.Controls.Add(CreateTaskRow(task, now));
                }
            }

            _tasksPanel.ResumeLayout();
        }

        private void ResizeTaskArea(int taskCount)
        {
            int oldRight = Right;
            int oldBottom = Bottom;
            int panelHeight = taskCount <= 0 ? 112 : 8 + (taskCount * 104);
            int targetHeight = 286 + panelHeight + 18;

            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            _tasksPanel.Height = panelHeight;
            ClientSize = new Size(366, targetHeight);
            MinimumSize = ClientSize;
            MaximumSize = ClientSize;

            if (Visible)
            {
                Screen screen = Screen.FromRectangle(new Rectangle(Location, Size));
                Rectangle area = screen.WorkingArea;
                int left = Math.Max(area.Left, Math.Min(oldRight - Width, area.Right - Width));
                int top = Math.Max(area.Top, Math.Min(oldBottom - Height, area.Bottom - Height));
                Location = new Point(left, top);
            }
        }

        public void ShowNearTaskbar()
        {
            _ignoreDeactivateUntilUtc = DateTime.UtcNow.AddSeconds(1);
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
            if (!Visible)
            {
                Show();
            }
            else
            {
                BringToFront();
            }

            Activate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(Color.FromArgb(70, 101, 113, 128)))
            using (Pen separator = new Pen(Color.FromArgb(45, 255, 255, 255)))
            {
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                args.Graphics.DrawLine(separator, 18, 110, Width - 18, 110);
                args.Graphics.DrawLine(separator, 18, 213, Width - 18, 213);
                args.Graphics.DrawLine(separator, 18, 249, Width - 18, 249);
            }
        }

        private static Control CreateTaskRow(CodexTaskInfo task, DateTime now)
        {
            Panel row = new Panel();
            row.Size = new Size(314, 96);
            row.Margin = new Padding(0, 0, 0, 8);
            row.BackColor = Color.FromArgb(39, 44, 51);

            Label name = MakeLabel(
                String.IsNullOrEmpty(task.Name) ? "Codex 任务" : task.Name,
                10, 5, 294, 21, 9.5f, FontStyle.Bold, Color.White);
            name.AutoEllipsis = true;

            Label detail = MakeLabel(
                String.IsNullOrEmpty(task.Detail) ? "正在执行" : task.Detail,
                10, 26, 294, 34, 8.5f, FontStyle.Regular,
                Color.FromArgb(180, 188, 199));
            detail.AutoEllipsis = true;

            Panel progressTrack = new Panel();
            progressTrack.Location = new Point(10, 65);
            progressTrack.Size = new Size(294, 5);
            progressTrack.BackColor = Color.FromArgb(67, 74, 86);

            Panel progressFill = new Panel();
            progressFill.Location = Point.Empty;
            progressFill.Size = new Size(
                Math.Max(2, (int)Math.Round(294 * Math.Max(0, Math.Min(100, task.EstimatedProgressPercent)) / 100d)),
                5);
            progressFill.BackColor = Color.FromArgb(31, 154, 112);
            progressTrack.Controls.Add(progressFill);

            TimeSpan elapsed = now > task.StartedAtUtc
                ? now - task.StartedAtUtc
                : TimeSpan.Zero;
            Label timing = MakeLabel(
                "已执行 " + FormatDuration(elapsed) +
                " · 估算 " + task.EstimatedProgressPercent.ToString() + "%" +
                " · 预计还需 " + FormatDuration(task.EstimatedRemaining),
                10, 73, 294, 18, 8f, FontStyle.Regular,
                Color.FromArgb(145, 158, 174));

            row.Controls.AddRange(new Control[] { name, detail, progressTrack, timing });
            return row;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return ((int)duration.TotalHours).ToString() + "时" +
                    duration.Minutes.ToString() + "分";
            }

            if (duration.TotalMinutes >= 1)
            {
                return ((int)duration.TotalMinutes).ToString() + "分" +
                    duration.Seconds.ToString() + "秒";
            }

            return Math.Max(0, duration.Seconds).ToString() + "秒";
        }

        private static Label MakeLabel(
            string text,
            int left,
            int top,
            int width,
            int height,
            float size,
            FontStyle style,
            Color color)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Text = text;
            label.Location = new Point(left, top);
            label.Size = new Size(width, height);
            label.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static void SetWindowRow(Label title, Label value, QuotaWindowInfo window, string fallbackTitle)
        {
            if (window == null)
            {
                title.Text = fallbackTitle;
                value.Text = "—";
                return;
            }

            title.Text = QuotaFormatting.FormatWindow(window.WindowMinutes);
            value.Text = "剩余 " + window.RemainingPercent.ToString() + "%";
            if (window.ResetLocalTime.HasValue)
            {
                value.Text += " · " + QuotaFormatting.FormatReset(window.ResetLocalTime);
            }
        }

        private static string FormatAdditionalBuckets(QuotaSnapshot snapshot)
        {
            string text = String.Empty;
            int count = Math.Min(2, snapshot.AdditionalBuckets.Count);
            for (int index = 0; index < count; index++)
            {
                QuotaBucketInfo bucket = snapshot.AdditionalBuckets[index];
                if (index > 0) text += " / ";
                text += bucket.DisplayName + " ";
                if (bucket.IsUnlimited)
                {
                    text += "∞";
                }
                else if (bucket.DisplayRemainingPercent.HasValue)
                {
                    text += bucket.DisplayRemainingPercent.Value.ToString() + "%";
                }
                else
                {
                    text += "—";
                }
            }

            if (snapshot.AdditionalBuckets.Count > count)
            {
                text += " …";
            }

            return text;
        }

        private void BeginDrag(object sender, MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }

        private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
        {
            public BufferedFlowLayoutPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }
    }
}
