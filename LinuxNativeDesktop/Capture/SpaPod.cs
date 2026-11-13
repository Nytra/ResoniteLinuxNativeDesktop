using System.Buffers.Binary;

namespace LinuxNativeDesktop.Capture;

/// <summary>
/// Builds and reads the few SPA pods needed to negotiate a raw video stream with PipeWire.
/// The SPA builder is header-only C, so the binary format is written by hand here;
/// tools/spa-reference.c prints the bytes the real builder makes, and the tests compare against them.
/// </summary>
public static class SpaPod
{
	// spa/utils/type.h
	const uint TypeId = 3;
	const uint TypeInt = 4;
	const uint TypeRectangle = 10;
	const uint TypeFraction = 11;
	const uint TypeObject = 15;
	const uint TypeChoice = 19;
	const uint ObjectFormat = 0x40003;
	const uint ObjectParamBuffers = 0x40004;

	// spa/pod/pod.h
	const uint ChoiceNone = 0;
	const uint ChoiceRange = 1;
	const uint ChoiceEnum = 3;
	const uint ChoiceFlags = 4;

	// spa/param/param.h
	public const uint ParamEnumFormat = 3;
	public const uint ParamFormat = 4;
	public const uint ParamBuffers = 5;

	// spa/param/format.h, buffers.h
	const uint FormatMediaType = 1;
	const uint FormatMediaSubtype = 2;
	const uint FormatVideoFormat = 0x20001;
	const uint FormatVideoModifier = 0x20002;
	const uint FormatVideoSize = 0x20003;
	const uint FormatVideoFramerate = 0x20004;
	const uint BuffersDataType = 6;
	const uint MediaTypeVideo = 2;
	const uint MediaSubtypeRaw = 1;

	// spa/param/video/raw.h
	public const uint VideoRGBx = 7;
	public const uint VideoBGRx = 8;
	public const uint VideoRGBA = 11;
	public const uint VideoBGRA = 12;

	// spa/buffer/buffer.h
	public const int DataMemPtr = 1;
	public const int DataMemFd = 2;

	/// <summary>
	/// Formats offered to the producer, in order of preference. All are 4 bytes per pixel. RGB order comes first
	/// because the renderer only takes RGBA32; BGR-ordered frames get swapped while copying.
	/// </summary>
	public static readonly uint[] OfferedFormats = [VideoRGBx, VideoRGBA, VideoBGRx, VideoBGRA];

	public static PixelLayout? LayoutOf(uint spaVideoFormat) => spaVideoFormat switch
	{
		VideoBGRx or VideoBGRA => PixelLayout.Bgra,
		VideoRGBx or VideoRGBA => PixelLayout.Rgba,
		_ => null,
	};

	/// <summary>
	/// EnumFormat param: raw video in one of <see cref="OfferedFormats"/>, any size, at most <paramref name="maxFramerate"/>
	/// so the compositor doesn't produce frames we would drop. No modifier is offered, so producers pick shared memory.
	/// </summary>
	public static byte[] BuildVideoEnumFormat(int maxFramerate)
	{
		uint fps = (uint)Math.Clamp(maxFramerate, 1, 360);
		var w = new PodWriter();
		int obj = w.BeginObject(ObjectFormat, ParamEnumFormat);
		w.Property(FormatMediaType); w.Id(MediaTypeVideo);
		w.Property(FormatMediaSubtype); w.Id(MediaSubtypeRaw);

		w.Property(FormatVideoFormat);
		uint[] formats = [OfferedFormats[0], .. OfferedFormats];
		w.Choice(ChoiceEnum, TypeId, 4, formats.Length, (span, i) => BinaryPrimitives.WriteUInt32LittleEndian(span, formats[i]));

		w.Property(FormatVideoSize);
		(uint, uint)[] sizes = [(1920, 1080), (1, 1), (16384, 16384)];
		w.Choice(ChoiceRange, TypeRectangle, 8, sizes.Length, (span, i) => WritePair(span, sizes[i]));

		w.Property(FormatVideoFramerate);
		(uint, uint)[] rates = [(fps, 1), (0, 1), (fps, 1)];
		w.Choice(ChoiceRange, TypeFraction, 8, rates.Length, (span, i) => WritePair(span, rates[i]));

		w.End(obj);
		return w.ToArray();
	}

	/// <summary>Buffers param asking for CPU-mappable memory (MemPtr or MemFd) instead of DMA-BUFs.</summary>
	public static byte[] BuildCpuBuffers()
	{
		var w = new PodWriter();
		int obj = w.BeginObject(ObjectParamBuffers, ParamBuffers);
		w.Property(BuffersDataType);
		w.Choice(ChoiceFlags, TypeInt, 4, 1, (span, _) => BinaryPrimitives.WriteInt32LittleEndian(span, (1 << DataMemPtr) | (1 << DataMemFd)));
		w.End(obj);
		return w.ToArray();
	}

	/// <summary>A fixed Format param, as a producer sends after negotiation. Used by tests and for symmetry.</summary>
	public static byte[] BuildVideoFormat(uint videoFormat, int width, int height)
	{
		var w = new PodWriter();
		int obj = w.BeginObject(ObjectFormat, ParamFormat);
		w.Property(FormatMediaType); w.Id(MediaTypeVideo);
		w.Property(FormatMediaSubtype); w.Id(MediaSubtypeRaw);
		w.Property(FormatVideoFormat); w.Id(videoFormat);
		w.Property(FormatVideoSize); w.Pod(TypeRectangle, 8, span => WritePair(span, ((uint)width, (uint)height)));
		w.Property(FormatVideoFramerate); w.Pod(TypeFraction, 8, span => WritePair(span, (0u, 1u)));
		w.End(obj);
		return w.ToArray();
	}

	public readonly record struct VideoFormat(uint Format, int Width, int Height, bool HasModifier);

	/// <summary>Reads format and size from a Format object pod. Returns null if the pod is not a video format.</summary>
	public static VideoFormat? ParseVideoFormat(ReadOnlySpan<byte> pod)
	{
		if (pod.Length < 16 || U32(pod, 4) != TypeObject)
			return null;
		int end = Math.Min(pod.Length, 8 + (int)U32(pod, 0));
		if (U32(pod, 8) != ObjectFormat)
			return null;

		uint? format = null;
		int width = 0, height = 0;
		bool modifier = false;
		int pos = 16;
		while (pos + 16 <= end)
		{
			uint key = U32(pod, pos);
			int size = (int)U32(pod, pos + 8);
			uint type = U32(pod, pos + 12);
			int body = pos + 16;
			if (size < 0 || body + size > end)
				return null;
			var value = FirstValue(pod.Slice(body, size), ref type);
			switch (key)
			{
				case FormatVideoFormat when type == TypeId && value.Length >= 4:
					format = U32(value, 0);
					break;
				case FormatVideoSize when type == TypeRectangle && value.Length >= 8:
					width = (int)U32(value, 0);
					height = (int)U32(value, 4);
					break;
				case FormatVideoModifier:
					modifier = true;
					break;
			}
			pos = body + Align8(size);
		}
		return format is uint f && width > 0 && height > 0 ? new VideoFormat(f, width, height, modifier) : null;
	}

	/// <summary>For a Choice, returns its first (default) value and its child type; otherwise the body itself.</summary>
	static ReadOnlySpan<byte> FirstValue(ReadOnlySpan<byte> body, ref uint type)
	{
		if (type != TypeChoice)
			return body;
		if (body.Length < 16)
			return [];
		int childSize = (int)U32(body, 8);
		type = U32(body, 12);
		return body.Length >= 16 + childSize ? body.Slice(16, childSize) : [];
	}

	static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]);

	static int Align8(int n) => (n + 7) & ~7;

	static void WritePair(Span<byte> span, (uint a, uint b) v)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(span, v.a);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], v.b);
	}

	/// <summary>Little-endian pod writer with the same padding rules as spa_pod_builder.</summary>
	sealed class PodWriter
	{
		byte[] _buf = new byte[256];
		int _len;

		public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

		Span<byte> Take(int n)
		{
			if (_len + n > _buf.Length)
				Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
			var span = _buf.AsSpan(_len, n);
			span.Clear();
			_len += n;
			return span;
		}

		void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);

		void Pad() => Take(Align8(_len) - _len);

		public int BeginObject(uint objectType, uint id)
		{
			int start = _len;
			U32(0); // size, patched in End
			U32(TypeObject);
			U32(objectType);
			U32(id);
			return start;
		}

		public void End(int start) =>
			BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(start), (uint)(_len - start - 8));

		public void Property(uint key)
		{
			U32(key);
			U32(0); // flags
		}

		public void Id(uint id) => Pod(TypeId, 4, span => BinaryPrimitives.WriteUInt32LittleEndian(span, id));

		public void Pod(uint type, int size, SpanAction write)
		{
			U32((uint)size);
			U32(type);
			write(Take(size));
			Pad();
		}

		public void Choice(uint choiceType, uint childType, int childSize, int count, IndexedSpanAction write)
		{
			U32((uint)(16 + childSize * count));
			U32(TypeChoice);
			U32(choiceType);
			U32(0); // flags
			U32((uint)childSize);
			U32(childType);
			for (int i = 0; i < count; i++)
				write(Take(childSize), i);
			Pad();
		}
	}

	delegate void SpanAction(Span<byte> span);

	delegate void IndexedSpanAction(Span<byte> span, int index);
}
