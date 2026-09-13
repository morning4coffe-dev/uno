#nullable enable

using System;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;
using static Private.Infrastructure.TestServices;

#if HAS_UNO
using static Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.WasmSemanticDomHelper;
#endif

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

public partial class Given_ContentDialog
{
	[TestMethod]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Nested_Accessibility_Scopes_Close_In_Either_Order(bool parentFirst, bool closePopup)
	{
#if __SKIA__
		var background = new TextBlock { Text = "Background" };
		var parent = new MyContentDialog { Title = "Parent", Content = "Parent content", CloseButtonText = "Close parent" };
		var child = new MyContentDialog { Title = "Child", Content = "Child content", CloseButtonText = "Close child" };
		try
		{
			await UITestHelper.Load(background);
			EnableAccessibilityThroughDom();
			SetXamlRootForIslandsOrWinUI(parent);
			SetXamlRootForIslandsOrWinUI(child);
			var parentShowing = parent.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == parent.Visual.Handle);
			var parentScope = ManagedModalScope();
			Assert.IsNotNull(parentScope);
			AssertModalState(parent.Visual.Handle);
			var childShowing = child.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == child.Visual.Handle);
			var childScope = ManagedModalScope();
			Assert.IsNotNull(childScope);
			Assert.AreSame(parentScope, ScopeProperty(childScope, "ParentScope"));
			AssertModalState(child.Visual.Handle);

			if (parentFirst)
			{
				if (closePopup)
				{
					parent._popup.IsOpen = false;
				}
				else
				{
					parent.Hide();
				}
				await parentShowing;
				AssertModalState(child.Visual.Handle);
				Assert.IsNull(ScopeProperty(childScope, "ParentScope"), "The surviving child must unlink the closed parent.");
				Assert.AreEqual(ScopeProperty(parentScope, "TriggerHandle"), ScopeProperty(childScope, "TriggerHandle"));
				child.Hide();
				await childShowing;
			}
			else
			{
				child.Hide();
				await childShowing;
				AssertModalState(parent.Visual.Handle);
				parent.Hide();
				await parentShowing;
			}
			AssertModalState(IntPtr.Zero);
			Assert.AreEqual(false, ScopeProperty(parentScope, "IsActive"));
			Assert.AreEqual(false, ScopeProperty(childScope, "IsActive"));
			await AnnounceBackgroundAfterModalClose(background);

			var reopened = parent.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == parent.Visual.Handle);
			Assert.IsNull(ScopeProperty(ManagedModalScope()!, "ParentScope"));
			parent.Hide();
			await reopened;
			AssertModalState(IntPtr.Zero);
			await AnnounceBackgroundAfterModalClose(background);
		}
		finally
		{
			child.Hide();
			parent.Hide();
			VisualTreeHelper.CloseAllPopups(WindowHelper.XamlRoot);
			WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Nested_Accessibility_Parent_Closes_Then_Background_Live_Event_Is_Announced()
	{
#if __SKIA__
		var background = new TextBlock { Text = "Background" };
		var parent = new MyContentDialog { Title = "Parent", CloseButtonText = "Close parent" };
		var child = new MyContentDialog { Title = "Child", CloseButtonText = "Close child" };
		try
		{
			await UITestHelper.Load(background);
			EnableAccessibilityThroughDom();
			SetXamlRootForIslandsOrWinUI(parent);
			SetXamlRootForIslandsOrWinUI(child);
			var parentShowing = parent.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == parent.Visual.Handle);
			var childShowing = child.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == child.Visual.Handle);
			parent.Hide();
			await parentShowing;
			child.Hide();
			await childShowing;
			await AnnounceBackgroundAfterModalClose(background);
			AssertModalState(IntPtr.Zero);
		}
		finally
		{
			child.Hide();
			parent.Hide();
			VisualTreeHelper.CloseAllPopups(WindowHelper.XamlRoot);
			WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Nested_Accessibility_Closed_Callback_Closes_Child()
	{
#if __SKIA__
		var background = new TextBlock { Text = "Background" };
		var parent = new MyContentDialog { Title = "Parent", CloseButtonText = "Close parent" };
		var child = new MyContentDialog { Title = "Child", CloseButtonText = "Close child" };
		void CloseChild(ContentDialog sender, ContentDialogClosedEventArgs args) => child.Hide();
		parent.Closed += CloseChild;
		try
		{
			await UITestHelper.Load(background);
			EnableAccessibilityThroughDom();
			SetXamlRootForIslandsOrWinUI(parent);
			SetXamlRootForIslandsOrWinUI(child);
			var parentShowing = parent.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == parent.Visual.Handle);
			var childShowing = child.ShowAsync().AsTask();
			await WindowHelper.WaitFor(() => BrowserModalHandle() == child.Visual.Handle);
			parent.Hide();
			await parentShowing;
			await childShowing;
			AssertModalState(IntPtr.Zero);
			await AnnounceBackgroundAfterModalClose(background);
		}
		finally
		{
			parent.Closed -= CloseChild;
			child.Hide();
			parent.Hide();
			VisualTreeHelper.CloseAllPopups(WindowHelper.XamlRoot);
			WindowHelper.WindowContent = null;
		}
#endif
	}

#if __SKIA__
	private static object BrowserAccessibility()
	{
		var type = Type.GetType("Uno.UI.Runtime.Skia.WebAssemblyAccessibility, Uno.UI.Runtime.Skia.WebAssembly.Browser", throwOnError: true)!;
		return type.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	}

	private static object? ManagedModalScope()
		=> BrowserAccessibility().GetType().GetProperty("ActiveModalScope", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(BrowserAccessibility());

	private static object? ScopeProperty(object scope, string name)
		=> scope.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope);

	private static IntPtr BrowserModalHandle()
		=> new(long.Parse(InvokeBrowserJs("Uno.UI.Runtime.Skia.FocusTrap.getActiveTrapHandle().toString()"), CultureInfo.InvariantCulture));

	private static void AssertModalState(IntPtr expected)
	{
		var owner = BrowserAccessibility();
		var active = ManagedModalScope();
		if (expected == IntPtr.Zero)
		{
			Assert.IsNull(active, "Managed ActiveModalScope must be cleared.");
		}
		else
		{
			Assert.IsNotNull(active);
			Assert.AreEqual(expected, ScopeProperty(active, "ModalHandle"));
		}
		var manager = owner.GetType().GetField("_liveRegionManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
		Assert.AreEqual(expected, manager.GetType().GetProperty("ActiveModalHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));
		Assert.AreEqual(expected, BrowserModalHandle(), "Managed and JS modal stacks must agree.");
	}

	private static async Task AnnounceBackgroundAfterModalClose(TextBlock background)
	{
		AutomationProperties.SetLiveSetting(background, AutomationLiveSetting.Off);
		var message = "Background after modal " + Guid.NewGuid().ToString("N");
		background.Text = message;
		await WindowHelper.WaitForIdle();
		AutomationProperties.SetLiveSetting(background, AutomationLiveSetting.Assertive);
		string Content() => InvokeBrowserJs("document.getElementById('uno-live-region-assertive').textContent");
		Assert.AreNotEqual(message, Content());
		var peer = background.GetOrCreateAutomationPeer();
		Assert.IsNotNull(peer);
		peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
		await WindowHelper.WaitFor(() => Content() == message, message: "Background LiveRegionChanged was suppressed after every modal closed.");
	}
#endif
}
