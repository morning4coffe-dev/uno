#if !IS_UNIT_TESTS
using Windows.Foundation;

namespace Microsoft.UI.Xaml.Controls;

public partial class ItemsWrapGrid
{
	public int FirstCacheIndex => _layout.FirstMaterializedIndex;

	public int LastCacheIndex => _layout.LastMaterializedIndex;

	protected override Size MeasureOverride(Size availableSize) => _layout.MeasureOverride(availableSize);

	protected override Size ArrangeOverride(Size finalSize) => _layout.ArrangeOverride(finalSize);
}
#endif
