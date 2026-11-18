using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class IdleDiagnosticsTests
{
    [TestMethod]
    public void RepeatedDisconnectsKeepRetryingWithinOneMinute()
    {
        var failures = 0;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            // 每次登录成功后仅监听3分钟，不能把连续失败次数重置为1。
            var retry = ImapIdleListener.NextRetry(failures, TimeSpan.FromMinutes(3));
            Assert.AreEqual(attempt, retry.Failures);
            Assert.AreEqual(TimeSpan.FromSeconds(new[] { 5, 15, 30, 60 }[attempt - 1]), retry.Delay);
            failures = retry.Failures;
        }
        var recovered = ImapIdleListener.NextRetry(failures, TimeSpan.FromMinutes(9));
        Assert.AreEqual(1, recovered.Failures);
        Assert.AreEqual(TimeSpan.FromSeconds(5), recovered.Delay);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ImapIdleListener.NextRetry(20, TimeSpan.Zero).Delay);
    }

    [TestMethod]
    public void FailedIdleNegotiationStillCountsAgainstRetryBudget()
    {
        var retry = ImapIdleListener.NextRetry(2, TimeSpan.Zero);
        Assert.AreEqual(3, retry.Failures);
        Assert.AreEqual(TimeSpan.FromSeconds(30), retry.Delay);
        retry = ImapIdleListener.NextRetry(retry.Failures, TimeSpan.FromMinutes(9).Subtract(TimeSpan.FromMilliseconds(1)));
        Assert.AreEqual(4, retry.Failures);
        Assert.AreEqual(TimeSpan.FromSeconds(60), retry.Delay);
    }

    [TestMethod]
    public void EofAndConnectionResetGetTwoShortRetriesThenUseExistingBackoff()
    {
        var eof = new MailKit.Security.SslHandshakeException("TLS handshake failed",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));
        Assert.AreEqual(TimeSpan.FromSeconds(1), ImapIdleListener.QuickRetryDelay(eof, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(3), ImapIdleListener.QuickRetryDelay(eof, 1));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(eof, 2));
        var retry = ImapIdleListener.NextRetry(0, TimeSpan.Zero);
        Assert.AreEqual(1, retry.Failures);
        Assert.AreEqual(TimeSpan.FromSeconds(5), retry.Delay);
        var reset = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset);
        Assert.AreEqual(TimeSpan.FromSeconds(1), ImapIdleListener.QuickRetryDelay(reset, 0));
    }

    [TestMethod]
    public void UnexpectedImapDisconnectGetsTwoShortRetries()
    {
        var error = new MailKit.Net.Imap.ImapProtocolException("The IMAP server has unexpectedly disconnected.");
        Assert.AreEqual(TimeSpan.FromSeconds(1), ImapIdleListener.QuickRetryDelay(error, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(3), ImapIdleListener.QuickRetryDelay(error, 1));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(error, 2));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(
            new MailKit.Net.Imap.ImapProtocolException("Unexpected token"), 0));
    }

    [TestMethod]
    public void CertificateAuthorizationAndTimeoutFailuresNeverUseShortRetries()
    {
        var certificate = new MailKit.Security.SslHandshakeException("Certificate rejected",
            new System.Security.Authentication.AuthenticationException("RemoteCertificateValidationCallback rejected certificate",
                new IOException("Transport error")));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(certificate, 0));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(new MailKit.Security.AuthenticationException("LOGIN rejected"), 0));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(new UnauthorizedAccessException("Missing credential"), 0));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(new TaskCanceledException("Timeout"), 0));
        Assert.IsNull(ImapIdleListener.QuickRetryDelay(new InvalidOperationException("Local state error"), 0));
    }

    [TestMethod]
    public void DiagnosticFileKeepsRedactedServerReasonAndRotatesOnlyOldOwnedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.Now;
            var oldPath = Path.Combine(directory, $"imap-idle-{now.AddDays(-7):yyyy-MM-dd}.log");
            var recentPath = Path.Combine(directory, $"imap-idle-{now.AddDays(-6):yyyy-MM-dd}.log");
            var unrelatedPath = Path.Combine(directory, "other.log");
            File.WriteAllText(oldPath, "old");
            File.WriteAllText(recentPath, "recent");
            File.WriteAllText(unrelatedPath, "keep");
            const string secret = "test-only-credential";
            var failure = SyncFailure.Describe(new IOException($"BYE Too many connections. {secret}"),
                ProviderKind.GmailImap, "等待IDLE通知", secret);
            var log = new DiagnosticLog(directory);
            Assert.IsTrue(log.TryAppend("告警", "监听中断", failure.Details));
            var text = File.ReadAllText(Path.Combine(directory, $"imap-idle-{now:yyyy-MM-dd}.log"), Encoding.UTF8);
            using var entry = JsonDocument.Parse(text);
            Assert.AreEqual("告警", entry.RootElement.GetProperty("Kind").GetString());
            StringAssert.Contains(text, "BYE Too many connections");
            StringAssert.Contains(text, "IOException");
            Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(recentPath));
            Assert.IsTrue(File.Exists(unrelatedPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void UnwritableLogLocationReturnsFailureWithoutThrowingIntoListener()
    {
        var path = Path.GetTempFileName();
        try { Assert.IsFalse(new DiagnosticLog(path).TryAppend("状态", "监听", "详情")); }
        finally { File.Delete(path); }
    }
}
