using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;

namespace OneBox.Tests;

[TestClass]
public sealed class TrayIconBadgeTests
{
    [TestMethod]
    public void BadgeRendersRedCountAndCapsAt99PlusWithoutShowingTrayIcon()
    {
        foreach (var count in new[] { 1, 42, 99, 100 })
        {
            using var icon = TrayIconBadge.Create(SystemIcons.Information, count);
            using var bitmap = icon.ToBitmap();
            Assert.AreEqual(32, bitmap.Width);
            Assert.AreEqual(32, bitmap.Height);
            var redPixels = 0;
            for (var x = 0; x < bitmap.Width; x++)
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R > 170 && pixel.G < 100 && pixel.B < 120) redPixels++;
                }
            Assert.IsTrue(redPixels > 100, "有未读邮件时应绘制可见的红色角标");
        }
        using var overflowIcon = TrayIconBadge.Create(SystemIcons.Information, 100);
        using var largerIcon = TrayIconBadge.Create(SystemIcons.Information, 999);
        using var overflowBitmap = overflowIcon.ToBitmap();
        using var largerBitmap = largerIcon.ToBitmap();
        AssertSamePixels(overflowBitmap, largerBitmap);
    }

    [TestMethod]
    public void ZeroUnreadRestoresOriginalIcon()
    {
        using var originalBitmap = SystemIcons.Information.ToBitmap();
        using var icon = TrayIconBadge.Create(SystemIcons.Information, 0);
        using var bitmap = icon.ToBitmap();
        AssertSamePixels(originalBitmap, bitmap);
    }

    private static void AssertSamePixels(Bitmap expected, Bitmap actual)
    {
        Assert.AreEqual(expected.Size, actual.Size);
        for (var x = 0; x < expected.Width; x++)
            for (var y = 0; y < expected.Height; y++)
                Assert.AreEqual(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }
}
