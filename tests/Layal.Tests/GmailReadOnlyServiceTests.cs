using System;
using System.Security.Cryptography;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GmailReadOnlyServiceTests
{
    [TestMethod]
    public void OAuthClientIdMustBeAnExpectedGoogleDesktopClientId()
    {
        const string clientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com";
        Assert.AreEqual(clientId, GmailReadOnlyService.ValidateOAuthClientId(clientId));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("not-a-client-id"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("paste-your-client-secret-here"));
    }

    [TestMethod]
    public void PkceVerifierIsRandomAndChallengeUsesSha256Base64Url()
    {
        var first = GmailReadOnlyService.CreatePkceVerifier();
        var second = GmailReadOnlyService.CreatePkceVerifier();

        Assert.AreEqual(43, first.Length);
        Assert.AreEqual(43, second.Length);
        Assert.AreNotEqual(first, second);
        StringAssert.Matches(first, new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]+$"));

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(first)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(expected, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.AreNotEqual(first, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.ThrowsExactly<ArgumentException>(() => GmailReadOnlyService.CreatePkceChallenge("short"));
    }

    [TestMethod]
    public void MessageBodyAndResultsAreBoundedByServiceContracts()
    {
        Assert.AreEqual(25, GmailReadOnlyService.MaximumListedMessages);
        Assert.AreEqual(15_000, GmailReadOnlyService.MaximumBodyCharacters);
    }
}
