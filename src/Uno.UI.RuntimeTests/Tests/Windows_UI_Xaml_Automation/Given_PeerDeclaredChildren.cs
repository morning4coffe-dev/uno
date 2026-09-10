#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
public class Given_PeerDeclaredChildren
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Initial_Tree_Excludes_Retained_Branch(bool enableBeforeAttach)
	{
#if __SKIA__
		var covered = new Button { Content = "Covered action" };
		var active = new Button { Content = "Active action" };
		var first = new PageRoot { Children = { covered } };
		var second = new PageRoot { Children = { active } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		try
		{
			if (enableBeforeAttach)
			{
				EnableAccessibilityThroughDom();
			}
			await UITestHelper.Load(boundary);
			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(active) && !SemanticElementExists(covered));
			Assert.IsTrue(first.IsLoaded && second.IsLoaded);
			Assert.AreEqual(Visibility.Visible, first.Visibility);
			Assert.AreEqual("button", GetSemanticElementTagName(active));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Live_Boundary_Switches_And_Raw_Containers_Keep_Children(bool structureEvent)
	{
#if __SKIA__
		var covered = new Button { Content = "Covered action" };
		var active = new Button { Content = "Active action" };
		var raw = new Border { Child = active };
		AutomationProperties.SetAccessibilityView(raw, AccessibilityView.Raw);
		var first = new PageRoot { Children = { covered } };
		var second = new PageRoot { Children = { raw } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(boundary);
			await UITestHelper.WaitFor(() => SemanticElementExists(active) && !SemanticElementExists(covered));
			Assert.IsFalse(SemanticElementExists(raw));

			var addedAction = new Button { Content = "New excluded branch action" };
			var addedRoot = new PageRoot { Children = { addedAction } };
			boundary.Children.Add(addedRoot);
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(addedAction));
			boundary.Children.Remove(addedRoot);
			second.Children.Insert(0, addedRoot);
			await UITestHelper.WaitFor(() => SemanticElementExists(addedAction));
			AssertUnique(addedAction);
			AssertPrecedes(addedAction, active);

			var dynamic = new Button { Content = "Dynamic covered action" };
			first.Children.Add(dynamic);
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(dynamic));
			first.Visibility = Visibility.Collapsed;
			first.Visibility = Visibility.Visible;
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(dynamic), "Geometry restoration must honor the peer boundary.");

			boundary.Select(first, structureEvent);
			await UITestHelper.WaitFor(() => SemanticElementExists(covered) && SemanticElementExists(dynamic) && !SemanticElementExists(active));
			var queries = boundary.Peer.ChildrenQueryCount;
			boundary.Select(second, structureEvent);
			boundary.Select(first, structureEvent);
			boundary.Select(second, structureEvent);
			await UITestHelper.WaitFor(() => SemanticElementExists(active) && !SemanticElementExists(dynamic));
			Assert.AreEqual(1, boundary.Peer.ChildrenQueryCount - queries, "Repeated invalidations must read the final children once.");
			AssertUnique(active);

			boundary.Select(first, structureEvent);
			TestServices.WindowHelper.WindowContent = null;
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(covered));
			Assert.IsFalse(SemanticElementExists(active));
			await UITestHelper.Load(boundary);
			await UITestHelper.WaitFor(() => SemanticElementExists(dynamic) && !SemanticElementExists(active));
			AssertUnique(dynamic);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Coalesced_Insertions_Keep_Final_Visual_Order()
	{
#if __SKIA__
		var first = new Button { Content = "First existing action" };
		var last = new Button { Content = "Last existing action" };
		var panel = new PageRoot { Children = { first, last } };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(panel);
			await UITestHelper.WaitFor(() => SemanticElementExists(first) && SemanticElementExists(last));
			var earlier = new Button { Content = "Earlier inserted action" };
			var later = new Button { Content = "Later inserted action" };
			var middle = new Button { Content = "Middle inserted action" };
			var transparent = new Border { Child = new StackPanel { Children = { earlier, later } } };
			panel.Children.Add(transparent);
			panel.Children.Insert(0, middle);
			panel.Children.Remove(transparent);
			panel.Children.Insert(0, transparent);
			await UITestHelper.WaitFor(() => SemanticElementExists(earlier) &&
				SemanticElementExists(later) && SemanticElementExists(middle));
			Assert.IsFalse(SemanticElementExists(transparent));
			AssertPrecedes(earlier, later);
			AssertPrecedes(later, middle);
			AssertPrecedes(middle, first);
			AssertPrecedes(first, last);
			AssertUnique(earlier);
			AssertUnique(later);
			AssertUnique(middle);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Reincluded_Branch_Precedes_Retained_Later_Branch(bool structureEvent)
	{
#if __SKIA__
		var earlier = new Button { Content = "Earlier restored action" };
		var later = new Button { Content = "Later restored action" };
		var retained = new Button { Content = "Retained action" };
		var first = new PageRoot { Children = { earlier, new Border { Child = later } } };
		var second = new PageRoot { Children = { retained } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(boundary);
			await UITestHelper.WaitFor(() => SemanticElementExists(retained) && !SemanticElementExists(earlier));
			boundary.IncludeAllChildren = true;
			if (structureEvent)
			{
				boundary.Peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
			}
			else
			{
				boundary.Peer.InvalidatePeer();
			}
			await UITestHelper.WaitFor(() => SemanticElementExists(earlier) && SemanticElementExists(later));
			Assert.IsTrue(first.IsLoaded && second.IsLoaded);
			AssertPrecedes(earlier, later);
			AssertPrecedes(later, retained);
			AssertUnique(retained);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(false, true)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Raw_Item_Container_Branch_Retains_Order(bool useRepeater, bool insertBefore)
	{
#if __SKIA__
		var earlier = new Button { Content = "Earlier raw-container action", Height = 40 };
		var later = new Button { Content = "Later raw-container action", Height = 40 };
		var retained = new Button { Content = "Retained action" };
		FrameworkElement items;
		Func<int> realizedCount;
		if (useRepeater)
		{
			var repeater = new ItemsRepeater { ItemsSource = new[] { earlier, later }, Width = 300, Height = 160 };
			items = repeater;
			realizedCount = () => repeater.Children.Count;
		}
		else
		{
			var list = new GuardedListView { ItemsSource = new[] { earlier, later }, Width = 300, Height = 160 };
			items = list;
			realizedCount = () => list.MaterializedContainers.Count();
		}
		AutomationProperties.SetAccessibilityView(items, AccessibilityView.Raw);
		var first = new PageRoot { Children = { items } };
		AutomationProperties.SetAccessibilityView(first, AccessibilityView.Raw);
		var second = new PageRoot { Children = { retained } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(boundary);
			await UITestHelper.WaitFor(() => earlier.IsLoaded && later.IsLoaded && SemanticElementExists(retained));
			Assert.IsTrue(realizedCount() is >= 2 and <= 100);
			Assert.IsFalse(SemanticElementExists(earlier));
			Assert.IsFalse(SemanticElementExists(later));
			if (insertBefore)
			{
				boundary.Children.Remove(first);
				second.Children.Insert(0, first);
			}
			else
			{
				boundary.IncludeAllChildren = true;
				boundary.Peer.InvalidatePeer();
			}
			await UITestHelper.WaitFor(() => SemanticElementExists(earlier) && SemanticElementExists(later));
			Assert.IsFalse(SemanticElementExists(first));
			Assert.IsFalse(SemanticElementExists(items));
			AssertPrecedes(earlier, later);
			AssertPrecedes(later, retained);
			AssertUnique(earlier);
			AssertUnique(later);
			AssertUnique(retained);
			Assert.IsTrue(realizedCount() is >= 2 and <= 100);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Nested_Boundaries_Change_Independently()
	{
#if __SKIA__
		var background = new Button { Content = "Background action" };
		var firstAction = new Button { Content = "First nested action" };
		var secondAction = new Button { Content = "Second nested action" };
		var backgroundPage = new PageRoot { Children = { background } };
		var first = new PageRoot { Children = { firstAction } };
		var second = new PageRoot { Children = { secondAction } };
		var inner = new BoundaryPanel(first, second) { Selected = second };
		var foreground = new PageRoot { Children = { inner } };
		var outer = new BoundaryPanel(backgroundPage, foreground) { Selected = foreground };
		try
		{
			await UITestHelper.Load(outer);
			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(secondAction) && !SemanticElementExists(firstAction) && !SemanticElementExists(background));
			inner.Select(first);
			await UITestHelper.WaitFor(() => SemanticElementExists(firstAction) && !SemanticElementExists(secondAction));
			outer.Select(backgroundPage);
			await UITestHelper.WaitFor(() => SemanticElementExists(background) && !SemanticElementExists(firstAction));
			inner.Select(second);
			outer.Select(foreground);
			await UITestHelper.WaitFor(() => SemanticElementExists(secondAction) && !SemanticElementExists(background));
			AssertUnique(secondAction);
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Excluded_Virtualized_List_Is_Restored_Without_Enumerating_Item_Peers()
	{
#if __SKIA__
		var first = new PageRoot();
		var second = new PageRoot { Children = { new Button { Content = "Foreground action" } } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		var list = new GuardedListView
		{
			Width = 300,
			Height = 160,
			ItemsSource = Enumerable.Range(0, 100000).Select(index => $"Item {index}").ToArray(),
		};
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(boundary);
			first.Children.Add(list);
			await UITestHelper.WaitFor(() => list.ContainerFromIndex(0) is ListViewItem);
			var item = (ListViewItem)list.ContainerFromIndex(0);
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(list));
			Assert.IsFalse(SemanticElementExists(item));
			Assert.IsTrue(list.MaterializedContainers.Count() <= 100);

			boundary.Select(first);
			await UITestHelper.WaitFor(() => SemanticElementExists(list) && SemanticElementExists(item));
			AssertUnique(item);
			Assert.AreEqual("100000", GetSemanticAttribute(item, "aria-setsize"));
			Assert.IsTrue(list.MaterializedContainers.Count() <= 100);
			boundary.Select(second);
			await UITestHelper.WaitFor(() => !SemanticElementExists(list) && !SemanticElementExists(item));
			list.ItemsSource = new[] { "Replacement item" };
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(list));
			boundary.Select(first);
			await UITestHelper.WaitFor(() => list.ContainerFromIndex(0) is ListViewItem restored &&
				SemanticElementExists(restored) && GetSemanticAttribute(restored, "aria-setsize") == "1");
			AssertUnique((ListViewItem)list.ContainerFromIndex(0));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Excluded_Repeater_Restores_Only_Realized_Options()
	{
#if __SKIA__
		var first = new PageRoot();
		var second = new PageRoot { Children = { new Button { Content = "Foreground action" } } };
		var boundary = new BoundaryPanel(first, second) { Selected = second };
		var repeater = new ItemsRepeater
		{
			ItemsSource = Enumerable.Range(0, 100000).ToArray(),
			ItemTemplate = new DataTemplate(() => new Button { Content = "Repeated action", Height = 30 }),
		};
		var viewport = new ScrollViewer { Content = repeater, Width = 300, Height = 160 };
		AutomationProperties.SetName(repeater, "Repeated options");
		AutomationProperties.SetAccessibilityView(repeater, AccessibilityView.Control);
		try
		{
			EnableAccessibilityThroughDom();
			await UITestHelper.Load(boundary);
			first.Children.Add(viewport);
			await UITestHelper.WaitFor(() => repeater.TryGetElement(0) is not null);
			var item = repeater.TryGetElement(0);
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(repeater));
			Assert.IsFalse(SemanticElementExists(item));
			Assert.IsTrue(repeater.Children.Count <= 100);
			boundary.Select(first);
			await UITestHelper.WaitFor(() => SemanticElementExists(item));
			AssertUnique(item);
			Assert.AreEqual("100000", GetSemanticAttribute(item, "aria-setsize"));
			Assert.IsTrue(repeater.Children.Count <= 100);

			boundary.Select(second);
			await UITestHelper.WaitFor(() => !SemanticElementExists(repeater) && !SemanticElementExists(item));
			repeater.ItemsSource = new[] { 1, 2 };
			await UITestHelper.WaitForIdle();
			Assert.IsFalse(SemanticElementExists(repeater));
			boundary.Select(first);
			await UITestHelper.WaitFor(() => repeater.TryGetElement(0) is { } restored &&
				SemanticElementExists(restored) && GetSemanticAttribute(restored, "aria-setsize") == "2");
			AssertUnique(repeater.TryGetElement(0));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Declared_Peer_Has_No_Unambiguous_Child_Owner(bool parentOwned)
	{
#if __SKIA__
		var button = new Button { Content = "Must remain exposed" };
		var first = new PageRoot { Children = { button } };
		var boundary = new BoundaryPanel(first, new PageRoot()) { AmbiguousChildren = true, ParentOwnedChild = parentOwned };
		try
		{
			await UITestHelper.Load(boundary);
			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(button));
			boundary.Peer.InvalidatePeer();
			await UITestHelper.WaitForIdle();
			Assert.IsTrue(SemanticElementExists(button));
		}
		finally
		{
			TestServices.WindowHelper.WindowContent = null;
		}
#endif
	}

#if __SKIA__
	static void AssertPrecedes(UIElement earlier, UIElement later) =>
		Assert.AreEqual("true", InvokeBrowserJs($"((document.getElementById('{GetSemanticElementId(earlier)}').compareDocumentPosition(document.getElementById('{GetSemanticElementId(later)}')) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0).toString()"),
			"Semantic document order must match the final visual sibling order.");

	static void AssertUnique(UIElement element) =>
		Assert.AreEqual("1", InvokeBrowserJs($"document.querySelectorAll('[id=\"{GetSemanticElementId(element)}\"]').length.toString()"));

	sealed class PageRoot : StackPanel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
	}

	sealed class BoundaryPanel : Grid
	{
		BoundaryPeer? _peer;
		public BoundaryPanel(PageRoot first, PageRoot second)
		{
			Children.Add(first);
			Children.Add(second);
			Selected = second;
		}
		public PageRoot Selected { get; set; }
		public bool AmbiguousChildren { get; set; }
		public bool ParentOwnedChild { get; set; }
		public bool IncludeAllChildren { get; set; }
		public BoundaryPeer Peer => _peer ??= new BoundaryPeer(this);
		protected override AutomationPeer OnCreateAutomationPeer() => Peer;
		public void Select(PageRoot page, bool structureEvent = false)
		{
			Selected = page;
			if (structureEvent)
			{
				Peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
			}
			else
			{
				Peer.InvalidatePeer();
			}
		}
	}

	sealed class BoundaryPeer(BoundaryPanel owner) : FrameworkElementAutomationPeer(owner)
	{
		public int ChildrenQueryCount { get; private set; }
		protected override IList<AutomationPeer> GetChildrenCore()
		{
			ChildrenQueryCount++;
			if (owner.IncludeAllChildren)
			{
				return base.GetChildrenCore();
			}
			return owner.AmbiguousChildren
				? new List<AutomationPeer> { owner.ParentOwnedChild ? new FrameworkElementAutomationPeer(owner) : new OwnerlessPeer() }
				: new List<AutomationPeer> { FrameworkElementAutomationPeer.CreatePeerForElement(owner.Selected) };
		}
	}

	sealed class OwnerlessPeer : AutomationPeer
	{
	}

	sealed class GuardedListView : ListView
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new GuardedListPeer(this);
	}

	sealed class GuardedListPeer(GuardedListView owner) : FrameworkElementAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
		protected override IList<AutomationPeer> GetChildrenCore() =>
			throw new InvalidOperationException("Browser must not enumerate virtualized data-item peers.");
	}
#endif
}
