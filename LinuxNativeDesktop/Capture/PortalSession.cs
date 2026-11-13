using System.Diagnostics;
using System.Runtime.InteropServices;
using static LinuxNativeDesktop.Capture.Native.GLib;

namespace LinuxNativeDesktop.Capture;

public sealed class PortalException(string message) : Exception(message);

/// <summary>
/// An xdg-desktop-portal ScreenCast session (org.freedesktop.portal.ScreenCast), spoken over GDBus. With remote
/// control it is a RemoteDesktop session with the screen cast attached, which also allows injecting input.
/// Works from inside the Steam runtime container, which forwards the session bus. The PipeWire fd it
/// returns grants access to just the chosen screens, so no PipeWire socket is needed in the container.
/// </summary>
public sealed class PortalSession : IDisposable
{
	const string Bus = "org.freedesktop.portal.Desktop";
	const string Path = "/org/freedesktop/portal/desktop";
	const string ScreenCast = "org.freedesktop.portal.ScreenCast";
	const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
	const uint DeviceKeyboard = 1;
	const uint DevicePointer = 2;
	const uint SourceMonitor = 1;
	const uint CursorHidden = 1;
	const uint CursorEmbedded = 2;
	const uint PersistUntilRevoked = 2;
	static readonly TimeSpan DialogTimeout = TimeSpan.FromMinutes(5);
	static readonly SignalCallback ResponseDelegate = OnResponse;
	static int _tokenCounter;

	readonly object _gate = new(); // input calls may come from any thread while Dispose runs
	IntPtr _connection;
	string _sessionPath = "";

	public IReadOnlyList<StreamInfo> Streams { get; private set; } = [];

	/// <summary>Token to skip the share dialog next time (portal version 4+). Tokens are single use, so save each new one.</summary>
	public string? RestoreToken { get; private set; }

	/// <summary>True when the user allowed pointer and keyboard control (a RemoteDesktop session).</summary>
	public bool CanInject { get; private set; }

	/// <summary>The PipeWire remote fd. Ownership passes to whoever connects with it.</summary>
	public int PipeWireFd { get; private set; } = -1;

	PortalSession() { }

	/// <summary>Runs the whole handshake, blocking until the user has answered the share dialog. Call on a background thread.</summary>
	/// <param name="remoteControl">Also ask for keyboard and pointer control. Restore tokens are not interchangeable between the two modes.</param>
	public static PortalSession Open(string? restoreToken, bool showCursor, bool remoteControl, ICaptureLog log, CancellationToken cancel)
	{
		var session = new PortalSession();
		IntPtr context = g_main_context_new();
		g_main_context_push_thread_default(context);
		try
		{
			session.Run(restoreToken, showCursor, remoteControl, log, context, cancel);
			return session;
		}
		catch
		{
			session.Dispose();
			throw;
		}
		finally
		{
			g_main_context_pop_thread_default(context);
			g_main_context_unref(context);
		}
	}

	void Run(string? restoreToken, bool showCursor, bool remoteControl, ICaptureLog log, IntPtr context, CancellationToken cancel)
	{
		_connection = g_bus_get_sync(BusTypeSession, IntPtr.Zero, out IntPtr error);
		Check(error, "Connecting to the D-Bus session bus");
		string sender = Utf8(g_dbus_connection_get_unique_name(_connection))!.TrimStart(':').Replace('.', '_');

		uint version = GetUInt32Property(ScreenCast, "version") ?? 1;
		uint cursorModes = GetUInt32Property(ScreenCast, "AvailableCursorModes") ?? 0;
		uint cursor = showCursor && (cursorModes & CursorEmbedded) != 0 ? CursorEmbedded : CursorHidden;
		log.Info($"ScreenCast portal version {version}, cursor modes {cursorModes}");
		// Replies are only accepted from the portal's unique name; any process on the bus can emit signals.
		// (Reading the version above has started the portal if it was D-Bus activated.)
		string portal = GetNameOwner(_connection, Bus) ?? throw new PortalException("xdg-desktop-portal is not running");

		uint? remoteVersion = remoteControl ? GetUInt32Property(RemoteDesktop, "version") : null;
		uint deviceTypes = remoteVersion is null ? 0 : GetUInt32Property(RemoteDesktop, "AvailableDeviceTypes") ?? 0;
		bool remote = remoteVersion is not null && (deviceTypes & (DeviceKeyboard | DevicePointer)) != 0;
		if (remoteControl)
			log.Info(remote ? $"RemoteDesktop portal version {remoteVersion}, device types {deviceTypes}" : "No RemoteDesktop portal; desktop input is unavailable");
		// A remote desktop session is created and started through RemoteDesktop; the screen cast is attached to it.
		string sessionInterface = remote ? RemoteDesktop : ScreenCast;

		string sessionToken = NewToken();
		IntPtr results = Request(portal, sender, context, cancel, sessionInterface, "CreateSession",
			t => $"({{'handle_token': <{Quote(t)}>, 'session_handle_token': <{Quote(sessionToken)}>}},)");
		_sessionPath = LookupString(results, "session_handle") ?? throw new PortalException("CreateSession returned no session handle");
		g_variant_unref(results);

		// Persistence belongs to whichever interface owns the session (RemoteDesktop version 2+, ScreenCast 4+).
		bool canPersist = remote ? remoteVersion >= 2 : version >= 4;
		string persist = canPersist
			? $", 'persist_mode': <uint32 {PersistUntilRevoked}>" + (restoreToken is null ? "" : $", 'restore_token': <{Quote(restoreToken)}>")
			: "";
		if (remote)
		{
			uint devices = deviceTypes & (DeviceKeyboard | DevicePointer);
			results = Request(portal, sender, context, cancel, RemoteDesktop, "SelectDevices",
				t => $"(objectpath {Quote(_sessionPath)}, {{'handle_token': <{Quote(t)}>, 'types': <uint32 {devices}>{persist}}})");
			g_variant_unref(results);
			persist = "";
		}
		results = Request(portal, sender, context, cancel, ScreenCast, "SelectSources",
			t => $"(objectpath {Quote(_sessionPath)}, {{'handle_token': <{Quote(t)}>, 'types': <uint32 {SourceMonitor}>, 'multiple': <true>, 'cursor_mode': <uint32 {cursor}>{persist}}})");
		g_variant_unref(results);

		results = Request(portal, sender, context, cancel, sessionInterface, "Start",
			t => $"(objectpath {Quote(_sessionPath)}, '', {{'handle_token': <{Quote(t)}>}})");
		try
		{
			Streams = ParseStreams(results);
			RestoreToken = LookupString(results, "restore_token");
			CanInject = remote && (LookupUInt32(results, "devices") & (DeviceKeyboard | DevicePointer)) != 0;
		}
		finally
		{
			g_variant_unref(results);
		}
		if (remote && !CanInject)
			log.Warn("Remote control was not granted; desktop input is unavailable");
		if (Streams.Count == 0)
			throw new PortalException("No screens were shared");

		PipeWireFd = OpenPipeWireRemote();
	}

	int OpenPipeWireRemote()
	{
		IntPtr parameters = Parse($"(objectpath {Quote(_sessionPath)}, @a{{sv}} {{}})");
		IntPtr reply = g_dbus_connection_call_with_unix_fd_list_sync(_connection, Bus, Path, ScreenCast, "OpenPipeWireRemote",
			parameters, IntPtr.Zero, 0, -1, IntPtr.Zero, out IntPtr fdList, IntPtr.Zero, out IntPtr error);
		g_variant_unref(parameters);
		Check(error, "OpenPipeWireRemote");
		try
		{
			IntPtr handle = g_variant_get_child_value(reply, 0);
			int index = g_variant_get_handle(handle);
			g_variant_unref(handle);
			int fd = g_unix_fd_list_get(fdList, index, out error); // returns a dup we own
			Check(error, "Reading the PipeWire fd");
			return fd;
		}
		finally
		{
			g_variant_unref(reply);
			if (fdList != IntPtr.Zero)
				g_object_unref(fdList);
		}
	}

	uint? GetUInt32Property(string iface, string name)
	{
		IntPtr parameters = Parse($"({Quote(iface)}, {Quote(name)})");
		IntPtr reply = g_dbus_connection_call_sync(_connection, Bus, Path, "org.freedesktop.DBus.Properties", "Get",
			parameters, IntPtr.Zero, 0, 5000, IntPtr.Zero, out IntPtr error);
		g_variant_unref(parameters);
		if (error != IntPtr.Zero)
		{
			g_error_free(error);
			return null;
		}
		IntPtr boxed = g_variant_get_child_value(reply, 0);
		IntPtr value = g_variant_get_variant(boxed);
		uint? result = TypeOf(value) == "u" ? g_variant_get_uint32(value) : null;
		g_variant_unref(value);
		g_variant_unref(boxed);
		g_variant_unref(reply);
		return result;
	}

	sealed class PendingResponse
	{
		public volatile bool Done;
		public uint Code;
		public IntPtr Results;
	}

	/// <summary>
	/// Calls a portal method that answers through a Request object, and waits for its Response signal.
	/// Subscribes before calling, at the path the portal derives from our handle token, so the reply can't be missed.
	/// </summary>
	IntPtr Request(string portal, string sender, IntPtr context, CancellationToken cancel, string iface, string method, Func<string, string> buildParameters)
	{
		string token = NewToken();
		string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
		var pending = new PendingResponse();
		var handle = GCHandle.Alloc(pending);
		uint subscription = g_dbus_connection_signal_subscribe(_connection, portal, "org.freedesktop.portal.Request", "Response",
			requestPath, IntPtr.Zero, 0, ResponseDelegate, GCHandle.ToIntPtr(handle), IntPtr.Zero);
		try
		{
			IntPtr parameters = Parse(buildParameters(token));
			IntPtr reply = g_dbus_connection_call_sync(_connection, Bus, Path, iface, method,
				parameters, IntPtr.Zero, 0, -1, IntPtr.Zero, out IntPtr error);
			g_variant_unref(parameters);
			Check(error, method);
			g_variant_unref(reply);

			var waited = Stopwatch.StartNew();
			while (!pending.Done)
			{
				cancel.ThrowIfCancellationRequested();
				if (waited.Elapsed > DialogTimeout)
					throw new PortalException($"{method}: timed out waiting for the portal");
				if (g_main_context_iteration(context, 0) == 0)
					Thread.Sleep(10);
			}
		}
		finally
		{
			g_dbus_connection_signal_unsubscribe(_connection, subscription);
			handle.Free();
		}

		if (pending.Code != 0)
		{
			if (pending.Results != IntPtr.Zero)
				g_variant_unref(pending.Results);
			throw new PortalException(pending.Code == 1 ? $"{method}: the share request was cancelled" : $"{method}: the portal refused (code {pending.Code})");
		}
		return pending.Results;
	}

	static void OnResponse(IntPtr connection, IntPtr senderName, IntPtr objectPath, IntPtr interfaceName, IntPtr signalName, IntPtr parameters, IntPtr userData)
	{
		if (GCHandle.FromIntPtr(userData).Target is not PendingResponse pending || pending.Done)
			return;
		if (ReadResponse(parameters) is not var (code, results))
			return;
		pending.Code = code;
		pending.Results = results;
		pending.Done = true;
	}

	/// <summary>Reads a Response signal's (ua{sv}), or null if it has any other shape. The caller owns the results.</summary>
	internal static (uint Code, IntPtr Results)? ReadResponse(IntPtr parameters)
	{
		if (TypeOf(parameters) != "(ua{sv})")
			return null;
		IntPtr code = g_variant_get_child_value(parameters, 0);
		uint value = g_variant_get_uint32(code);
		g_variant_unref(code);
		return (value, g_variant_get_child_value(parameters, 1));
	}

	/// <summary>The unique name (":1.42") that currently owns a well-known bus name, or null if nobody does.</summary>
	internal static string? GetNameOwner(IntPtr connection, string name)
	{
		IntPtr parameters = Parse($"({Quote(name)},)");
		IntPtr reply = g_dbus_connection_call_sync(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
			"GetNameOwner", parameters, IntPtr.Zero, 0, 5000, IntPtr.Zero, out IntPtr error);
		g_variant_unref(parameters);
		if (error != IntPtr.Zero)
		{
			g_error_free(error);
			return null;
		}
		try
		{
			IntPtr owner = g_variant_get_child_value(reply, 0);
			try { return Utf8(g_variant_get_string(owner, IntPtr.Zero)); }
			finally { g_variant_unref(owner); }
		}
		finally
		{
			g_variant_unref(reply);
		}
	}

	/// <summary>Reads the "streams" entry, a(ua{sv}), from Start's results.</summary>
	internal static List<StreamInfo> ParseStreams(IntPtr results)
	{
		var streams = new List<StreamInfo>();
		IntPtr array = g_variant_lookup_value(results, "streams", IntPtr.Zero);
		if (array == IntPtr.Zero)
			return streams;
		try
		{
			for (nuint i = 0, n = g_variant_n_children(array); i < n; i++)
			{
				IntPtr entry = g_variant_get_child_value(array, i);
				IntPtr node = g_variant_get_child_value(entry, 0);
				IntPtr props = g_variant_get_child_value(entry, 1);
				streams.Add(new StreamInfo(g_variant_get_uint32(node), LookupPair(props, "position"), LookupPair(props, "size")));
				g_variant_unref(props);
				g_variant_unref(node);
				g_variant_unref(entry);
			}
		}
		finally
		{
			g_variant_unref(array);
		}
		return streams;
	}

	static (int, int)? LookupPair(IntPtr dict, string key)
	{
		IntPtr value = g_variant_lookup_value(dict, key, IntPtr.Zero);
		if (value == IntPtr.Zero)
			return null;
		try
		{
			if (TypeOf(value) != "(ii)")
				return null;
			IntPtr a = g_variant_get_child_value(value, 0), b = g_variant_get_child_value(value, 1);
			var pair = (g_variant_get_int32(a), g_variant_get_int32(b));
			g_variant_unref(a);
			g_variant_unref(b);
			return pair;
		}
		finally
		{
			g_variant_unref(value);
		}
	}

	static uint LookupUInt32(IntPtr dict, string key)
	{
		IntPtr value = g_variant_lookup_value(dict, key, IntPtr.Zero);
		if (value == IntPtr.Zero)
			return 0;
		try { return TypeOf(value) == "u" ? g_variant_get_uint32(value) : 0; }
		finally { g_variant_unref(value); }
	}

	internal static string? LookupString(IntPtr dict, string key)
	{
		IntPtr value = g_variant_lookup_value(dict, key, IntPtr.Zero);
		if (value == IntPtr.Zero)
			return null;
		try { return TypeOf(value) is "s" or "o" ? Utf8(g_variant_get_string(value, IntPtr.Zero)) : null; }
		finally { g_variant_unref(value); }
	}

	// Input. Calls are sent without waiting for a reply, in order, from any thread (GDBusConnection is thread-safe).

	public void PointerMotionAbsolute(uint stream, double x, double y) =>
		Notify("NotifyPointerMotionAbsolute", $"uint32 {stream}, {Double(x)}, {Double(y)}");

	/// <param name="button">evdev button code, e.g. BTN_LEFT (0x110).</param>
	public void PointerButton(int button, bool pressed) =>
		Notify("NotifyPointerButton", $"{button}, uint32 {(pressed ? 1 : 0)}");

	/// <param name="keycode">evdev key code.</param>
	public void KeyboardKeycode(int keycode, bool pressed) =>
		Notify("NotifyKeyboardKeycode", $"{keycode}, uint32 {(pressed ? 1 : 0)}");

	/// <summary>Mouse-wheel steps. Positive scrolls down (vertical) or right (horizontal), like wl_pointer.</summary>
	public void PointerAxisDiscrete(bool horizontal, int steps) =>
		Notify("NotifyPointerAxisDiscrete", $"uint32 {(horizontal ? 1 : 0)}, {steps}");

	/// <summary>Smooth scrolling in pixels (with KDE, positive dy scrolls up). <paramref name="finish"/> marks the end of a scroll gesture.</summary>
	public void PointerAxis(double dx, double dy, bool finish = false) =>
		Notify("NotifyPointerAxis", $"{Double(dx)}, {Double(dy)}", finish ? "{'finish': <true>}" : null);

	public void KeyboardKeysym(int keysym, bool pressed) =>
		Notify("NotifyKeyboardKeysym", $"{keysym}, uint32 {(pressed ? 1 : 0)}");

	void Notify(string method, string arguments, string? options = null)
	{
		lock (_gate)
		{
			if (!CanInject || _connection == IntPtr.Zero)
				return;
			IntPtr parameters = Parse(NotifyParameters(_sessionPath, arguments, options));
			// No callback: GDBus sends it flagged NO_REPLY_EXPECTED and never needs a main loop.
			g_dbus_connection_call(_connection, Bus, Path, RemoteDesktop, method, parameters, IntPtr.Zero, 0, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			g_variant_unref(parameters);
		}
	}

	internal static string NotifyParameters(string sessionPath, string arguments, string? options = null) =>
		$"(objectpath {Quote(sessionPath)}, @a{{sv}} {options ?? "{}"}, {arguments})";

	/// <summary>A GVariant double literal (always with a decimal point, or it would parse as an integer).</summary>
	internal static string Double(double value) => value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>A GVariant text-format string literal.</summary>
	internal static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

	static string NewToken() => $"lnd{Environment.ProcessId}_{Interlocked.Increment(ref _tokenCounter)}";

	public void Dispose()
	{
		lock (_gate)
			DisposeLocked();
	}

	void DisposeLocked()
	{
		if (_connection == IntPtr.Zero)
			return;
		if (_sessionPath.Length > 0)
		{
			IntPtr reply = g_dbus_connection_call_sync(_connection, Bus, _sessionPath, "org.freedesktop.portal.Session", "Close",
				IntPtr.Zero, IntPtr.Zero, 0, 2000, IntPtr.Zero, out IntPtr error);
			if (error != IntPtr.Zero)
				g_error_free(error);
			if (reply != IntPtr.Zero)
				g_variant_unref(reply);
		}
		g_object_unref(_connection);
		_connection = IntPtr.Zero;
	}
}
