using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OneBox.App;

namespace OneBox.Tests;

[TestClass]
public sealed class WpfHtmlReaderTests
{
    [TestMethod]
    public void WpfReaderLoadsStyledHtmlWithoutShowingWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var source = new HwndSource(new HwndSourceParameters("OneBox阅读测试")
            {
                Width = 800, Height = 600, WindowStyle = unchecked((int)0x80000000)
            });
            using var view = new WebView2 { Visibility = Visibility.Hidden };
            var grid = new Grid();
            grid.Children.Add(view);
            source.RootVisual = grid;
            grid.Measure(new Size(800, 600));
            grid.Arrange(new Rect(0, 0, 800, 600));
            grid.UpdateLayout();
            async Task CheckAsync()
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
                    Path.Combine(Path.GetTempPath(), $"OneBox-wpf-test-{Guid.NewGuid():N}"));
                await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(5));
                view.CoreWebView2.Settings.IsScriptEnabled = false;
                var links = new MailLinkNavigation(_ => Assert.Fail("阅读正文不应打开外部应用"));
                view.CoreWebView2.NavigationStarting += links.OnNavigationStarting;
                view.CoreWebView2.NewWindowRequested += links.OnNewWindowRequested;
                var renderer = new HtmlMessageRenderer(view.CoreWebView2);
                Assert.IsTrue(await renderer.ShowAsync("""
                    <html><head><style>.card{background:rgb(0,85,221);padding:24px}</style></head>
                    <body style="background:rgb(240,240,240)"><div class="card">带样式的邮件</div></body></html>
                    """, false).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual("\"rgb(0, 85, 221)\"", await view.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.card')).backgroundColor"));
                Assert.AreEqual("\"rgb(240, 240, 240)\"", await view.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
                await view.CoreWebView2.ExecuteScriptAsync("""
                    const range = document.createRange();
                    range.selectNodeContents(document.querySelector('.card'));
                    window.getSelection().removeAllRanges();
                    window.getSelection().addRange(range);
                    """);
                Assert.AreEqual("带样式的邮件", await renderer.ReadSelectedTextAsync());
                Assert.IsFalse(view.CoreWebView2.Settings.IsScriptEnabled, "选中自动复制不应启用邮件脚本");
                Assert.AreEqual(Visibility.Hidden, view.Visibility);
            }
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await CheckAsync(); }
                catch (Exception error) { failure = error; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "WPF正文检查超时");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
