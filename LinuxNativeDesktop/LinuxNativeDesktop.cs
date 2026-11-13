using FrooxEngine;
using HarmonyLib;
using LinuxNativeDesktop.Capture;
using ResoniteModLoader;

namespace LinuxNativeDesktop;

public class LinuxNativeDesktop : ResoniteMod
{
	public override string Name => "LinuxNativeDesktop";
	public override string Author => "Nytra";
	public override string Version => "1.0.0";
	public override string Link => "https://github.com/Nytra/ResoniteLinuxNativeDesktop";

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<bool> EnabledKey = new("Enabled", "Enable the desktop tab and capture the desktop natively (needs a restart)", () => true);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<int> MaxFramerateKey = new("Max framerate", "Desktop capture frame rate limit", () => 30, valueValidator: v => v is >= 1 and <= 240);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<bool> ShowCursorKey = new("Show cursor", "Draw the mouse cursor into the captured image (needs a restart)", () => true);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<bool> AllowInputKey = new("Allow desktop input", "Let the desktop tab click and type on your desktop (asks for remote control permission; needs a restart)", () => true);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<float> ScrollSpeedKey = new("Scroll speed", "Mouse-wheel steps per second with the thumbstick fully pushed while pointing at the desktop", () => 100f, valueValidator: v => v is >= 1f and <= 200f);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<bool> DebugLogKey = new("Debug logging", "Log desktop view lifetimes, frame statistics and input events (never typed text or click positions)", () => false);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<string?> RestoreTokenKey = new("Portal restore token", "Lets the portal reuse your last screen choice without asking", () => null, internalAccessOnly: true);

	[AutoRegisterConfigKey]
	static readonly ModConfigurationKey<string?> RemoteRestoreTokenKey = new("Portal remote control restore token", "Lets the portal reuse your last screen and input choice without asking", () => null, internalAccessOnly: true);

	static ModConfiguration? _config;
	static CaptureService? _service;

	/// <summary>Whether the mod is active this session. Read once at startup, since what it does there can't be undone live.</summary>
	internal static bool Enabled { get; private set; }

	/// <summary>Whether desktop input is active this session (the injector is only registered at startup).</summary>
	internal static bool InputEnabled { get; private set; }

	internal static int MaxFramerate => _config?.GetValue(MaxFramerateKey) ?? 30;

	internal static float ScrollSpeed => _config?.GetValue(ScrollSpeedKey) ?? 100f;

	/// <summary>The desktop input sink, once a desktop view has started the portal session.</summary>
	internal static IDesktopInput? Input => _service;


	internal static readonly ICaptureLog Log = new ModLog();

	internal static void Debug(string message)
	{
		if (_config?.GetValue(DebugLogKey) ?? false)
			Msg("[debug] " + message);
	}

	/// <summary>The shared capture service, created when a desktop texture first needs it (engine thread).</summary>
	internal static CaptureService Service => _service ??= CreateCaptureService();

	public override void OnEngineInit()
	{
		_config = GetConfiguration();
		if (!OperatingSystem.IsLinux())
		{
			Msg("Not running on Linux; the built-in desktop capture is used instead.");
			return;
		}
		Enabled = _config?.GetValue(EnabledKey) ?? true;
		InputEnabled = Enabled && (_config?.GetValue(AllowInputKey) ?? true);
		new Harmony("owo.Nytra.LinuxNativeDesktop").PatchAll();

		// The engine turns the whole desktop feature off under Wine or off Windows. Input to the desktop goes through
		// the system input injector, which the engine only provides on Windows; the Linux one is added after startup.
		if (Enabled && Engine.Config is { } engineConfig)
		{
			engineConfig.DisableDesktop = false;
			Msg("Re-enabled the desktop feature");
			if (InputEnabled)
				Engine.Current.RunPostInit(RegisterInputInjector);
		}
	}

	static void RegisterInputInjector()
	{
		var inputInterface = Engine.Current.InputInterface;
		if (inputInterface.IsInputInjectionSupported)
		{
			Warn("Another input injector is already registered; not adding the Linux one");
			return;
		}
		// Hands out the service only once a desktop view created it, so input never triggers the share dialog itself.
		inputInterface.RegisterSystemInputInjector(new LinuxInputInjector(inputInterface, () => _service));
	}

	internal static List<DisplayRect> Displays(InputInterface inputInterface)
	{
		var displays = new List<DisplayRect>();
		for (int i = 0; inputInterface.TryGetDisplay(i) is { } d; i++)
			displays.Add(new DisplayRect(d.Offset.x, d.Offset.y, d.Resolution.x, d.Resolution.y));
		return displays;
	}

	static CaptureService CreateCaptureService()
	{
		var service = new CaptureService(new CaptureOptions
		{
			MaxFramerate = () => MaxFramerate,
			ShowCursor = () => _config?.GetValue(ShowCursorKey) ?? true,
			AllowInput = () => InputEnabled,
			LoadRestoreToken = remote => _config?.GetValue(remote ? RemoteRestoreTokenKey : RestoreTokenKey),
			SaveRestoreToken = (remote, token) =>
			{
				if (_config is null)
					return;
				_config.Set(remote ? RemoteRestoreTokenKey : RestoreTokenKey, token);
				_config.Save(true);
			},
		}, Log);
		Engine.Current.OnShutdown += service.Dispose;
		return service;
	}

	sealed class ModLog : ICaptureLog
	{
		public void Debug(string message) => LinuxNativeDesktop.Debug(message);
		public void Info(string message) => Msg(message);
		public void Warn(string message) => ResoniteMod.Warn(message);
		public void Error(string message) => ResoniteMod.Error(message);
	}
}
