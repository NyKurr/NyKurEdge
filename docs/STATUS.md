# Status

## Validated in 0.1.0

- Packaged WinUI application launch and clean shutdown.
- Zero-warning Debug and Release builds on .NET 10.
- Core tests for accent selection, event subscriptions, settings persistence and
  normalization, rapid hover state, media source names, notification filtering,
  privacy previews, glance/pin-safe interaction state, and adaptive edge geometry.
- Visible transparent Win2D idle presentation: the desktop remains visible beneath
  the pressure field with no intentional black slab or rectangular fill.
- Native HWND creation and frame delivery, including explicit `WM_NCCREATE`
  acceptance, are implemented behind a development opt-in. That target is not the
  production default because it can report successful frames while presenting no
  visible pixels on at least one tested Windows/GPU path.
- Vertically extended spring-interpolated contours with long falloff, seventeen
  fine filaments, restrained radial pressure/bloom layers, and a stable
  half-embedded optical mesh orb.
- Mirrored right and left inward-flowing geometry validated at runtime, with
  deterministic 100% and 125% layout/DPI coverage in core tests.
- Layered fluid traces, glass orb, idle breathing, and restrained playing motion.
- Memory-only default-output loopback analysis with FFT-derived energy and
  low/mid/high bands, smooth fallback to the calm idle tide, and bounded recovery
  after transient capture failures.
- Diffuse accent glow behind the field and orb, driven gently by the same live
  signal while preserving a fully transparent collapsed surface.
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
- Native composition visibility/compatibility across GPUs, Windows builds,
  remote-desktop sessions, and display-driver paths before production enablement.
- Long-session performance profiling of both the Win2D production path and the
  experimental native composition path.
- Audio-response tuning across different output devices, sample formats, music
  genres, and mixed system-audio conditions.
- Automatic migration when Windows changes the default render device without
  stopping the previous endpoint's active loopback stream.

The prior short native Debug/playing sample used about `24.7%` of one logical core
and held private memory near `106 MiB`, with no growth across the sample. That is a
bounded native-path sanity check, not a measurement of the current production
Win2D path or a substitute for the long-session/GPU matrix above.

## Intentionally deferred

- Process-specific loopback isolation. The current implementation intentionally
  analyzes the default system-output mix while compatible media reports playback.
- Multi-monitor selection and per-monitor profiles. Display abstractions already
  return multiple monitors, while presentation currently chooses the primary.
- Panel Mode. It will consume the same core events and integration state.
- Provider-specific media features such as Spotify Like/Unlike.
- Notification replies, reactions, or destructive actions.
- Pre-banner notification interception, which Windows does not expose through a
  reliable supported public API.
- Final NE production iconography and installer/store distribution.
