using System.Runtime.InteropServices;

namespace LinuxNativeDesktop.Capture.Native;

/// <summary>libpipewire-0.3 entry points and struct layouts (checked with tools/spa-reference.c).</summary>
internal static class PipeWire
{
	const string Lib = "libpipewire-0.3.so.0";

	public const uint IdAny = 0xffffffff;
	public const int DirectionInput = 0;
	public const int FlagAutoconnect = 1 << 0;
	public const int FlagMapBuffers = 1 << 2;
	public const int StreamEventsVersion = 2;
	public const int HookSize = 48;
	public const int ChunkFlagCorrupted = 1;

	public enum StreamState
	{
		Error = -1,
		Unconnected = 0,
		Connecting = 1,
		Paused = 2,
		Streaming = 3,
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate void StateChangedFn(IntPtr data, StreamState old, StreamState state, IntPtr error);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate void ParamChangedFn(IntPtr data, uint id, IntPtr param);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate void ProcessFn(IntPtr data);

	/// <summary>struct pw_stream_events (version 2, 96 bytes).</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct StreamEvents
	{
		public uint Version;
		public IntPtr Destroy;
		public IntPtr StateChanged;
		public IntPtr ControlInfo;
		public IntPtr IoChanged;
		public IntPtr ParamChanged;
		public IntPtr AddBuffer;
		public IntPtr RemoveBuffer;
		public IntPtr Process;
		public IntPtr Drained;
		public IntPtr Command;
		public IntPtr TriggerDone;
	}

	// struct spa_buffer { uint32 n_metas; uint32 n_datas; spa_meta *metas; spa_data *datas; }
	public const int BufferNDatas = 4;
	public const int BufferDatas = 16;
	// struct spa_data { uint32 type; uint32 flags; int64 fd; uint32 mapoffset; uint32 maxsize; void *data; spa_chunk *chunk; }
	public const int DataMaxSize = 20;
	public const int DataData = 24;
	public const int DataChunk = 32;
	// struct spa_chunk { uint32 offset; uint32 size; int32 stride; int32 flags; }
	public const int ChunkOffset = 0;
	public const int ChunkSize = 4;
	public const int ChunkStride = 8;
	public const int ChunkFlags = 12;

	[DllImport(Lib)] public static extern void pw_init(IntPtr argc, IntPtr argv);
	[DllImport(Lib)] public static extern IntPtr pw_thread_loop_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
	[DllImport(Lib)] public static extern IntPtr pw_thread_loop_get_loop(IntPtr loop);
	[DllImport(Lib)] public static extern int pw_thread_loop_start(IntPtr loop);
	[DllImport(Lib)] public static extern void pw_thread_loop_stop(IntPtr loop);
	[DllImport(Lib)] public static extern void pw_thread_loop_destroy(IntPtr loop);
	[DllImport(Lib)] public static extern void pw_thread_loop_lock(IntPtr loop);
	[DllImport(Lib)] public static extern void pw_thread_loop_unlock(IntPtr loop);

	[DllImport(Lib)] public static extern IntPtr pw_context_new(IntPtr loop, IntPtr props, nuint userDataSize);
	[DllImport(Lib)] public static extern void pw_context_destroy(IntPtr context);
	[DllImport(Lib)] public static extern IntPtr pw_context_connect(IntPtr context, IntPtr props, nuint userDataSize);
	[DllImport(Lib)] public static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
	[DllImport(Lib)] public static extern int pw_core_disconnect(IntPtr core);

	[DllImport(Lib)] public static extern IntPtr pw_properties_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string args);

	[DllImport(Lib)] public static extern IntPtr pw_stream_new(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
	[DllImport(Lib)] public static extern void pw_stream_add_listener(IntPtr stream, IntPtr listener, IntPtr events, IntPtr data);
	[DllImport(Lib)] public static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, int flags, IntPtr[] parameters, uint nParams);
	[DllImport(Lib)] public static extern void pw_stream_destroy(IntPtr stream);
	[DllImport(Lib)] public static extern int pw_stream_update_params(IntPtr stream, IntPtr[] parameters, uint nParams);
	[DllImport(Lib)] public static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);
	[DllImport(Lib)] public static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);

	/// <summary>Copies a pod into unmanaged memory, which PipeWire reads synchronously during the call it is passed to.</summary>
	public static IntPtr Alloc(byte[] pod)
	{
		IntPtr p = Marshal.AllocHGlobal(pod.Length);
		Marshal.Copy(pod, 0, p, pod.Length);
		return p;
	}
}
