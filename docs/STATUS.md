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
- Work-area-height layout and DPI conversion are covered by deterministic core
  tests; the compact `432 × 318` contextual bloom remains vertically centered
  inside the `152`-DIP-wide collapsed render surface.
- Full-height fallback input is split between a noninteractive visual HWND and a
  compact no-redirection launcher HWND that owns only the centered orb region;
  DPI-aware Win32 hit testing continues to constrain the expanding organic bloom.
  The collapsed WinUI root is also gated, so approaching the wave above or below
  the orb cannot trigger a preview.
- Native Windows notification-area registration with NyKur Edge branding, Open
  Settings and Exit commands, deterministic cleanup, and Explorer-restart recovery.
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
- Fresh runtime visual tuning of the new full-height moving field: three traveling
  vertical pressure zones, brighter distributed transparent glow, stronger idle
  motion, more sensitive real-audio response, and click-through behavior outside
  the orb/organic panel. A bounded right-edge fallback run confirmed the transparent
  `152 × 864`-pixel surface, moving upper/middle/lower contours, and centered orb;
  the wider wallpaper/DPI/refresh-rate matrix remains open.
- Automatic migration when Windows changes the default render device without
  stopping the previous endpoint's active loopback stream.

The prior short native Debug/playing sample used about `24.7%` of one logical core
and held private memory near `106 MiB`, with no growth across the sample. That is a
bounded native-path sanity check, not a measurement of the current production
Win2D path or a substitute for the long-session/GPU matrix above.

The current production Win2D fallback used about `44%` of one logical core
(`5.5%` of the eight-logical-core test machine) and `127 MiB` private memory during
a bounded ten-second Release idle sample at the smooth 60 Hz presentation cadence.
The richer always-moving field deliberately trades more CPU for motion quality in
this pass; reducing that cost without reintroducing visible stepping remains part
of the long-session optimization work.

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
