#nullable enable

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Uno.UI.Runtime.Skia.Win32;

[ComImport, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaInvokeProvider
{
	void Invoke();
}

[ComVisible(true)]
internal sealed class UiaInvokeProviderWrapper : IUiaInvokeProvider
{
	internal const int ElementNotAvailableHResult = UiaProviderDispatcher.ElementNotAvailableHResult;
	internal const int ElementNotEnabledHResult = UiaProviderDispatcher.ElementNotEnabledHResult;

	private readonly IInvokeProvider _inner;
	private readonly UiaProviderDispatcher _dispatcher;

	internal UiaInvokeProviderWrapper(IInvokeProvider inner, DispatcherQueue dispatcherQueue, Func<bool> isAvailable, Func<bool> isEnabled)
		: this(inner, new UiaProviderDispatcher(dispatcherQueue, isAvailable, isEnabled))
	{
	}

	internal UiaInvokeProviderWrapper(IInvokeProvider inner, UiaProviderDispatcher dispatcher)
	{
		_inner = inner;
		_dispatcher = dispatcher;
	}

	public void Invoke() => _dispatcher.Invoke(_inner.Invoke);
}
