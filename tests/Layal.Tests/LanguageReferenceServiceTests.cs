using System;
using System.IO;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LanguageReferenceServiceTests
{
    [TestMethod]
    public void BuiltInCatalogueLoadsWithThreeLanguageVarietiesAndTopicCoverage()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        Assert.AreEqual(1, service.Catalog.SchemaVersion);
        Assert.IsTrue(service.Catalog.Topics.Count >= 15);
        Assert.IsTrue(service.Catalog.Concepts.Count >= 15);
        Assert.IsTrue(service.Catalog.Dialogues.Count >= 3);
        Assert.IsTrue(service.Catalog.Concepts.SelectMany(item => item.Forms).Any(form => form.Language == "ar-EG"));
        Assert.IsTrue(service.Catalog.Concepts.SelectMany(item => item.Forms).Any(form => form.Language == "ar"));
        Assert.IsTrue(service.Catalog.Concepts.SelectMany(item => item.Forms).Any(form => form.Language == "en"));
    }

    [TestMethod]
    public void Normalize_MatchesArabicAlefVariantsDiacriticsAndTatweel()
    {
        Assert.AreEqual("ازيك", LanguageTextNormalizer.Normalize("إِزَّيــــك؟"));
        Assert.AreEqual("ازيك", LanguageTextNormalizer.Normalize("ازيك"));
    }

    [TestMethod]
    public void Search_FindsEgyptianColloquialFormAndReturnsOriginalDisplayForms()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        var match = service.Search("ازيك", language: "ar-EG").Single();

        Assert.AreEqual("greeting-how-are-you", match.Concept.Id);
        Assert.IsTrue(match.Concept.Forms.Any(form => form.Language == "ar-EG" && form.Text == "إزيك؟"));
        Assert.IsTrue(match.Concept.Forms.Any(form => form.Language == "ar" && form.Text == "كيف حالك؟"));
        Assert.IsTrue(match.Concept.Forms.Any(form => form.Language == "en" && form.Text == "How are you?"));
    }

    [TestMethod]
    public void Search_FindsEnglishTechnicalTermsAndSupportsTopicFilter()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        var result = service.Search("browser", language: "en", topicId: "internet");

        Assert.IsTrue(result.Any(item => item.Concept.Id == "computer-browser"));
        Assert.IsFalse(service.Search("browser", language: "en", topicId: "health")
            .Any(item => item.Concept.Id == "computer-browser"));
    }

    [TestMethod]
    public void Search_FindsCodeSwitchedTechnicalVocabulary()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        var results = service.Search("عندي meeting بكرة وعايز أراجع ال report");

        Assert.IsTrue(results.Any(item => item.Concept.Id == "language-code-switching"));
    }

    [TestMethod]
    public void SearchDialogues_FindsEgyptianAndEnglishTechnicalDialogue()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        var egyptian = service.SearchDialogues("meeting error", language: "ar-EG");
        var english = service.SearchDialogues("error", language: "en");

        Assert.IsTrue(egyptian.Any(item => item.Id == "dialogue-tech-help"));
        Assert.IsTrue(service.SearchDialogues("المساعدة التقنية", language: "ar-EG")
            .Any(item => item.Id == "dialogue-tech-help"));
        Assert.IsTrue(english.Any(item => item.Id == "dialogue-tech-help"));
    }

    [TestMethod]
    public void Search_EmptyQueryAndInvalidTopicReturnNoMatches()
    {
        var service = LanguageReferenceService.LoadBuiltIn();

        Assert.AreEqual(0, service.Search("   ").Count);
        Assert.AreEqual(0, service.Search("إزيك", topicId: "missing-topic").Count);
        Assert.AreEqual(0, service.SearchDialogues(" ").Count);
    }

    [TestMethod]
    public void FromJson_RejectsUnsupportedSchemaAndDuplicateTopicIds()
    {
        const string unsupported = "{\"schemaVersion\":99,\"catalogVersion\":\"test\",\"topics\":[],\"concepts\":[],\"dialogues\":[]}";
        const string duplicates = "{\"schemaVersion\":1,\"catalogVersion\":\"test\",\"topics\":[{\"id\":\"a\",\"nameAr\":\"أ\",\"nameEn\":\"A\"},{\"id\":\"a\",\"nameAr\":\"ب\",\"nameEn\":\"B\"}],\"concepts\":[],\"dialogues\":[]}";

        AssertInvalidData(() => LanguageReferenceService.FromJson(unsupported));
        AssertInvalidData(() => LanguageReferenceService.FromJson(duplicates));
    }

    private static void AssertInvalidData(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.Fail("Expected an InvalidDataException, but the action completed without one.");
    }
}
