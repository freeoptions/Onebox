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
                const string html = """
                    <html><head><style>.card{background:rgb(0,85,221);padding:24px}td{border:1px solid red}</style></head>
                    <body style="background:rgb(240,240,240)"><div class="card"><h1>Invoice overdue</h1>
                    <table><tr><td><strong>Amount:</strong></td><td>$9.99 USD</td></tr></table>
                    <a href="https://example.invalid/invoice?id=2017256" style="color:red">Pay now</a>
                    <img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7" width="1" height="1">
                    </div><script>document.title='mail-script-ran'</script></body></html>
                    """;
                Assert.IsTrue(await renderer.ShowAsync(html, false));
                var originalHtml = await view.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
                var originalImage = await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('img').getAttribute('src')");
                var textList = await renderer.ReadTranslationTextAsync(html);
                CollectionAssert.AreEqual(new[] { "Invoice overdue", "Amount:", "$9.99 USD", "Pay now" }, textList.ToArray());
                Assert.IsTrue(await renderer.ShowAsync(html, false, translatedTextList:
                    new[] { "首次账单逾期通知", "应付金额：", "$9.99 USD", "付款<b>原样文字</b>" }));
                Assert.AreEqual("\"rgb(0, 85, 221)\"", await view.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.card')).backgroundColor"));
                Assert.AreEqual("\"24px\"", await view.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.card')).padding"));
                Assert.AreEqual("1", await view.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('table tr').length"));
                Assert.AreEqual("\"应付金额：\"", await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('strong').textContent"));
                Assert.AreEqual("\"https://example.invalid/invoice?id=2017256\"", await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('a').getAttribute('href')"));
                Assert.AreEqual(originalImage, await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('img').getAttribute('src')"));
                Assert.AreEqual("0", await view.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('a b').length"), "译文中的HTML必须作为文字显示");
                Assert.IsTrue(await renderer.ShowAsync(html, true, translatedTextList:
                    new[] { "首次账单逾期通知", "应付金额：", "$9.99 USD", "付款<b>原样文字</b>" }));
                Assert.AreEqual("\"应付金额：\"", await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('strong').textContent"), "切换图片加载策略时应保留译文");
                StringAssert.Contains(await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('meta[http-equiv]').content"), "https: http:");
                var imageWidth = "0";
                for (var attempt = 0; attempt < 20 && imageWidth == "0"; attempt++)
                {
                    await Task.Delay(50);
                    imageWidth = await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('img').naturalWidth");
                }
                Assert.AreEqual("1", imageWidth, "重载后的译文图片应可解码");
                Assert.IsTrue(await renderer.ShowAsync(html, false));
                Assert.AreEqual(originalHtml, await view.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML"), "切回原文应完整恢复原HTML");
                await Assert.ThrowsAsync<InvalidDataException>(() => renderer.ShowAsync(html, false, translatedTextList: new[] { "不完整译文" }));
                Assert.AreEqual(originalHtml, await view.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML"), "节点不匹配时不应覆盖原文");
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
