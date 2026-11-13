using System.Diagnostics;
using LinuxNativeDesktop.Capture;
using Xunit.Abstractions;

namespace LinuxNativeDesktop.Tests;

/// <summary>
/// End to end against the running PipeWire daemon: GStreamer publishes a solid-colour video source, and
/// <see cref="PipeWireVideoStream"/> negotiates with it and receives frames, exercising the real pods, struct
/// offsets and callbacks. Skipped when PipeWire or gst-launch-1.0 with pipewiresink isn't available.
/// </summary>
public class PipeWireStreamTests(ITestOutputHelper output)
{
	sealed class Log(ITestOutputHelper output) : ICaptureLog
	{
		public void Debug(string message) => Info(message);
		public void Info(string message) => output.WriteLine(message);
		public void Warn(string message) => output.WriteLine("WARN " + message);
		public void Error(string message) => output.WriteLine("ERROR " + message);
	}

	sealed class Sink : IFrameSink
	{
		public readonly TaskCompletionSource<(int Width, int Height, PixelLayout Layout, byte[] Pixels)> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Count;

		public void OnFrame(in VideoFrame frame)
		{
			Interlocked.Increment(ref Count);
			var pixels = new byte[frame.Width * frame.Height * 4];
			if (FrameCopy.CopyToRgba(frame.Data, frame.Stride, frame.Layout, pixels, frame.Width, frame.Height))
				First.TrySetResult((frame.Width, frame.Height, frame.Layout, pixels));
		}
	}

	[SkippableTheory]
	[InlineData("BGRx", PixelLayout.Bgra)]
	[InlineData("RGBA", PixelLayout.Rgba)]
	public async Task ReceivesFramesFromAPipeWireSource(string gstFormat, PixelLayout layout)
	{
		Skip.IfNot(HasGstPipeWire(), "gst-launch-1.0 with pipewiresink is not installed");
		string node = $"lnd-test-{Environment.ProcessId}-{gstFormat}";
		using var source = Process.Start(new ProcessStartInfo("gst-launch-1.0",
		[
			"-q", "videotestsrc", "pattern=solid-color", "foreground-color=0xff3366cc", "is-live=true",
			"!", $"video/x-raw,format={gstFormat},width=64,height=48,framerate=30/1",
			"!", "pipewiresink", "mode=provide", $"stream-properties=props,node.name={node},media.class=Video/Source",
		]) { RedirectStandardError = true })!;
		try
		{
			using var connection = PipeWireConnection.Connect(null, new Log(output));
			var sink = new Sink();
			// Wait for the source node to exist; AUTOCONNECT only links to targets that are already there.
			await Task.Delay(1000);
			using var stream = connection.CreateStream(LinuxNativeDesktop.Capture.Native.PipeWire.IdAny, 30, sink, $"target.object={node}");

			var done = await Task.WhenAny(sink.First.Task, Task.Delay(TimeSpan.FromSeconds(10)));
			Assert.True(done == sink.First.Task, $"No frame within 10 s (stream state {stream.State}, gst stderr: {StdErr(source)})");

			// Whatever order the source uses, the copy comes out as opaque RGBA (colour 0x3366cc).
			byte[] expectedPixel = [0x33, 0x66, 0xcc, 0xff];
			var (width, height, actualLayout, pixels) = await sink.First.Task;
			Assert.Equal((64, 48, layout), (width, height, actualLayout));
			Assert.Equal(expectedPixel, pixels[..4]);
			Assert.Equal(expectedPixel, pixels[^4..]);
			Assert.False(stream.Failed);
		}
		finally
		{
			source.Kill();
			await source.WaitForExitAsync();
		}
	}

	[SkippableFact]
	public void DisposingAStreamStopsFrames()
	{
		Skip.IfNot(File.Exists($"/run/user/{Environment.GetEnvironmentVariable("UID") ?? GetUid()}/pipewire-0"), "PipeWire is not running");
		using var connection = PipeWireConnection.Connect(null, new Log(output));
		var sink = new Sink();
		var stream = connection.CreateStream(LinuxNativeDesktop.Capture.Native.PipeWire.IdAny, 30, sink, "target.object=lnd-does-not-exist");
		stream.Dispose();
		stream.Dispose(); // idempotent
		int count = sink.Count;
		Thread.Sleep(200);
		Assert.Equal(count, sink.Count);
	}

	static string GetUid() => File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];

	static string StdErr(Process p)
	{
		if (!p.HasExited)
			return "(still running)";
		return p.StandardError.ReadToEnd();
	}

	static bool HasGstPipeWire()
	{
		try
		{
			using var p = Process.Start(new ProcessStartInfo("gst-inspect-1.0", ["pipewiresink"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
			p.WaitForExit(5000);
			return p.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}
}
