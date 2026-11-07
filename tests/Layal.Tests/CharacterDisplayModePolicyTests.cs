using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CharacterDisplayModePolicyTests
{
    [TestMethod]
    public void NamedModesShowOnlyTheRequestedArtwork()
    {
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsLayal("layal"));
        Assert.IsFalse(CharacterDisplayModePolicy.ShowsRobot("layal"));
        Assert.IsFalse(CharacterDisplayModePolicy.ShowsLayal("robot"));
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsRobot("robot"));
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsLayal("duo"));
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsRobot("duo"));
    }

    [TestMethod]
    public void MissingOrInvalidModeDefaultsToOriginalDuoPresentation()
    {
        Assert.AreEqual("duo", CharacterDisplayModePolicy.Normalize(null));
        Assert.AreEqual("duo", CharacterDisplayModePolicy.Normalize("unknown"));
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsLayal("unknown"));
        Assert.IsTrue(CharacterDisplayModePolicy.ShowsRobot("unknown"));
    }

    [TestMethod]
    public void AccessibleNamesAndDescriptionsFollowVisibleCharacter()
    {
        Assert.AreEqual("Layal character", CharacterDisplayModePolicy.GetAutomationName("layal", false));
        Assert.AreEqual("مساعد ليال الروبوت", CharacterDisplayModePolicy.GetAutomationName("robot", true));
        Assert.AreEqual("Layal and her robot assistant", CharacterDisplayModePolicy.GetAutomationName("duo", false));
        Assert.IsFalse(string.IsNullOrWhiteSpace(CharacterDisplayModePolicy.GetHelpText("robot", true)));
    }
}
