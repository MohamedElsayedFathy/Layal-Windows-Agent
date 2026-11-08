using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Layal.Controls;

/// <summary>
/// A TabControl whose visual template intentionally omits the tab strip.
/// The selected page still needs to expose its interactive children to UI Automation.
/// </summary>
public sealed class ContentOnlyTabControl : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ContentOnlyTabControlAutomationPeer(this);

    private sealed class ContentOnlyTabControlAutomationPeer : TabControlAutomationPeer
    {
        public ContentOnlyTabControlAutomationPeer(ContentOnlyTabControl owner)
            : base(owner)
        {
        }

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? new List<AutomationPeer>();
            if (Owner is not ContentOnlyTabControl owner)
                return children;

            owner.ApplyTemplate();
            if (owner.Template?.FindName("PART_SelectedContentHost", owner) is not ContentPresenter contentHost)
                return children;

            // WPF's TabItemAutomationPeer normally adds selected content to the UIA tree.
            // This content-only template has no visual TabPanel/TabItem peers, so provide
            // the selected ContentPresenter's interactive descendants directly.
            var contentHostPeer = new FrameworkElementAutomationPeer(contentHost);
            var contentChildren = contentHostPeer.GetChildren();
            if (contentChildren is null)
                return children;

            foreach (var child in contentChildren)
            {
                if (child is not null && !children.Contains(child))
                    children.Add(child);
            }

            return children;
        }
    }
}
