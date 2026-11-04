using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class QuickAskRequestPolicyTests
{
    [TestMethod]
    public void PrivateChatAlwaysBlocksQuickAsk()
    {
        Assert.AreEqual(QuickAskBlockReason.PrivateChat,
            QuickAskRequestPolicy.Evaluate(true, false, false, false, false));
        Assert.AreEqual(QuickAskBlockReason.PrivateChat,
            QuickAskRequestPolicy.Evaluate(true, true, true, true, true));
    }

    [TestMethod]
    public void AnExistingChatOrVoiceRequestBlocksDuplicateQuickAsk()
    {
        Assert.AreEqual(QuickAskBlockReason.RequestBusy,
            QuickAskRequestPolicy.Evaluate(false, true, false, false, false));
        Assert.AreEqual(QuickAskBlockReason.RequestBusy,
            QuickAskRequestPolicy.Evaluate(false, false, true, false, false));
    }

    [TestMethod]
    public void PendingDocumentOrImageBlocksQuickAskInsteadOfAttachingItSilently()
    {
        Assert.AreEqual(QuickAskBlockReason.PendingAttachment,
            QuickAskRequestPolicy.Evaluate(false, false, false, true, false));
        Assert.AreEqual(QuickAskBlockReason.PendingAttachment,
            QuickAskRequestPolicy.Evaluate(false, false, false, false, true));
    }

    [TestMethod]
    public void CleanNormalChatAllowsQuickAsk()
    {
        Assert.AreEqual(QuickAskBlockReason.None,
            QuickAskRequestPolicy.Evaluate(false, false, false, false, false));
    }
}
