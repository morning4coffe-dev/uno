using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Uno.UI.RuntimeTests.Helpers;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
public class Given_ItemsWrapGrid
{
	private const int ItemCount = 100_000;

	[TestMethod]
	[DataRow(Orientation.Vertical, 1)]
	[DataRow(Orientation.Vertical, 2)]
	[DataRow(Orientation.Vertical, 3)]
	[DataRow(Orientation.Horizontal, 1)]
	[DataRow(Orientation.Horizontal, 2)]
	[DataRow(Orientation.Horizontal, 3)]
	public async Task When_Large_Uniform_Grid_Scrolls_And_Recycles(Orientation scrollOrientation, int span)
	{
		var source = Enumerable.Range(0, ItemCount).ToArray();
		var (grid, panel) = CreateGrid(scrollOrientation, span);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid);
			await Validate(grid, source);
			var initial = Realized(grid);
			var scroll = Descendants<ScrollViewer>(grid).Single();
			Assert.AreEqual(320, grid.ActualWidth, 1);
			Assert.AreEqual(240, grid.ActualHeight, 1);
			var lineCount = (ItemCount + span - 1) / span;
			Assert.AreEqual(lineCount * 80d, scrollOrientation == Orientation.Vertical ? scroll.ExtentHeight : scroll.ExtentWidth, 1);

			var offset = lineCount / 2 * 80d;
			scroll.ChangeView(scrollOrientation == Orientation.Horizontal ? offset : null,
				scrollOrientation == Orientation.Vertical ? offset : null, null, disableAnimation: true);
			await WindowHelper.WaitFor(() => panel.FirstVisibleIndex > 40_000);
			await Validate(grid, source);
			Assert.IsTrue(initial.Intersect(Realized(grid)).Any(), "A deep jump must reuse containers.");

			grid.ScrollIntoView(source[ItemCount - 1], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => grid.ContainerFromIndex(ItemCount - 1) is GridViewItem { ActualHeight: > 0, ActualWidth: > 0 });
			await Validate(grid, source);
			Assert.AreEqual(ItemCount - 1, panel.LastVisibleIndex);

			grid.SelectedItem = source[ItemCount - 1];
			grid.ScrollIntoView(source[0], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => panel.FirstVisibleIndex == 0);
			await Validate(grid, source);
			Assert.AreEqual(source[ItemCount - 1], grid.SelectedItem);
			Assert.IsTrue(Realized(grid).All(item => !item.IsSelected), "Recycled containers must not retain selection.");
			grid.ScrollIntoView(source[ItemCount - 1], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => grid.ContainerFromIndex(ItemCount - 1) is GridViewItem { IsSelected: true });
			await Validate(grid, source);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(0)]
	[DataRow(32)]
	public async Task When_Bounded_Grid_Grows_And_Replaces_100000_Items(int initialCount)
	{
		var source = new ObservableCollection<int>(Enumerable.Range(0, initialCount));
		var (grid, panel) = CreateGrid(Orientation.Vertical, 2, extent: 40);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid, element => element.IsLoaded);
			for (var i = initialCount; i < 65; i++)
			{
				source.Add(i);
			}
			await Validate(grid, source);
			var replacement = Enumerable.Range(0, ItemCount).ToArray();
			grid.ItemsSource = replacement;
			await Validate(grid, replacement);
			Assert.AreEqual(ItemCount, grid.Items.Count);
			Assert.AreSame(panel, grid.ItemsPanelRoot);
			source.Add(-1);
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(ItemCount, grid.Items.Count);
			grid.ItemsSource = Array.Empty<int>();
			await WindowHelper.WaitFor(() => Realized(grid).Length == 0);
			Assert.AreEqual(-1, panel.FirstVisibleIndex);
			grid.ItemsSource = replacement;
			await Validate(grid, replacement);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(Orientation.Vertical)]
	[DataRow(Orientation.Horizontal)]
	public async Task When_Grid_Mutations_And_Resize_Preserve_Anchor(Orientation scrollOrientation)
	{
		var source = new RangeCollection(Enumerable.Range(0, ItemCount));
		var (grid, panel) = CreateGrid(scrollOrientation, 3);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid);
			grid.ScrollIntoView(source[50_001], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => grid.ContainerFromIndex(50_001) is GridViewItem);
			await Validate(grid, source);
			var anchor = source[panel.FirstVisibleIndex];
			var oldPosition = Position(grid, source.IndexOf(anchor), scrollOrientation);
			source.InsertRange(0, new[] { -3, -2 });
			await Validate(grid, source);
			Assert.AreEqual(oldPosition, Position(grid, source.IndexOf(anchor), scrollOrientation), 1);
			source.RemoveAt(0);
			source.Move(3, 8);
			await Validate(grid, source);
			Assert.AreEqual(oldPosition, Position(grid, source.IndexOf(anchor), scrollOrientation), 1);
			grid.SelectedItem = anchor;

			panel.MaximumRowsOrColumns = 2;
			await Validate(grid, source);
			Assert.AreEqual(oldPosition, Position(grid, source.IndexOf(anchor), scrollOrientation), 1);
			Assert.AreEqual(anchor, grid.SelectedItem);
			if (scrollOrientation == Orientation.Vertical)
			{
				grid.Width = 280;
				panel.ItemWidth = 140;
			}
			else
			{
				grid.Height = 200;
				panel.ItemHeight = 100;
			}
			await Validate(grid, source);
			Assert.AreEqual(oldPosition, Position(grid, source.IndexOf(anchor), scrollOrientation), 1);

			var anchorIndex = source.IndexOf(anchor);
			source[anchorIndex] = -99;
			await Validate(grid, source);
			source.RemoveAt(anchorIndex);
			await Validate(grid, source);
			source.Clear();
			await WindowHelper.WaitFor(() => Realized(grid).Length == 0);
			Assert.AreEqual(-1, panel.FirstVisibleIndex);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	public async Task When_Grid_Unloads_And_Reattaches()
	{
		var source = Enumerable.Range(0, ItemCount).ToArray();
		var (grid, panel) = CreateGrid(Orientation.Vertical, 2);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid);
			grid.ScrollIntoView(source[50_000], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => panel.FirstVisibleIndex > 40_000);
			await Validate(grid, source);
			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			await UITestHelper.Load(grid);
			await Validate(grid, source);
			grid.ItemsSource = null;
			await WindowHelper.WaitFor(() => Realized(grid).Length == 0);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(Orientation.Vertical)]
	[DataRow(Orientation.Horizontal)]
	public async Task When_Auto_Cell_Size_And_Template_Change(Orientation scrollOrientation)
	{
		var source = Enumerable.Range(0, ItemCount).ToArray();
		var (grid, panel) = CreateGrid(scrollOrientation, 2);
		if (scrollOrientation == Orientation.Vertical)
		{
			panel.ItemHeight = double.NaN;
		}
		else
		{
			panel.ItemWidth = double.NaN;
		}
		grid.ItemTemplate = Template(40);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid);
			await Validate(grid, source);
			grid.ScrollIntoView(source[50_000], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => panel.FirstVisibleIndex > 40_000);
			await Validate(grid, source);
			grid.ItemTemplate = Template(60);
			await Validate(grid, source);
			var scroll = Descendants<ScrollViewer>(grid).Single();
			Assert.AreEqual(ItemCount / 2 * 60d, scrollOrientation == Orientation.Vertical ? scroll.ExtentHeight : scroll.ExtentWidth, 1);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}

		DataTemplate Template(double extent) => new(() =>
		{
			var text = new TextBlock { Name = "GridItemText" };
			text.SetBinding(TextBlock.TextProperty, new Binding());
			return new Border
			{
				Width = scrollOrientation == Orientation.Horizontal ? extent : double.NaN,
				Height = scrollOrientation == Orientation.Vertical ? extent : double.NaN,
				Child = text
			};
		});
	}

	[TestMethod]
	public async Task When_Deep_Scroll_Does_Not_Enumerate_Indexed_Source()
	{
		var source = new CountingList();
		var expected = Enumerable.Range(0, ItemCount).ToArray();
		foreach (var value in expected)
		{
			source.Add(value);
		}
		var (grid, _) = CreateGrid(Orientation.Vertical, 2);
		grid.ItemsSource = source;
		try
		{
			await UITestHelper.Load(grid);
			await Validate(grid, expected);
			source.Reads = 0;
			var enumerations = source.Enumerations;
			grid.ScrollIntoView(ItemCount - 1, ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => grid.ContainerFromIndex(ItemCount - 1) is GridViewItem);
			await Validate(grid, expected);
			Assert.AreEqual(enumerations, source.Enumerations);
			Assert.IsTrue(source.Reads < 1000, $"A viewport jump read {source.Reads} source entries.");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(Orientation.Vertical)]
	[DataRow(Orientation.Horizontal)]
	public async Task When_Auto_Measure_Preserves_Own_Container(Orientation orientation)
	{
		var (grid, panel) = CreateGrid(orientation, 2);
		grid.ItemTemplate = null;
		if (orientation == Orientation.Vertical)
		{
			panel.ItemHeight = double.NaN;
		}
		else
		{
			panel.ItemWidth = double.NaN;
		}
		var text = new TextBlock { Height = 80, Width = 80 };
		text.SetBinding(TextBlock.TextProperty, new Binding());
		var first = new GridViewItem { DataContext = "own first", Content = text };
		grid.ItemsSource = new[] { first, new GridViewItem { Content = "second" } };
		try
		{
			await UITestHelper.Load(grid);
			await WindowHelper.WaitForIdle();
			Assert.AreSame(first, grid.ContainerFromIndex(0));
			Assert.AreEqual("own first", first.DataContext);
			Assert.AreSame(text, first.Content);
			Assert.AreEqual("own first", text.Text);
			panel.InvalidateMeasure();
			await WindowHelper.WaitForIdle();
			Assert.AreEqual("own first", first.DataContext);
			Assert.AreEqual("own first", text.Text);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(Orientation.Vertical, false)]
	[DataRow(Orientation.Horizontal, false)]
	[DataRow(Orientation.Vertical, true)]
	[DataRow(Orientation.Horizontal, true)]
	public async Task When_Presenter_Insets_And_Decorations_Scroll_And_Remeasure(Orientation orientation, bool decorations)
	{
		var (grid, panel) = CreateGrid(orientation, 2);
		var source = Enumerable.Range(0, 100).ToArray();
		grid.ItemsSource = source;
		grid.Padding = orientation == Orientation.Vertical ? new Thickness(0, 40, 0, 40) : new Thickness(40, 0, 40, 0);
		if (decorations)
		{
			grid.Header = Decoration(60);
			grid.Footer = Decoration(100);
		}
		try
		{
			await UITestHelper.Load(grid);
			await Validate(grid, source);
			var scroll = Descendants<ScrollViewer>(grid).Single();
			var viewport = orientation == Orientation.Vertical ? scroll.ViewportHeight : scroll.ViewportWidth;
			var expectedExtent = 4000 + 80 + (decorations ? 160 : 0);
			Assert.AreEqual(expectedExtent, orientation == Orientation.Vertical ? scroll.ExtentHeight : scroll.ExtentWidth, 1);
			var endOffset = expectedExtent - viewport;
			scroll.ChangeView(orientation == Orientation.Horizontal ? endOffset : null,
				orientation == Orientation.Vertical ? endOffset : null, null, disableAnimation: true);
			await Validate(grid, source);
			Assert.AreEqual(endOffset, Offset(), 1);
			Assert.AreEqual(viewport - 40 - (decorations ? 100 : 0), Position(grid, 99, orientation) + 80, 1);

			panel.InvalidateMeasure();
			await Validate(grid, source);
			Assert.AreEqual(endOffset, Offset(), 1, "Remeasure must retain the full ScrollViewer end offset.");
			Assert.AreEqual(viewport - 40 - (decorations ? 100 : 0), Position(grid, 99, orientation) + 80, 1);

			grid.ScrollIntoView(source[50], ScrollIntoViewAlignment.Leading);
			await WindowHelper.WaitFor(() => grid.ContainerFromIndex(50) is GridViewItem);
			await Validate(grid, source);
			Assert.AreEqual(0, Position(grid, 50, orientation), 1, "Unrealized ScrollIntoView must include presenter offsets.");
			grid.ScrollIntoView(source[48], ScrollIntoViewAlignment.Leading);
			await Validate(grid, source);
			Assert.AreEqual(0, Position(grid, 48, orientation), 1, "Materialized ScrollIntoView must use the same coordinates.");

			double Offset() => orientation == Orientation.Vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}

		Border Decoration(double extent) => new()
		{
			Height = orientation == Orientation.Vertical ? extent : double.NaN,
			Width = orientation == Orientation.Horizontal ? extent : double.NaN
		};
	}

	[TestMethod]
	[DataRow(Orientation.Vertical)]
	[DataRow(Orientation.Horizontal)]
	public async Task When_Collapsed_First_Cell_Does_Not_Stop_Small_Scrolls(Orientation orientation)
	{
		var (grid, panel) = CreateGrid(orientation, 2);
		var items = Enumerable.Range(0, 100).Select(index => new GridViewItem { Content = index.ToString(CultureInfo.InvariantCulture) }).ToArray();
		items[0].Visibility = Visibility.Collapsed;
		grid.ItemTemplate = null;
		grid.ItemsSource = items;
		try
		{
			await UITestHelper.Load(grid);
			var scroll = Descendants<ScrollViewer>(grid).Single();
			for (var offset = 40; offset <= 1200; offset += 40)
			{
				scroll.ChangeView(orientation == Orientation.Horizontal ? offset : null,
					orientation == Orientation.Vertical ? offset : null, null, disableAnimation: true);
				await WindowHelper.WaitForIdle();
				var first = offset / 80 * 2;
				Assert.IsNotNull(grid.ContainerFromIndex(first), $"Small scroll to {offset} must realize item {first}.");
				Assert.AreEqual(first, panel.FirstVisibleIndex);
				Assert.IsTrue(Realized(grid).Length is > 0 and <= 100);
			}
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[DataRow(Orientation.Vertical, true)]
	[DataRow(Orientation.Horizontal, true)]
	[DataRow(Orientation.Vertical, false)]
	[DataRow(Orientation.Horizontal, false)]
	public async Task When_Empty_Grid_Decoration_Scroll_Survives_Measure(Orientation orientation, bool header)
	{
		var (grid, panel) = CreateGrid(orientation, 2);
		grid.ItemsSource = Array.Empty<int>();
		var decoration = new Border
		{
			Height = orientation == Orientation.Vertical ? 1000 : double.NaN,
			Width = orientation == Orientation.Horizontal ? 1000 : double.NaN
		};
		if (header)
		{
			grid.Header = decoration;
		}
		else
		{
			grid.Footer = decoration;
		}
		try
		{
			await UITestHelper.Load(grid, element => element.IsLoaded);
			await WindowHelper.WaitForIdle();
			var scroll = Descendants<ScrollViewer>(grid).Single();
			scroll.ChangeView(orientation == Orientation.Horizontal ? 200 : null,
				orientation == Orientation.Vertical ? 200 : null, null, disableAnimation: true);
			panel.InvalidateMeasure();
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(200, Offset(), 1, "An empty panel must not reset scrolling through its decorations.");
			panel.InvalidateMeasure();
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(200, Offset(), 1);
			Assert.AreEqual(0, grid.Items.Count);
			Assert.AreEqual(-1, panel.FirstVisibleIndex);

			grid.ItemsSource = new List<int>();
			await WindowHelper.WaitForIdle();
			Assert.AreEqual(0, Offset(), 1, "Explicit source replacement retains reset-to-start behavior.");

			double Offset() => orientation == Orientation.Vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	private static (GridView Grid, ItemsWrapGrid Panel) CreateGrid(Orientation scrollOrientation, int span, double extent = 80)
	{
		var panel = new ItemsWrapGrid
		{
			Orientation = scrollOrientation == Orientation.Vertical ? Orientation.Horizontal : Orientation.Vertical,
			MaximumRowsOrColumns = span,
			ItemWidth = scrollOrientation == Orientation.Vertical ? 320d / span : extent,
			ItemHeight = scrollOrientation == Orientation.Vertical ? extent : 240d / span
		};
		var grid = new GridView
		{
			Width = 320,
			Height = 240,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Padding = new Thickness(0),
			ItemsPanel = new ItemsPanelTemplate(() => panel),
			ItemContainerStyle = new Style(typeof(GridViewItem))
			{
				Setters =
				{
					new Setter(Control.PaddingProperty, new Thickness(0)),
					new Setter(FrameworkElement.MarginProperty, new Thickness(0)),
					new Setter(FrameworkElement.MinWidthProperty, 0d),
					new Setter(FrameworkElement.MinHeightProperty, 0d)
				}
			},
			ItemTemplate = new DataTemplate(() =>
			{
				var text = new TextBlock { Name = "GridItemText" };
				text.SetBinding(TextBlock.TextProperty, new Binding());
				return text;
			})
		};
		ScrollViewer.SetHorizontalScrollMode(grid, scrollOrientation == Orientation.Horizontal ? ScrollMode.Enabled : ScrollMode.Disabled);
		ScrollViewer.SetVerticalScrollMode(grid, scrollOrientation == Orientation.Vertical ? ScrollMode.Enabled : ScrollMode.Disabled);
		ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
		ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
		return (grid, panel);
	}

	private static async Task Validate(GridView grid, IList<int> source)
	{
		await WindowHelper.WaitForIdle();
		await WindowHelper.WaitFor(() =>
		{
			var items = Realized(grid);
			return items.Length > 0 && items.All(item => item.ActualWidth > 0 && item.ActualHeight > 0);
		});
		var realized = Realized(grid);
		Assert.IsTrue(realized.Length is > 0 and <= 100, $"Realized {realized.Length} containers for {source.Count} items.");
		var indices = new HashSet<int>();
		foreach (var item in realized)
		{
			var index = grid.IndexFromContainer(item);
			Assert.IsTrue(index >= 0 && index < source.Count, $"Invalid container index {index}.");
			Assert.IsTrue(indices.Add(index), $"Duplicate container index {index}.");
			Assert.AreEqual(source[index], item.DataContext);
			Assert.AreEqual(source[index].ToString(CultureInfo.InvariantCulture), Descendants<TextBlock>(item).Single(text => text.Name == "GridItemText").Text);
			var panel = (ItemsWrapGrid)grid.ItemsPanelRoot;
			var span = panel.MaximumRowsOrColumns;
			var point = item.TransformToVisual(panel).TransformPoint(default);
			var itemWidth = double.IsNaN(panel.ItemWidth) ? item.ActualWidth : panel.ItemWidth;
			var itemHeight = double.IsNaN(panel.ItemHeight) ? item.ActualHeight : panel.ItemHeight;
			Assert.AreEqual((panel.Orientation == Orientation.Horizontal ? index % span : index / span) * itemWidth, point.X, 1);
			Assert.AreEqual((panel.Orientation == Orientation.Horizontal ? index / span : index % span) * itemHeight, point.Y, 1);
		}
	}

	private static double Position(GridView grid, int index, Orientation orientation)
	{
		var container = grid.ContainerFromIndex(index) as GridViewItem;
		Assert.IsNotNull(container, $"Anchor {index} must remain realized.");
		var point = container.TransformToVisual(grid).TransformPoint(default);
		return orientation == Orientation.Vertical ? point.Y : point.X;
	}

	private static GridViewItem[] Realized(GridView grid)
		=> grid.ItemsPanelRoot.Children.OfType<GridViewItem>().Where(item => item.Visibility == Visibility.Visible).ToArray();

	private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			if (child is T match)
			{
				yield return match;
			}
			foreach (var descendant in Descendants<T>(child))
			{
				yield return descendant;
			}
		}
	}

	private sealed class RangeCollection : ObservableCollection<int>
	{
		public RangeCollection(IEnumerable<int> items) : base(items) { }

		public void InsertRange(int index, int[] values)
		{
			for (var offset = 0; offset < values.Length; offset++)
			{
				Items.Insert(index + offset, values[offset]);
			}
			OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, values, index));
		}
	}

	private sealed class CountingList : ArrayList
	{
		public int Reads { get; set; }
		public int Enumerations { get; private set; }

		public override object this[int index]
		{
			get
			{
				Reads++;
				return base[index];
			}
			set => base[index] = value;
		}

		public override IEnumerator GetEnumerator()
		{
			Enumerations++;
			return base.GetEnumerator();
		}
	}
}
