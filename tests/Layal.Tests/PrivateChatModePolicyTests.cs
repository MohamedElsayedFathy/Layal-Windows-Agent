using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PrivateChatModePolicyTests
{
    [TestMethod]
    public void PrivateChatBlocksTaskWorkspaceDetailsCopiesAndNewAgentTasks()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessTaskWorkspace(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanAccessTaskDetails(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanCopyTaskData(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanStartAgentTask(privateChatActive: true));
    }

    [TestMethod]
    public void PrivateChatBlocksPersonalMemoryAccess()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessPersonalMemory(privateChatActive: true));
    }

    [TestMethod]
    public void PrivateChatHidesSavedHistoryAndBlocksAuxiliaryDataSurfaces()
    {
        Assert.IsFalse(PrivateChatModePolicy.CanAccessSavedChatHistory(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanPersistChatMessages(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanUseQuickAsk(privateChatActive: true));
        Assert.IsFalse(PrivateChatModePolicy.CanUseGmail(privateChatActive: true));
    }

    [TestMethod]
    public void NormalModeRetainsTaskMemoryHistoryAndAuxiliaryAccess()
    {
        Assert.IsTrue(PrivateChatModePolicy.CanAccessTaskWorkspace(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessTaskDetails(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanCopyTaskData(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanStartAgentTask(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessPersonalMemory(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanAccessSavedChatHistory(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanPersistChatMessages(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanUseQuickAsk(privateChatActive: false));
        Assert.IsTrue(PrivateChatModePolicy.CanUseGmail(privateChatActive: false));
    }
}
