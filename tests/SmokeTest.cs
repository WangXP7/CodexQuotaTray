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

        private static void RenderIconPreview(string outputPath)
        {
            int?[] values = new int?[] { 88, 42, 9, null };
            string[] labels = new string[] { "88", "42", "9", "?" };
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
                    using (Icon icon = IconRenderer.Create(values[index], false))
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
                    "上：任务栏实际尺寸    下：像素放大预览",
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
