namespace LinuxNativeDesktop.Capture;

/// <summary>Byte order of a 4-byte pixel. The 4th byte is alpha or padding.</summary>
public enum PixelLayout
{
	Bgra,
	Rgba,
}

/// <summary>One captured frame. Only valid during the <see cref="IFrameSink.OnFrame"/> call.</summary>
public readonly ref struct VideoFrame
{
	public readonly int Width;
	public readonly int Height;
	public readonly int Stride;
	public readonly PixelLayout Layout;
	public readonly ReadOnlySpan<byte> Data;

	public VideoFrame(int width, int height, int stride, PixelLayout layout, ReadOnlySpan<byte> data)
	{
		Width = width;
		Height = height;
		Stride = stride;
		Layout = layout;
		Data = data;
	}
}

/// <summary>Receives frames on the capture thread. Implementations must copy what they need and return quickly.</summary>
public interface IFrameSink
{
	void OnFrame(in VideoFrame frame);
}

public interface ICaptureLog
{
	/// <summary>Detail that is only useful when troubleshooting (stream state changes, formats).</summary>
	void Debug(string message);
	void Info(string message);
	void Warn(string message);
	void Error(string message);
}
