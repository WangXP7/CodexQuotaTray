using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuotaTray.Tests
{
    internal static class SmokeTest
    {
        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string previewPath = args.Length > 0
                    ? Path.GetFullPath(args[0])
                    : Path.GetFullPath("popup-preview.png");
                string iconPreviewPath = Path.Combine(
                    Path.GetDirectoryName(previewPath) ?? String.Empty,
                    "icon-preview.png");

                TestAppServerPayload();
                TestLiveAppServer();
                TestIconHandles();
                TestSquareIconShape();
                TestTwoDigitReadability();
                TestDisconnectedIconColors();
                TestTaskMonitor();

                QuotaSnapshot snapshot = SessionLogQuotaProvider.ReadLatest();
                if (snapshot == null)
                {
                    snapshot = MakeSampleSnapshot();
                    Console.WriteLine("SESSION_CACHE=not_available (sample used for preview)");
                }
                else
                {
                    Console.WriteLine("SESSION_CACHE=ok");
                    Console.WriteLine("SESSION_REMAINING=" +
                        (snapshot.DisplayRemainingPercent.HasValue
                            ? snapshot.DisplayRemainingPercent.Value.ToString()
                            : "unknown"));
                    Console.WriteLine("SESSION_SOURCE=" + snapshot.SourceName);
                }

                RenderPopup(snapshot, previewPath);
                RenderIconPreview(iconPreviewPath);
                Console.WriteLine("PREVIEW=" + previewPath);
                Console.WriteLine("ICON_PREVIEW=" + iconPreviewPath);
                Console.WriteLine("SMOKE_TEST=passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                Console.Error.WriteLine("SMOKE_TEST=failed");
                return 1;
            }
        }

        private static void TestAppServerPayload()
        {
            const string json =
                "{\"rateLimits\":{\"limitId\":\"codex\",\"planType\":\"plus\"," +
                "\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1784549620}," +
                "\"secondary\":{\"usedPercent\":40,\"windowDurationMins\":10080,\"resetsAt\":1784549620}," +
                "\"credits\":{\"hasCredits\":false,\"unlimited\":false,\"balance\":null}}," +
                "\"rateLimitsByLimitId\":{" +
                "\"codex\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":5,\"windowDurationMins\":300}}," +
                "\"codex_other\":{\"limitId\":\"codex_other\",\"limitName\":\"Fast\"," +
                "\"primary\":{\"usedPercent\":80,\"windowDurationMins\":60}}}," +
                "\"rateLimitResetCredits\":{\"availableCount\":1}}";

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            IDictionary<string, object> result = serializer.DeserializeObject(json) as IDictionary<string, object>;
            QuotaSnapshot snapshot = QuotaJsonParser.ParseAppServerResult(result, DateTime.UtcNow);
            if (snapshot == null || snapshot.DisplayRemainingPercent != 60 ||
                snapshot.Primary == null || snapshot.Primary.WindowMinutes != 300 ||
                snapshot.ResetCreditCount != 1 || snapshot.AdditionalBuckets == null ||
                snapshot.AdditionalBuckets.Count != 1 ||
                snapshot.AdditionalBuckets[0].DisplayRemainingPercent != 20)
            {
                throw new InvalidOperationException("App Server payload parsing produced an unexpected result.");
            }

            Console.WriteLine("APP_SERVER_PARSER=ok");
        }

        private static void TestIconHandles()
        {
            Process process = Process.GetCurrentProcess();
            using (Icon warmup = IconRenderer.Create(50, false))
            {
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            int before = GetGuiResources(process.Handle, 0);
            for (int index = 0; index < 300; index++)
            {
                using (Icon icon = IconRenderer.Create(index % 101, false))
                {
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            int after = GetGuiResources(process.Handle, 0);
            if (after - before > 4)
            {
                throw new InvalidOperationException("Dynamic icon rendering leaked GDI handles: " + before + " -> " + after);
            }

            Console.WriteLine("ICON_GDI_HANDLES=" + before.ToString() + "->" + after.ToString());
        }

        private static void TestLiveAppServer()
        {
            QuotaSnapshot received = null;
            string lastStatus = null;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            using (CodexRateLimitClient client = new CodexRateLimitClient())
            {
                client.SnapshotReceived += delegate(QuotaSnapshot snapshot)
                {
                    received = snapshot;
                    completed.Set();
                };
                client.StatusChanged += delegate(string status) { lastStatus = status; };
                client.Start();
                completed.WaitOne(20000);
            }

            if (received == null)
            {
                Console.WriteLine("LIVE_APP_SERVER=unavailable (" + (lastStatus ?? "no status") + ")");
                return;
            }

            Console.WriteLine("LIVE_APP_SERVER=ok");
            Console.WriteLine("LIVE_REMAINING=" +
                (received.DisplayRemainingPercent.HasValue
                    ? received.DisplayRemainingPercent.Value.ToString()
                    : (received.IsUnlimited ? "unlimited" : "unknown")));
        }

        private static void TestSquareIconShape()
        {
            using (Icon icon = IconRenderer.Create(88, false))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                if (bitmap.Width != 16 || bitmap.Height != 16 ||
                    bitmap.GetPixel(3, 3).A < 200 ||
                    bitmap.GetPixel(8, 8).A < 240 ||
                    bitmap.GetPixel(0, 0).A > 20)
                {
                    throw new InvalidOperationException("The tray icon is not the expected rounded-square shape.");
                }
            }

            Console.WriteLine("ICON_SHAPE=rounded-square");
        }

        private static void TestTwoDigitReadability()
        {
            int[] values = new int[] { 11, 42, 88, 99 };
            foreach (int value in values)
            {
                int minX = 16;
                int minY = 16;
                int maxX = -1;
                int maxY = -1;
                using (Icon icon = IconRenderer.Create(value, false))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            if (pixel.A > 100 && pixel.R > 205 && pixel.G > 205 && pixel.B > 205)
                            {
                                minX = Math.Min(minX, x);
                                minY = Math.Min(minY, y);
                                maxX = Math.Max(maxX, x);
                                maxY = Math.Max(maxY, y);
                            }
                        }
                    }
                }

                Console.WriteLine(
                    "ICON_TEXT_BOUNDS=" + value.ToString() + ":" +
                    minX.ToString() + "," + minY.ToString() + "-" +
                    maxX.ToString() + "," + maxY.ToString());

                if (minX < 1 || maxX > 14 || maxX - minX + 1 < 10 || maxY - minY + 1 < 8)
                {
                    throw new InvalidOperationException(
                        "Two-digit icon text is clipped or too small for " + value.ToString() +
                        ": bounds=" + minX.ToString() + "," + minY.ToString() + "-" +
                        maxX.ToString() + "," + maxY.ToString());
                }
            }

            Console.WriteLine("ICON_TWO_DIGIT_READABILITY=ok");
        }

        private static void TestDisconnectedIconColors()
        {
            int[] values = new int[] { 10, 30, 70 };
            foreach (int value in values)
            {
                bool hasBlackText = false;
                using (Icon icon = IconRenderer.Create(value, false, true))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    Color expected = IconRenderer.GetColor(value, false);
                    Color background = bitmap.GetPixel(8, 2);
                    if (Math.Abs(background.R - expected.R) > 5 ||
                        Math.Abs(background.G - expected.G) > 5 ||
                        Math.Abs(background.B - expected.B) > 5)
                    {
                        throw new InvalidOperationException("Disconnected icon changed its quota background color.");
                    }

                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            if (pixel.A > 150 && pixel.R < 50 && pixel.G < 50 && pixel.B < 50)
                            {
                                hasBlackText = true;
                            }
                        }
                    }
                }

                if (!hasBlackText)
                {
                    throw new InvalidOperationException("Disconnected icon did not render black digits.");
                }
            }

            Console.WriteLine("ICON_DISCONNECTED_COLORS=ok");
        }

        private static void TestTaskMonitor()
        {
            string originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            string testHome = Path.Combine(
                Path.GetTempPath(),
                "CodexQuotaTray-" + Guid.NewGuid().ToString("N"));
            string sessionDirectory = Path.Combine(testHome, "sessions", "2026", "07", "30");
            Directory.CreateDirectory(sessionDirectory);
            long startedAt = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();

            try
            {
                for (int index = 1; index <= 2; index++)
                {
                    string turnId = "parallel-" + index.ToString();
                    string timestamp = DateTime.UtcNow.ToString("o");
                    string log =
                        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"" +
                        turnId + "\",\"started_at\":" + startedAt.ToString() + "}}\n" +
                        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"并行任务 " +
                        index.ToString() + "\"}}\n" +
                        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"phase\":\"commentary\",\"message\":\"正在执行步骤 " +
                        index.ToString() + "\"}}\n";
                    File.WriteAllText(
                        Path.Combine(sessionDirectory, "rollout-" + index.ToString() + ".jsonl"),
                        log,
                        new System.Text.UTF8Encoding(false));
                }

                Environment.SetEnvironmentVariable("CODEX_HOME", testHome);
                IList<CodexTaskInfo> observed = null;
                using (ManualResetEvent completed = new ManualResetEvent(false))
                using (CodexTaskMonitor monitor = new CodexTaskMonitor())
                {
                    monitor.TasksChanged += delegate(IList<CodexTaskInfo> tasks)
                    {
                        observed = tasks;
                        completed.Set();
                    };
                    monitor.Start();
                    completed.WaitOne(15000);
                }

                if (observed == null || observed.Count != 2)
                {
                    throw new InvalidOperationException("Task monitor did not return two parallel tasks.");
                }

                foreach (CodexTaskInfo task in observed)
                {
                    if (String.IsNullOrEmpty(task.Name) ||
                        task.EstimatedProgressPercent <= 0 ||
                        task.EstimatedProgressPercent > 100)
                    {
                        throw new InvalidOperationException("Task monitor produced invalid task data.");
                    }
                }

                Console.WriteLine("TASK_MONITOR_PARALLEL=" + observed.Count.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
                try { Directory.Delete(testHome, true); }
                catch { }
            }
        }

        private static void RenderIconPreview(string outputPath)
        {
            int?[] values = new int?[] { 11, 42, 88, 99 };
            string[] labels = new string[] { "11", "42", "88", "99" };
            using (Bitmap canvas = new Bitmap(480, 178))
            using (Graphics graphics = Graphics.FromImage(canvas))
            using (Font labelFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush labelBrush = new SolidBrush(Color.FromArgb(218, 224, 232)))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(29, 33, 39)))
            {
                graphics.FillRectangle(background, 0, 0, canvas.Width, canvas.Height);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                for (int index = 0; index < values.Length; index++)
                {
                    int left = 18 + index * 116;
                    using (Icon icon = IconRenderer.Create(values[index], false, true))
                    using (Bitmap iconBitmap = icon.ToBitmap())
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(iconBitmap, new Rectangle(left + 46, 18, 16, 16));
                        graphics.DrawString(labels[index] + " · 16 px", labelFont, labelBrush, left + 24, 41);

                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(iconBitmap, new Rectangle(left + 20, 69, 72, 72));
                    }
                }

                graphics.DrawString(
                    "断线状态：保留红/黄/绿底色，数字改为黑色",
                    labelFont,
                    labelBrush,
                    135,
                    151);
                canvas.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void RenderPopup(QuotaSnapshot snapshot, string outputPath)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string directory = Path.GetDirectoryName(outputPath);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (QuotaPopupForm form = new QuotaPopupForm())
            using (Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.UpdateSnapshot(snapshot);
                form.UpdateTasks(new List<CodexTaskInfo>
                {
                    new CodexTaskInfo
                    {
                        Id = "task-preview-1",
                        Name = "增加 Codex 并行任务状态区域",
                        Detail = "正在编译程序并检查任务卡片布局",
                        StartedAtUtc = DateTime.UtcNow.AddMinutes(-4).AddSeconds(-18),
                        EstimatedProgressPercent = 62,
                        EstimatedRemaining = TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(38))
                    },
                    new CodexTaskInfo
                    {
                        Id = "task-preview-2",
                        Name = "后台测试额度数据刷新",
                        Detail = "正在运行测试",
                        StartedAtUtc = DateTime.UtcNow.AddMinutes(-1).AddSeconds(-12),
                        EstimatedProgressPercent = 28,
                        EstimatedRemaining = TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(5))
                    }
                });
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10000, -10000);
                form.Show();
                Application.DoEvents();
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                form.Hide();
            }
        }

        private static QuotaSnapshot MakeSampleSnapshot()
        {
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.Primary = new QuotaWindowInfo
            {
                UsedPercent = 18,
                WindowMinutes = 300,
                ResetsAtUnix = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
            };
            snapshot.Secondary = new QuotaWindowInfo
            {
                UsedPercent = 34,
                WindowMinutes = 10080,
                ResetsAtUnix = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds()
            };
            snapshot.PlanType = "plus";
            snapshot.FetchedAtUtc = DateTime.UtcNow;
            snapshot.ObservedAtUtc = DateTime.UtcNow;
            snapshot.SourceName = "测试数据";
            return snapshot;
        }
    }
}
