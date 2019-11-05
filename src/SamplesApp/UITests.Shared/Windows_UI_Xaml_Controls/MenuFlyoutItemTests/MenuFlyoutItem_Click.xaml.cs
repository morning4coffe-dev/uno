﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236
		  
namespace UITests.Shared.Windows_UI_Xaml_Controls.MenuFlyoutItemTests
{
    public sealed partial class MenuFlyoutItem_Click : UserControl
    {
        public MenuFlyoutItem_Click()
        {
            this.InitializeComponent();
        }

		public void FlyoutItem_Click(object sender, object args)
		{
			mfiResult.Text = "success";
		}
	}
}
