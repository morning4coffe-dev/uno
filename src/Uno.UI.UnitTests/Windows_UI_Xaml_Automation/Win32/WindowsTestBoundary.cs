#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

internal static class WindowsTestBoundary
{
	[ModuleInitializer]
	internal static void RequireWindows()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("The Win32 automation test runner executes UIAutomationCore and requires Windows.");
		}
	}
}
