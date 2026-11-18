using System.Globalization;
using LinuxNativeDesktop.Capture;
using LinuxNativeDesktop.Input;
using Renderite.Shared;
using static LinuxNativeDesktop.Capture.Native.GLib;

namespace LinuxNativeDesktop.Tests;

public class KeyMapTests
{
	[Fact]
	public void WindowsKeyIsLeftMeta()
	{
		// The dash's Start button sends Key.LeftWindows; a Meta tap opens the KDE application launcher.
		Assert.Equal(LinuxKey.Code(125), KeyMap.Map(Key.LeftWindows));
		Assert.Equal(LinuxKey.Code(125), KeyMap.Map(Key.Windows));
		Assert.True(KeyMap.IsModifier(Key.LeftWindows));
	}

	[Theory]
	[InlineData(new[] { Key.A })]
	[InlineData(new[] { Key.LeftShift, Key.Alpha7 })]
	[InlineData(new[] { Key.At })]
	[InlineData(new[] { Key.Space })]
	[InlineData(new[] { Key.Keypad3 })]
	[InlineData(new[] { Key.KeypadPlus })]
	public void TypingIsNeverLogged(Key[] keys)
	{
		// Legacy text input sends every character as a key press; logs are public, so not even a "?" per character.
		Assert.Null(KeyMap.ForLog(keys));
	}

	[Theory]
	[InlineData(new[] { Key.LeftWindows }, "LeftWindows")]
	[InlineData(new[] { Key.Return }, "Return")]
	[InlineData(new[] { Key.Backspace }, "Backspace")]
	[InlineData(new[] { Key.LeftControl, Key.C }, "LeftControl+?")]
	[InlineData(new[] { Key.LeftShift, Key.Tab }, "LeftShift+Tab")]
	public void ShortcutsAndSpecialKeysAreLogged(Key[] keys, string expected) => Assert.Equal(expected, KeyMap.ForLog(keys));

	[Fact]
	public void ModifiersAreHeldAroundOtherKeys()
	{
		LinuxKey ctrl = KeyMap.Map(Key.LeftControl)!.Value, shift = KeyMap.Map(Key.LeftShift)!.Value, c = KeyMap.Map(Key.C)!.Value;
		Assert.Equal([(ctrl, true), (shift, true), (c, true), (c, false), (shift, false), (ctrl, false)],
			KeyMap.Tap([Key.LeftControl, Key.C, Key.LeftShift]));
	}

	[Fact]
	public void LoneModifiersAreTapped()
	{
		// The dash's Windows-logo button: Meta down and up opens the launcher.
		LinuxKey meta = LinuxKey.Code(KeyMap.LeftMeta);
		Assert.Equal([(meta, true), (meta, false)], KeyMap.Tap([Key.LeftWindows]));
	}

	[Theory]
	[InlineData(new[] { Key.F3 })]
	[InlineData(new[] { Key.LeftControl, Key.LeftAlt, Key.Delete })]
	[InlineData(new[] { Key.LeftShift, Key.A, Key.B })]
	public void EveryPressedKeyIsReleasedAfterItsPress(Key[] keys)
	{
		// The injector re-sends these releases later (engine fact 10), which only works if nothing is left held.
		var events = KeyMap.Tap(keys);
		foreach (var (key, i) in events.Select((e, i) => (e.Key, i)).Where(x => events[x.i].Pressed))
			Assert.Contains(events.Skip(i + 1), e => e.Key == key && !e.Pressed);
		Assert.Equal(events.Count(e => e.Pressed), events.Count(e => !e.Pressed));
	}

	[Fact]
	public void EveryKeyIsMapped()
	{
		// Covers every key the Windows injector handles, and more.
		var unmapped = Enum.GetValues<Key>().Where(k => k != Key.None && KeyMap.Map(k) is null).ToList();
		Assert.Empty(unmapped);
	}

	[Fact]
	public void CharacterKeysUseKeysymsSoTheLayoutDecides()
	{
		Assert.Equal(new LinuxKey(null, 'a'), KeyMap.Map(Key.A));
		Assert.Equal(new LinuxKey(null, 'z'), KeyMap.Map(Key.Z));
		Assert.Equal(new LinuxKey(null, '0'), KeyMap.Map(Key.Alpha0));
		Assert.Equal(new LinuxKey(null, '@'), KeyMap.Map(Key.At));
		Assert.Equal(new LinuxKey(null, '\\'), KeyMap.Map(Key.Backslash));
	}

	[Theory]
	[InlineData(Key.Escape, 1)]
	[InlineData(Key.Backspace, 14)]
	[InlineData(Key.Return, 28)]
	[InlineData(Key.Space, 57)]
	[InlineData(Key.F1, 59)]
	[InlineData(Key.F10, 68)]
	[InlineData(Key.F11, 87)]
	[InlineData(Key.F12, 88)]
	[InlineData(Key.F15, 185)]
	[InlineData(Key.LeftControl, 29)]
	[InlineData(Key.LeftShift, 42)]
	[InlineData(Key.LeftAlt, 56)]
	[InlineData(Key.UpArrow, 103)]
	[InlineData(Key.Delete, 111)]
	[InlineData(Key.Keypad0, 82)]
	[InlineData(Key.Keypad7, 71)]
	[InlineData(Key.KeypadEnter, 96)]
	public void SpecialKeysUseEvdevCodes(Key key, int code) => Assert.Equal(LinuxKey.Code(code), KeyMap.Map(key));

	[Fact]
	public void AllModifiersAreKeycodes()
	{
		foreach (var key in Enum.GetValues<Key>().Where(KeyMap.IsModifier))
			Assert.NotNull(KeyMap.Map(key)?.Keycode);
	}
}

public class KeysymTests
{
	[Theory]
	[InlineData('a', 0x61)]
	[InlineData(' ', 0x20)]
	[InlineData('~', 0x7e)]
	[InlineData('é', 0xe9)]
	[InlineData('€', 0x010020ac)]
	[InlineData('あ', 0x01003042)]
	public void CharactersMapToKeysyms(char c, int keysym) => Assert.Equal(keysym, Keysyms.FromCodePoint(c));

	[Theory]
	[InlineData(0x00)]
	[InlineData(0x1b)]
	[InlineData(0x7f)]
	[InlineData(0x85)]
	public void ControlCharactersHaveNone(int c) => Assert.Null(Keysyms.FromCodePoint(c));

	[Fact]
	public void TextWithControlsAndSurrogates()
	{
		Assert.Equal([0x48, 0x69, Keysyms.Return, Keysyms.Tab, 0x0101F600, Keysyms.BackSpace, Keysyms.Return],
			Keysyms.FromText("Hi\r\n\t😀\b\n\u0007"));
	}
}

public class PointerMappingTests
{
	static readonly DisplayRect[] OneDisplay = [new(0, 0, 2560, 1440)];

	[Fact]
	public void SameSizeMapsOneToOne()
	{
		var point = PointerMapping.Map(OneDisplay, [new(42, (0, 0), (2560, 1440))], [0], 100, 200);
		Assert.Equal(new StreamPoint(42, 100, 200), point);
	}

	[Fact]
	public void ScalesToTheStreamsLogicalSize()
	{
		var point = PointerMapping.Map(OneDisplay, [new(42, (0, 0), (1280, 720))], [0], 1280, 720);
		Assert.Equal(new StreamPoint(42, 640, 360), point);
	}

	[Fact]
	public void UsesTheDisplayUnderThePoint()
	{
		DisplayRect[] displays = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
		StreamInfo[] streams = [new(1, (0, 0), (1920, 1080)), new(2, (1920, 0), (1920, 1080))];
		Assert.Equal(new StreamPoint(2, 80, 40), PointerMapping.Map(displays, streams, [0, 1], 2000, 40));
	}

	[Fact]
	public void ClampsPointsOutsideToTheNearestEdge()
	{
		var point = PointerMapping.Map(OneDisplay, [new(42, null, null)], [0], 3000, -5);
		Assert.Equal(new StreamPoint(42, 2559, 0), point);
	}

	[Fact]
	public void UnsharedDisplayGivesNothing()
	{
		DisplayRect[] displays = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
		Assert.Null(PointerMapping.Map(displays, [new(2, null, null)], [-1, 0], 10, 10));
		Assert.Null(PointerMapping.Map([], [], [], 10, 10));
	}
}

/// <summary>The input messages, checked against the RemoteDesktop method signatures by the real GLib parser.</summary>
public class PortalInputMessageTests
{
	const string Session = "/org/freedesktop/portal/desktop/session/1_2/lnd1";

	[Theory]
	[InlineData("uint32 63, 100.0, 200.5", "(oa{sv}udd)")] // NotifyPointerMotionAbsolute
	[InlineData("272, uint32 1", "(oa{sv}iu)")]             // NotifyPointerButton / Keycode / Keysym
	[InlineData("uint32 0, -3", "(oa{sv}ui)")]              // NotifyPointerAxisDiscrete
	[InlineData("0.0, 15.0", "(oa{sv}dd)")]                 // NotifyPointerAxis
	public void NotifyParametersHaveTheRightSignature(string arguments, string signature)
	{
		IntPtr v = Parse(PortalSession.NotifyParameters(Session, arguments));
		try { Assert.Equal(signature, TypeOf(v)); }
		finally { g_variant_unref(v); }
	}

	[Fact]
	public void NotifyOptionsParse()
	{
		IntPtr v = Parse(PortalSession.NotifyParameters(Session, "0.0, 0.0", "{'finish': <true>}"));
		try { Assert.Equal("(oa{sv}dd)", TypeOf(v)); }
		finally { g_variant_unref(v); }
	}

	[Fact]
	public void DoublesAlwaysParseAsDoublesInAnyCulture()
	{
		var culture = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
			foreach (double value in new[] { 0, 1279, 640.25, 1e-7 })
			{
				IntPtr v = Parse($"({PortalSession.Double(value)},)");
				try { Assert.Equal("(d)", TypeOf(v)); }
				finally { g_variant_unref(v); }
			}
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
		}
	}
}

public class ScrollAccumulatorTests
{
	const double Speed = 12, Frame = 1 / 90.0;

	static (int Vertical, int Horizontal) Run(ScrollAccumulator s, float x, float y, double seconds)
	{
		int v = 0, h = 0;
		for (int i = 0; i < (int)Math.Round(seconds / Frame); i++)
		{
			var (dv, dh) = s.Update(x, y, Speed, Frame);
			v += dv;
			h += dh;
		}
		return (v, h);
	}

	[Fact]
	public void FullUpScrollsUpAtTheConfiguredRate()
	{
		var (v, h) = Run(new ScrollAccumulator(), 0, 1, 1);
		Assert.Equal(-12, v, tolerance: 1);
		Assert.Equal(0, h);
	}

	[Fact]
	public void DownAndRightArePositive()
	{
		var (v, h) = Run(new ScrollAccumulator(), 1, -1, 1);
		Assert.True(v >= 11, $"vertical {v}");
		Assert.True(h >= 11, $"horizontal {h}");
	}

	[Fact]
	public void DeadZoneDoesNothing() => Assert.Equal((0, 0), Run(new ScrollAccumulator(), 0.15f, -0.19f, 2));

	[Fact]
	public void SpeedGrowsWithDeflection()
	{
		int half = -Run(new ScrollAccumulator(), 0, 0.6f, 2).Vertical; // halfway between dead zone and full
		int full = -Run(new ScrollAccumulator(), 0, 1, 2).Vertical;
		Assert.InRange(half, full / 2 - 1, full / 2 + 1);
	}

	[Fact]
	public void ReleasingTheStickDropsLeftoverFractions()
	{
		var s = new ScrollAccumulator();
		Run(s, 0, 1, 0.05); // less than one step accumulated
		s.Update(0, 0, Speed, Frame);
		// Starting again needs a full step's worth of time, not the old remainder.
		Assert.Equal((0, 0), s.Update(0, 1, Speed, Frame));
	}

	[Fact]
	public void IgnoresGarbage() => Assert.Equal((0, 0), new ScrollAccumulator().Update(float.NaN, float.NaN, Speed, Frame));

	[Fact]
	public void StepsAreOneSmoothEventWhenThePointerStayedStillWithVerticalNegated() =>
		Assert.Equal([(15.0, 30.0)], ScrollAccumulator.Events(-2, 1, afterPointerMotion: false));

	[Fact]
	public void AfterPointerMotionASmallPrimerGoesFirst() =>
		Assert.Equal([(0.0, 1.0), (0.0, 29.0)], ScrollAccumulator.Events(-2, 0, afterPointerMotion: true));

	[Fact]
	public void PrimerCoversEachScrolledAxisAndKeepsTheTotal()
	{
		var events = ScrollAccumulator.Events(1, -1, afterPointerMotion: true).ToList();
		Assert.Equal((-1.0, -1.0), events[0]);
		Assert.Equal((-15.0, -15.0), (events.Sum(e => e.Dx), events.Sum(e => e.Dy)));
	}

	[Fact]
	public void NoStepsNoEvents() => Assert.Empty(ScrollAccumulator.Events(0, 0, afterPointerMotion: true));
}

public class TouchGestureTests
{
	const double Threshold = 30, Hold = 1, Frame = 0.1;

	static readonly PointerEvent LeftDown = new PointerEvent.Button(false, true), LeftUp = new PointerEvent.Button(false, false);
	static readonly PointerEvent RightDown = new PointerEvent.Button(true, true), RightUp = new PointerEvent.Button(true, false);
	static PointerEvent Move(double x, double y) => new PointerEvent.Move(x, y);

	static IReadOnlyList<PointerEvent> Press(TouchGesture g, double x, double y, double hold = Hold) => g.Update(x, y, true, Threshold, hold, Frame);
	static IReadOnlyList<PointerEvent> Release(TouchGesture g, double x, double y) => g.Update(x, y, false, Threshold, Hold, Frame);

	[Fact]
	public void HoveringFollowsTheLaserWithoutRepeats()
	{
		var g = new TouchGesture();
		Assert.Equal([Move(5, 7)], Release(g, 5, 7));
		Assert.Empty(Release(g, 5, 7));
	}

	[Fact]
	public void ATapClicksAtThePressPointDespiteShake()
	{
		var g = new TouchGesture();
		Release(g, 100, 100);
		Assert.Empty(Press(g, 100, 100)); // nothing until the gesture is known
		Assert.Empty(Press(g, 120, 110));
		Assert.Equal([LeftDown], Release(g, 80, 79));
		Assert.Equal([LeftUp], Release(g, 80, 79)); // the pointer stays on the click for a double click
	}

	[Fact]
	public void AClickHoldsItsButtonForTheClickLength()
	{
		// Sent microseconds apart, a release can get lost and leave the button held.
		var g = new TouchGesture();
		g.Update(100, 100, true, Threshold, Hold, 0.01);
		Assert.Equal([LeftDown], g.Update(110, 100, false, Threshold, Hold, 0.01));
		for (int i = 0; i < 4; i++) // 0.04 s
			Assert.Empty(g.Update(110, 100, false, Threshold, Hold, 0.01));
		Assert.Equal([LeftUp], g.Update(110, 100, false, Threshold, Hold, 0.02));
	}

	[Fact]
	public void PressingAgainEndsTheClickFirst()
	{
		var g = new TouchGesture();
		g.Update(100, 100, true, Threshold, Hold, 0.01);
		g.Update(100, 100, false, Threshold, Hold, 0.01);
		Assert.Equal([LeftUp, Move(150, 100)], g.Update(150, 100, true, Threshold, Hold, 0.01));
		Assert.Null(g.HeldRight);
		Assert.Equal([LeftDown], g.Update(150, 100, false, Threshold, Hold, 0.01)); // the double click's second
	}

	[Fact]
	public void MovingAwayPressesAtThePressPointThenDrags()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Assert.Equal([LeftDown, Move(131, 100)], Press(g, 131, 100));
		Assert.Equal([Move(105, 100)], Press(g, 105, 100)); // back inside the threshold, the drag goes on
		Assert.Equal([LeftUp, Move(105, 101)], Release(g, 105, 101));
	}

	[Fact]
	public void HoldingStillRightClicksOnRelease()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		for (int i = 0; i < 11; i++) // 1.1 s
			Assert.Empty(Press(g, 110, 100));
		Assert.Empty(Press(g, 200, 200)); // past the hold time, moving doesn't drag
		Assert.Equal([RightDown], Release(g, 200, 200));
		Assert.Equal(true, g.HeldRight);
		Assert.Equal([RightUp, Move(200, 200)], Release(g, 200, 200));
	}

	[Fact]
	public void MovingBeforeTheHoldTimeIsADrag()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		for (int i = 0; i < 9; i++)
			Press(g, 100, 100);
		Assert.Equal([LeftDown, Move(200, 100)], Press(g, 200, 100));
		for (int i = 0; i < 20; i++)
			Press(g, 200, 100);
		Assert.Equal([LeftUp], Release(g, 200, 100));
	}

	[Fact]
	public void WithoutHoldTimeTheLeftButtonGoesDownAtOnce()
	{
		var g = new TouchGesture();
		Assert.Equal([Move(100, 100), LeftDown], Press(g, 100, 100, hold: 0));
		Assert.Empty(Press(g, 120, 100, hold: 0));
		Assert.Equal(false, g.HeldRight);
		Assert.Equal([LeftUp, Move(120, 100)], Release(g, 120, 100));
	}

	[Fact]
	public void AQuickSecondTapNearTheFirstLandsOnTheSameSpot()
	{
		var g = new TouchGesture();
		Release(g, 100, 100);
		Press(g, 100, 100);
		Assert.Equal([LeftDown], Release(g, 110, 100));
		Assert.Equal([LeftUp], Release(g, 112, 104)); // the pointer stays on the click
		Assert.Empty(Release(g, 115, 106));
		Assert.Empty(Press(g, 118, 108)); // pressed where the first click was
		Assert.Equal([LeftDown], Release(g, 120, 110));
		Assert.Equal([LeftUp], Release(g, 120, 110)); // and a third tap would make a triple click
	}

	[Fact]
	public void AfterTheDoubleClickTimeThePointerFollowsAgain()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Release(g, 100, 100);
		Release(g, 100, 100); // click over
		for (int i = 0; i < 5; i++)
			Assert.Empty(Release(g, 110, 100));
		Assert.Equal([Move(110, 100)], Release(g, 110, 100)); // 0.6 s after the click
	}

	[Fact]
	public void MovingAwayFromAClickEndsTheSnapping()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Release(g, 100, 100);
		Release(g, 100, 100);
		Assert.Equal([Move(140, 100)], Release(g, 140, 100));
		Assert.Equal([Move(110, 100)], Release(g, 110, 100)); // coming back doesn't snap again
		Assert.Empty(Press(g, 110, 100));
		Assert.Equal([LeftDown], Release(g, 110, 100));
	}

	[Fact]
	public void APressNextToARightClickIsNotSnapped()
	{
		var g = new TouchGesture();
		for (int i = 0; i < 12; i++)
			Press(g, 100, 100);
		Release(g, 100, 100);
		Assert.Equal([RightUp], Release(g, 100, 100));
		Assert.Equal([Move(110, 100)], Release(g, 110, 100)); // onto the context menu
	}

	[Fact]
	public void AfterAnOutsideMoveTheNextClickGoesBackToItsPressPoint()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Release(g, 100, 100);
		Release(g, 100, 100); // clicked
		g.PointerMoved(); // a secondary-action right-click at the laser
		Assert.Equal([Move(110, 100)], Press(g, 110, 100)); // not snapped to the click any more
		g.PointerMoved(); // and again during the press
		Assert.Equal([Move(110, 100), LeftDown], Release(g, 110, 100));
	}

	[Fact]
	public void CancelDropsAPressThatHasNotClickedYet()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Assert.True(g.Active);
		Assert.Empty(g.Cancel());
		Assert.False(g.Active);
		Assert.Equal([Move(500, 500)], Press(g, 500, 500)); // a new press starts at its own point
		Assert.Empty(Press(g, 510, 500));
	}

	[Fact]
	public void CancelReleasesAButtonThatIsDown()
	{
		var g = new TouchGesture();
		Press(g, 100, 100);
		Press(g, 200, 100); // dragging
		Assert.Equal([LeftUp], g.Cancel());
		Assert.Null(g.HeldRight);

		for (int i = 0; i < 12; i++)
			Press(g, 100, 100); // held past the hold time
		Release(g, 100, 100);
		Assert.Equal(true, g.HeldRight); // mid-click
		Assert.Equal([RightUp], g.Cancel());
		Assert.Empty(g.Cancel());
	}
}
