using System.Text;

namespace LinuxNativeDesktop.Capture;

/// <summary>X11 keysyms (xkbcommon/xkbcommon-keysyms.h) for text and the few control characters typing produces.</summary>
public static class Keysyms
{
	public const int BackSpace = 0xff08;
	public const int Tab = 0xff09;
	public const int Return = 0xff0d;

	/// <summary>
	/// Latin-1 printable characters are their own keysym; every other Unicode character is 0x01000000 + code point.
	/// Returns null for control characters.
	/// </summary>
	public static int? FromCodePoint(int codePoint) => codePoint switch
	{
		< 0x20 or 0x7f => null,
		< 0x7f or (>= 0xa0 and <= 0xff) => codePoint,
		>= 0x80 and < 0xa0 => null, // C1 controls
		_ => 0x01000000 | codePoint,
	};

	/// <summary>
	/// Turns typed text into keysyms. Backspace, newlines and tab become their keys (\r\n counts once);
	/// other control characters are dropped.
	/// </summary>
	public static List<int> FromText(string text)
	{
		var result = new List<int>(text.Length);
		int previous = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			int c = rune.Value;
			switch (c)
			{
				case '\b': result.Add(BackSpace); break;
				case '\t': result.Add(Tab); break;
				case '\r': result.Add(Return); break;
				case '\n' when previous != '\r': result.Add(Return); break;
				default:
					if (FromCodePoint(c) is int sym)
						result.Add(sym);
					break;
			}
			previous = c;
		}
		return result;
	}
}
