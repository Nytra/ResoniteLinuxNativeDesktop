namespace LinuxNativeDesktop.Capture;

public sealed class CaptureOptions
{
	public Func<int> MaxFramerate { get; init; } = () => 30;
	public Func<bool> ShowCursor { get; init; } = () => true;

	/// <summary>Ask for keyboard and pointer control too. Read when the portal session starts.</summary>
	public Func<bool> AllowInput { get; init; } = () => false;

	/// <summary>Restore tokens per mode (true = with input); a token from one mode is rejected by the other.</summary>
	public Func<bool, string?> LoadRestoreToken { get; init; } = _ => null;
	public Action<bool, string?> SaveRestoreToken { get; init; } = (_, _) => { };
}

/// <summary>Sends input to the desktop through the portal session. Calls are ignored while it isn't available.</summary>
public interface IDesktopInput
{
	bool CanInject { get; }

	/// <summary>Moves the pointer to a point in Wine's desktop coordinates.</summary>
	void MovePointer(IReadOnlyList<DisplayRect> displays, double x, double y);

	/// <param name="button">evdev button code.</param>
	void PointerButton(int button, bool pressed);

	/// <param name="keycode">evdev key code.</param>
	void Keycode(int keycode, bool pressed);

	void Keysym(int keysym, bool pressed);

	/// <summary>Scrolls at the pointer by whole wheel notches. Positive scrolls down / right.</summary>
	void Scroll(int vertical, int horizontal);
}

/// <summary>
/// Shares one portal session between all consumers and runs one PipeWire stream per display that has
/// subscribers. The portal is only asked once a display is actually needed, and kept for the process lifetime
/// so the share dialog doesn't come back when the desktop tab is reopened.
/// </summary>
public sealed class CaptureService : IDesktopInput, IDisposable
{
	static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

	readonly CaptureOptions _options;
	readonly ICaptureLog _log;
	readonly object _gate = new();
	readonly Dictionary<int, Feed> _feeds = [];
	IReadOnlyList<DisplayRect> _displays = [];
	volatile PortalSession? _portal;
	PipeWireConnection? _pipewire;
	CancellationTokenSource? _starting;
	DateTime _failedAt = DateTime.MinValue;
	bool _disposed;
	volatile bool _movedSinceScroll = true;

	public CaptureService(CaptureOptions options, ICaptureLog log)
	{
		_options = options;
		_log = log;
	}

	/// <summary>Starts feeding <paramref name="sink"/> frames of a display. Dispose the result to stop.</summary>
	public IDisposable Subscribe(int displayIndex, IReadOnlyList<DisplayRect> displays, IFrameSink sink)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_displays = displays;
			if (!_feeds.TryGetValue(displayIndex, out var feed))
				_feeds[displayIndex] = feed = new Feed();
			feed.Add(sink);

			if (_feeds.Values.Any(f => f.Stream?.Failed == true))
			{
				_log.Warn("A capture stream ended (sharing was probably stopped); asking the portal again");
				TearDown();
			}
			if (_pipewire is not null)
				EnsureStream(displayIndex, feed);
			else
				EnsureStarted();
		}
		return new Subscription(this, displayIndex, sink);
	}

	void Unsubscribe(int displayIndex, IFrameSink sink)
	{
		PipeWireVideoStream? stop = null;
		lock (_gate)
		{
			if (!_feeds.TryGetValue(displayIndex, out var feed))
				return;
			feed.Remove(sink);
			if (feed.IsEmpty)
			{
				stop = feed.Stream;
				feed.Stream = null;
				_feeds.Remove(displayIndex);
			}
		}
		stop?.Dispose();
	}

	void EnsureStarted()
	{
		if (_starting is not null || DateTime.UtcNow - _failedAt < RetryDelay)
			return;
		var cancel = _starting = new CancellationTokenSource();
		new Thread(() => Start(cancel.Token)) { IsBackground = true, Name = "LinuxNativeDesktop portal" }.Start();
	}

	void Start(CancellationToken cancel)
	{
		PortalSession? portal = null;
		PipeWireConnection? pipewire = null;
		try
		{
			_log.Info("Requesting screen capture through xdg-desktop-portal");
			bool input = _options.AllowInput();
			portal = PortalSession.Open(_options.LoadRestoreToken(input), _options.ShowCursor(), input, _log, cancel);
			_options.SaveRestoreToken(input, portal.RestoreToken);
			_log.Info($"Portal shared {portal.Streams.Count} screen(s): {string.Join(", ", portal.Streams)}");
			pipewire = PipeWireConnection.Connect(portal.PipeWireFd, _log);

			lock (_gate)
			{
				_starting = null;
				if (_disposed || cancel.IsCancellationRequested)
					throw new OperationCanceledException();
				_portal = portal;
				_pipewire = pipewire;
				foreach (var (index, feed) in _feeds)
					EnsureStream(index, feed);
			}
		}
		catch (Exception e)
		{
			pipewire?.Dispose();
			portal?.Dispose();
			lock (_gate)
			{
				if (_starting?.Token == cancel)
					_starting = null;
				_failedAt = DateTime.UtcNow;
			}
			if (e is not OperationCanceledException)
			{
				_log.Error($"Screen capture unavailable: {e.Message}");
				// A rejected restore token would fail every time; forget it so the next try shows the dialog.
				if (e is PortalException)
					_options.SaveRestoreToken(_options.AllowInput(), null);
			}
		}
	}

	// Called with _gate held, once the portal is ready.
	void EnsureStream(int displayIndex, Feed feed)
	{
		if (feed.Stream is not null || _portal is null || _pipewire is null)
			return;
		int[] mapping = DisplayMatcher.Match(_displays, _portal.Streams);
		int stream = displayIndex >= 0 && displayIndex < mapping.Length ? mapping[displayIndex] : -1;
		if (stream < 0)
		{
			_log.Warn($"No shared screen matches display {displayIndex}; share it in the portal dialog to see it");
			return;
		}
		var info = _portal.Streams[stream];
		_log.Debug($"Display {displayIndex} -> PipeWire node {info.NodeId}");
		try
		{
			feed.Stream = _pipewire.CreateStream(info.NodeId, _options.MaxFramerate(), feed);
		}
		catch (Exception e)
		{
			_log.Error($"Could not open PipeWire stream for display {displayIndex}: {e.Message}");
		}
	}

	// Called with _gate held. Keeps the subscribers so they get frames again once restarted.
	void TearDown()
	{
		_starting?.Cancel();
		_starting = null;
		foreach (var feed in _feeds.Values)
		{
			feed.Stream?.Dispose();
			feed.Stream = null;
		}
		_pipewire?.Dispose();
		_portal?.Dispose();
		_pipewire = null;
		_portal = null;
		_failedAt = DateTime.MinValue;
	}

	public bool CanInject => _portal?.CanInject == true;

	public void MovePointer(IReadOnlyList<DisplayRect> displays, double x, double y)
	{
		var portal = _portal;
		if (portal is not { CanInject: true })
			return;
		int[] mapping = DisplayMatcher.Match(displays, portal.Streams);
		if (PointerMapping.Map(displays, portal.Streams, mapping, x, y) is { } point)
		{
			portal.PointerMotionAbsolute(point.NodeId, point.X, point.Y);
			_movedSinceScroll = true;
		}
	}

	public void PointerButton(int button, bool pressed) => _portal?.PointerButton(button, pressed);

	public void Keycode(int keycode, bool pressed) => _portal?.KeyboardKeycode(keycode, pressed);

	public void Keysym(int keysym, bool pressed) => _portal?.KeyboardKeysym(keysym, pressed);

	/// <summary>
	/// Sent as smooth scrolling, 15 units per notch (what KWin makes of one wheel notch). KWin marks all remote scroll
	/// as source "unknown", and XWayland apps such as Steam ignore discrete steps from it; smooth scrolling works in
	/// both X11 and Wayland apps. Sending both would scroll X11 apps twice. After pointer motion a primer goes first
	/// (see <see cref="ScrollAccumulator.Events"/>).
	/// </summary>
	public void Scroll(int vertical, int horizontal)
	{
		if (_portal is not { } portal)
			return;
		foreach (var (dx, dy) in ScrollAccumulator.Events(vertical, horizontal, _movedSinceScroll))
			portal.PointerAxis(dx, dy);
		_movedSinceScroll = false;
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			TearDown();
			_feeds.Clear();
		}
	}

	/// <summary>Fans frames of one display out to its subscribers. The sink list is copy-on-write.</summary>
	sealed class Feed : IFrameSink
	{
		volatile IFrameSink[] _sinks = [];

		public PipeWireVideoStream? Stream;

		public bool IsEmpty => _sinks.Length == 0;

		public void Add(IFrameSink sink) => _sinks = [.. _sinks, sink];

		public void Remove(IFrameSink sink) => _sinks = [.. _sinks.Where(s => s != sink)];

		public void OnFrame(in VideoFrame frame)
		{
			foreach (var sink in _sinks)
				sink.OnFrame(frame);
		}
	}

	sealed class Subscription(CaptureService service, int displayIndex, IFrameSink sink) : IDisposable
	{
		int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				service.Unsubscribe(displayIndex, sink);
		}
	}
}
