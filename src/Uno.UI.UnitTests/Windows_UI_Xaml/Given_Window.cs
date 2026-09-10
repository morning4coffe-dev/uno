using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;

namespace Uno.UI.Tests.Windows_UI_Xaml;

[TestClass]
public class Given_Window
{
	[TestInitialize]
	public void Init()
	{
		UnitTestsApp.App.EnsureApplication();
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(false, true)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	public void When_Closed_Callback_Clears_Content_Native_Wrapper_Is_Closed(bool clearContent, bool reenterClose)
	{
		var window = new Window { Content = new Microsoft.UI.Xaml.Controls.Border() };
		var native = (UnitTestsApp.TestNativeWindowWrapper)window.NativeWrapper;
		var closedCount = 0;
		window.Closed += (_, _) =>
		{
			closedCount++;
			if (clearContent)
			{
				window.Content = null;
			}
			if (reenterClose)
			{
				window.Close();
			}
		};
		try
		{
			window.Close();
			Assert.AreEqual(1, closedCount);
			Assert.AreEqual(1, native.CloseCount);
			Assert.AreSame(native, window.NativeWrapper);
			Assert.IsNull(window.Content);
			window.Close();
			Assert.AreEqual(1, closedCount);
			Assert.AreEqual(1, native.CloseCount);
		}
		finally
		{
			window.Close();
		}
	}

	[TestMethod]
	[Ignore("https://github.com/unoplatform/uno/issues/17399 — relied on the mock-only Window.CleanupCurrentForTestsOnly helper that resets the static Window.Current. The Skia window lifecycle does not expose an equivalent reset hook.")]
	public void New_Window_Becomes_Current()
	{
		var window = new Microsoft.UI.Xaml.Window();
		window.Activate();
		Assert.AreEqual(window, Microsoft.UI.Xaml.Window.Current);
	}
}
