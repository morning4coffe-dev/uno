using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;

#if HAS_UNO
using Uno.UI.Runtime.Skia;
using static Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.WasmSemanticDomHelper;
#endif

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation
{
	/// <summary>
	/// Runtime tests for accessible list view behavior.
	/// Tests automation peer properties, selection pattern, and ARIA attribute mapping.
	/// </summary>
	[TestClass]
	public class Given_AccessibleListView
	{
		/// <summary>
		/// T067: Verifies that a list exposes item count via automation.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_List_Focused_Then_ItemCount_Announced()
		{
			// Arrange
			var listView = new ListView
			{
				ItemsSource = new List<string> { "Item 1", "Item 2", "Item 3" }
			};

			await UITestHelper.Load(listView);
			await TestServices.WindowHelper.WaitForIdle();

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(listView);

			// Assert
			Assert.IsNotNull(peer, "ListView should have an automation peer");
			Assert.AreEqual(AutomationControlType.List, peer.GetAutomationControlType());
		}

		/// <summary>
		/// T068: Verifies that list item position is reported via automation.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Arrow_Pressed_Then_Position_Announced()
		{
			// Arrange
			var listView = new ListView
			{
				ItemsSource = new List<string> { "Alpha", "Beta", "Gamma" },
				SelectionMode = ListViewSelectionMode.Single
			};

			await UITestHelper.Load(listView);
			await TestServices.WindowHelper.WaitForIdle();

			// Act - Select the second item
			listView.SelectedIndex = 1;
			await TestServices.WindowHelper.WaitForIdle();

			// Assert
			Assert.AreEqual(1, listView.SelectedIndex, "Selected index should be 1");
			Assert.AreEqual("Beta", listView.SelectedItem, "Selected item should be Beta");
		}

		/// <summary>
		/// T069: Verifies that pressing Space selects a list item.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_Space_Pressed_Then_Item_Selected()
		{
			// Arrange
			var listView = new ListView
			{
				ItemsSource = new List<string> { "A", "B", "C" },
				SelectionMode = ListViewSelectionMode.Single
			};

			await UITestHelper.Load(listView);
			await TestServices.WindowHelper.WaitForIdle();

			// Act
			listView.SelectedIndex = 0;
			await TestServices.WindowHelper.WaitForIdle();

			// Assert
			Assert.AreEqual(0, listView.SelectedIndex);
		}

		/// <summary>
		/// Verifies that ListView automation peer has correct control type.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_ListView_Created_Then_Has_List_ControlType()
		{
			// Arrange
			var listView = new ListView
			{
				ItemsSource = new List<string> { "A" }
			};
			await UITestHelper.Load(listView);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(listView);
			var controlType = peer?.GetAutomationControlType();

			// Assert
			Assert.AreEqual(AutomationControlType.List, controlType);
		}

#if HAS_UNO
		/// <summary>
		/// Verifies that AriaMapper correctly identifies ListView semantic element type.
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		public async Task When_ListView_Mapped_Then_SemanticElementType_Is_ListBox()
		{
			// Arrange
			var listView = new ListView
			{
				ItemsSource = new List<string> { "A" }
			};
			await UITestHelper.Load(listView);

			// Act
			var peer = FrameworkElementAutomationPeer.CreatePeerForElement(listView);
			var elementType = AriaMapper.GetSemanticElementType(peer);

			// Assert
			Assert.AreEqual(SemanticElementType.ListBox, elementType);
		}
#endif
#if __SKIA__

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_Template_Children_Removed_Then_Row_Name_Changes(bool removeSubtree)
		{
			var title = new TextBlock { Text = "First" };
			var status = new TextBlock { Text = "Active" };
			UIElement removable = removeSubtree ? new Border { Child = status } : status;
			var content = new StackPanel { Children = { title, removable } };
			var list = new ListView
			{
				Width = 320,
				Height = 240,
				ItemsSource = new[] { new object() },
				ItemTemplate = new DataTemplate(() => content)
			};
			try
			{
				await UITestHelper.Load(list);
				EnableAccessibilityThroughDom();
				await UITestHelper.WaitFor(() => list.ContainerFromIndex(0) is ListViewItem);
				var item = (ListViewItem)list.ContainerFromIndex(0);
				await UITestHelper.WaitFor(() => GetSemanticAttribute(item, "aria-label") == "First, Active");
				await UITestHelper.WaitForIdle();

				content.Children.Remove(removable);
				await UITestHelper.WaitFor(() => GetSemanticAttribute(item, "aria-label") == "First");
				content.Children.Clear();
				await UITestHelper.WaitFor(() => !SemanticElementHasAttribute(item, "aria-label"));
				status.Text = "Changed";
				if (removable is Border border)
				{
					border.Child = null;
				}
				content.Children.Add(status);
				await UITestHelper.WaitFor(() => GetSemanticAttribute(item, "aria-label") == "Changed");
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		/// <summary>
		/// T067/FR-016 (WASM DOM): a ListView emits a composite container with role="listbox". Under the roving
		/// tab model the container is not itself a tab stop (tabindex must not be "0").
		/// </summary>
		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_ListView_Then_Dom_Role_Is_Listbox_And_Not_A_Tab_Stop()
		{
			var listView = new ListView
			{
				ItemsSource = new List<string> { "Item 1", "Item 2", "Item 3" }
			};

			await UITestHelper.Load(listView);
			listView.GetOrCreateAutomationPeer();
			await TestServices.WindowHelper.WaitForIdle();

			EnableAccessibilityThroughDom();
			await UITestHelper.WaitFor(() => SemanticElementExists(listView), timeoutMS: 5000, message: "Timed out waiting for the listbox container semantic element to be created.");
			await UITestHelper.WaitForIdle();

			Assert.AreEqual("listbox", GetSemanticAttribute(listView, "role"), "A ListView must emit role=listbox on its container.");
			Assert.AreNotEqual("0", GetSemanticAttribute(listView, "tabindex"), "A composite listbox container must not be a tab stop (tabindex must not be \"0\"); the roving stop lives on the active item.");
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_Virtualized_Item_Contains_Action_Then_Action_Is_Exposed_And_Invokable()
		{
			Button action = null;
			var invocationCount = 0;
			var listView = new ListView
			{
				Width = 320,
				Height = 240,
				ItemsSource = new[] { new object() },
				ItemTemplate = new DataTemplate(() =>
				{
					action = new Button { Content = "Open details" };
					action.Click += (_, _) => invocationCount++;
					return new StackPanel
					{
						Children =
						{
							new TextBlock { Text = "Account 42" },
							action
						}
					};
				})
			};

			try
			{
				await UITestHelper.Load(listView);
				await UITestHelper.WaitFor(() => listView.ContainerFromIndex(0) is ListViewItem && action?.IsLoaded == true);
				var item = (ListViewItem)listView.ContainerFromIndex(0);

				EnableAccessibilityThroughDom();
				await UITestHelper.WaitFor(() => SemanticElementExists(item), timeoutMS: 5000, message: "Timed out waiting for the virtualized option.");
				await UITestHelper.WaitFor(() => action is not null && SemanticElementExists(action), timeoutMS: 5000, message: "Timed out waiting for the actionable template descendant.");

				await UITestHelper.WaitFor(
					() => GetSemanticAttribute(item, "aria-label") == "Account 42",
					timeoutMS: 5000,
					message: "The virtualized option did not refresh its name from the realized template.");
				Assert.AreEqual("button", GetSemanticElementTagName(action!), "The template action must retain button semantics.");
				Assert.AreNotEqual(
					GetSemanticElementId(item),
					InvokeBrowserJs($"(function(){{const e=document.getElementById('{GetSemanticElementId(action!)}');return e?.parentElement?.id||'';}})()"),
					"An interactive control must not be nested under role=option, whose descendants are flattened by accessibility APIs.");

				InvokeBrowserJs($"(function(){{document.getElementById('{GetSemanticElementId(action!)}')?.click();return 'ok';}})()");
				await UITestHelper.WaitFor(() => invocationCount == 1, timeoutMS: 5000, message: "The semantic button click did not reach the XAML Button.");
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		[TestMethod]
		[RunsOnUIThread]
		[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
		public async Task When_Default_Item_Renders_Text_Then_Option_Has_The_Same_Name()
		{
			var listView = new ListView
			{
				Width = 320,
				Height = 240,
				ItemsSource = new[] { new NamedItem("John Doe") }
			};

			try
			{
				await UITestHelper.Load(listView);
				await UITestHelper.WaitFor(() => listView.ContainerFromIndex(0) is ListViewItem);
				var item = (ListViewItem)listView.ContainerFromIndex(0);

				EnableAccessibilityThroughDom();
				await UITestHelper.WaitFor(
					() => GetSemanticAttribute(item, "aria-label") == "John Doe",
					timeoutMS: 5000,
					message: "The default item option did not match its visibly rendered text.");
			}
			finally
			{
				TestServices.WindowHelper.WindowContent = null;
			}
		}

		sealed class NamedItem(string name)
		{
			public override string ToString() => name;
		}

#endif

	}
}
