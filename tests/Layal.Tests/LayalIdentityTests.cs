using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LayalIdentityTests
{
    [TestMethod]
    public void SystemPromptDoesNotAssumeEveryUserIsTheDeveloper()
    {
        var prompt = LayalIdentity.SystemPrompt;

        Assert.IsTrue(prompt.Contains("Do not assume that the current user is the developer", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(prompt.Contains("verified information", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(prompt.Contains("The person using you is the developer and project owner", StringComparison.OrdinalIgnoreCase));
    }
}
