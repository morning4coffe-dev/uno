#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Uno.UI.RuntimeTests.BrowserInput;

public sealed partial class App : Application
{
	private Window? _window;

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		Resources.MergedDictionaries.Add(new XamlControlsResources());
		var status = new TextBlock { Text = "Consumed from owning source", FontSize = 24 };
		var panel = new DerivedNamePanel { Children = { status } };
		var action = new Button { Content = "Handle event", MinWidth = 180, MinHeight = 48 };
		AutomationProperties.SetAutomationId(panel, "derived-name-panel");
		AutomationProperties.SetAutomationId(action, "handle-event");
		action.Click += (_, _) => status.Text = "Event handled through the owning source Uno runtime";
		_window = new Window
		{
			Title = "Derived ancestor name source consumer",
			Content = new StackPanel
			{
				Margin = new Thickness(24),
				Spacing = 16,
				Children = { panel, action }
			}
		};
		_window.Activate();
	}

	private sealed partial class DerivedNamePanel : StackPanel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);
	}

	private sealed class PanelPeer(DerivedNamePanel owner) : FrameworkElementAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
		protected override string GetClassNameCore() => "DerivedNamePanel";
		protected override bool IsKeyboardFocusableCore() => false;
	}
}
