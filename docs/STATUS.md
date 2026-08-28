# Status

## Validated in 0.1.0

- Packaged WinUI application launch and clean shutdown.
- Zero-warning Debug and Release builds on .NET 10.
- Core tests for accent selection, event subscriptions, settings persistence and
  normalization, rapid hover state, media source names, notification filtering,
  privacy previews, glance/pin-safe interaction state, and adaptive edge geometry.
- Native per-pixel-alpha idle composition: the desktop remains visible beneath the
  pressure field with no black slab, rectangular fill, or visible window boundary.
- Correct native HWND creation and frame delivery, including explicit
  `WM_NCCREATE` acceptance and a one-pixel WinUI render keep-alive.
- Vertically extended spring-interpolated contours with long falloff, seventeen
  fine filaments, restrained radial pressure/bloom layers, and a stable
  half-embedded optical mesh orb.
- Mirrored right and left inward-flowing geometry validated at runtime, with
  deterministic 100% and 125% layout/DPI coverage in core tests.
- Layered fluid traces, glass orb, idle breathing, and restrained playing motion.
- Hover preview and click-to-pin launcher states with an organic acrylic capsule
  bloom around the existing practical content surface.
- Notification travel, expanded orb, icon layer, ripple, hold, and return are
  integrated into the shared field model and exposed through bounded visual-test
  controls.
- Live Spotify media metadata, artwork, timeline, and playback status through the
  Windows global media-session API.
- Automatic artwork accent, manual color changes, and return to automatic mode.
- Settings scrolling and persisted edge/accent changes.
- Clock glance appearance and return to the previous surface.
- Notification access state without triggering a permission prompt.

## Implemented but requiring broader field testing

- Previous/play-pause/next/seek behavior across multiple media providers.
- Notification parsing across Telegram, Discord, and browser notification shapes.
- Notification arrival parsing/icon quality with real Telegram, Discord, and
  browser notifications, plus fresh end-to-end capture of the native pulse path.
- StartupTask transitions across all Windows user-controlled startup states.
- DPI/resolution changes while the process remains active for many hours.
- Long-session performance profiling of the native composition path, including
  older GPUs, remote-desktop sessions, and native-host fallback behavior.

The current short Debug/playing sample used about `24.7%` of one logical core and
held private memory near `106 MiB`, with no growth across the sample. This is a
bounded sanity check, not a substitute for the long-session/GPU matrix above.

## Intentionally deferred

- Real application-loopback audio levels/FFT. The current motion is procedural and
  labeled as such.
- Multi-monitor selection and per-monitor profiles. Display abstractions already
  return multiple monitors, while presentation currently chooses the primary.
- Panel Mode. It will consume the same core events and integration state.
- Provider-specific media features such as Spotify Like/Unlike.
- Notification replies, reactions, or destructive actions.
- Pre-banner notification interception, which Windows does not expose through a
  reliable supported public API.
- Final NE production iconography and installer/store distribution.
