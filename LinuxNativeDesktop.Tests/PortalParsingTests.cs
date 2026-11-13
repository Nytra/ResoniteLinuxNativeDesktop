using LinuxNativeDesktop.Capture;
using static LinuxNativeDesktop.Capture.Native.GLib;

namespace LinuxNativeDesktop.Tests;

/// <summary>Runs the portal's GVariant handling against the real GLib, with replies shaped like xdg-desktop-portal's.</summary>
public class PortalParsingTests
{
	[Fact]
	public void ParsesStartResults()
	{
		IntPtr results = Parse("""
			{'streams': <[(uint32 63, {'position': <(0, 0)>, 'size': <(2560, 1440)>, 'source_type': <uint32 1>}),
			              (uint32 64, {'id': <'1'>})]>,
			 'restore_token': <'3d3b2c1a'>}
			""");
		try
		{
			var streams = PortalSession.ParseStreams(results);
			Assert.Equal([new StreamInfo(63, (0, 0), (2560, 1440)), new StreamInfo(64, null, null)], streams);
			Assert.Equal("3d3b2c1a", PortalSession.LookupString(results, "restore_token"));
			Assert.Null(PortalSession.LookupString(results, "missing"));
		}
		finally
		{
			g_variant_unref(results);
		}
	}

	[Fact]
	public void MissingStreamsIsEmpty()
	{
		IntPtr results = Parse("@a{sv} {}");
		try { Assert.Empty(PortalSession.ParseStreams(results)); }
		finally { g_variant_unref(results); }
	}

	[Fact]
	public void ReadsResponseSignals()
	{
		IntPtr parameters = Parse("(uint32 1, {'restore_token': <'abc'>})");
		try
		{
			var (code, results) = PortalSession.ReadResponse(parameters)!.Value;
			Assert.Equal(1u, code);
			Assert.Equal("abc", PortalSession.LookupString(results, "restore_token"));
			g_variant_unref(results);
		}
		finally
		{
			g_variant_unref(parameters);
		}
	}

	[Theory]
	[InlineData("('forged',)")]
	[InlineData("(uint32 0,)")]
	[InlineData("(uint32 0, 'not a dict')")]
	public void IgnoresMisshapenResponseSignals(string text)
	{
		// Signal subscriptions don't check signatures; reading these as (ua{sv}) would hit GLib criticals or NULLs.
		IntPtr parameters = Parse(text);
		try { Assert.Null(PortalSession.ReadResponse(parameters)); }
		finally { g_variant_unref(parameters); }
	}

	[SkippableFact]
	public void ResolvesBusNameOwners()
	{
		IntPtr bus = g_bus_get_sync(BusTypeSession, IntPtr.Zero, out IntPtr error);
		if (error != IntPtr.Zero)
			g_error_free(error);
		Skip.If(bus == IntPtr.Zero, "No D-Bus session bus");
		try
		{
			// The bus daemon owns its own name; our own unique name owns itself.
			Assert.Equal("org.freedesktop.DBus", PortalSession.GetNameOwner(bus, "org.freedesktop.DBus"));
			string self = Utf8(g_dbus_connection_get_unique_name(bus))!;
			Assert.Equal(self, PortalSession.GetNameOwner(bus, self));
			Assert.Null(PortalSession.GetNameOwner(bus, "org.example.LinuxNativeDesktop.Nobody"));
		}
		finally
		{
			g_object_unref(bus);
		}
	}

	[Fact]
	public void SessionHandleMayBeAnObjectPath()
	{
		IntPtr results = Parse("{'session_handle': <objectpath '/org/freedesktop/portal/desktop/session/1_2/t'>}");
		try { Assert.Equal("/org/freedesktop/portal/desktop/session/1_2/t", PortalSession.LookupString(results, "session_handle")); }
		finally { g_variant_unref(results); }
	}

	[Theory]
	[InlineData("plain")]
	[InlineData("it's")]
	[InlineData(@"back\slash")]
	[InlineData("ünïcode ✓")]
	public void QuotedStringsRoundTrip(string value)
	{
		IntPtr v = Parse(PortalSession.Quote(value));
		try { Assert.Equal(value, Utf8(g_variant_get_string(v, IntPtr.Zero))); }
		finally { g_variant_unref(v); }
	}

	[Fact]
	public void RequestParametersParse()
	{
		// The exact shapes PortalSession sends, so a typo in the text format fails here and not in the game.
		string session = PortalSession.Quote("/org/freedesktop/portal/desktop/session/1_2/lnd1");
		string[] texts =
		[
			"({'handle_token': <'lnd1'>, 'session_handle_token': <'lnd2'>},)",
			$"(objectpath {session}, {{'handle_token': <'lnd3'>, 'types': <uint32 1>, 'multiple': <true>, 'cursor_mode': <uint32 2>, 'persist_mode': <uint32 2>, 'restore_token': <'abc'>}})",
			$"(objectpath {session}, '', {{'handle_token': <'lnd4'>}})",
			$"(objectpath {session}, @a{{sv}} {{}})",
		];
		string[] types = ["(a{sv})", "(oa{sv})", "(osa{sv})", "(oa{sv})"];
		for (int i = 0; i < texts.Length; i++)
		{
			IntPtr v = Parse(texts[i]);
			try { Assert.Equal(types[i], TypeOf(v)); }
			finally { g_variant_unref(v); }
		}
	}
}
