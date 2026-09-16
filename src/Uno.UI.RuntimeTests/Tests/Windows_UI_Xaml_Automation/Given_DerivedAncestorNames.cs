#nullable enable

using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;

#if HAS_UNO
using static Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.WasmSemanticDomHelper;
#endif

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation;

[TestClass]
[RunsOnUIThread]
public class Given_DerivedAncestorNames
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Absorbed_Text_Changes_Without_Ancestor_Invalidation(bool enableBeforeAttach)
	{
#if __SKIA__
		var text = new TextBlock { Text = "Consumed from packages" };
		var action = new Button { Content = "Independent action" };
		var panel = new PeerPanel { Children = { text, action } };
		try
		{
			if (enableBeforeAttach)
			{
				EnableAccessibilityThroughDom();
			}
			await UITestHelper.Load(panel);
			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text), "This is absorbed text, not the standalone paragraph path.");
			text.Text = "Intermediate";
			text.Text = "MAUI event handled through the packaged Uno runtime";
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text));
			Assert.AreEqual("button", GetSemanticElementTagName(action));
			Assert.AreEqual("Independent action", GetSemanticAttribute(action, "aria-label"));
			text.Text = "";
			await UITestHelper.WaitFor(() => !SemanticElementHasAttribute(panel, "aria-label"));
			text.Text = "Restored text";
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Body_Text_Is_Inside_NonScrollable_Pane_Then_Remains_Standalone()
	{
#if __SKIA__
		var text = new TextBlock { Text = "Consumed from packages" };
		var action = new Button { Content = "Tap" };
		var panel = new PanePeerPanel { Width = 320, Height = 120, Children = { text, action } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(panel);
			await UITestHelper.WaitFor(() => SemanticElementExists(action), timeoutMS: 5000,
				message: "Timed out waiting for the independent button control.");
			Assert.AreEqual("button", GetSemanticElementTagName(action));
			Assert.AreEqual("Tap", GetSemanticAttribute(action, "aria-label"));

			await UITestHelper.WaitFor(() => SemanticElementExists(text), timeoutMS: 5000,
				message: "Body text was absorbed by a role-less non-scrollable pane.");

			Assert.AreEqual("p", GetSemanticElementTagName(text));
			Assert.AreEqual(text.Text, GetSemanticTextContent(text));

			text.Text = "MAUI event handled through the packaged Uno runtime";
			await UITestHelper.WaitFor(() => GetSemanticTextContent(text) == text.Text, timeoutMS: 5000,
				message: "Timed out waiting for the body-text paragraph to follow Text.");

			Assert.AreEqual("Tap", GetSemanticAttribute(action, "aria-label"));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Body_Text_Is_Inside_Generic_Peer_Then_Remains_Standalone()
	{
#if __SKIA__
		var text = new TextBlock { Text = "Generic initial text" };
		var action = new Button { Content = "Generic action" };
		var panel = new GenericPeerPanel { Width = 320, Height = 120, Children = { text, action } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(panel);
			await UITestHelper.WaitFor(() => SemanticElementExists(action), timeoutMS: 5000,
				message: "Timed out waiting for the generic peer's independent button.");
			Assert.AreEqual("button", GetSemanticElementTagName(action));
			Assert.AreEqual("Generic action", GetSemanticAttribute(action, "aria-label"));

			await UITestHelper.WaitFor(() => SemanticElementExists(text), timeoutMS: 5000,
				message: "Body text was absorbed by a name-prohibited generic peer.");

			Assert.AreEqual("p", GetSemanticElementTagName(text));
			Assert.AreEqual(text.Text, GetSemanticTextContent(text));

			text.Text = "Generic updated text";
			await UITestHelper.WaitFor(() => GetSemanticTextContent(text) == text.Text, timeoutMS: 5000,
				message: "Timed out waiting for the generic peer's body-text paragraph to follow Text.");

			Assert.AreEqual("Generic action", GetSemanticAttribute(action, "aria-label"));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Absorbed_Text_Is_Reparented_Then_Both_Names_Refresh()
	{
#if __SKIA__
		var text = new TextBlock { Text = "Moving name" };
		var first = new PeerPanel { MinHeight = 40, Children = { text } };
		var second = new PeerPanel { MinHeight = 40 };
		var root = new StackPanel { Children = { first, second } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(root);
			await UITestHelper.WaitFor(() => GetSemanticAttribute(first, "aria-label") == text.Text);
			text.Text = "Before move";
			first.Children.Remove(text);
			second.Children.Add(text);
			text.Text = "After move";
			await UITestHelper.WaitFor(() => !SemanticElementHasAttribute(first, "aria-label") &&
				GetSemanticAttribute(second, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text));
			root.Children.Remove(second);
			text.Text = "Detached name";
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(second));
			Assert.IsFalse(SemanticElementExists(text));
			root.Children.Add(second);
			await UITestHelper.WaitFor(() => GetSemanticAttribute(second, "aria-label") == text.Text);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Authored_Name_Changes_Then_Body_Text_Membership_Follows()
	{
#if __SKIA__
		var text = new TextBlock { Text = "Description" };
		var panel = new PeerPanel { Children = { text } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(panel);
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text));
			AutomationProperties.SetName(panel, "Authored name");
			await UITestHelper.WaitFor(() => SemanticElementExists(text));
			text.Text = "Changed description";
			await UITestHelper.WaitFor(() => GetSemanticTextContent(text) == text.Text);
			Assert.AreEqual("p", GetSemanticElementTagName(text));
			Assert.IsFalse(SemanticElementHasAttribute(text, "aria-label"));
			Assert.AreEqual("Authored name", GetSemanticAttribute(panel, "aria-label"));
			AutomationProperties.SetName(panel, "");
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text &&
				!SemanticElementExists(text));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Absorbing_Owner_Is_Hidden_During_A_Pending_Change()
	{
#if __SKIA__
		var text = new TextBlock { Text = "Visible name" };
		var panel = new PeerPanel { Children = { text } };
		var root = new StackPanel { Children = { panel } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(root);
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			text.Text = "Pending change";
			panel.Visibility = Visibility.Collapsed;
			text.Text = "Hidden change";
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(panel));
			Assert.IsFalse(SemanticElementExists(text));
			panel.Visibility = Visibility.Visible;
			await UITestHelper.WaitFor(() => GetSemanticAttribute(panel, "aria-label") == text.Text);
			Assert.IsFalse(SemanticElementExists(text));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

#if __SKIA__
	private sealed partial class PeerPanel : StackPanel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new GroupAutomationPeer(this);
	}

	private sealed partial class GenericPeerPanel : StackPanel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
	}

	private sealed partial class PanePeerPanel : StackPanel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new PaneAutomationPeer(this);
	}

	private sealed class PaneAutomationPeer(PanePeerPanel owner) : FrameworkElementAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
		protected override bool IsControlElementCore() => true;
	}

	private sealed class GroupAutomationPeer(PeerPanel owner) : FrameworkElementAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
	}
#endif
}
