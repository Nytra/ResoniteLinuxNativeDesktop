using System.Numerics;
using System.Runtime.InteropServices;

namespace LinuxNativeDesktop.Capture;

public static class FrameCopy
{
	/// <summary>
	/// Copies a 4-byte-per-pixel frame into a tightly packed RGBA destination. Every alpha byte is set to
	/// 255, since BGRx/RGBx sources leave the padding byte undefined (often 0, which would render invisible), and
	/// BGRA-ordered sources get red and blue swapped, because the renderer's D3D11 path can't take BGRA32.
	/// With <paramref name="flipVertical"/> the last source row becomes the first destination row.
	/// Returns false if either buffer is too small.
	/// </summary>
	public static bool CopyToRgba(ReadOnlySpan<byte> source, int sourceStride, PixelLayout layout, Span<byte> destination, int width, int height, bool flipVertical = false)
	{
		int rowBytes = width * 4;
		if (width <= 0 || height <= 0 || sourceStride < rowBytes)
			return false;
		if (source.Length < (long)sourceStride * (height - 1) + rowBytes || destination.Length < (long)rowBytes * height)
			return false;

		for (int y = 0; y < height; y++)
		{
			var dstRow = destination.Slice((flipVertical ? height - 1 - y : y) * rowBytes, rowBytes);
			source.Slice(y * sourceStride, rowBytes).CopyTo(dstRow);
			var pixels = MemoryMarshal.Cast<byte, uint>(dstRow);
			if (layout == PixelLayout.Bgra)
				SwapRedBlueOpaque(pixels);
			else
				SetAlphaOpaque(pixels);
		}
		return true;
	}

	// Pixels are little-endian: byte 0 is the low byte, byte 3 (alpha/padding) the high byte.
	const uint Alpha = 0xFF000000;
	const uint GreenAlpha = 0xFF00FF00;

	static void SetAlphaOpaque(Span<uint> pixels)
	{
		int i = 0;
		if (Vector.IsHardwareAccelerated)
		{
			var alpha = new Vector<uint>(Alpha);
			var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
			for (int v = 0; v < vectors.Length; v++)
				vectors[v] |= alpha;
			i = vectors.Length * Vector<uint>.Count;
		}
		for (; i < pixels.Length; i++)
			pixels[i] |= Alpha;
	}

	/// <summary>B,G,R,x → R,G,B,255.</summary>
	static void SwapRedBlueOpaque(Span<uint> pixels)
	{
		int i = 0;
		if (Vector.IsHardwareAccelerated)
		{
			var alpha = new Vector<uint>(Alpha);
			var green = new Vector<uint>(GreenAlpha);
			var low = new Vector<uint>(0xFF);
			var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
			for (int v = 0; v < vectors.Length; v++)
			{
				var p = vectors[v];
				vectors[v] = (p & green) | ((p & low) << 16) | ((p >> 16) & low) | alpha;
			}
			i = vectors.Length * Vector<uint>.Count;
		}
		for (; i < pixels.Length; i++)
		{
			uint p = pixels[i];
			pixels[i] = (p & GreenAlpha) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF) | Alpha;
		}
	}
}
