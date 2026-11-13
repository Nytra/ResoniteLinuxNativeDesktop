using System.Diagnostics;
using Elements.Core;
using FrooxEngine;
using LinuxNativeDesktop.Capture;
using LinuxNativeDesktop.Input;
using Renderite.Shared;

namespace LinuxNativeDesktop;

/// <summary>
/// The Linux counterpart of the engine's WindowsInputInjector: desktop-tab lasers, clicks, keys and typed text are
/// sent through the RemoteDesktop portal. Laser "touches" drive the real mouse pointer (one at a time), which works
/// in every application and shows the cursor where you point.
/// </summary>
sealed class LinuxInputInjector(InputInterface inputInterface, Func<IDesktopInput?> input) : ISystemInputInjector
{
	const int MaxTouches = 10;

	// Injected clicks usually land on Resonite's own fullscreen window, and Wine hands them back to the game as real
	// mouse clicks, which upset its VR UI. While a button is held, and briefly after each injected button event
	// (Wine reports them a little later), the game's own mouse buttons are ignored in VR (see GameMouseFilterPatch).
	static readonly long EchoWindow = Stopwatch.Frequency / 2;
	static long _ignoreGameButtonsUntil;
	static volatile bool _anyButtonHeld;

	public static bool IsInjectingClicks => _anyButtonHeld || Stopwatch.GetTimestamp() < Interlocked.Read(ref _ignoreGameButtonsUntil);

	static void NoteButtonEvent(bool held)
	{
		_anyButtonHeld = held;
		Interlocked.Exchange(ref _ignoreGameButtonsUntil, Stopwatch.GetTimestamp() + EchoWindow);
	}

	// KDE's portal or KWin sometimes loses a key release that reached the bus (engine fact 10), and XWayland then
	// auto-repeats the key. Every key is released straight after its press, so a second release a little later is a
	// no-op unless the first was lost, and it comes long before the 0.6 s repeat delay.
	static readonly TimeSpan ReleaseAgainAfter = TimeSpan.FromMilliseconds(50);

	sealed class Touch(int id)
	{
		public readonly Pointer Pointer = new(id, PointerType.Touch);
		public bool Allocated;
		public float2 LastPosition;
	}

	readonly object _gate = new();
	readonly Touch[] _touches = [.. Enumerable.Range(0, MaxTouches).Select(i => new Touch(i))];
	Touch? _owner;       // the touch currently driving the pointer
	bool _buttonDown;    // left button held on behalf of _owner
	bool? _wasAvailable; // for logging when input becomes (un)available

	public int UpdateOrder => 0;

	public void RegisterInputs(InputInterface inputInterface) => LinuxNativeDesktop.Msg("Linux input injector registered");

	public void CollectDeviceInfos(DataTreeList list) { }

	public Pointer InjectTouch()
	{
		lock (_gate)
		{
			var touch = _touches.FirstOrDefault(t => !t.Allocated);
			if (touch is null)
			{
				LinuxNativeDesktop.Debug($"InjectTouch: all {MaxTouches} touches are in use");
				return null!; // all in use; the engine handles null like the Windows injector's
			}
			touch.Allocated = true;
			touch.LastPosition = new float2(float.NaN, float.NaN);
			LinuxNativeDesktop.Debug($"InjectTouch: handed out touch {touch.Pointer.Id} ({_touches.Count(t => t.Allocated)} in use)");
			return touch.Pointer;
		}
	}

	public void RemoveInjectedTouch(Pointer touch)
	{
		lock (_gate)
		{
			var t = _touches.FirstOrDefault(t => t.Pointer == touch)
				?? throw new InvalidOperationException("This touch doesn't belong to the Linux input injector");
			t.Allocated = false; // the button is released in the next update
			LinuxNativeDesktop.Debug($"RemoveInjectedTouch: touch {t.Pointer.Id} returned ({_touches.Count(t => t.Allocated)} in use)");
		}
	}

	public void UpdateInputs(float deltaTime)
	{
		var desktop = Available();
		if (_wasAvailable != desktop is not null)
		{
			_wasAvailable = desktop is not null;
			LinuxNativeDesktop.Debug(desktop is not null ? "Desktop input available" : "Desktop input unavailable (no portal session with remote control yet)");
		}
		if (desktop is null)
			return;
		lock (_gate)
		{
			if (_owner is { Allocated: false })
				ReleaseOwner(desktop);

			// While the button is held, the pressing touch keeps the pointer. Otherwise a pressed touch takes it,
			// then whichever touch moved, so two lasers on the desktop don't make the cursor jump every frame.
			if (!_buttonDown)
			{
				var candidate = _touches.FirstOrDefault(t => t.Allocated && t.Pointer.PrimaryPress.Held)
					?? _touches.FirstOrDefault(t => t.Allocated && t.Pointer.Position.Value != t.LastPosition);
				if (candidate is not null && candidate != _owner)
				{
					_owner = candidate;
					LinuxNativeDesktop.Debug($"Pointer now follows touch {candidate.Pointer.Id}");
				}
			}

			if (_owner is { } owner)
			{
				var position = owner.Pointer.Position.Value;
				if (position != owner.LastPosition)
					desktop.MovePointer(Displays(), position.x, position.y);
				bool pressed = owner.Pointer.PrimaryPress.Held;
				if (pressed != _buttonDown)
				{
					desktop.PointerButton(KeyMap.BtnLeft, pressed);
					_buttonDown = pressed;
					NoteButtonEvent(pressed);
					LinuxNativeDesktop.Debug($"Left button {(pressed ? "down" : "up")} (touch {owner.Pointer.Id})");
				}
			}

			foreach (var t in _touches)
				if (t.Allocated)
					t.LastPosition = t.Pointer.Position.Value;
		}
	}

	void ReleaseOwner(IDesktopInput desktop)
	{
		if (_buttonDown)
		{
			desktop.PointerButton(KeyMap.BtnLeft, false);
			NoteButtonEvent(false);
			LinuxNativeDesktop.Debug($"Left button up (touch {_owner?.Pointer.Id} was returned)");
		}
		_buttonDown = false;
		_owner = null;
	}

	public void SetMousePosition(int2 position) => input()?.MovePointer(Displays(), position.x, position.y);

	public void SendLeftClick(int2 position) => Click(position, KeyMap.BtnLeft);

	public void SendRightClick(int2 position) => Click(position, KeyMap.BtnRight);

	public void SendMiddleClick(int2 position) => Click(position, KeyMap.BtnMiddle);

	void Click(int2 position, int button)
	{
		LinuxNativeDesktop.Debug($"Click button 0x{button:x}"); // no position: it could reveal what was clicked
		if (Available() is not { } desktop)
			return;
		lock (_gate)
		{
			desktop.MovePointer(Displays(), position.x, position.y);
			desktop.PointerButton(button, true);
			desktop.PointerButton(button, false);
			NoteButtonEvent(false);
		}
	}

	public void InjectString(string str)
	{
		if (Available() is not { } desktop)
			return;
		var keys = Keysyms.FromText(str).Select(sym => new LinuxKey(null, sym)).ToList();
		lock (_gate)
		{
			foreach (var key in keys)
			{
				Send(desktop, key, true);
				Send(desktop, key, false);
			}
		}
		ReleaseAgainLater(desktop, keys);
	}

	/// <summary>See <see cref="KeyMap.Tap"/>.</summary>
	public void InjectKeyPress(List<Key> keys)
	{
		var desktop = Available();
		if (KeyMap.ForLog(keys) is { } logged) // never typed text: logs are public
			LinuxNativeDesktop.Debug($"InjectKeyPress: {logged}{(desktop is null ? " (dropped: input unavailable)" : "")}");
		if (desktop is null)
			return;
		var events = KeyMap.Tap(keys);
		lock (_gate)
		{
			foreach (var (key, pressed) in events)
				Send(desktop, key, pressed);
		}
		ReleaseAgainLater(desktop, events.Select(e => e.Key));
	}

	// Under _gate, so it can't land inside another key sequence (between Ctrl's press and C's).
	void ReleaseAgainLater(IDesktopInput desktop, IEnumerable<LinuxKey> keys)
	{
		var released = keys.Distinct().ToList();
		if (released.Count == 0)
			return;
		Task.Delay(ReleaseAgainAfter).ContinueWith(_ =>
		{
			lock (_gate)
			{
				foreach (var key in released)
					Send(desktop, key, false);
			}
		});
	}

	static void Send(IDesktopInput desktop, LinuxKey key, bool pressed)
	{
		if (key.Keycode is int code)
			desktop.Keycode(code, pressed);
		else if (key.Keysym is int sym)
			desktop.Keysym(sym, pressed);
	}

	/// <summary>The portal session, if one is running with remote control granted.</summary>
	IDesktopInput? Available() => input() is { CanInject: true } desktop ? desktop : null;

	List<DisplayRect> Displays() => LinuxNativeDesktop.Displays(inputInterface);

	public void Dispose()
	{
		lock (_gate)
		{
			if (_buttonDown && input() is { } desktop)
				desktop.PointerButton(KeyMap.BtnLeft, false);
			_buttonDown = false;
			_anyButtonHeld = false;
		}
	}
}
