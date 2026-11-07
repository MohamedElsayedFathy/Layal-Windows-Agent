using System.Collections.Generic;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GameModeAutoDismissalPolicyTests
{
    private static readonly IReadOnlyCollection<string> Allowlist = new[] { "game-a", "game-b" };

    [TestMethod]
    public void AllowedForegroundCanOpenHudOnFirstDetection()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a.exe", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"));
    }

    [TestMethod]
    public void UserDismissalDoesNotReopenForTheSameForegroundProcess()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-a.exe");

        Assert.IsTrue(policy.IsAllowedForeground("GAME-A.EXE", Allowlist));
        Assert.IsTrue(policy.IsDismissedFor("game-a"),
            "A user-dismissed HUD must remain hidden while the same game stays in front.");
    }

    [TestMethod]
    public void LeavingAllowlistClearsDismissalAndReturningCanOpenHudAgain()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-a");

        Assert.IsFalse(policy.IsAllowedForeground("explorer", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"));

        Assert.IsTrue(policy.IsAllowedForeground("game-a.exe", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"),
            "Returning to the game after focus left it starts a fresh detection session.");
    }

    [TestMethod]
    public void SwitchingToAnotherAllowedGameStartsANewHudSession()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-a");

        Assert.IsTrue(policy.IsAllowedForeground("game-b.exe", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-b"));
    }

    [TestMethod]
    public void UnlistedInvalidAndEmptyForegroundValuesFailClosedAndResetDismissal()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-a");

        Assert.IsFalse(policy.IsAllowedForeground("../game-a", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"));
        Assert.IsFalse(policy.IsAllowedForeground("unknown-game", Allowlist));
        Assert.IsFalse(policy.IsAllowedForeground("game-a", System.Array.Empty<string>()));
        Assert.IsFalse(policy.IsAllowedForeground(null, Allowlist));
    }

    [TestMethod]
    public void DismissalCannotBeAppliedToAProcessThatIsNotTheCurrentAllowedForeground()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-b");

        Assert.IsFalse(policy.IsDismissedFor("game-a"));
        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"));
    }

    [TestMethod]
    public void ResetClearsTheDismissalAndCurrentForeground()
    {
        var policy = new GameModeAutoDismissalPolicy();

        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        policy.DismissCurrentForeground("game-a");
        policy.Reset();

        Assert.IsFalse(policy.IsDismissedFor("game-a"));
        Assert.IsTrue(policy.IsAllowedForeground("game-a", Allowlist));
        Assert.IsFalse(policy.IsDismissedFor("game-a"));
    }
}
