using LinuxNativeDesktop.Capture;
using Renderite.Shared;

namespace LinuxNativeDesktop.Input;

/// <summary>How to send one Resonite <see cref="Key"/> through the RemoteDesktop portal.</summary>
public readonly record struct LinuxKey(int? Keycode, int? Keysym)
{
	public static LinuxKey Code(int evdev) => new(evdev, null);

	public static LinuxKey Sym(char c) => new(null, Keysyms.FromCodePoint(c));
}

/// <summary>
/// Maps Resonite keys to Linux input. Keys that produce characters are sent as keysyms, so the compositor picks the
/// right physical key (and Shift) for the user's layout, which also covers Shift-only symbols like '@'. Everything
/// else is sent as an evdev keycode (linux/input-event-codes.h).
/// </summary>
public static class KeyMap
{
	// linux/input-event-codes.h
	public const int LeftShift = 42, LeftCtrl = 29, LeftAlt = 56, LeftMeta = 125;
	public const int BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112;

	public static bool IsModifier(Key key) => key is Key.LeftShift or Key.RightShift or Key.Shift
		or Key.LeftControl or Key.RightControl or Key.Control
		or Key.LeftAlt or Key.RightAlt or Key.Alt or Key.AltGr
		or Key.LeftWindows or Key.RightWindows or Key.Windows
		or Key.LeftApple or Key.RightApple or Key.LeftCommand or Key.RightCommand;

	/// <summary>
	/// A key press for the debug log, or null if it types text. Logs get posted in bug reports, so they must not show
	/// what was typed, nor how much (Resonite's legacy text input sends every character as a key press). Shortcuts
	/// are logged with their character keys as "?".
	/// </summary>
	public static string? ForLog(IReadOnlyList<Key> keys) =>
		keys.Any(IsCharacter) && keys.All(k => IsCharacter(k) || k is Key.LeftShift or Key.RightShift or Key.Shift)
			? null
			: string.Join("+", keys.Select(k => IsCharacter(k) ? "?" : k.ToString()));

	static bool IsCharacter(Key key) => key is Key.Space or >= Key.Keypad0 and <= Key.KeypadEquals || Map(key)?.Keysym is not null;

	/// <summary>
	/// The events for one key press list: modifiers held around the other keys (Ctrl+C), or, with only modifiers,
	/// tapped together (the dash's Windows-logo button sends a lone Windows key: Meta opens the launcher).
	/// Every key pressed is released again.
	/// </summary>
	public static List<(LinuxKey Key, bool Pressed)> Tap(IEnumerable<Key> keys)
	{
		var modifiers = keys.Where(IsModifier).Select(Map).OfType<LinuxKey>().ToList();
		var events = modifiers.Select(m => (m, true)).ToList();
		foreach (var key in keys.Where(k => !IsModifier(k)).Select(Map).OfType<LinuxKey>())
			events.AddRange([(key, true), (key, false)]);
		events.AddRange(modifiers.AsEnumerable().Reverse().Select(m => (m, false)));
		return events;
	}

	public static LinuxKey? Map(Key key)
	{
		if (key is >= Key.A and <= Key.Z)
			return LinuxKey.Sym((char)('a' + (key - Key.A)));
		if (key is >= Key.Alpha0 and <= Key.Alpha9)
			return LinuxKey.Sym((char)('0' + (key - Key.Alpha0)));
		if (key is >= Key.F1 and <= Key.F10)
			return LinuxKey.Code(59 + (key - Key.F1));
		if (key is >= Key.F13 and <= Key.F15)
			return LinuxKey.Code(183 + (key - Key.F13));

		return key switch
		{
			Key.Backspace => LinuxKey.Code(14),
			Key.Tab => LinuxKey.Code(15),
			Key.Clear => LinuxKey.Code(0x163),
			Key.Return => LinuxKey.Code(28),
			Key.Pause or Key.Break => LinuxKey.Code(119),
			Key.Escape => LinuxKey.Code(1),
			Key.Space => LinuxKey.Code(57),
			Key.Delete => LinuxKey.Code(111),
			Key.F11 => LinuxKey.Code(87),
			Key.F12 => LinuxKey.Code(88),

			Key.Keypad0 => LinuxKey.Code(82),
			Key.Keypad1 => LinuxKey.Code(79),
			Key.Keypad2 => LinuxKey.Code(80),
			Key.Keypad3 => LinuxKey.Code(81),
			Key.Keypad4 => LinuxKey.Code(75),
			Key.Keypad5 => LinuxKey.Code(76),
			Key.Keypad6 => LinuxKey.Code(77),
			Key.Keypad7 => LinuxKey.Code(71),
			Key.Keypad8 => LinuxKey.Code(72),
			Key.Keypad9 => LinuxKey.Code(73),
			Key.KeypadPeriod => LinuxKey.Code(83),
			Key.KeypadDivide => LinuxKey.Code(98),
			Key.KeypadMultiply => LinuxKey.Code(55),
			Key.KeypadMinus => LinuxKey.Code(74),
			Key.KeypadPlus => LinuxKey.Code(78),
			Key.KeypadEnter => LinuxKey.Code(96),
			Key.KeypadEquals => LinuxKey.Code(117),

			Key.UpArrow => LinuxKey.Code(103),
			Key.DownArrow => LinuxKey.Code(108),
			Key.RightArrow => LinuxKey.Code(106),
			Key.LeftArrow => LinuxKey.Code(105),
			Key.Insert => LinuxKey.Code(110),
			Key.Home => LinuxKey.Code(102),
			Key.End => LinuxKey.Code(107),
			Key.PageUp => LinuxKey.Code(104),
			Key.PageDown => LinuxKey.Code(109),

			Key.Numlock => LinuxKey.Code(69),
			Key.CapsLock => LinuxKey.Code(58),
			Key.ScrollLock => LinuxKey.Code(70),
			Key.LeftShift or Key.Shift => LinuxKey.Code(LeftShift),
			Key.RightShift => LinuxKey.Code(54),
			Key.LeftControl or Key.Control => LinuxKey.Code(LeftCtrl),
			Key.RightControl => LinuxKey.Code(97),
			Key.LeftAlt or Key.Alt => LinuxKey.Code(LeftAlt),
			Key.RightAlt or Key.AltGr => LinuxKey.Code(100),
			Key.LeftWindows or Key.Windows or Key.LeftApple or Key.LeftCommand => LinuxKey.Code(LeftMeta),
			Key.RightWindows or Key.RightApple or Key.RightCommand => LinuxKey.Code(126),
			Key.Menu => LinuxKey.Code(127), // the context-menu key is KEY_COMPOSE in evdev
			Key.Help => LinuxKey.Code(138),
			Key.Print or Key.SysReq => LinuxKey.Code(99),

			Key.Exclaim => LinuxKey.Sym('!'),
			Key.DoubleQuote => LinuxKey.Sym('"'),
			Key.Hash => LinuxKey.Sym('#'),
			Key.Dollar => LinuxKey.Sym('$'),
			Key.Percent => LinuxKey.Sym('%'),
			Key.Ampersand => LinuxKey.Sym('&'),
			Key.Quote => LinuxKey.Sym('\''),
			Key.LeftParenthesis => LinuxKey.Sym('('),
			Key.RightParenthesis => LinuxKey.Sym(')'),
			Key.Asterisk => LinuxKey.Sym('*'),
			Key.Plus => LinuxKey.Sym('+'),
			Key.Comma => LinuxKey.Sym(','),
			Key.Minus => LinuxKey.Sym('-'),
			Key.Period => LinuxKey.Sym('.'),
			Key.Slash => LinuxKey.Sym('/'),
			Key.Colon => LinuxKey.Sym(':'),
			Key.Semicolon => LinuxKey.Sym(';'),
			Key.Less => LinuxKey.Sym('<'),
			Key.Equals => LinuxKey.Sym('='),
			Key.Greater => LinuxKey.Sym('>'),
			Key.Question => LinuxKey.Sym('?'),
			Key.At => LinuxKey.Sym('@'),
			Key.LeftBracket => LinuxKey.Sym('['),
			Key.Backslash => LinuxKey.Sym('\\'),
			Key.RightBracket => LinuxKey.Sym(']'),
			Key.Caret => LinuxKey.Sym('^'),
			Key.Underscore => LinuxKey.Sym('_'),
			Key.BackQuote => LinuxKey.Sym('`'),
			Key.LeftBrace => LinuxKey.Sym('{'),
			Key.VerticalBar => LinuxKey.Sym('|'),
			Key.RightBrace => LinuxKey.Sym('}'),
			Key.Tilde => LinuxKey.Sym('~'),
			_ => null,
		};
	}
}
