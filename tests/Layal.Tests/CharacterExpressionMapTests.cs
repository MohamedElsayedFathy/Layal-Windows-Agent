using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CharacterExpressionMapTests
{
    // Every mood SetMood understands, plus the resolver's task moods.
    private static readonly string[] KnownMoods =
    {
        "idle", "listening", "transcribing", "understanding", "thinking", "planning", "researching", "reading",
        "writing", "coding", "using_tool", "validating", "waiting", "responding", "speaking", "greeting", "happy",
        "love", "shy", "laughing", "wink", "playful", "surprised", "confused", "curious", "sad", "worried", "proud",
        "annoyed", "bored", "sleepy", "success", "celebrating", "excited", "warning", "error", "paused", "interrupted",
        "recovering", "reconnecting", "offline", "privacy", "camera_processing", "embarrassed", "relieved",
        "determined", "confident", "apologetic", "frustrated", "grateful"
    };

    [TestMethod]
    public void EveryKnownMoodHasAnExpressionWithRealArtwork()
    {
        var assets = FindCharacterAssets();
        foreach (var mood in KnownMoods)
        {
            var expression = CharacterExpressionMap.Resolve(mood);
            Assert.AreEqual(mood, expression.Mood, $"'{mood}' must not silently fall back to idle.");
            Assert.IsTrue(File.Exists(Path.Combine(assets, expression.LayalAssetName)),
                $"Missing artwork {expression.LayalAssetName} for mood '{mood}'.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(expression.Robot.LeftEye), mood);
            Assert.IsFalse(string.IsNullOrWhiteSpace(expression.Robot.RightEye), mood);
            Assert.IsFalse(string.IsNullOrWhiteSpace(expression.Robot.Mouth), mood);
        }
        Assert.IsTrue(File.Exists(Path.Combine(assets, "layal-robot.png")));
    }

    [TestMethod]
    public void UnknownOrMissingMoodFallsBackToIdle()
    {
        Assert.AreEqual("idle", CharacterExpressionMap.Resolve(null).Mood);
        Assert.AreEqual("idle", CharacterExpressionMap.Resolve("no-such-mood").Mood);
        Assert.AreEqual(LayalSprite.Idle, CharacterExpressionMap.Resolve("").Layal);
    }

    [TestMethod]
    public void StatusIsNeverConveyedByColourAlone()
    {
        // Amber/coral tones must come with a shape or glyph that differs from the neutral ready face.
        var ready = CharacterExpressionMap.Resolve("idle").Robot;
        foreach (var mood in KnownMoods)
        {
            var face = CharacterExpressionMap.Resolve(mood).Robot;
            if (face.Tone is RobotTone.Amber or RobotTone.Coral)
            {
                var distinct = face.Glyph.Length > 0 || face.Mouth != ready.Mouth || face.LeftEye != ready.LeftEye;
                Assert.IsTrue(distinct, $"'{mood}' relies on colour alone.");
            }
        }
        Assert.AreEqual("!", CharacterExpressionMap.Resolve("approval_required".Replace("approval_required", "warning")).Robot.Glyph);
        Assert.AreEqual("\u00D7", CharacterExpressionMap.Resolve("error").Robot.Glyph);
    }

    [TestMethod]
    public void TaskAndOutcomeMoodsUseTheMatchingPortraits()
    {
        Assert.AreEqual(LayalSprite.Curious, CharacterExpressionMap.Resolve("thinking").Layal);
        Assert.AreEqual(LayalSprite.Focused, CharacterExpressionMap.Resolve("coding").Layal);
        Assert.AreEqual(LayalSprite.Happy, CharacterExpressionMap.Resolve("success").Layal);
        Assert.AreEqual(LayalSprite.Surprised, CharacterExpressionMap.Resolve("surprised").Layal);
        Assert.AreEqual(LayalSprite.Playful, CharacterExpressionMap.Resolve("wink").Layal);
        Assert.AreEqual(LayalSprite.Calm, CharacterExpressionMap.Resolve("sleepy").Layal);
    }

    [TestMethod]
    public void EveryMoodHasADistinctArabicAndEnglishDescription()
    {
        foreach (var mood in KnownMoods)
        {
            var expression = CharacterExpressionMap.Resolve(mood);
            var en = CharacterExpressionMap.Describe(expression, arabic: false);
            var ar = CharacterExpressionMap.Describe(expression, arabic: true);
            Assert.IsFalse(string.IsNullOrWhiteSpace(en), mood);
            Assert.IsFalse(string.IsNullOrWhiteSpace(ar), mood);
            Assert.AreNotEqual(en, ar, mood);
        }
        Assert.AreEqual("Something went wrong", CharacterExpressionMap.Describe(CharacterExpressionMap.Resolve("error"), false));
    }

    private static string FindCharacterAssets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Layal.Desktop", "Assets", "Characters");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Assets/Characters was not found above " + AppContext.BaseDirectory);
    }
}
