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
    public void PrivateChatBlocksEveryTaskRelatedWorkspaceSurface()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessTaskHistory(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanAccessSavedRoutines(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanAccessReminderManager(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanAccessWorkspaceHub(privateChatActive: true));
    }

    [TestMethod]
    public void NormalModeRetainsTaskMemoryAndWorkspaceAccess()
    {
        Assert.IsTrue(PrivateChatModePolicy.CanAccessTaskWorkspace(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanStartAgentTask(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessTaskHistory(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessSavedRoutines(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessReminderManager(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessWorkspaceHub(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessPersonalMemory(privateChatActive: false));
    }
}
