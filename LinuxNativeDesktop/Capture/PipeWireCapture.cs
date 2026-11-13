using System.Runtime.InteropServices;
using LinuxNativeDesktop.Capture.Native;
using static LinuxNativeDesktop.Capture.Native.PipeWire;

namespace LinuxNativeDesktop.Capture;

/// <summary>A PipeWire connection with its own loop thread. Streams created on it deliver frames on that thread.</summary>
public sealed class PipeWireConnection : IDisposable
{
	static readonly object InitLock = new();
	static bool _initialized;

	readonly ICaptureLog _log;
	readonly List<PipeWireVideoStream> _streams = [];
	IntPtr _loop, _context, _core;

	internal IntPtr Loop => _loop;
	internal IntPtr Core => _core;

	PipeWireConnection(ICaptureLog log) => _log = log;

	/// <summary>
	/// Connects to PipeWire. With <paramref name="fd"/> (from the portal's OpenPipeWireRemote) the connection
	/// takes ownership of the fd; without it, connects to the default daemon.
	/// </summary>
	public static PipeWireConnection Connect(int? fd, ICaptureLog log)
	{
		lock (InitLock)
		{
			if (!_initialized)
			{
				pw_init(IntPtr.Zero, IntPtr.Zero);
				_initialized = true;
			}
		}

		var c = new PipeWireConnection(log);
		try
		{
			c._loop = pw_thread_loop_new("LinuxNativeDesktop", IntPtr.Zero);
			if (c._loop == IntPtr.Zero)
				throw new InvalidOperationException("pw_thread_loop_new failed");
			c._context = pw_context_new(pw_thread_loop_get_loop(c._loop), IntPtr.Zero, 0);
			if (c._context == IntPtr.Zero)
				throw new InvalidOperationException("pw_context_new failed");
			if (pw_thread_loop_start(c._loop) < 0)
				throw new InvalidOperationException("pw_thread_loop_start failed");

			pw_thread_loop_lock(c._loop);
			try
			{
				c._core = fd is int f ? pw_context_connect_fd(c._context, f, IntPtr.Zero, 0) : pw_context_connect(c._context, IntPtr.Zero, 0);
			}
			finally
			{
				pw_thread_loop_unlock(c._loop);
			}
			if (c._core == IntPtr.Zero)
				throw new InvalidOperationException($"Could not connect to PipeWire (errno {Marshal.GetLastPInvokeError()})");
			return c;
		}
		catch
		{
			c.Dispose();
			throw;
		}
	}

	/// <param name="targetId">Node id to connect to, or <see cref="PipeWire.IdAny"/>.</param>
	/// <param name="extraProperties">Extra "key=value" stream properties, space separated (e.g. target.object=name).</param>
	public PipeWireVideoStream CreateStream(uint targetId, int maxFramerate, IFrameSink sink, string? extraProperties = null)
	{
		var stream = new PipeWireVideoStream(this, sink, _log);
		Locked(() => stream.Connect(targetId, maxFramerate, extraProperties));
		lock (_streams)
			_streams.Add(stream);
		return stream;
	}

	internal void Remove(PipeWireVideoStream stream)
	{
		lock (_streams)
			_streams.Remove(stream);
	}

	internal void Locked(Action action)
	{
		pw_thread_loop_lock(_loop);
		try { action(); }
		finally { pw_thread_loop_unlock(_loop); }
	}

	public void Dispose()
	{
		PipeWireVideoStream[] streams;
		lock (_streams)
			streams = [.. _streams];
		foreach (var s in streams)
			s.Dispose();

		if (_loop != IntPtr.Zero)
			pw_thread_loop_stop(_loop);
		if (_core != IntPtr.Zero)
			pw_core_disconnect(_core);
		if (_context != IntPtr.Zero)
			pw_context_destroy(_context);
		if (_loop != IntPtr.Zero)
			pw_thread_loop_destroy(_loop);
		_core = _context = _loop = IntPtr.Zero;
	}
}

/// <summary>A video capture stream that negotiates a 4-byte RGB format in CPU memory and hands frames to a sink.</summary>
public sealed class PipeWireVideoStream : IDisposable
{
	// Kept in static fields so the delegates are never collected while PipeWire holds the pointers.
	static readonly StateChangedFn StateChangedDelegate = OnStateChanged;
	static readonly ParamChangedFn ParamChangedDelegate = OnParamChanged;
	static readonly ProcessFn ProcessDelegate = OnProcess;

	readonly PipeWireConnection _connection;
	readonly IFrameSink _sink;
	readonly ICaptureLog _log;
	GCHandle _self;
	IntPtr _stream, _events, _hook;
	SpaPod.VideoFormat? _format;
	bool _disposed;

	internal StreamState State { get; private set; } = StreamState.Unconnected;

	/// <summary>True once the stream has hit an error (for example the user stopped sharing).</summary>
	public bool Failed { get; private set; }

	public SpaPod.VideoFormat? Format => _format;

	internal PipeWireVideoStream(PipeWireConnection connection, IFrameSink sink, ICaptureLog log)
	{
		_connection = connection;
		_sink = sink;
		_log = log;
	}

	// Called with the loop lock held.
	internal void Connect(uint targetId, int maxFramerate, string? extraProperties)
	{
		string props = "media.type=Video media.category=Capture media.role=Screen" + (extraProperties is null ? "" : " " + extraProperties);
		_stream = pw_stream_new(_connection.Core, "Resonite desktop", pw_properties_new_string(props));
		if (_stream == IntPtr.Zero)
			throw new InvalidOperationException("pw_stream_new failed");

		_self = GCHandle.Alloc(this);
		var events = new StreamEvents
		{
			Version = StreamEventsVersion,
			StateChanged = Marshal.GetFunctionPointerForDelegate(StateChangedDelegate),
			ParamChanged = Marshal.GetFunctionPointerForDelegate(ParamChangedDelegate),
			Process = Marshal.GetFunctionPointerForDelegate(ProcessDelegate),
		};
		_events = Marshal.AllocHGlobal(Marshal.SizeOf<StreamEvents>());
		Marshal.StructureToPtr(events, _events, false);
		_hook = Marshal.AllocHGlobal(HookSize);
		unsafe { new Span<byte>((void*)_hook, HookSize).Clear(); }
		pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));

		IntPtr pod = Alloc(SpaPod.BuildVideoEnumFormat(maxFramerate));
		try
		{
			int res = pw_stream_connect(_stream, DirectionInput, targetId, FlagAutoconnect | FlagMapBuffers, [pod], 1);
			if (res < 0)
				throw new InvalidOperationException($"pw_stream_connect failed ({res})");
		}
		finally
		{
			Marshal.FreeHGlobal(pod);
		}
	}

	static PipeWireVideoStream? From(IntPtr data) =>
		data == IntPtr.Zero ? null : GCHandle.FromIntPtr(data).Target as PipeWireVideoStream;

	static void OnStateChanged(IntPtr data, StreamState old, StreamState state, IntPtr error)
	{
		if (From(data) is not { } self)
			return;
		self.State = state;
		if (state == StreamState.Error)
		{
			self.Failed = true;
			self._log.Error($"PipeWire stream error: {GLib.Utf8(error)}");
		}
		else
		{
			self._log.Debug($"PipeWire stream {old} -> {state}");
			if (state == StreamState.Unconnected && old != StreamState.Unconnected)
				self.Failed = true;
		}
	}

	static void OnParamChanged(IntPtr data, uint id, IntPtr param)
	{
		if (id != SpaPod.ParamFormat || param == IntPtr.Zero || From(data) is not { } self)
			return;
		try
		{
			int size = 8 + Marshal.ReadInt32(param);
			var bytes = new byte[size];
			Marshal.Copy(param, bytes, 0, size);
			var format = SpaPod.ParseVideoFormat(bytes);
			if (format is not { } f || SpaPod.LayoutOf(f.Format) is null)
			{
				self._log.Warn($"PipeWire negotiated an unsupported format: {format}");
				return;
			}
			self._format = f;
			self._log.Debug($"PipeWire format: {f.Width}x{f.Height}, SPA video format {f.Format}");

			IntPtr pod = Alloc(SpaPod.BuildCpuBuffers());
			try { pw_stream_update_params(self._stream, [pod], 1); }
			finally { Marshal.FreeHGlobal(pod); }
		}
		catch (Exception e)
		{
			self._log.Error($"Handling PipeWire format failed: {e}");
		}
	}

	static void OnProcess(IntPtr data)
	{
		if (From(data) is not { } self)
			return;
		IntPtr stream = self._stream;
		IntPtr buffer = pw_stream_dequeue_buffer(stream);
		if (buffer == IntPtr.Zero)
			return;
		// Skip to the newest buffer; older ones are stale by now.
		for (IntPtr next; (next = pw_stream_dequeue_buffer(stream)) != IntPtr.Zero; buffer = next)
			pw_stream_queue_buffer(stream, buffer);
		try
		{
			self.Deliver(buffer);
		}
		catch (Exception e)
		{
			self._log.Error($"Delivering frame failed: {e}");
		}
		finally
		{
			pw_stream_queue_buffer(stream, buffer);
		}
	}

	unsafe void Deliver(IntPtr pwBuffer)
	{
		if (_format is not { } format || SpaPod.LayoutOf(format.Format) is not { } layout)
			return;
		IntPtr spaBuffer = Marshal.ReadIntPtr(pwBuffer);
		if (spaBuffer == IntPtr.Zero || Marshal.ReadInt32(spaBuffer, BufferNDatas) < 1)
			return;
		IntPtr spaData = Marshal.ReadIntPtr(spaBuffer, BufferDatas);
		IntPtr ptr = Marshal.ReadIntPtr(spaData, DataData);
		IntPtr chunk = Marshal.ReadIntPtr(spaData, DataChunk);
		if (ptr == IntPtr.Zero || chunk == IntPtr.Zero)
			return;

		uint maxSize = (uint)Marshal.ReadInt32(spaData, DataMaxSize);
		uint offset = (uint)Marshal.ReadInt32(chunk, ChunkOffset);
		uint size = (uint)Marshal.ReadInt32(chunk, ChunkSize);
		int stride = Marshal.ReadInt32(chunk, ChunkStride);
		int flags = Marshal.ReadInt32(chunk, ChunkFlags);
		if (size == 0 || (flags & ChunkFlagCorrupted) != 0 || offset >= maxSize)
			return;
		if (stride <= 0)
			stride = format.Width * 4;

		int length = (int)Math.Min(size, maxSize - offset);
		var frame = new VideoFrame(format.Width, format.Height, stride, layout, new ReadOnlySpan<byte>((byte*)ptr + offset, length));
		_sink.OnFrame(frame);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_connection.Remove(this);
		if (_stream != IntPtr.Zero)
		{
			// Destroying under the loop lock guarantees no callback is running or will run afterwards.
			_connection.Locked(() => pw_stream_destroy(_stream));
			_stream = IntPtr.Zero;
		}
		if (_events != IntPtr.Zero)
			Marshal.FreeHGlobal(_events);
		if (_hook != IntPtr.Zero)
			Marshal.FreeHGlobal(_hook);
		_events = _hook = IntPtr.Zero;
		if (_self.IsAllocated)
			_self.Free();
	}
}
