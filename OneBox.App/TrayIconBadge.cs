using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OneBox.App;

public static class TrayIconBadge
{
    public static Icon Create(Icon baseIcon, int unreadCount)
    {
        if (unreadCount <= 0) return (Icon)baseIcon.Clone();
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            var text = unreadCount > 99 ? "99+" : unreadCount.ToString(CultureInfo.InvariantCulture);
            var bounds = text.Length switch
            {
                1 => new RectangleF(10, 0, 22, 22),
                2 => new RectangleF(6, 0, 26, 24),
                _ => new RectangleF(0, 0, 32, 24)
            };
            using var badgeBrush = new SolidBrush(Color.FromArgb(224, 53, 65));
            graphics.FillEllipse(badgeBrush, bounds);
            using var font = new Font("Segoe UI", text.Length switch { 1 => 17, 2 => 14, _ => 11 }, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(text, font, Brushes.White, bounds, format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
