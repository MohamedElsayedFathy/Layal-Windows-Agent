using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PrivateChatModePolicyTests
{
    [TestMethod]
    public void PrivateChatBlocksTaskWorkspaceAndNewAgentTasks()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessTaskWorkspace(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanStartAgentTask(privateChatActive: true));
    }

    [TestMethod]
    public void PrivateChatBlocksPersonalMemoryAccess()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessPersonalMemory(privateChatActive: true));
    }

    [TestMethod]
    public void NormalModeRetainsTaskAndMemoryAccess()
    {
        Assert.IsTrue(PrivateChatModePolicy.CanAccessTaskWorkspace(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanStartAgentTask(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessPersonalMemory(privateChatActive: false));
    }
}
