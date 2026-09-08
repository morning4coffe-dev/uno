#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.Extensions;
using Uno.UI.DataBinding;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
public class Given_AutomationEnabledState
{
	[TestInitialize]
	public void Init() => UnitTestsApp.App.EnsureApplication();

	[TestMethod]
	public void When_First_Exposed_Disabled_Then_Enable_Notifies_Exactly_Once()
	{
		var previous = AutomationPeer.TestAutomationPeerListener;
		var listener = new Listener();
		try
		{
			AutomationPeer.TestAutomationPeerListener = listener;
			var button = new Button { IsEnabled = false };
			var peer = button.GetOrCreateAutomationPeer();
			Assert.IsNotNull(peer);
			Assert.IsFalse(peer.IsEnabled());
			listener.EnabledChanges.Clear();

			button.IsEnabled = true;
			CollectionAssert.AreEqual(new[] { (false, true) }, listener.EnabledChanges);
			button.IsEnabled = true;
			Assert.AreEqual(1, listener.EnabledChanges.Count);
			button.IsEnabled = false;
			CollectionAssert.AreEqual(new[] { (false, true), (true, false) }, listener.EnabledChanges);
		}
		finally
		{
			AutomationPeer.TestAutomationPeerListener = previous;
		}
	}

	[TestMethod]
	public void When_Disabled_Is_Inherited_Then_Effective_Changes_Notify()
	{
		var previous = AutomationPeer.TestAutomationPeerListener;
		var parent = new ContentControl { IsEnabled = false };
		var button = new Button();
		var listener = new Listener();
		try
		{
			// Exercise dependency-property inheritance without loading a visual tree.
			button.SetParent(parent);
			Assert.IsFalse(button.IsEnabled);
			listener.Subject = button.GetOrCreateAutomationPeer();
			AutomationPeer.TestAutomationPeerListener = listener;
			parent.IsEnabled = true;
			parent.IsEnabled = false;
			CollectionAssert.AreEqual(new[] { (false, true), (true, false) }, listener.EnabledChanges);
		}
		finally
		{
			AutomationPeer.TestAutomationPeerListener = previous;
			button.SetParent(null);
		}
	}

	[TestMethod]
	public void When_Command_CanExecute_Changes_Then_Enabled_Notifies()
	{
		var previous = AutomationPeer.TestAutomationPeerListener;
		var command = new TestCommand();
		var button = new Button { Command = command };
		var listener = new Listener { Subject = button.GetOrCreateAutomationPeer() };
		try
		{
			Assert.IsFalse(button.IsEnabled);
			AutomationPeer.TestAutomationPeerListener = listener;
			command.SetCanExecute(true);
			command.SetCanExecute(true);
			command.SetCanExecute(false);
			CollectionAssert.AreEqual(new[] { (false, true), (true, false) }, listener.EnabledChanges);
		}
		finally
		{
			AutomationPeer.TestAutomationPeerListener = previous;
			button.Command = null;
		}
	}

	private sealed class TestCommand : ICommand
	{
		private bool _canExecute;
		public event EventHandler? CanExecuteChanged;
		public bool CanExecute(object? parameter) => _canExecute;
		public void Execute(object? parameter) { }
		public void SetCanExecute(bool value)
		{
			_canExecute = value;
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private sealed class Listener : IAutomationPeerListener
	{
		public AutomationPeer? Subject { get; set; }
		public List<(bool Old, bool New)> EnabledChanges { get; } = new();
		public bool ListenerExistsHelper(AutomationEvents eventId) => true;
		public void OnAutomationEvent(AutomationPeer peer, AutomationEvents eventId) { }
		public void NotifyAutomationEvent(AutomationPeer peer, AutomationEvents eventId) { }
		public void NotifyInvalidatePeer(AutomationPeer peer) { }
		public void NotifyPropertyChangedEvent(AutomationPeer peer, AutomationProperty property, object oldValue, object newValue)
		{
			if (property == AutomationElementIdentifiers.IsEnabledProperty && (Subject is null || Subject == peer))
			{
				EnabledChanges.Add(((bool)oldValue, (bool)newValue));
			}
		}
		public void NotifyNotificationEvent(AutomationPeer peer, AutomationNotificationKind kind, AutomationNotificationProcessing processing, string text, string activityId) { }
	}
}
