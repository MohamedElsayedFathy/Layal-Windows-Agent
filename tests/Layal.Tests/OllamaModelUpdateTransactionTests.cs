using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaModelUpdateTransactionTests
{
    [TestMethod]
    public async Task CommitsPinOnlyAfterCandidateVerificationSucceeds()
    {
        var calls = new List<string>();
        var digest = new string('a', 64);

        var result = await OllamaModelUpdateTransaction.ExecuteAsync(
            "layal-rollback:qwen",
            _ => { calls.Add("backup"); return Task.CompletedTask; },
            _ => { calls.Add("pull"); return Task.FromResult(digest); },
            (candidate, _) =>
            {
                calls.Add("verify");
                Assert.AreEqual(digest, candidate);
                return Task.FromResult(true);
            },
            (_, _) => { calls.Add("commit"); return Task.CompletedTask; },
            _ => { calls.Add("rollback"); return Task.CompletedTask; },
            _ => { calls.Add("verify-rollback"); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.AreEqual(digest, result);
        CollectionAssert.AreEqual(new[] { "backup", "pull", "verify", "commit" }, calls);
    }

    [TestMethod]
    public async Task RejectsCandidateAndRestoresPreviousModelWithoutCommittingPin()
    {
        var calls = new List<string>();
        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            OllamaModelUpdateTransaction.ExecuteAsync(
                "layal-rollback:qwen",
                _ => { calls.Add("backup"); return Task.CompletedTask; },
                _ => { calls.Add("pull"); return Task.FromResult("wrong-digest"); },
                (_, _) => { calls.Add("verify"); return Task.FromResult(false); },
                (_, _) => { calls.Add("commit"); return Task.CompletedTask; },
                _ => { calls.Add("rollback"); return Task.CompletedTask; },
                _ => { calls.Add("verify-rollback"); return Task.CompletedTask; },
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "did not match the Registry digest");
        CollectionAssert.AreEqual(
            new[] { "backup", "pull", "verify", "rollback", "verify-rollback" },
            calls);
    }

    [TestMethod]
    public async Task CancellationDuringUpdateStillRestoresThePreviousModel()
    {
        var calls = new List<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            OllamaModelUpdateTransaction.ExecuteAsync(
                "layal-rollback:qwen",
                _ => { calls.Add("backup"); return Task.CompletedTask; },
                async token =>
                {
                    calls.Add("pull");
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "unreachable";
                },
                (_, _) => Task.FromResult(true),
                (_, _) => { calls.Add("commit"); return Task.CompletedTask; },
                token =>
                {
                    Assert.IsFalse(token.CanBeCanceled || token.IsCancellationRequested);
                    calls.Add("rollback");
                    return Task.CompletedTask;
                },
                _ => { calls.Add("verify-rollback"); return Task.CompletedTask; },
                cancellation.Token));

        CollectionAssert.AreEqual(
            new[] { "backup", "pull", "rollback", "verify-rollback" },
            calls);
    }

    [TestMethod]
    public async Task ReportsRollbackAliasAndBothFailuresWhenRestoreCannotBeVerified()
    {
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            OllamaModelUpdateTransaction.ExecuteAsync(
                "layal-rollback:qwen-20261118",
                _ => Task.CompletedTask,
                _ => Task.FromException<string>(new HttpRequestException("pull failed")),
                (_, _) => Task.FromResult(true),
                (_, _) => Task.CompletedTask,
                _ => Task.FromException(new IOException("restore failed")),
                _ => Task.CompletedTask,
                CancellationToken.None));

        StringAssert.Contains(error.Message, "layal-rollback:qwen-20261118");
        Assert.IsInstanceOfType<AggregateException>(error.InnerException);
        Assert.AreEqual(2, ((AggregateException)error.InnerException!).InnerExceptions.Count);
    }
}
