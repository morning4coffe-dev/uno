#nullable enable

using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Uno.UI.Accessibility;

internal static class TemplateAutomationText
{
	internal static string? GetName(FrameworkElement root)
	{
		StringBuilder? text = null;
		Append(root, true, ref text);
		return text?.ToString();
	}

	private static void Append(UIElement element, bool isRoot, ref StringBuilder? text)
	{
		if (element.Visibility == Visibility.Collapsed ||
			(!isRoot && element is ButtonBase or TextBox or RangeBase or Selector) ||
			(!isRoot && element is Control { IsTabStop: true }))
		{
			return;
		}

		if (AutomationProperties.GetAccessibilityView(element) != AccessibilityView.Raw)
		{
			var name = AutomationProperties.GetName(element);
			if (string.IsNullOrWhiteSpace(name) && AutomationProperties.GetLabeledBy(element) is UIElement label &&
				!ReferenceEquals(label, element))
			{
				name = label.GetOrCreateAutomationPeer()?.GetName();
			}
			if (string.IsNullOrWhiteSpace(name) && element is TextBlock textBlock)
			{
				name = textBlock.Text;
			}
			if (!string.IsNullOrWhiteSpace(name))
			{
				text ??= new StringBuilder();
				if (text.Length > 0)
				{
					text.Append(", ");
				}
				text.Append(name);
				return;
			}
		}

		foreach (var child in element.GetChildren())
		{
			Append(child, false, ref text);
		}
	}
}
