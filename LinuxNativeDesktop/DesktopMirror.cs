using System.Diagnostics;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using LinuxNativeDesktop.Capture;
using Renderite.Shared;

namespace LinuxNativeDesktop;

/// <summary>
/// Shadows one <see cref="DesktopTexture"/> with a regular <see cref="Texture2D"/> that is filled from the native
/// capture. The capture thread copies frames into a shared-memory bitmap; the engine thread hands that bitmap to the
/// texture (which then owns it and frees it when given the next one) and allocates a fresh one for the next frame.
/// Only one upload is in flight; the capture thread keeps filling the fresh bitmap meanwhile.
/// </summary>
sealed class DesktopMirror : IFrameSink
{
	static readonly object RegistryLock = new();
	static readonly Dictionary<DesktopTextureProvider, DesktopMirror> ByProvider = [];
	static readonly Dictionary<DesktopTexture, DesktopMirror> ByDesktop = [];
	static readonly AccessTools.FieldRef<DesktopTextureProvider, DesktopTexture?> DesktopTexField =
		AccessTools.FieldRefAccess<DesktopTextureProvider, DesktopTexture?>("_desktopTex");
	static readonly Action<DesktopTexture, int2> SetDesktopSize =
		AccessTools.MethodDelegate<Action<DesktopTexture, int2>>(AccessTools.PropertySetter(typeof(DesktopTexture), nameof(DesktopTexture.Size)));
	static readonly long StatsInterval = Stopwatch.Frequency * 5;
	static int _nextId;

	readonly DesktopTextureProvider _provider;
	readonly DesktopTexture _desktop;
	readonly World _world;
	readonly Texture2D _texture;
	readonly int _id = Interlocked.Increment(ref _nextId); // for debug logs
	readonly object _gate = new();
	Bitmap2D? _back; // ours until handed to the texture
	bool _writing, _backReady, _uploading;
	int2 _frameSize, _uploadSize, _shownSize;
	long _lastFrame;
	bool _disposed, _freed;
	int _displayIndex = -1;
	IDisposable? _subscription;
	// Debug statistics, reset every StatsInterval: frames the capture delivered, frames copied, uploads finished.
	int _framesIn, _framesCopied, _uploadsDone;
	long _statsSince = Stopwatch.GetTimestamp();
	bool _shownFirstFrame;

	DesktopMirror(DesktopTextureProvider provider, DesktopTexture desktop)
	{
		_provider = provider;
		_desktop = desktop;
		_world = provider.World;
		_texture = new Texture2D();
		_texture.InitializeDynamic(provider.Engine.AssetManager);
	}

	/// <summary>Called after the provider updated its asset: create, retarget or drop the mirror to match.</summary>
	public static void Sync(DesktopTextureProvider provider)
	{
		var desktop = DesktopTexField(provider);
		DesktopMirror? existing;
		lock (RegistryLock)
			ByProvider.TryGetValue(provider, out existing);

		if (desktop is null || !LinuxNativeDesktop.Enabled || (existing is not null && existing._desktop != desktop))
		{
			if (existing is not null)
				LinuxNativeDesktop.Debug($"Desktop view {existing._id}: {(desktop is null ? "provider has no texture" : existing._desktop != desktop ? "texture replaced" : "mod disabled")}");
			Release(provider);
			existing = null;
		}
		if (desktop is null || !LinuxNativeDesktop.Enabled)
			return;

		if (existing is null)
		{
			existing = new DesktopMirror(provider, desktop);
			lock (RegistryLock)
			{
				ByProvider[provider] = existing;
				ByDesktop[desktop] = existing;
			}
			LinuxNativeDesktop.Debug($"Desktop view {existing._id} created for display {provider.DisplayIndex.Value}");
			existing._world.RunInUpdates(1, existing.Pump);
		}
		existing.ShowDisplay(provider.DisplayIndex.Value);
	}

	public static void Release(DesktopTextureProvider provider)
	{
		DesktopMirror? mirror;
		lock (RegistryLock)
		{
			if (!ByProvider.Remove(provider, out mirror))
				return;
			ByDesktop.Remove(mirror._desktop);
		}
		LinuxNativeDesktop.Debug($"Desktop view {mirror._id} released (slot active: {provider.Slot.IsActive}, provider enabled: {provider.Enabled}, destroyed: {provider.IsDestroyed})");
		mirror.Dispose();
	}

	public static Texture2D? TryGetTexture(DesktopTexture desktop)
	{
		lock (RegistryLock)
			return ByDesktop.TryGetValue(desktop, out var mirror) ? mirror._texture : null;
	}

	public static void ApplySize(DesktopTexture desktop)
	{
		DesktopMirror? mirror;
		lock (RegistryLock)
			ByDesktop.TryGetValue(desktop, out mirror);
		if (mirror is not null && mirror._shownSize != int2.Zero)
			SetDesktopSize(desktop, mirror._shownSize);
	}

	void ShowDisplay(int index)
	{
		if (index == _displayIndex && _subscription is not null)
			return;
		_subscription?.Dispose();
		_displayIndex = index;
		_subscription = LinuxNativeDesktop.Service.Subscribe(index, LinuxNativeDesktop.Displays(_provider.Engine.InputInterface), this);
	}

	// Capture thread.
	public void OnFrame(in VideoFrame frame)
	{
		Bitmap2D target;
		lock (_gate)
		{
			_framesIn++;
			if (_disposed || _writing)
				return;
			long now = Stopwatch.GetTimestamp();
			// 10% slack so a source running exactly at the limit isn't halved by jitter.
			if (now - _lastFrame < Stopwatch.Frequency * 9 / (10 * LinuxNativeDesktop.MaxFramerate))
				return;
			_frameSize = new int2(frame.Width, frame.Height);
			// Bitmaps come from the engine's shared memory, so they are allocated on the engine thread (Pump).
			if (_back is null || _back.Size != _frameSize)
				return;
			target = _back;
			_writing = true;
			_backReady = false;
			_lastFrame = now;
		}

		// Flipped: the renderer shows desktop textures bottom row first.
		bool copied = FrameCopy.CopyToRgba(frame.Data, frame.Stride, frame.Layout, target.Buffer.Memory.Span, frame.Width, frame.Height, flipVertical: true);

		lock (_gate)
		{
			_writing = false;
			_backReady = copied && !_disposed;
			if (copied)
				_framesCopied++;
			FreeIfIdle();
		}
	}

	// Engine thread, every update while alive.
	void Pump()
	{
		Bitmap2D? upload = null;
		lock (_gate)
		{
			if (_disposed)
				return;
			if (_backReady && !_uploading && !_writing)
			{
				upload = _back;
				_uploadSize = upload!.Size;
				_back = null;
				_backReady = false;
				_uploading = true;
			}
			if (!_writing && _frameSize != int2.Zero && (_back is null || _back.Size != _frameSize))
			{
				_back?.Buffer.Dispose();
				_back = new Bitmap2D(_frameSize.x, _frameSize.y, TextureFormat.RGBA32, false, ColorProfile.sRGB, flipY: true, null!, _provider.Engine.AssetManager.TextureAllocator);
				_backReady = false;
			}
		}
		LogStats();
		if (upload is not null)
		{
			// The texture takes ownership of the bitmap: assigning the next one disposes this one's shared memory.
			// RGBA32 + sRGB: the renderer's D3D11 path has no BGRA32 at all (it is a fatal renderer error).
			_texture.SetFromBitmap2D(upload, new TextureUploadHint { readable = false }, TextureFilterMode.Bilinear, 0,
				TextureWrapMode.Clamp, TextureWrapMode.Clamp, 0f, OnUploaded);
		}
		_world.RunInUpdates(1, Pump);
	}

	// Engine thread. Shows whether frames stop arriving, stop being copied, or stop being uploaded.
	void LogStats()
	{
		long now = Stopwatch.GetTimestamp();
		if (now - _statsSince < StatsInterval)
			return;
		int framesIn, copied, uploads;
		bool uploading;
		lock (_gate)
		{
			(framesIn, copied, uploads, uploading) = (_framesIn, _framesCopied, _uploadsDone, _uploading);
			_framesIn = _framesCopied = _uploadsDone = 0;
		}
		_statsSince = now;
		LinuxNativeDesktop.Debug($"Desktop view {_id}: last 5 s: {framesIn} frames in, {copied} copied, {uploads} uploaded{(uploading ? ", upload in flight" : "")}");
	}

	// Renderer message thread.
	void OnUploaded(bool instanceChanged)
	{
		bool report;
		lock (_gate)
		{
			_uploading = false;
			_uploadsDone++;
			if (_disposed)
			{
				FreeIfIdle();
				return;
			}
			report = instanceChanged || _uploadSize != _shownSize;
			_shownSize = _uploadSize;
			if (!_shownFirstFrame)
			{
				_shownFirstFrame = true;
				LinuxNativeDesktop.Debug($"Desktop view {_id}: first frame uploaded ({_uploadSize.x}x{_uploadSize.y})");
			}
		}
		if (report)
			_world.RunSynchronously(ReportSize);
	}

	/// <summary>
	/// Goes through the same path as the renderer's reply, so the provider re-announces its asset and every material
	/// using it re-sends the (redirected) texture id. Needed whenever the renderer created a new texture instance.
	/// </summary>
	void ReportSize()
	{
		if (_disposed)
			return;
		_desktop.HandlePropertiesUpdate(new DesktopTexturePropertiesUpdate
		{
			assetId = _desktop.AssetId,
			size = new RenderVector2i(_shownSize.x, _shownSize.y),
		});
	}

	void Dispose()
	{
		_subscription?.Dispose();
		_subscription = null;
		lock (_gate)
		{
			_disposed = true;
			FreeIfIdle();
		}
	}

	// Called with _gate held. Shared memory may only be released once no copy or upload is reading it.
	void FreeIfIdle()
	{
		if (!_disposed || _freed || _writing || _uploading)
			return;
		_freed = true;
		_back?.Buffer.Dispose();
		_back = null;
		_texture.Unload(); // frees the bitmap the texture owns
	}
}
