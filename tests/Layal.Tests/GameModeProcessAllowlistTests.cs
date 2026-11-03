using System;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GameModeProcessAllowlistTests
{
    [TestMethod]
    public void ProcessNamesAreNormalizedAndDeduplicated()
    {
        var result = GameModeProcessAllowlist.ParseAndValidate(
            "Cyberpunk2077.exe\r\ncs2; Cyberpunk2077, game-win64.exe");

        CollectionAssert.AreEqual(
            new[] { "Cyberpunk2077", "cs2", "game-win64" },
            result);
    }

    [TestMethod]
    public void ProcessNamesRejectPathsShellCharactersAndWhitespace()
    {
        foreach (var invalid in new[]
                 {
                     @"C:\Games\game.exe",
                     "game name",
                     "game;start",
                     "game|other",
                     "game.exe --launch",
                     "game:stream",
                     "../game"
                 })
        {
            Assert.IsFalse(GameModeProcessAllowlist.TryNormalizeName(invalid, out _), invalid);
        }
    }

    [TestMethod]
    public void AllowlistInputHasALimitAndStoredValuesAreSanitized()
    {
        var tooMany = string.Join(Environment.NewLine,
            Enumerable.Range(1, GameModeProcessAllowlist.MaximumEntries + 1)
                .Select(index => "game" + index));
        Assert.ThrowsExactly<ArgumentException>(() => GameModeProcessAllowlist.ParseAndValidate(tooMany));

        var normalized = GameModeProcessAllowlist.NormalizeSettings(new[]
        {
            "GameA.exe", "gamea", "../bad", new string('x', GameModeProcessAllowlist.MaximumNameCharacters + 1),
            "GameB.exe"
        });
        CollectionAssert.AreEqual(new[] { "GameA", "GameB" }, normalized);
    }
}
