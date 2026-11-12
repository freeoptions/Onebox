using MailKit.Net.Proxy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class ProxyTests
{
    [TestMethod]
    public void ExistingSettingsDefaultToSystemProxyAndChangesSurviveReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "reader-settings.json");
        try
        {
            File.WriteAllText(path, "{\"AutoLoadRemoteImages\":false,\"FontScalePercent\":130}");
            var settings = ReaderSettings.Load(path);
            Assert.AreEqual(ProxyMode.System, settings.Proxy.Mode);
            settings = settings with { Proxy = new ProxySettings { Mode = ProxyMode.Custom, Address = "socks5://127.0.0.1:1080" } };
            settings.Save(path);
            var reloaded = ReaderSettings.Load(path);
            Assert.AreEqual(settings.Proxy, reloaded.Proxy);
            Assert.IsFalse(reloaded.AutoLoadRemoteImages);
            Assert.AreEqual(130, reloaded.FontScalePercent);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ProxySwitchesForSubsequentMailConnectionsWithoutRestart()
    {
        var proxy = new MailProxy();
        var destination = new Uri("https://imap.gmail.com:993");
        var expected = HttpClient.DefaultProxy.IsBypassed(destination) ? null : HttpClient.DefaultProxy.GetProxy(destination);
        Assert.AreEqual(expected, proxy.GetProxy(destination));
        proxy.Settings = new ProxySettings { Mode = ProxyMode.Custom, Address = "http://127.0.0.1:7890" };
        Assert.IsInstanceOfType<HttpProxyClient>(proxy.CreateClient("imap.gmail.com", 993));
        Assert.AreEqual(new Uri("http://127.0.0.1:7890"), proxy.GetProxy(destination));
        proxy.Settings = proxy.Settings with { Address = "socks5://127.0.0.1:1080" };
        Assert.IsInstanceOfType<Socks5Client>(proxy.CreateClient("smtp.gmail.com", 465));
        proxy.Settings = proxy.Settings with { Address = "https://127.0.0.1:8443" };
        Assert.IsInstanceOfType<HttpsProxyClient>(proxy.CreateClient("smtp.gmail.com", 465));
        proxy.Settings = proxy.Settings with { Mode = ProxyMode.Direct };
        Assert.IsTrue(proxy.IsBypassed(destination));
        Assert.IsNull(proxy.CreateClient("imap.gmail.com", 993));
    }

    [TestMethod]
    [DataRow("127.0.0.1:7890")]
    [DataRow("ftp://127.0.0.1:7890")]
    [DataRow("http://127.0.0.1:0")]
    [DataRow("http://127.0.0.1:70000")]
    [DataRow("http://example.test/path")]
    [DataRow("http://example.test?token=example")]
    [DataRow("http://user:example@127.0.0.1:7890")]
    public void InvalidProxyOrEmbeddedCredentialsAreRejectedWithoutEchoingInput(string address)
    {
        var error = Assert.Throws<ArgumentException>(() => new ProxySettings { Address = address }.ParseAddress());
        Assert.IsFalse(error.Message.Contains(address, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CanceledConnectionValidationDoesNotReportCancellationAsInvalidPassword()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new ImapSmtpProvider(ProviderKind.GmailImap,
            new MailProxy { Settings = new ProxySettings { Mode = ProxyMode.Direct } });
        var account = new Account(Guid.NewGuid(), "example@gmail.com", "测试", ProviderKind.GmailImap);
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.TestConnectionAsync(account, "unused", cancellation.Token));
    }
}
