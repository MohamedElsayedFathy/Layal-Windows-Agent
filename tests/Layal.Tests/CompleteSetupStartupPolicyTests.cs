using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CompleteSetupStartupPolicyTests
{
    [TestMethod]
    public void NormalFirstRunShowsWizardWithoutAcceptanceOverride()
    {
        Assert.IsTrue(CompleteSetupStartupPolicy.ShouldShow(
            isScheduledReminderLaunch: false,
            isIsolatedAcceptance: false,
            acceptanceOverride: null));
    }

    [TestMethod]
    public void IsolatedAcceptanceHidesWizardByDefault()
    {
        Assert.IsFalse(CompleteSetupStartupPolicy.ShouldShow(
            isScheduledReminderLaunch: false,
            isIsolatedAcceptance: true,
            acceptanceOverride: null));
    }

    [TestMethod]
    public void IsolatedAcceptanceShowsWizardOnlyWithExplicitOverride()
    {
        Assert.IsTrue(CompleteSetupStartupPolicy.ShouldShow(
            isScheduledReminderLaunch: false,
            isIsolatedAcceptance: true,
            acceptanceOverride: "1"));
    }

    [TestMethod]
    public void AcceptanceOverrideDoesNotAffectNormalProductionLaunch()
    {
        Assert.IsTrue(CompleteSetupStartupPolicy.ShouldShow(
            isScheduledReminderLaunch: false,
            isIsolatedAcceptance: false,
            acceptanceOverride: "1"));
    }

    [TestMethod]
    public void ScheduledReminderLaunchNeverShowsWizard()
    {
        Assert.IsFalse(CompleteSetupStartupPolicy.ShouldShow(
            isScheduledReminderLaunch: true,
            isIsolatedAcceptance: true,
            acceptanceOverride: "1"));
    }
}
