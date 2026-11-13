namespace LinuxNativeDesktop.Capture;

/// <summary>
/// Turns a thumbstick held at some deflection into a steady stream of mouse-wheel steps. Speed grows linearly from
/// the edge of the dead zone to <c>stepsPerSecond</c> at full deflection; fractional steps carry over between frames.
/// </summary>
public sealed class ScrollAccumulator
{
	public const float DeadZone = 0.2f;
	/// <summary>Smooth-scroll units in one wheel notch (KWin's size).</summary>
	public const double UnitsPerNotch = 15;
	/// <summary>The part of the first step after pointer motion that is sent on its own, for Chromium to drop.</summary>
	public const double PrimerUnits = 1;

	double _vertical, _horizontal;

	/// <summary>
	/// The smooth-scroll events (dx, dy) for some wheel steps. XWayland reports absolute pointer motion and scrolling
	/// from different slave devices, and Chromium-based apps (Steam) drop the first scroll after each switch. So when
	/// the pointer has moved since the last scroll, a small slice of the steps goes first as a primer for them to drop.
	/// Vertical is negated: through KDE's portal a positive dy scrolls up (it arrives in X as wheel-up, button 4).
	/// </summary>
	public static IEnumerable<(double Dx, double Dy)> Events(int vertical, int horizontal, bool afterPointerMotion)
	{
		if (vertical == 0 && horizontal == 0)
			yield break;
		double dx = horizontal * UnitsPerNotch, dy = -vertical * UnitsPerNotch;
		if (afterPointerMotion)
		{
			double px = Math.Sign(horizontal) * PrimerUnits, py = -Math.Sign(vertical) * PrimerUnits;
			yield return (px, py);
			dx -= px;
			dy -= py;
		}
		yield return (dx, dy);
	}

	/// <param name="x">Thumbstick right (+) / left (-), -1..1.</param>
	/// <param name="y">Thumbstick up (+) / down (-), -1..1.</param>
	/// <returns>Steps to send: vertical positive = scroll down, horizontal positive = scroll right.</returns>
	public (int Vertical, int Horizontal) Update(float x, float y, double stepsPerSecond, double deltaTime)
	{
		double sx = Curve(x), sy = Curve(y);
		if (sx == 0)
			_horizontal = 0;
		if (sy == 0)
			_vertical = 0;
		// Stick up means scroll up, which is a negative (upward) wheel step.
		_vertical += -sy * stepsPerSecond * deltaTime;
		_horizontal += sx * stepsPerSecond * deltaTime;
		int v = (int)Math.Truncate(_vertical);
		int h = (int)Math.Truncate(_horizontal);
		_vertical -= v;
		_horizontal -= h;
		return (v, h);
	}

	/// <summary>0 inside the dead zone, then rescaled to 0..1 so speed starts from zero at its edge.</summary>
	static double Curve(float value)
	{
		if (float.IsNaN(value))
			return 0;
		double magnitude = Math.Min(Math.Abs(value), 1);
		if (magnitude <= DeadZone)
			return 0;
		return Math.Sign(value) * (magnitude - DeadZone) / (1 - DeadZone);
	}
}
