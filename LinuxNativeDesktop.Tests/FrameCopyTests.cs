using LinuxNativeDesktop.Capture;

namespace LinuxNativeDesktop.Tests;

public class FrameCopyTests
{
	[Fact]
	public void CopiesRowsAndDropsStridePadding()
	{
		// 3x2 pixels, source rows padded to 16 bytes (12 used).
		byte[] source =
		[
			1, 2, 3, 0,  4, 5, 6, 0,  7, 8, 9, 0,  0xEE, 0xEE, 0xEE, 0xEE,
			10, 11, 12, 0,  13, 14, 15, 0,  16, 17, 18, 0,  0xEE, 0xEE, 0xEE, 0xEE,
		];
		var destination = new byte[3 * 2 * 4];

		Assert.True(FrameCopy.CopyToRgba(source, 16, PixelLayout.Rgba, destination, 3, 2));

		Assert.Equal(new byte[]
		{
			1, 2, 3, 255,  4, 5, 6, 255,  7, 8, 9, 255,
			10, 11, 12, 255,  13, 14, 15, 255,  16, 17, 18, 255,
		}, destination);
	}

	[Fact]
	public void FlipsRowsVertically()
	{
		byte[] source =
		[
			1, 1, 1, 0,  2, 2, 2, 0,  0xEE, 0xEE, 0xEE, 0xEE, // row 0, padded
			3, 3, 3, 0,  4, 4, 4, 0,  0xEE, 0xEE, 0xEE, 0xEE, // row 1
			5, 5, 5, 0,  6, 6, 6, 0,                          // row 2, unpadded
		];
		var destination = new byte[2 * 3 * 4];

		Assert.True(FrameCopy.CopyToRgba(source, 12, PixelLayout.Rgba, destination, 2, 3, flipVertical: true));

		Assert.Equal(new byte[]
		{
			5, 5, 5, 255,  6, 6, 6, 255,
			3, 3, 3, 255,  4, 4, 4, 255,
			1, 1, 1, 255,  2, 2, 2, 255,
		}, destination);
	}

	[Fact]
	public void SwapsBgraToRgba()
	{
		byte[] source = [0xB0, 0x60, 0x30, 0x00,  0x01, 0x02, 0x03, 0x7F];
		var destination = new byte[8];

		Assert.True(FrameCopy.CopyToRgba(source, 8, PixelLayout.Bgra, destination, 2, 1));

		Assert.Equal(new byte[] { 0x30, 0x60, 0xB0, 255,  0x03, 0x02, 0x01, 255 }, destination);
	}

	[Theory]
	[InlineData(PixelLayout.Rgba)]
	[InlineData(PixelLayout.Bgra)]
	public void HandlesRowsLongerThanAVector(PixelLayout layout)
	{
		const int width = 37; // not a multiple of any vector width, so both the vector and scalar paths run
		var source = new byte[width * 4];
		for (int x = 0; x < width; x++)
			source.AsSpan(x * 4, 4).Fill((byte)x);
		source[0] = 10; // make byte 0 differ from byte 2 in the first pixel
		var destination = new byte[width * 4];

		Assert.True(FrameCopy.CopyToRgba(source, width * 4, layout, destination, width, 1));

		for (int x = 0; x < width; x++)
		{
			var s = source.AsSpan(x * 4, 4);
			byte[] expected = layout == PixelLayout.Bgra ? [s[2], s[1], s[0], 255] : [s[0], s[1], s[2], 255];
			Assert.Equal(expected, destination.AsSpan(x * 4, 4).ToArray());
		}
	}

	[Fact]
	public void LastRowDoesNotNeedFullStride()
	{
		// Producers may cut the final row's padding off the buffer.
		var source = new byte[16 + 12];
		Assert.True(FrameCopy.CopyToRgba(source, 16, PixelLayout.Bgra, new byte[24], 3, 2));
	}

	[Theory]
	[InlineData(11, 16, 24)] // source too short
	[InlineData(28, 16, 23)] // destination too short
	[InlineData(28, 8, 24)]  // stride shorter than a row
	public void RejectsBuffersThatDoNotFit(int sourceLength, int stride, int destinationLength) =>
		Assert.False(FrameCopy.CopyToRgba(new byte[sourceLength], stride, PixelLayout.Rgba, new byte[destinationLength], 3, 2));

	[Fact]
	public void RejectsEmptyFrames() => Assert.False(FrameCopy.CopyToRgba([], 0, PixelLayout.Rgba, [], 0, 0));
}
