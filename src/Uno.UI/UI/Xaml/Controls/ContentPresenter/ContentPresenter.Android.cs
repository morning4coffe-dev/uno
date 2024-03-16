﻿using Android.Views;
using Android.Widget;
using Uno.Foundation.Logging;
using Uno.Extensions;
using Uno.UI.DataBinding;
using Uno.UI.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using Uno.Disposables;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Graphics;
using Android.Graphics.Drawables;
using System.Drawing;
using System.Linq;
using Uno.UI;
using Uno.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml.Controls;

public partial class ContentPresenter
{
	private readonly BorderLayerRenderer _borderRenderer;

	public ContentPresenter()
	{
		_borderRenderer = new BorderLayerRenderer(this);
		InitializeContentPresenter();

		IFrameworkElementHelper.Initialize(this);
	}

	private void SetUpdateTemplate()
	{
		UpdateContentTemplateRoot();
		RequestLayout();
	}

	partial void RegisterContentTemplateRoot()
	{
		//This validation is present in order to remove the child from its parent if it already has a parent.
		//This prevents an exception for an InvalidState when we try to set a new template.
		if (ContentTemplateRoot.Parent != null)
		{
			(ContentTemplateRoot.Parent as ViewGroup)?.RemoveView(ContentTemplateRoot);
		}

		AddView(ContentTemplateRoot);
	}

	partial void UnregisterContentTemplateRoot()
	{
		this.RemoveViewAndDispose(ContentTemplateRoot);
	}

	partial void OnBackgroundSizingChangedPartial(DependencyPropertyChangedEventArgs e) => UpdateBorder();

	protected override void OnDraw(Android.Graphics.Canvas canvas)
	{
		AdjustCornerRadius(canvas, CornerRadius);
	}

	private void UpdateCornerRadius(CornerRadius radius) => UpdateBorder();

	private void UpdateBorder() => _borderRenderer.Update();

	partial void OnPaddingChangedPartial(Thickness oldValue, Thickness newValue) => UpdateBorder();

	bool ICustomClippingElement.AllowClippingToLayoutSlot => true;

	bool ICustomClippingElement.ForceClippingToLayoutSlot => CornerRadius != CornerRadius.None;
}
