#nullable enable

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.Skia;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
public class Given_VirtualizedSemanticRegionState
{
	[TestMethod]
	public void When_Reindexed_Then_Late_Clearing_Does_Not_Remove_Reused_Handle()
	{
		using var state = new VirtualizedSemanticRegionState();
		Assert.IsTrue(state.TryRealize((IntPtr)1, 0, out _));
		Assert.IsTrue(state.TryRealize((IntPtr)1, 4, out _));
		Assert.IsFalse(state.TryUnrealize((IntPtr)1, 0));
		Assert.IsTrue(state.TryGetIndex((IntPtr)1, out var index));
		Assert.AreEqual(4, index);
		Assert.IsTrue(state.TryUnrealize((IntPtr)1, 4));
		Assert.IsFalse(state.Contains((IntPtr)1));
	}

	[TestMethod]
	public void When_Index_Replaced_Then_Late_Clearing_Preserves_New_Item()
	{
		using var state = new VirtualizedSemanticRegionState();
		state.TryRealize((IntPtr)1, 0, out _);
		state.TryRealize((IntPtr)2, 0, out var displaced);
		Assert.AreEqual((IntPtr)1, displaced);
		Assert.IsFalse(state.TryUnrealize((IntPtr)1, 0));
		Assert.IsTrue(state.Contains((IntPtr)2));
	}

	[TestMethod]
	public void When_Disposed_Then_Unsubscribes_Once_And_Rejects_Late_Callbacks()
	{
		var state = new VirtualizedSemanticRegionState();
		var unsubscribed = 0;
		state.Own(() => unsubscribed++);
		state.TryRealize((IntPtr)1, 0, out _);
		state.Dispose();
		state.Dispose();
		Assert.AreEqual(1, unsubscribed);
		Assert.IsFalse(state.Contains((IntPtr)1));
		Assert.IsFalse(state.TryRealize((IntPtr)1, 0, out _));
		Assert.IsFalse(state.TryUnrealize((IntPtr)1, 0));
		state.Own(() => unsubscribed++);
		Assert.AreEqual(2, unsubscribed, "Late subscription ownership must dispose immediately.");
	}
}
