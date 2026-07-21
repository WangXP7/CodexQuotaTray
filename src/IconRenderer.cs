using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexQuotaTray
{
    internal static class IconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(int? remainingPercent, bool unlimited)
        {
            const int size = 16;
            using (Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                graphics.Clear(Color.Transparent);

                Color fill = GetColor(remainingPercent, unlimited);
                using (SolidBrush background = new SolidBrush(fill))
                using (GraphicsPath backgroundPath = CreateRoundedRectangle(
                    new RectangleF(0f, 0f, 16f, 16f), 2.2f))
                {
                    graphics.FillPath(background, backgroundPath);
                }

                string text;
                string fontName = "Bahnschrift SemiCondensed";
                if (unlimited)
                {
                    text = "\u221E";
                    fontName = "Segoe UI Symbol";
                }
                else if (!remainingPercent.HasValue)
                {
                    text = "?";
                }
                else if (remainingPercent.Value >= 100)
                {
                    text = "\u2713";
                    fontName = "Segoe UI Symbol";
                }
                else
                {
                    text = remainingPercent.Value.ToString();
                }

                float maximumWidth = text.Length >= 2 ? 14.5f : 13.5f;
                float maximumHeight = 14.5f;
                float fontSize = FindLargestFontSize(
                    graphics, text, fontName, maximumWidth, maximumHeight);

                using (Font font = new Font(fontName, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush foreground = new SolidBrush(Color.White))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags = StringFormatFlags.NoWrap;
                    graphics.DrawString(text, font, foreground, new RectangleF(0.5f, -0.35f, 15f, 16.35f), format);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static float FindLargestFontSize(
            Graphics graphics,
            string text,
            string fontName,
            float maximumWidth,
            float maximumHeight)
        {
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                for (float size = 15f; size >= 8f; size -= 0.25f)
                {
                    using (Font font = new Font(fontName, size, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        SizeF measured = graphics.MeasureString(text, font, Int32.MaxValue, format);
                        if (measured.Width <= maximumWidth && measured.Height <= maximumHeight)
                        {
                            return size;
                        }
                    }
                }
            }

            return 8f;
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color GetColor(int? remainingPercent, bool unlimited)
        {
            if (unlimited)
            {
                return Color.FromArgb(8, 127, 91);
            }

            if (!remainingPercent.HasValue)
            {
                return Color.FromArgb(75, 85, 99);
            }

            int value = remainingPercent.Value;
            if (value < 20)
            {
                return Color.FromArgb(180, 35, 24);
            }

            if (value < 50)
            {
                return Color.FromArgb(154, 90, 0);
            }

            return Color.FromArgb(8, 127, 91);
        }
    }
}
