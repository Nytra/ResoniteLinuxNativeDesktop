namespace LinuxNativeDesktop.Capture;

/// <summary>Something a laser gesture does to the pointer, in the order it has to happen.</summary>
public abstract record PointerEvent
{
	public sealed record Move(double X, double Y) : PointerEvent;
	public sealed record Button(bool Right, bool Pressed) : PointerEvent;
}

/// <summary>
/// Turns a laser into mouse input the way Windows turns the engine's injected touches into it. A laser shakes when
/// the trigger is pulled, so the pointer stays where the press began until the laser has moved further than the drag
/// threshold. Nothing is pressed until the gesture is known: letting go clicks, moving away presses the left button
/// at the press point and drags, and holding still for the hold time makes the release a right-click. With no hold
/// time the left button goes down straight away. A click made on release holds its button for <see cref="ClickLength"/>:
/// sent microseconds apart, a release can get lost on the way to the desktop (KWin loses key releases that way), and
/// then the button stays held. For <see cref="DoubleClickTime"/> after a left click the pointer stays on it while the
/// laser is within the threshold, and a press there lands on the same spot, so shake can't spoil a double click (Windows
/// has a double-tap distance for this).
/// </summary>
public sealed class TouchGesture
{
	/// <summary>Seconds a click made on release holds its button.</summary>
	public const double ClickLength = 0.05;

	/// <summary>Seconds after a left click in which a press near it lands on the same spot.</summary>
	public const double DoubleClickTime = 0.5;

	enum State { Up, Pending, LeftDown, Held, Clicking }

	State _state;
	bool _dragging, _clickRight;
	double _anchorX, _anchorY, _heldFor, _clickFor;
	double? _sinceClick; // since the last left click (its point is the anchor), while the laser stays near it
	(double X, double Y)? _pointer;
	readonly List<PointerEvent> _events = [];

	/// <summary>Whether a press is in progress (the touch keeps the pointer until it ends).</summary>
	public bool Active => _state != State.Up;

	/// <summary>Whether the right button (true) or the left one (false) is down, or null if neither.</summary>
	public bool? HeldRight => _state switch
	{
		State.LeftDown => false,
		State.Clicking => _clickRight,
		_ => null,
	};

	/// <param name="x">The laser's position on the desktop.</param>
	/// <param name="y">The laser's position on the desktop.</param>
	/// <param name="pressed">Whether the trigger is held.</param>
	/// <param name="threshold">How far, in desktop pixels, the laser can move before a press becomes a drag.</param>
	/// <param name="holdTime">Seconds to hold still for a right-click; 0 presses the left button straight away.</param>
	/// <param name="deltaTime">Seconds since the last update.</param>
	/// <returns>What to send; the list is reused by the next call.</returns>
	public IReadOnlyList<PointerEvent> Update(double x, double y, bool pressed, double threshold, double holdTime, double deltaTime)
	{
		_events.Clear();
		if (_sinceClick is { } since)
			_sinceClick = since + deltaTime <= DoubleClickTime && Near(x, y, threshold) ? since + deltaTime : null;
		if (_state == State.Clicking)
		{
			_clickFor += deltaTime;
			if (!pressed && _clickFor < ClickLength)
				return _events;
			EndClick(); // also when pressed again before the click is over
		}
		if (!pressed)
		{
			Lift(); // before the pointer leaves the press point
			if (_state == State.Up && !Snapped(x, y, threshold))
				MoveTo(x, y);
			return _events;
		}
		if (_state == State.Up)
		{
			if (!Snapped(x, y, threshold)) // otherwise it presses where the last click was
			{
				(_anchorX, _anchorY) = (x, y);
				MoveTo(x, y);
			}
			(_heldFor, _dragging, _sinceClick) = (0, false, null);
			if (holdTime > 0)
				_state = State.Pending;
			else
				PressLeft();
			return _events;
		}
		bool moved = !Near(x, y, threshold);
		if (_state == State.Pending)
		{
			_heldFor += deltaTime;
			if (moved)
				PressLeft();
			else if (_heldFor >= holdTime)
				_state = State.Held;
		}
		if (_state == State.LeftDown && moved)
			_dragging = true;
		if (_dragging)
			MoveTo(x, y);
		return _events;
	}

	/// <summary>
	/// Ends the press where the pointer is (the touch driving it was returned). A tap or hold that hasn't pressed
	/// anything yet is dropped rather than clicked: its press and release would go back to back. A button that is
	/// down was pressed in an earlier update, so releasing it now is safe.
	/// </summary>
	public IReadOnlyList<PointerEvent> Cancel()
	{
		_events.Clear();
		if (HeldRight is { } right)
			_events.Add(new PointerEvent.Button(right, false));
		_state = State.Up;
		_sinceClick = null;
		return _events;
	}

	/// <summary>
	/// Something else moved the pointer (a secondary-action click). The next button event moves it back to the press
	/// point first, and the next press doesn't land on the last click.
	/// </summary>
	public void PointerMoved() => (_pointer, _sinceClick) = (null, null);

	void Lift()
	{
		switch (_state)
		{
			case State.Pending or State.Held:
				_clickRight = _state == State.Held;
				_clickFor = 0;
				MoveTo(_anchorX, _anchorY);
				_events.Add(new PointerEvent.Button(_clickRight, true));
				_state = State.Clicking;
				return;
			case State.LeftDown:
				_events.Add(new PointerEvent.Button(false, false));
				break;
		}
		_state = State.Up;
	}

	void EndClick()
	{
		_events.Add(new PointerEvent.Button(_clickRight, false));
		_state = State.Up;
		_sinceClick = _clickRight ? null : 0; // a press next to a right-click is a menu choice, not a double click
	}

	bool Snapped(double x, double y, double threshold) => _sinceClick is not null && Near(x, y, threshold);

	bool Near(double x, double y, double threshold)
	{
		double dx = x - _anchorX, dy = y - _anchorY;
		return dx * dx + dy * dy <= threshold * threshold;
	}

	void PressLeft()
	{
		MoveTo(_anchorX, _anchorY);
		_events.Add(new PointerEvent.Button(false, true));
		_state = State.LeftDown;
	}

	void MoveTo(double x, double y)
	{
		if (_pointer == (x, y))
			return;
		_pointer = (x, y);
		_events.Add(new PointerEvent.Move(x, y));
	}
}
