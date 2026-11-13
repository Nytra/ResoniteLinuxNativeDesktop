namespace LinuxNativeDesktop.Capture;

/// <summary>A monitor as Resonite (through Wine) reports it.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height);

/// <summary>A portal screen-cast stream. Position and size are optional in the portal API.</summary>
public readonly record struct StreamInfo(uint NodeId, (int X, int Y)? Position, (int Width, int Height)? Size);

public static class DisplayMatcher
{
	/// <summary>
	/// Pairs Resonite displays with portal streams. Returns, for each display, the index of its stream or -1.
	/// Wine and the compositor may use different origins (and the portal reports logical, scaled sizes),
	/// so it tries, in order: same rect after moving both layouts to start at 0,0; same size; then layout order.
	/// </summary>
	public static int[] Match(IReadOnlyList<DisplayRect> displays, IReadOnlyList<StreamInfo> streams)
	{
		var result = Enumerable.Repeat(-1, displays.Count).ToArray();
		var used = new bool[streams.Count];

		var displayOrigin = (displays.Count == 0 ? 0 : displays.Min(d => d.X), displays.Count == 0 ? 0 : displays.Min(d => d.Y));
		var positioned = streams.Where(s => s.Position.HasValue).Select(s => s.Position!.Value).ToList();
		var streamOrigin = (positioned.Count == 0 ? 0 : positioned.Min(p => p.X), positioned.Count == 0 ? 0 : positioned.Min(p => p.Y));

		// 1. Same rect, with both layouts moved to start at 0,0 or as is. When every screen is shared the layouts
		//    cover the same area, so their origins correspond; when only some are, the stream origin shifts, so
		//    absolute positions are the better first guess.
		if (streams.Count == displays.Count)
		{
			MatchRects(displayOrigin, streamOrigin);
			MatchRects((0, 0), (0, 0));
		}
		else
		{
			MatchRects((0, 0), (0, 0));
			MatchRects(displayOrigin, streamOrigin);
		}

		// 2. Same size, but only if every stream size is one Wine reports. Otherwise scaling is in play, and a scaled
		//    monitor's logical size can equal another monitor's real size, so size says nothing.
		bool sizesComparable = streams.All(s => s.Size is not { } size || displays.Any(d => d.Width == size.Width && d.Height == size.Height));
		for (int d = 0; d < displays.Count && sizesComparable; d++)
		{
			for (int s = 0; s < streams.Count && result[d] < 0; s++)
			{
				if (!used[s] && streams[s].Size is { } size && size.Width == displays[d].Width && size.Height == displays[d].Height)
					Take(d, s);
			}
		}

		// 3. Remaining ones in left-to-right, top-to-bottom order (streams without a position keep portal order).
		var freeDisplays = Enumerable.Range(0, displays.Count).Where(d => result[d] < 0)
			.OrderBy(d => displays[d].X).ThenBy(d => displays[d].Y).ToList();
		var freeStreams = Enumerable.Range(0, streams.Count).Where(s => !used[s])
			.OrderBy(s => streams[s].Position?.X ?? int.MaxValue).ThenBy(s => streams[s].Position?.Y ?? int.MaxValue).ThenBy(s => s).ToList();
		for (int i = 0; i < Math.Min(freeDisplays.Count, freeStreams.Count); i++)
			Take(freeDisplays[i], freeStreams[i]);

		return result;

		void Take(int d, int s)
		{
			result[d] = s;
			used[s] = true;
		}

		void MatchRects((int X, int Y) displayOffset, (int X, int Y) streamOffset)
		{
			for (int d = 0; d < displays.Count; d++)
			{
				var disp = displays[d];
				for (int s = 0; s < streams.Count && result[d] < 0; s++)
				{
					if (used[s] || streams[s].Position is not { } pos || streams[s].Size is not { } size)
						continue;
					if (pos.X - streamOffset.X == disp.X - displayOffset.X && pos.Y - streamOffset.Y == disp.Y - displayOffset.Y
						&& size.Width == disp.Width && size.Height == disp.Height)
						Take(d, s);
				}
			}
		}
	}
}
