using System.Runtime.InteropServices;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ClipboardReadPolicyTests
{
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);

    [TestMethod]
    public async Task ReadWithRetryAsyncRetriesOnlyTransientClipboardLock()
    {
        var attempts = 0;

        var value = await ClipboardReadPolicy.ReadWithRetryAsync(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new ExternalException("Clipboard is temporarily locked.", ClipboardCannotOpenHResult);
            return "clipboard snapshot";
        });

        Assert.AreEqual("clipboard snapshot", value);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ReadWithRetryAsyncStopsAfterItsBoundedAttemptBudget()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsExactlyAsync<ExternalException>(async () =>
            await ClipboardReadPolicy.ReadWithRetryAsync<string>(() =>
            {
                attempts++;
                throw new ExternalException("Clipboard is locked.", ClipboardCannotOpenHResult);
            }));

        Assert.AreEqual(ClipboardCannotOpenHResult, exception.ErrorCode);
        Assert.AreEqual(4, attempts);
    }

    [TestMethod]
    public async Task ReadWithRetryAsyncDoesNotRetryUnrelatedFailures()
    {
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ClipboardReadPolicy.ReadWithRetryAsync<string>(() =>
            {
                attempts++;
                throw new InvalidOperationException("Not a clipboard-lock failure.");
            }));

        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task ReadWithRetryAsyncHonorsCancellationBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await ClipboardReadPolicy.ReadWithRetryAsync(() =>
            {
                attempts++;
                return "must not be read";
            }, cancellation.Token));

        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public void LimitTextKeepsContentWithinTheConfiguredCharacterBudget()
    {
        var source = new string('x', 10_001);

        var result = ClipboardReadPolicy.LimitText(source, 10_000);

        Assert.AreEqual(10_000, result.Text.Length);
        Assert.IsTrue(result.WasTruncated);
    }

    [TestMethod]
    public void LimitTextDoesNotSplitASurrogatePairAtTheBoundary()
    {
        var source = new string('x', 9_999) + "😀" + "tail";

        var result = ClipboardReadPolicy.LimitText(source, 10_000);

        Assert.AreEqual(9_999, result.Text.Length);
        Assert.IsTrue(result.WasTruncated);
        Assert.IsFalse(char.IsHighSurrogate(result.Text[^1]));
    }

    [TestMethod]
    public void LimitTextPreservesAnUntruncatedSnapshotVerbatim()
    {
        const string source = "  نص عربي\r\nEnglish  ";

        var result = ClipboardReadPolicy.LimitText(source, 10_000);

        Assert.AreEqual(source, result.Text);
        Assert.IsFalse(result.WasTruncated);
    }
}
