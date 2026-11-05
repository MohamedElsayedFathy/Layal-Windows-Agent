using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class RoutineToolOutcomePolicyTests
{
    [DataTestMethod]
    [DataRow("User declined file creation. No file was created.")]
    [DataRow("WEB SEARCH NOT SHARED\nStatus: unexpected_redirect")]
    [DataRow("The claim search was completed, but the separate contrary-evidence search was declined. No verification conclusion is possible.")]
    [DataRow("No direct source opened: the dedicated browser is unavailable.")]
    [DataRow("No eligible direct HTTPS source candidate on a new registrable domain was found.")]
    [DataRow("The approved source page was opened, but its bounded evidence snapshot could not be parsed; no evidence from it was shared.")]
    public void ExplicitDeclinesAndUnavailableToolResultsStopRoutine(string output)
    {
        Assert.IsTrue(RoutineToolOutcomePolicy.ShouldStopRoutine(output));
    }

    [TestMethod]
    public void EmptyToolResultStopsRoutineBecauseNoResultCanBeVerified()
    {
        Assert.IsTrue(RoutineToolOutcomePolicy.ShouldStopRoutine(string.Empty));
        Assert.IsTrue(RoutineToolOutcomePolicy.ShouldStopRoutine("  "));
    }

    [DataTestMethod]
    [DataRow("A web page quotes the words: User declined file creation.")]
    [DataRow("The document says that no direct source opened in the previous example.")]
    [DataRow("The operation completed and returned a verified file path.")]
    public void UntrustedContentThatMentionsFailurePhrasesDoesNotStopRoutine(string output)
    {
        Assert.IsFalse(RoutineToolOutcomePolicy.ShouldStopRoutine(output));
    }
}
