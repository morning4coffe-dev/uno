#if !IS_UNIT_TESTS
#nullable enable
using System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using ItemIndexPath = Uno.UI.IndexPath;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Uniform-cell virtualization for ungrouped sources. Auto dimensions are measured
/// from the first bound item; variable-height rows and section headers are not supported.
/// </summary>
partial class ItemsWrapGridLayout
{
	private Size _itemSize;
	private int _itemsPerLine = 1;
	private bool _hasItemSize;
	private double _measuredBreadth = double.NaN;

	private protected override bool HasUniformLines => true;

	private protected override double UniformLineExtent => GetExtent(_itemSize);

	private protected override void PrepareLayout(Size availableSize)
	{
		if (ItemsControl?.IsGrouping == true)
		{
			throw new NotSupportedException("Managed ItemsWrapGrid does not support grouped sources or sticky group headers.");
		}
		if (ItemsControl is ListViewBase { CanReorderItems: true })
		{
			throw new NotSupportedException("Managed ItemsWrapGrid does not support drag-and-drop reordering.");
		}

		var breadth = GetBreadth(availableSize);
		if (!_hasItemSize || breadth != _measuredBreadth || double.IsNaN(ItemWidth) || double.IsNaN(ItemHeight))
		{
			_measuredBreadth = breadth;
			_hasItemSize = false;
			ValidateDimension(ItemWidth, nameof(ItemWidth));
			ValidateDimension(ItemHeight, nameof(ItemHeight));
			if (MaximumRowsOrColumns == 0 || MaximumRowsOrColumns < -1)
			{
				throw new ArgumentOutOfRangeException(nameof(MaximumRowsOrColumns));
			}

			if (ItemsControl?.NumberOfItems is not > 0)
			{
				_itemSize = default;
				_itemsPerLine = 1;
				return;
			}

			var size = new Size(ItemWidth, ItemHeight);
			if (double.IsNaN(size.Width) || double.IsNaN(size.Height))
			{
				var prototype = Generator.DequeueViewForItem(0)
					?? throw new InvalidOperationException("Unable to create the first grid item.");
				var wasAttached = prototype.Parent is not null;
				try
				{
					var constraint = new Size(
						double.IsNaN(size.Width) ? (ScrollOrientation == Orientation.Vertical ? breadth : double.PositiveInfinity) : size.Width,
						double.IsNaN(size.Height) ? (ScrollOrientation == Orientation.Horizontal ? breadth : double.PositiveInfinity) : size.Height);
					MeasureView(prototype, constraint);
					size = new Size(
						double.IsNaN(size.Width) ? prototype.DesiredSize.Width : size.Width,
						double.IsNaN(size.Height) ? prototype.DesiredSize.Height : size.Height);
				}
				finally
				{
					Generator.ReleaseMeasuredView(prototype, 0, wasAttached);
				}
			}

			if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
			{
				throw new InvalidOperationException("Managed ItemsWrapGrid requires a positive finite uniform item size. Set ItemWidth and ItemHeight when the first item cannot determine its size.");
			}
			_itemSize = size;
			_hasItemSize = true;
		}

		var bySpace = double.IsFinite(breadth)
			? Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(breadth / GetBreadth(_itemSize))))
			: Math.Max(1, MaximumRowsOrColumns);
		_itemsPerLine = MaximumRowsOrColumns > 0 ? Math.Min(MaximumRowsOrColumns, bySpace) : bySpace;
	}

	private static void ValidateDimension(double value, string name)
	{
		if (!double.IsNaN(value) && (!double.IsFinite(value) || value <= 0))
		{
			throw new ArgumentOutOfRangeException(name, "An item dimension must be Auto (NaN) or a positive finite value.");
		}
	}

	private protected override Line CreateLine(GeneratorDirection fillDirection, double extentOffset, double availableBreadth, ItemIndexPath nextVisibleItem)
	{
		var first = nextVisibleItem.Row / _itemsPerLine * _itemsPerLine;
		var count = Math.Min(_itemsPerLine, ItemsControl!.NumberOfItems - first);
		var items = new (FrameworkElement container, ItemIndexPath index)[count];
		for (var column = 0; column < count; column++)
		{
			var index = first + column;
			var view = Generator.DequeueViewForItem(index)
				?? throw new InvalidOperationException($"Unable to create grid item {index}.");
			AddView(view, GeneratorDirection.Forward, first / _itemsPerLine * UniformLineExtent,
				column * GetBreadth(_itemSize), _itemSize);
			items[column] = (view, ItemIndexPath.FromRowSection(index, 0));
		}
		return new Line(first, items);
	}

	protected override int GetItemsPerLine() => _itemsPerLine;

	protected override Rect GetElementArrangeBounds(int elementIndex, Rect containerBounds, Size windowConstraint, Size finalSize)
		=> containerBounds;

	private protected override void ResetLayoutInfo()
	{
		_hasItemSize = false;
		_itemSize = default;
		_itemsPerLine = 1;
	}

	partial void OnItemWidthChangedPartial(double oldItemWidth, double newItemWidth) => InvalidateItemSize();

	partial void OnItemHeightChangedPartial(double oldItemHeight, double newItemHeight) => InvalidateItemSize();

	partial void OnMaximumRowsOrColumnsChangedPartial(int oldMaximumRowsOrColumns, int newMaximumRowsOrColumns) => InvalidateLayout();

	private void InvalidateItemSize()
	{
		_hasItemSize = false;
		InvalidateLayout();
	}
}
#endif
