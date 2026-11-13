using System.Diagnostics;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using LinuxNativeDesktop.Capture;
using Renderite.Shared;

namespace LinuxNativeDesktop;

/// <summary>Keeps a <see cref="DesktopMirror"/> alive for every desktop texture a provider has created.</summary>
[HarmonyPatch(typeof(DesktopTextureProvider))]
static class DesktopTextureProviderPatches
{
	[HarmonyPostfix]
	[HarmonyPatch("UpdateAsset")]
	static void UpdateAsset(DesktopTextureProvider __instance) => DesktopMirror.Sync(__instance);

	[HarmonyPrefix]
	[HarmonyPatch("FreeAsset")]
	static void FreeAsset(DesktopTextureProvider __instance) => DesktopMirror.Release(__instance);
}

/// <summary>
/// Every material, UI image and blit refers to a texture through this id. Swapping a desktop texture's id for its
/// mirror's Texture2D makes them all show the native capture, with no renderer changes.
/// </summary>
[HarmonyPatch(typeof(RenderableHelper), nameof(RenderableHelper.PackRenderableId), [typeof(ITexture)])]
static class PackRenderableIdPatch
{
	static bool Prefix(ITexture texture, ref int __result)
	{
		if (texture is not DesktopTexture desktop || DesktopMirror.TryGetTexture(desktop) is not { } mirror)
			return true;
		__result = mirror.PackRenderableId();
		return false;
	}
}

/// <summary>
/// <see cref="DesktopController"/> only builds its display when it is attached with the desktop enabled, and the
/// dash is saved. A dash saved while the engine had the desktop disabled therefore has an empty controller;
/// rebuild it the way <c>DesktopScreen.Setup</c> first made it.
/// </summary>
[HarmonyPatch(typeof(DesktopScreen), "OnStart")]
static class DesktopScreenPatch
{
	static readonly AccessTools.FieldRef<DesktopController, SyncRef<DesktopTextureProvider>> DesktopTextureField =
		AccessTools.FieldRefAccess<DesktopController, SyncRef<DesktopTextureProvider>>("_desktopTexture");

	static void Postfix(DesktopScreen __instance)
	{
		if (Engine.Config.DisableDesktop || !__instance.World.IsUserspace())
			return;
		var controller = __instance.ScreenCanvas.Slot.GetComponentInChildren<DesktopController>();
		if (controller is not null && DesktopTextureField(controller).Target is not null)
			return;
		LinuxNativeDesktop.Msg("Rebuilding the desktop screen, which was saved while the desktop was disabled");
		controller?.Slot.Destroy();
		AccessTools.Method(typeof(DesktopScreen), "Setup").Invoke(__instance, null);
	}
}

/// <summary>
/// Never ask the renderer to capture. Under Wine its desktop duplication fails and it falls back to
/// uWindowCapture, which can crash the whole renderer; its result would be replaced by the mirror anyway. The engine's callback is kept, and <see cref="DesktopMirror"/> fires it
/// (through HandlePropertiesUpdate) once the first native frame is uploaded. An UnloadDesktopTexture for an id the
/// renderer never saw is harmless: it creates an empty entry and removes it again.
/// </summary>
[HarmonyPatch(typeof(DesktopTexture), nameof(DesktopTexture.Update))]
static class DesktopTextureUpdatePatch
{
	static readonly AccessTools.FieldRef<DesktopTexture, Action> OnUpdatedField =
		AccessTools.FieldRefAccess<DesktopTexture, Action>("onUpdated");

	static bool Prefix(DesktopTexture __instance, Action onUpdated)
	{
		if (!LinuxNativeDesktop.Enabled)
			return true;
		OnUpdatedField(__instance) = onUpdated;
		return false;
	}
}

/// <summary>The renderer reports its (non-working) desktop texture's size; keep the size of what we actually show.</summary>
[HarmonyPatch(typeof(DesktopTexture), nameof(DesktopTexture.HandlePropertiesUpdate))]
static class HandlePropertiesUpdatePatch
{
	static void Postfix(DesktopTexture __instance) => DesktopMirror.ApplySize(__instance);
}

/// <summary>
/// The engine locks the cursor whenever the game window has focus, even in VR. Under Wine on Wayland that confines
/// the real pointer to the game window, and the compositor applies it to injected motion too. So once a desktop-tab
/// click landed on Resonite's own window, the laser could no longer move the pointer out of it. In VR the mouse
/// steers nothing, so the lock is never sent to the renderer there; desktop mode keeps its mouse-look lock.
/// </summary>
[HarmonyPatch(typeof(InputInterface), "CollectOutputState")]
static class CursorLockPatch
{
	static void Postfix(InputInterface __instance, OutputState __result)
	{
		if (!LinuxNativeDesktop.InputEnabled || !__instance.VR_Active)
			return;
		__result.lockCursor = false;
		__result.lockCursorPosition = null;
	}
}

/// <summary>
/// Keeps the mod's own injected clicks from reaching the game. They usually land on Resonite's fullscreen window, and
/// Wine reports them as the game's mouse buttons, which makes the in-game UI stop responding to the VR lasers.
/// VR only, because in desktop mode the real mouse is what drives the laser, so its clicks must reach the game.
/// </summary>
[HarmonyPatch(typeof(Mouse), nameof(Mouse.Update))]
static class GameMouseFilterPatch
{
	static void Prefix(MouseState state)
	{
		if (!LinuxInputInjector.IsInjectingClicks || Engine.Current?.InputInterface?.VR_Active != true)
			return;
		state.leftButtonState = false;
		state.rightButtonState = false;
		state.middleButtonState = false;
	}
}

/// <summary>
/// Thumbstick scrolling. In VR the thumbstick is bound to the interaction handler's <c>Axis</c>; the canvas scroll
/// path (<c>Canvas.ProcessAxis</c>, fed by <c>TouchAxis</c>) is only bound to the mouse wheel, so stock Resonite never
/// scrolls UI with the stick. While a laser is on the desktop image, the handler's Axis claims the stick
/// (<c>RegisterBlocks</c>, as the engine does for tools that use it), so movement and turning don't also get it, and
/// its value becomes scrolling at the pointer, in wheel-notch-sized steps.
/// </summary>
static class DesktopScroll
{
	static readonly AccessTools.FieldRef<InteractionHandler, InteractionHandlerInputs> Inputs =
		AccessTools.FieldRefAccess<InteractionHandler, InteractionHandlerInputs>("_inputs");
	static readonly ConditionalWeakTable<TouchSource, StrongBox<long>> LastOnDesktop = new();
	static readonly ConditionalWeakTable<InteractionHandler, ScrollAccumulator> Accumulators = new();
	// The relay sees the laser every frame it points at the desktop; allow a few missed frames.
	static readonly long HoverTimeout = Stopwatch.Frequency / 10;

	public static void MarkOnDesktop(TouchSource source) =>
		LastOnDesktop.GetOrCreateValue(source).Value = Stopwatch.GetTimestamp();

	static bool IsOnDesktop(InteractionHandler handler) =>
		LinuxNativeDesktop.InputEnabled && handler.InputInterface.VR_Active
		&& handler.Laser?.TouchSource is { } source && LastOnDesktop.TryGetValue(source, out var last)
		&& Stopwatch.GetTimestamp() - last.Value < HoverTimeout;

	[HarmonyPatch(typeof(DesktopInteractionRelay), nameof(DesktopInteractionRelay.ProcessEvent))]
	static class RelayHoverPatch
	{
		static void Postfix(Canvas.InteractionData eventData, bool __result)
		{
			if (__result && eventData.hover != EventState.End && eventData.source is { } source)
				MarkOnDesktop(source);
		}
	}

	[HarmonyPatch(typeof(InteractionHandler), nameof(InteractionHandler.BeforeInputUpdate))]
	static class ClaimStickPatch
	{
		static void Postfix(InteractionHandler __instance)
		{
			if (IsOnDesktop(__instance))
				Inputs(__instance).Axis.RegisterBlocks = true;
		}
	}

	[HarmonyPatch(typeof(InteractionHandler), "OnInputUpdate")]
	static class ScrollPatch
	{
		static void Postfix(InteractionHandler __instance)
		{
			var accumulator = Accumulators.GetOrCreateValue(__instance);
			if (!IsOnDesktop(__instance))
			{
				accumulator.Update(0, 0, 0, 0); // forget leftover fractions
				return;
			}
			float2 axis = Inputs(__instance).Axis;
			var (vertical, horizontal) = accumulator.Update(axis.x, axis.y, LinuxNativeDesktop.ScrollSpeed, __instance.Time.Delta);
			if ((vertical != 0 || horizontal != 0) && LinuxNativeDesktop.Input is { CanInject: true } desktop)
			{
				desktop.Scroll(vertical, horizontal);
				LinuxNativeDesktop.Debug($"Scroll {vertical} down, {horizontal} right");
			}
		}
	}
}
