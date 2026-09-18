#if HAS_UNO
#nullable enable

using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.RuntimeTests.Helpers;
using Windows.Graphics.Display;

namespace Uno.UI.RuntimeTests.Tests.Windows_Graphics;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaMacOS)]
public class Given_DisplayInformation_MacOS
{
	private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

	[TestMethod]
	public void When_MainDisplayMetricsAreReported_Then_AspectMatchesCoreGraphics()
	{
		var display = CGMainDisplayID();
		var nativeWidth = CGDisplayPixelsWide(display);
		var nativeHeight = CGDisplayPixelsHigh(display);
		var expectedOrientation = nativeWidth >= nativeHeight
			? DisplayOrientations.Landscape
			: DisplayOrientations.Portrait;
		var actual = DisplayInformation.GetForCurrentView();

		Assert.AreEqual(expectedOrientation, actual.CurrentOrientation);
		Assert.AreEqual(
			nativeWidth >= nativeHeight,
			actual.ScreenWidthInRawPixels >= actual.ScreenHeightInRawPixels,
			"DisplayInformation must preserve the native screen aspect when converting backing dimensions.");
	}

	[DllImport(CoreGraphics)]
	private static extern uint CGMainDisplayID();

	[DllImport(CoreGraphics)]
	private static extern nuint CGDisplayPixelsWide(uint display);

	[DllImport(CoreGraphics)]
	private static extern nuint CGDisplayPixelsHigh(uint display);
}
#endif
