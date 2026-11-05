using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Layal.Models;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LoreWebWindowTests
{
    [TestMethod]
    public void LoreWebBuildsBoundedAccessibleThreeDimensionalMapForOneHundredNotes()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Lore Web uses the Windows WPF 3D desktop runtime.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var notes = Enumerable.Range(0, 100)
                    .Select(index => new MemoryNote
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = "Shared project planning note",
                        Category = "Projects",
                        Content = index == 0
                            ? "محتوى عربي طويل مع English detail — " + new string('x', 6000)
                            : $"Note content {index}",
                        IsArchived = index % 5 == 0,
                        CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                        UpdatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index)
                    })
                    .ToList();

                // Explicit user links must survive the inferred-edge density limit.
                for (var index = 1; index <= MemoryServiceRelationshipLimit; index++)
                {
                    notes[0].Relationships.Add(new MemoryNoteRelationship
                    {
                        TargetNoteId = notes[index].Id,
                        Label = "references"
                    });
                    notes[index].Relationships.Add(new MemoryNoteRelationship
                    {
                        TargetNoteId = notes[0].Id,
                        Label = "references"
                    });
                }

                var window = new LoreWebWindow(
                    notes,
                    isArabic: true,
                    _ => { },
                    _ => true,
                    _ => true,
                    (_, _, _) => true,
                    (_, _) => true);

                var viewport = GetField<Viewport3D>(window, "_viewport");
                var noteList = GetField<ListBox>(window, "_noteList");
                var selectedContent = GetField<TextBlock>(window, "_selectedContent");
                var edgeVisuals = GetField<Dictionary<ModelVisual3D, (string FromId, string ToId)>>(window, "_edgeVisuals");

                Assert.AreEqual(100, noteList.Items.Count, "The graph must include the full supported memory retention set.");
                Assert.AreEqual(100, GetField<Dictionary<string, ModelVisual3D>>(window, "_noteVisuals").Count);
                Assert.IsTrue(viewport.Focusable, "The 3D viewport must be reachable by keyboard.");
                Assert.IsTrue(KeyboardNavigation.GetIsTabStop(viewport), "The 3D viewport must be a tab stop.");
                Assert.AreEqual(0, KeyboardNavigation.GetTabIndex(viewport));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(viewport),
                    "الأسهم للتدوير");

                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(noteList)));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(selectedContent)));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(GetField<ComboBox>(window, "_linkTargetSelector"))));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(GetField<TextBox>(window, "_linkLabelInput"))));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(GetField<Button>(window, "_linkButton"))));
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(GetField<Button>(window, "_unlinkButton"))));

                Assert.IsTrue(edgeVisuals.Count > 100, "Chronology and local grouping should produce navigable connections.");
                Assert.IsTrue(edgeVisuals.Count <= (100 * 6 / 2) + MemoryServiceRelationshipLimit,
                    "Inferred links must remain sparse even when every title and category is identical.");

                var renderedEdges = edgeVisuals.Values.ToHashSet();
                for (var index = 1; index <= MemoryServiceRelationshipLimit; index++)
                    Assert.IsTrue(renderedEdges.Contains(NormalizeEdge(notes[0].Id, notes[index].Id)),
                        "Every user-defined relationship must remain visible in the graph.");

                var buildEdges = typeof(LoreWebWindow).GetMethod("BuildEdges", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new AssertFailedException("The graph edge builder was not found.");
                var edges = (IReadOnlyList<(string FromId, string ToId)>)buildEdges.Invoke(null, new object[] { notes })!;
                var explicitEdges = new HashSet<(string FromId, string ToId)>();
                foreach (var note in notes)
                    foreach (var relation in note.Relationships)
                        explicitEdges.Add(NormalizeEdge(note.Id, relation.TargetNoteId));

                var inferredDegree = notes.ToDictionary(note => note.Id, _ => 0, StringComparer.Ordinal);
                foreach (var edge in edges.Where(edge => !explicitEdges.Contains(edge)))
                {
                    inferredDegree[edge.FromId]++;
                    inferredDegree[edge.ToId]++;
                }
                Assert.IsTrue(inferredDegree.Values.All(degree => degree <= 6),
                    "No note may receive more than six inferred connections.");

                Assert.AreEqual(1 + notes.Count + edgeVisuals.Count, viewport.Children.Count,
                    "The 3D scene should contain one light group, one visual per note, and one visual per edge.");

                noteList.SelectedIndex = 0;
                StringAssert.Contains(selectedContent.Text, notes[0].Content);
                StringAssert.Contains(selectedContent.Text, "العلاقات التي حددتها");
                StringAssert.Contains(selectedContent.Text, notes[1].Title);
                Assert.AreEqual(TextTrimming.None, selectedContent.TextTrimming,
                    "Long note content must remain available through the scroll viewer rather than being trimmed.");

                var emptyWindow = new LoreWebWindow(
                    Array.Empty<MemoryNote>(),
                    isArabic: false,
                    _ => { },
                    _ => true,
                    _ => true,
                    (_, _, _) => true,
                    (_, _) => true);
                Assert.AreEqual(0, GetField<ListBox>(emptyWindow, "_noteList").Items.Count);
                Assert.AreEqual(0, GetField<Dictionary<ModelVisual3D, (string FromId, string ToId)>>(emptyWindow, "_edgeVisuals").Count);
                Assert.AreEqual(1, GetField<Viewport3D>(emptyWindow, "_viewport").Children.Count,
                    "An empty map should still render its light group without throwing.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
            Assert.Fail("Lore Web WPF construction test did not finish within 30 seconds.");
        if (failure is not null)
            Assert.Fail("Lore Web test failed: " + failure);
    }

    private const int MemoryServiceRelationshipLimit = 12;

    private static T GetField<T>(object instance, string name) where T : class =>
        typeof(LoreWebWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new AssertFailedException($"Lore Web field '{name}' was not found.");

    private static (string FromId, string ToId) NormalizeEdge(string left, string right) =>
        string.CompareOrdinal(left, right) < 0 ? (left, right) : (right, left);
}
