declare namespace Windows.ApplicationModel.Core {
	class CoreApplication {
		static waitForInitialized(): Promise<void>;
	}
}
