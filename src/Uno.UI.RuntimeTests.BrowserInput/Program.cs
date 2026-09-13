using Uno.UI.Hosting;

var host = UnoPlatformHostBuilder.Create()
	.App(() => new Uno.UI.RuntimeTests.BrowserInput.App())
	.UseWebAssembly()
	.Build();

await host.RunAsync();
