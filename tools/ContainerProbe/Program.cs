using LinuxNativeDesktop.Capture;
using static LinuxNativeDesktop.Capture.Native.GLib;

// Usage: ContainerProbe [pipewire-node-name | --portal | --input | --key <evdev code> [taps] | --scroll | --scroll-game]
// Prints what the portal offers and, given a node name, receives one frame from it through PipeWire.
var log = new ConsoleLog();
Console.WriteLine($"Session bus: {Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")}");

IntPtr bus = g_bus_get_sync(BusTypeSession, IntPtr.Zero, out IntPtr error);
Check(error, "Connecting to the session bus");
foreach (var property in new[] { "version", "AvailableSourceTypes", "AvailableCursorModes" })
{
	IntPtr reply = g_dbus_connection_call_sync(bus, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
		"org.freedesktop.DBus.Properties", "Get", Parse($"('org.freedesktop.portal.ScreenCast', '{property}')"),
		IntPtr.Zero, 0, 5000, IntPtr.Zero, out error);
	Check(error, $"Reading ScreenCast.{property}");
	Console.WriteLine($"ScreenCast.{property} = {Print(reply)}");
	g_variant_unref(reply);
}
g_object_unref(bus);

if (args is ["--portal"])
{
	// The full flow the mod runs: share dialog, then one frame from each shared screen.
	using var session = PortalSession.Open(null, true, false, log, CancellationToken.None);
	Console.WriteLine($"Streams: {string.Join(", ", session.Streams)}; restore token: {(session.RestoreToken is null ? "none" : "yes")}");
	using var connection = PipeWireConnection.Connect(session.PipeWireFd, log);
	foreach (var info in session.Streams)
	{
		var sink = new FirstFrame();
		using var stream = connection.CreateStream(info.NodeId, 30, sink);
		Console.WriteLine(sink.Got.Wait(TimeSpan.FromSeconds(10)) ? $"Node {info.NodeId} frame: {sink.Description}" : $"Node {info.NodeId}: no frame within 10 s");
	}
}
else if (args is ["--input"])
{
	// Asks for remote control, moves the pointer to the middle of the first shared screen, then taps Meta
	// twice (KDE: opens and closes the application launcher), as the dash's Start button does.
	using var session = PortalSession.Open(null, true, true, log, CancellationToken.None);
	Console.WriteLine($"Streams: {string.Join(", ", session.Streams)}; input granted: {session.CanInject}");
	var first = session.Streams[0];
	var (w, h) = first.Size ?? (1920, 1080);
	session.PointerMotionAbsolute(first.NodeId, w / 2.0, h / 2.0);
	foreach (var _ in new[] { 1, 2 })
	{
		Thread.Sleep(1500);
		session.KeyboardKeycode(125, true);
		session.KeyboardKeycode(125, false);
		Console.WriteLine("Tapped Meta");
	}
	Thread.Sleep(500);
}
else if (args is ["--key", var code, ..] && int.TryParse(code, out int keycode))
{
	// Taps an evdev key exactly as the mod does (press, then release), optionally several times 0.2 s apart, after
	// 5 s to focus a window, and keeps the session open 10 s so a key KWin left held keeps repeating (closing the
	// session releases it).
	int taps = args.Length > 2 && int.TryParse(args[2], out int n) ? n : 1;
	using var session = PortalSession.Open(null, true, true, log, CancellationToken.None);
	Console.WriteLine($"Input granted: {session.CanInject}. Tapping key {keycode} x{taps} in 5 s: focus a window now.");
	Thread.Sleep(5000);
	for (int i = 0; i < taps; i++)
	{
		if (i > 0)
			Thread.Sleep(200);
		session.KeyboardKeycode(keycode, true);
		session.KeyboardKeycode(keycode, false);
	}
	Console.WriteLine($"Tapped key {keycode} x{taps}; watching for 10 s");
	Thread.Sleep(10000);
}
else if (args is ["--scroll"])
{
	// Tries each way of scrolling over whatever window is in the middle of the first shared screen.
	using var session = PortalSession.Open(null, true, true, log, CancellationToken.None);
	var first = session.Streams[0];
	var (w, h) = first.Size ?? (1920, 1080);
	session.PointerMotionAbsolute(first.NodeId, w / 2.0, h / 2.0);
	(string Name, Action<int> Scroll)[] variants =
	[
		("A: wheel steps", sign => session.PointerAxisDiscrete(false, sign)),
		("B: smooth pixels", sign => session.PointerAxis(0, sign * 15)),
		("C: wheel steps + smooth pixels", sign => { session.PointerAxisDiscrete(false, sign); session.PointerAxis(0, sign * 15); }),
	];
	foreach (var (name, scroll) in variants)
	{
		Console.WriteLine($"{name}: 10 down, then 10 up");
		Thread.Sleep(2000);
		foreach (int sign in new[] { 1, -1 })
		{
			for (int i = 0; i < 10; i++)
			{
				scroll(sign);
				Thread.Sleep(100);
			}
			session.PointerAxis(0, 0, finish: true);
			Thread.Sleep(1000);
		}
	}
	Console.WriteLine("Done");
}
else if (args is ["--scroll-game"])
{
	// Scrolls the window in the middle of the first screen in several ways and measures from the screen cast how far
	// it moved. XWayland reports absolute motion and scrolling from different slave devices, which matters to
	// Chromium-based apps such as Steam when the pointer moves between steps, as a VR laser does.
	using var session = PortalSession.Open(null, false, true, log, CancellationToken.None);
	var first = session.Streams[0];
	var (w, h) = first.Size ?? (1920, 1080);
	double cx = w / 2.0, cy = h / 2.0;
	using var connection = PipeWireConnection.Connect(session.PipeWireFd, log);
	var latest = new LatestFrame();
	using var stream = connection.CreateStream(first.NodeId, 30, latest);
	session.PointerMotionAbsolute(first.NodeId, cx, cy);
	Thread.Sleep(2000);

	(string Name, bool Jitter, bool Primer)[] variants =
	[
		("pointer moving, primer", true, true),
		("still pointer", false, false),
		("still pointer, primer", false, true),
		("pointer moving, no primer", true, false),
	];
	foreach (var (name, jitter, primer) in variants)
		foreach (int sign in new[] { -1, 1 })
		{
			var before = latest.Snapshot();
			for (int frame = 0; frame < 4 * 30; frame++)
			{
				if (jitter)
					session.PointerMotionAbsolute(first.NodeId, cx + frame % 2, cy);
				if (frame % 30 == 15)
					foreach (var (dx, dy) in ScrollAccumulator.Events(sign, 0, afterPointerMotion: primer))
						session.PointerAxis(dx, dy);
				Thread.Sleep(11);
			}
			Thread.Sleep(1000);
			Console.WriteLine($"{name}, 4 steps of {sign * ScrollAccumulator.UnitsPerNotch} units: {LatestFrame.Shift(before, latest.Snapshot())}");
		}
}
else if (args.Length > 0)
{
	using var connection = PipeWireConnection.Connect(null, log);
	var sink = new FirstFrame();
	using var stream = connection.CreateStream(LinuxNativeDesktop.Capture.Native.PipeWire.IdAny, 30, sink, $"target.object={args[0]}");
	Console.WriteLine(sink.Got.Wait(TimeSpan.FromSeconds(10)) ? $"Frame: {sink.Description}" : "No frame within 10 s");
}

sealed class FirstFrame : IFrameSink
{
	public readonly ManualResetEventSlim Got = new();
	public string Description = "";

	public void OnFrame(in VideoFrame frame)
	{
		if (Got.IsSet)
			return;
		Description = $"{frame.Width}x{frame.Height} {frame.Layout}, stride {frame.Stride}, first pixel {Convert.ToHexString(frame.Data[..4])}";
		Got.Set();
	}
}

/// <summary>Keeps a copy of the newest frame, to tell how far the content in the middle scrolled.</summary>
sealed class LatestFrame : IFrameSink
{
	readonly object _gate = new();
	byte[] _data = [];
	int _width, _height, _stride;

	public void OnFrame(in VideoFrame frame)
	{
		lock (_gate)
		{
			_data = frame.Data.ToArray();
			(_width, _height, _stride) = (frame.Width, frame.Height, frame.Stride);
		}
	}

	public (byte[] Data, int Width, int Height, int Stride) Snapshot()
	{
		lock (_gate)
			return (_data, _width, _height, _stride);
	}

	/// <summary>The vertical shift (pixels, content moving up = positive) that best matches a central strip.</summary>
	public static string Shift((byte[] Data, int Width, int Height, int Stride) a, (byte[] Data, int Width, int Height, int Stride) b)
	{
		if (a.Data.Length == 0 || b.Data.Length != a.Data.Length)
			return "no frames";
		int x0 = a.Width / 2 - 200, x1 = a.Width / 2 + 200, y0 = a.Height / 2 - 150, y1 = a.Height / 2 + 150;
		long Diff(int shift)
		{
			long sum = 0;
			for (int y = y0; y < y1; y += 2)
				for (int x = x0; x < x1; x += 2)
					sum += Math.Abs(a.Data[(y + shift) * a.Stride + x * 4 + 1] - b.Data[y * b.Stride + x * 4 + 1]);
			return sum;
		}
		long still = Diff(0);
		if (still == 0)
			return "did not move";
		int best = 0;
		long bestDiff = still;
		for (int shift = -400; shift <= 400; shift++)
		{
			long d = Diff(shift);
			if (d < bestDiff)
				(best, bestDiff) = (shift, d);
		}
		return $"moved {best} px (match {100 - 100.0 * bestDiff / Math.Max(still, 1):F0}% better than unmoved)";
	}
}

sealed class ConsoleLog : ICaptureLog
{
	public void Debug(string message) => Info(message);
	public void Info(string message) => Console.WriteLine(message);
	public void Warn(string message) => Console.WriteLine("WARN " + message);
	public void Error(string message) => Console.WriteLine("ERROR " + message);
}
