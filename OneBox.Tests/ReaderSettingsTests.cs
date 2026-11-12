using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class ReaderSettingsTests
{
    [TestMethod]
    public void FontScaleDefaultsForExistingSettingsAndSurvivesOtherSettingChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "reader-settings.json");
        try
        {
            Assert.AreEqual(100, ReaderSettings.Load(path).FontScalePercent);
            Assert.IsTrue(ReaderSettings.Load(path).AutoCopySelectedText);
            File.WriteAllText(path, "{\"AutoLoadRemoteImages\":false,\"DownloadDirectory\":\"\"}");
            var settings = ReaderSettings.Load(path);
            Assert.IsTrue(settings.AutoCopySelectedText, "旧设置默认开启选中自动复制");
            settings = settings with { AutoCopySelectedText = false };
            Assert.AreEqual(100, settings.FontScalePercent);
            foreach (var percent in new[] { 85, 100, 115, 130, 150 })
            {
                settings = settings with { FontScalePercent = percent };
                settings.Save(path);
                Assert.AreEqual(percent, ReaderSettings.Load(path).FontScalePercent);
                Assert.IsFalse(ReaderSettings.Load(path).AutoLoadRemoteImages);
                Assert.IsFalse(ReaderSettings.Load(path).AutoCopySelectedText);
            }
            settings = settings with { AutoLoadRemoteImages = true, DownloadDirectory = directory };
            settings.Save(path);
            Assert.AreEqual(150, ReaderSettings.Load(path).FontScalePercent);
            Assert.IsTrue(ReaderSettings.Load(path).AutoLoadRemoteImages);
            Assert.AreEqual(directory, ReaderSettings.Load(path).DownloadDirectory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void RemoteImagesAreEnabledByDefaultAndChoiceSurvivesReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "reader-settings.json");
        try
        {
            Assert.IsTrue(ReaderSettings.Load(path).AutoLoadRemoteImages);
            Assert.AreEqual("", ReaderSettings.Load(path).DownloadDirectory);
            var downloadDirectory = Path.Combine(directory, "附件下载");
            new ReaderSettings { AutoLoadRemoteImages = false, DownloadDirectory = downloadDirectory }.Save(path);
            Assert.IsFalse(ReaderSettings.Load(path).AutoLoadRemoteImages);
            Assert.AreEqual(downloadDirectory, ReaderSettings.Load(path).DownloadDirectory);
            new ReaderSettings { AutoLoadRemoteImages = true, DownloadDirectory = downloadDirectory }.Save(path);
            Assert.IsTrue(ReaderSettings.Load(path).AutoLoadRemoteImages);
            Assert.AreEqual(downloadDirectory, ReaderSettings.Load(path).DownloadDirectory);
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
