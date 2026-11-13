using LinuxNativeDesktop.Capture;

namespace LinuxNativeDesktop.Tests;

public class DisplayMatcherTests
{
	static StreamInfo Stream(uint node, int x, int y, int w, int h) => new(node, (x, y), (w, h));

	[Fact]
	public void SingleDisplaySingleStream()
	{
		DisplayRect[] displays = [new(0, 0, 2560, 1440)];
		StreamInfo[] streams = [Stream(42, 0, 0, 2560, 1440)];
		Assert.Equal([0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void MatchesByRectRegardlessOfStreamOrder()
	{
		DisplayRect[] displays = [new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080)];
		StreamInfo[] streams = [Stream(2, 2560, 0, 1920, 1080), Stream(1, 0, 0, 2560, 1440)];
		Assert.Equal([1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void NormalizesDifferentOrigins()
	{
		// Wine puts the primary at 0,0 so a monitor on its left is negative; the compositor starts at 0,0.
		DisplayRect[] displays = [new(0, 0, 1920, 1080), new(-1920, 0, 1920, 1080)];
		StreamInfo[] streams = [Stream(1, 0, 0, 1920, 1080), Stream(2, 1920, 0, 1920, 1080)];
		Assert.Equal([1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void FallsBackToSizeWhenPositionsDisagree()
	{
		DisplayRect[] displays = [new(0, 0, 2560, 1440), new(2560, 300, 1920, 1080)];
		StreamInfo[] streams = [Stream(1, 0, 0, 1920, 1080), Stream(2, 1920, 0, 2560, 1440)];
		Assert.Equal([1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void FallsBackToLayoutOrderForScaledOutputs()
	{
		// With fractional scaling the portal reports logical sizes that match nothing Wine reports.
		DisplayRect[] displays = [new(3840, 0, 2560, 1440), new(0, 0, 3840, 2160)];
		StreamInfo[] streams = [Stream(1, 0, 0, 2560, 1440), Stream(2, 2560, 0, 1707, 960)];
		Assert.Equal([1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void MatchesAPartialShareAtItsRealPosition()
	{
		// Only the right-hand monitor was shared, so normalizing the stream layout alone would shift it to 0,0.
		DisplayRect[] displays = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
		StreamInfo[] streams = [Stream(9, 1920, 0, 1920, 1080)];
		Assert.Equal([-1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void StreamsWithoutGeometryKeepPortalOrder()
	{
		DisplayRect[] displays = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
		StreamInfo[] streams = [new(7, null, null), new(8, null, null)];
		Assert.Equal([0, 1], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void DisplaysWithoutASharedScreenGetNothing()
	{
		DisplayRect[] displays = [new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080)];
		StreamInfo[] streams = [Stream(5, 2560, 0, 1920, 1080)];
		Assert.Equal([-1, 0], DisplayMatcher.Match(displays, streams));
	}

	[Fact]
	public void NoDisplays() => Assert.Empty(DisplayMatcher.Match([], [Stream(1, 0, 0, 10, 10)]));
}
