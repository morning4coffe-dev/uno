#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Accessibility;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
public class Given_TemplateAutomationText
{
	[TestInitialize]
	public void Init() => UnitTestsApp.App.EnsureApplication();

	[TestMethod]
	public void When_Text_Changes_Then_Name_Reflects_Current_Text_And_Clearing()
	{
		var text = new TextBlock();
		Assert.IsNull(TemplateAutomationText.GetName(text));
		text.Text = "5";
		Assert.AreEqual("5", TemplateAutomationText.GetName(text));
		text.Text = "";
		Assert.IsNull(TemplateAutomationText.GetName(text));
		text.Text = "8";
		Assert.AreEqual("8", TemplateAutomationText.GetName(text));
	}

	[TestMethod]
	public void When_Explicit_Name_Or_Label_Exists_Then_It_Remains_Stable()
	{
		var text = new TextBlock { Text = "value" };
		var label = new TextBlock { Text = "label" };
		AutomationProperties.SetLabeledBy(text, label);
		Assert.AreEqual("label", TemplateAutomationText.GetName(text));
		AutomationProperties.SetName(text, "stable");
		text.Text = "changed";
		Assert.AreEqual("stable", TemplateAutomationText.GetName(text));
		AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
		Assert.IsNull(TemplateAutomationText.GetName(text));
	}

	[TestMethod]
	public void When_Templated_Item_Has_No_Text_Then_Opaque_Model_Is_Not_Its_Name()
	{
		var item = new ListViewItem
		{
			Content = new object(),
			ContentTemplate = new DataTemplate(() => new TextBlock())
		};
		Assert.IsTrue(string.IsNullOrEmpty(item.GetOrCreateAutomationPeer()?.GetName()));
		var literal = new ListViewItem { Content = "literal item" };
		Assert.AreEqual("literal item", literal.GetOrCreateAutomationPeer()?.GetName());
	}
}
