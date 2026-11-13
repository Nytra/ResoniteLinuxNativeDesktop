namespace LinuxNativeDesktop.Capture;

public readonly record struct StreamPoint(uint NodeId, double X, double Y);

public static class PointerMapping
{
	/// <summary>
	/// Converts a point in Wine's desktop coordinates to a position inside the matching portal stream, which is
	/// what RemoteDesktop's NotifyPointerMotionAbsolute takes. The point is assigned to the display containing it
	/// (or the nearest one), then scaled into the stream's size (logical, so it differs from Wine's with scaling).
	/// Returns null if that display isn't shared.
	/// </summary>
	public static StreamPoint? Map(IReadOnlyList<DisplayRect> displays, IReadOnlyList<StreamInfo> streams, int[] mapping, double x, double y)
	{
		int best = -1;
		double bestDistance = double.MaxValue;
		for (int i = 0; i < displays.Count; i++)
		{
			var d = displays[i];
			double dx = Math.Max(Math.Max(d.X - x, 0), x - (d.X + d.Width));
			double dy = Math.Max(Math.Max(d.Y - y, 0), y - (d.Y + d.Height));
			double distance = dx * dx + dy * dy;
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = i;
			}
		}
		if (best < 0 || best >= mapping.Length || mapping[best] < 0 || mapping[best] >= streams.Count)
			return null;

		var display = displays[best];
		var stream = streams[mapping[best]];
		var (width, height) = stream.Size ?? (display.Width, display.Height);
		double u = Math.Clamp((x - display.X) / display.Width, 0, 1);
		double v = Math.Clamp((y - display.Y) / display.Height, 0, 1);
		// Keep the point inside the stream: width itself would be one pixel past the right edge.
		return new StreamPoint(stream.NodeId, Math.Min(u * width, width - 1), Math.Min(v * height, height - 1));
	}
}
