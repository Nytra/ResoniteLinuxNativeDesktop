using System.Runtime.InteropServices;

namespace LinuxNativeDesktop.Capture.Native;

/// <summary>The parts of GLib/GIO used to talk to xdg-desktop-portal over D-Bus. Present in the Steam runtime.</summary>
internal static class GLib
{
	const string LibGLib = "libglib-2.0.so.0";
	const string LibGObject = "libgobject-2.0.so.0";
	const string LibGio = "libgio-2.0.so.0";

	public const int BusTypeSession = 2;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate void SignalCallback(IntPtr connection, IntPtr senderName, IntPtr objectPath, IntPtr interfaceName, IntPtr signalName, IntPtr parameters, IntPtr userData);

	[DllImport(LibGio)] public static extern IntPtr g_bus_get_sync(int busType, IntPtr cancellable, out IntPtr error);
	[DllImport(LibGio)] public static extern IntPtr g_dbus_connection_get_unique_name(IntPtr connection);

	[DllImport(LibGio)]
	public static extern IntPtr g_dbus_connection_call_sync(IntPtr connection,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string busName, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectPath,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName, [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
		IntPtr parameters, IntPtr replyType, int flags, int timeoutMsec, IntPtr cancellable, out IntPtr error);

	[DllImport(LibGio)]
	public static extern IntPtr g_dbus_connection_call_with_unix_fd_list_sync(IntPtr connection,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string busName, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectPath,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName, [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
		IntPtr parameters, IntPtr replyType, int flags, int timeoutMsec, IntPtr fdList, out IntPtr outFdList, IntPtr cancellable, out IntPtr error);

	[DllImport(LibGio)]
	public static extern void g_dbus_connection_call(IntPtr connection,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string busName, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectPath,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName, [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
		IntPtr parameters, IntPtr replyType, int flags, int timeoutMsec, IntPtr cancellable, IntPtr callback, IntPtr userData);

	[DllImport(LibGio)]
	public static extern uint g_dbus_connection_signal_subscribe(IntPtr connection,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string? sender, [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string member, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectPath,
		IntPtr arg0, int flags, SignalCallback callback, IntPtr userData, IntPtr userDataFreeFunc);

	[DllImport(LibGio)] public static extern void g_dbus_connection_signal_unsubscribe(IntPtr connection, uint subscriptionId);
	[DllImport(LibGio)] public static extern int g_unix_fd_list_get(IntPtr list, int index, out IntPtr error);

	[DllImport(LibGLib)] public static extern IntPtr g_main_context_new();
	[DllImport(LibGLib)] public static extern void g_main_context_unref(IntPtr context);
	[DllImport(LibGLib)] public static extern void g_main_context_push_thread_default(IntPtr context);
	[DllImport(LibGLib)] public static extern void g_main_context_pop_thread_default(IntPtr context);
	[DllImport(LibGLib)] public static extern int g_main_context_iteration(IntPtr context, int mayBlock);

	[DllImport(LibGLib)]
	public static extern IntPtr g_variant_parse(IntPtr type, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, IntPtr limit, IntPtr endptr, out IntPtr error);
	[DllImport(LibGLib)] public static extern void g_variant_unref(IntPtr value);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_get_child_value(IntPtr value, nuint index);
	[DllImport(LibGLib)] public static extern nuint g_variant_n_children(IntPtr value);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_get_type_string(IntPtr value);
	[DllImport(LibGLib)] public static extern uint g_variant_get_uint32(IntPtr value);
	[DllImport(LibGLib)] public static extern int g_variant_get_int32(IntPtr value);
	[DllImport(LibGLib)] public static extern int g_variant_get_handle(IntPtr value);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_get_string(IntPtr value, IntPtr length);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_get_variant(IntPtr value);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_lookup_value(IntPtr dictionary, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr expectedType);
	[DllImport(LibGLib)] public static extern IntPtr g_variant_print(IntPtr value, int typeAnnotate);

	[DllImport(LibGLib)] public static extern void g_error_free(IntPtr error);
	[DllImport(LibGLib)] public static extern void g_free(IntPtr mem);
	[DllImport(LibGObject)] public static extern void g_object_unref(IntPtr obj);

	/// <summary>Throws with the GError message if <paramref name="error"/> is set, and frees it.</summary>
	public static void Check(IntPtr error, string what)
	{
		if (error == IntPtr.Zero)
			return;
		// struct GError { GQuark domain; gint code; gchar *message; }
		string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
		g_error_free(error);
		throw new InvalidOperationException($"{what}: {message}");
	}

	public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

	public static IntPtr Parse(string text)
	{
		IntPtr v = g_variant_parse(IntPtr.Zero, text, IntPtr.Zero, IntPtr.Zero, out IntPtr error);
		Check(error, "Parsing GVariant"); // not the text: it can hold a restore token, and errors are logged
		return v;
	}

	public static string Print(IntPtr value)
	{
		IntPtr s = g_variant_print(value, 1);
		try { return Utf8(s) ?? ""; }
		finally { g_free(s); }
	}

	public static string TypeOf(IntPtr value) => Utf8(g_variant_get_type_string(value)) ?? "";
}
