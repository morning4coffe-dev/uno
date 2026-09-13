#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Accessibility;
using Uno.UI.DataBinding;
using Uno.UI.Runtime.Skia;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
public class Given_SemanticNameRefreshQueue
{
	[TestInitialize]
	public void Init() => UnitTestsApp.App.EnsureApplication();

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Descendant_Name_Changes_Then_Current_Ancestor_Is_Queued(bool virtualized)
	{
		var text = new TextBlock { Text = "Consumed from packages" };
		var owner = new PeerContentControl { Content = text };
		var semantic = new Dictionary<IntPtr, IntPtr>();
		var realized = new HashSet<IntPtr>();
		if (virtualized)
		{
			realized.Add(owner.Visual.Handle);
		}
		else
		{
			semantic.Add(owner.Visual.Handle, IntPtr.Zero);
		}
		var queue = new SemanticNameRefreshQueue(semantic, realized.Contains, _ => true);
		try
		{
			// Parent linkage only: no template, layout, native window or loaded visual tree.
			text.SetParent(owner);
			Assert.AreEqual(text.Text, AriaMapper.ResolveLabel(owner.GetOrCreateAutomationPeer()!));
			text.Text = "MAUI event handled through the packaged Uno runtime";
			Assert.IsTrue(queue.Enqueue(text), "The semantic ancestor carrying the derived name must be dirtied.");
			text.Text = "Final text";
			Assert.IsFalse(queue.Enqueue(text), "Repeated changes coalesce until drain.");
			Assert.IsTrue(queue.TryDequeue(out var refreshed));
			Assert.AreSame(owner, refreshed);
			Assert.AreEqual("Final text", AriaMapper.ResolveLabel(refreshed.GetOrCreateAutomationPeer()!));
			Assert.IsFalse(queue.TryDequeue(out _));
		}
		finally
		{
			text.SetParent(null);
			owner.Content = null;
		}
	}

	[TestMethod]
	public void When_Owner_Is_Detached_Before_Drain_Then_No_Refresh()
	{
		var owner = new ContentControl();
		var attached = true;
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => attached);
		Assert.IsTrue(queue.Enqueue(owner));
		attached = false;
		Assert.IsFalse(queue.TryDequeue(out _));
	}

	[TestMethod]
	public void When_Semantic_Owner_Is_Removed_Before_Drain_Then_No_Refresh()
	{
		var owner = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		Assert.IsTrue(queue.Enqueue(owner));
		semantic.Clear();
		Assert.IsFalse(queue.TryDequeue(out _));
	}

	[TestMethod]
	public void When_Reparented_Then_Both_Owners_Refresh_From_Final_State()
	{
		var text = new TextBlock { Text = "Moving text" };
		var oldOwner = new PeerContentControl { Content = text };
		var newOwner = new PeerContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr>
		{
			[oldOwner.Visual.Handle] = IntPtr.Zero,
			[newOwner.Visual.Handle] = IntPtr.Zero
		};
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		try
		{
			text.SetParent(oldOwner);
			queue.Enqueue(text);
			oldOwner.Content = null;
			queue.Enqueue(oldOwner);
			newOwner.Content = text;
			text.SetParent(newOwner);
			queue.Enqueue(text);
			text.Text = "Moved and changed";
			var names = new Dictionary<UIElement, string?>();
			while (queue.TryDequeue(out var owner))
			{
				names.Add(owner, AriaMapper.ResolveLabel(owner.GetOrCreateAutomationPeer()!));
			}
			Assert.AreEqual(2, names.Count);
			Assert.IsTrue(string.IsNullOrEmpty(names[oldOwner]));
			Assert.AreEqual("Moved and changed", names[newOwner]);
		}
		finally
		{
			text.SetParent(null);
			oldOwner.Content = null;
			newOwner.Content = null;
		}
	}

	[TestMethod]
	public void When_Explicit_Name_Exists_Then_Reevaluation_Does_Not_Replace_It_With_Descendant_Text()
	{
		var text = new TextBlock { Text = "Text" };
		var owner = new PeerContentControl { Content = text };
		AutomationProperties.SetName(owner, "Authored name");
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		try
		{
			text.SetParent(owner);
			text.Text = "Changed text";
			queue.Enqueue(text);
			Assert.IsTrue(queue.TryDequeue(out var refreshed));
			Assert.AreEqual("Authored name", AriaMapper.ResolveLabel(refreshed.GetOrCreateAutomationPeer()!));
		}
		finally
		{
			text.SetParent(null);
			owner.Content = null;
		}
	}

	[TestMethod]
	public void When_Cancelled_Then_No_Stale_Target_Survives()
	{
		var first = new ContentControl();
		var second = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr>
		{
			[first.Visual.Handle] = IntPtr.Zero,
			[second.Visual.Handle] = IntPtr.Zero
		};
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		Assert.IsTrue(queue.Enqueue(first));
		queue.Remove(first);
		Assert.IsFalse(queue.TryDequeue(out _));
		Assert.IsTrue(queue.Enqueue(first));
		Assert.IsTrue(queue.Enqueue(second));
		queue.Clear();
		Assert.IsFalse(queue.TryDequeue(out _));
	}

	private sealed partial class PeerContentControl : ContentControl
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
	}

	[TestMethod]
	public void When_Source_Is_Excluded_Then_It_Cannot_Dirty_An_Included_Ancestor()
	{
		var text = new TextBlock();
		var owner = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, element => element != text);
		try
		{
			text.SetParent(owner);
			Assert.IsFalse(queue.Enqueue(text));
			Assert.IsFalse(queue.TryDequeue(out _));
		}
		finally
		{
			text.SetParent(null);
		}
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Hidden_Or_Excluded_Before_Drain_Then_Reattachment_Uses_Current_State(bool excluded)
	{
		var owner = new ContentControl();
		var included = true;
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false,
			element => included && element.Visibility == Visibility.Visible);
		Assert.IsTrue(queue.Enqueue(owner));
		if (excluded)
		{
			included = false;
		}
		else
		{
			owner.Visibility = Visibility.Collapsed;
		}
		Assert.IsFalse(queue.TryDequeue(out _));
		included = true;
		owner.Visibility = Visibility.Visible;
		Assert.IsTrue(queue.Enqueue(owner));
		Assert.IsTrue(queue.TryDequeue(out var refreshed));
		Assert.AreSame(owner, refreshed);
		Assert.IsFalse(queue.TryDequeue(out _));
	}

	[TestMethod]
	public void When_Inside_Realized_Item_Then_Ordinary_Ancestors_Refresh_Without_Visiting_Data_Peers()
	{
		var text = new TextBlock();
		var inner = new ContentControl();
		var item = new ListViewItem();
		var list = new ListView();
		var outside = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr>
		{
			[inner.Visual.Handle] = item.Visual.Handle,
			[list.Visual.Handle] = outside.Visual.Handle,
			[outside.Visual.Handle] = IntPtr.Zero
		};
		var visited = 0;
		var queue = new SemanticNameRefreshQueue(semantic, handle =>
		{
			visited++;
			return handle == item.Visual.Handle;
		}, _ => true);
		try
		{
			text.SetParent(inner);
			inner.SetParent(item);
			item.SetParent(list);
			list.SetParent(outside);
			queue.Enqueue(text);
			Assert.AreEqual(3, visited);
			var refreshed = new HashSet<UIElement>();
			while (queue.TryDequeue(out var owner))
			{
				refreshed.Add(owner);
			}
			Assert.IsTrue(refreshed.SetEquals(new UIElement[] { inner, item }));
		}
		finally
		{
			text.SetParent(null);
			inner.SetParent(null);
			item.SetParent(null);
			list.SetParent(null);
		}
	}

	[TestMethod]
	public void When_No_Naming_Ancestor_Then_Search_Is_Bounded_And_Allocation_Free_After_Warmup()
	{
		var chain = new Border[40];
		var visited = 0;
		var queue = new SemanticNameRefreshQueue(new Dictionary<IntPtr, IntPtr>(), _ =>
		{
			visited++;
			return false;
		}, _ => true);
		try
		{
			for (var i = 0; i < chain.Length; i++)
			{
				chain[i] = new Border();
				if (i > 0)
				{
					chain[i - 1].SetParent(chain[i]);
				}
			}
			Assert.IsFalse(queue.Enqueue(chain[0]));
			Assert.AreEqual(SemanticNameRefreshQueue.MaxAncestorDepth + 1, visited);
			for (var i = 0; i < 100; i++)
			{
				queue.Enqueue(chain[0]);
			}
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 100; i++)
			{
				queue.Enqueue(chain[0]);
			}
			Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
		}
		finally
		{
			foreach (var element in chain)
			{
				element?.SetParent(null);
			}
		}
	}

	[TestMethod]
	public void When_Refresh_Requeues_An_Owner_Then_It_Yields_To_Pending_Tree_Reconciliation()
	{
		var owner = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		var calls = 0;
		void Refresh(UIElement element)
		{
			calls++;
			if (calls == 1)
			{
				queue.Enqueue(element);
			}
		}
		queue.Enqueue(owner);
		queue.DrainBatch(Refresh);
		Assert.AreEqual(1, calls, "Reentrant work must wait for a new dispatcher turn, not drain recursively.");
		Assert.IsTrue(queue.HasPending);
		queue.DrainBatch(Refresh);
		Assert.AreEqual(2, calls);
		Assert.IsFalse(queue.HasPending);
	}

	[TestMethod]
	public void When_Repeatedly_Refreshing_One_Owner_Then_Queue_Operations_Do_Not_Allocate_After_Warmup()
	{
		var owner = new ContentControl();
		var semantic = new Dictionary<IntPtr, IntPtr> { [owner.Visual.Handle] = IntPtr.Zero };
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		Action<UIElement> refresh = static _ => { };
		for (var i = 0; i < 100; i++)
		{
			queue.Enqueue(owner);
			queue.DrainBatch(refresh);
		}
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 100; i++)
		{
			queue.Enqueue(owner);
			queue.DrainBatch(refresh);
		}
		Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[TestMethod]
	public void When_Actionable_Child_Changes_Then_Parent_Name_Still_Uses_Its_Own_Resolver()
	{
		var text = new TextBlock { Text = "Description" };
		var owner = new PeerContentControl { Content = text };
		var button = new Button { Content = "Action" };
		var semantic = new Dictionary<IntPtr, IntPtr>
		{
			[owner.Visual.Handle] = IntPtr.Zero,
			[button.Visual.Handle] = owner.Visual.Handle
		};
		var queue = new SemanticNameRefreshQueue(semantic, _ => false, _ => true);
		try
		{
			button.SetParent(owner);
			button.Content = "Changed action";
			queue.Enqueue(button);
			var names = new Dictionary<UIElement, string?>();
			while (queue.TryDequeue(out var refreshed))
			{
				names.Add(refreshed, AriaMapper.ResolveLabel(refreshed.GetOrCreateAutomationPeer()!));
			}
			Assert.AreEqual(2, names.Count);
			Assert.AreEqual("Description", names[owner]);
			Assert.AreEqual("Changed action", names[button]);
		}
		finally
		{
			button.SetParent(null);
			owner.Content = null;
		}
	}
}
