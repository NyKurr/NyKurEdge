# Status

## Validated in 0.1.0

- Packaged WinUI application launch and clean shutdown.
- Zero-warning Debug build on .NET 10.
- Core tests for accent selection, event subscriptions, settings persistence and
  normalization, rapid hover state, media source names, notification filtering,
  privacy previews, glance/pin-safe interaction state, and full-height edge geometry.
- Transparent full-work-area-height Edge with sparse wave/input ribbons rather
  than a filled sidebar or rectangular idle backdrop.
- Mirrored right and left inward-flowing wave geometry and half-embedded orb at
  125% display scaling.
- Deterministic 100% and 125% layout/DPI coverage in core tests.
- Layered fluid traces, glass orb, idle breathing, and restrained playing motion.
- Hover preview and click-to-pin launcher states with an organic acrylic capsule
  bloom around the existing practical content surface.
- Notification pulse, expanded orb, icon layer, ripple, hold, and return sequence
  through the bounded visual-test mode.
- Live Spotify media metadata, artwork, timeline, and playback status through the
  Windows global media-session API.
- Automatic artwork accent, manual color changes, and return to automatic mode.
- Settings scrolling and persisted edge/accent changes.
- Clock glance appearance and return to the previous surface.
- Notification access state without triggering a permission prompt.
- Settled runtime sampling after the redesign measured `8.8%` of one logical
  core (`~1.1%` total CPU on the eight-thread validation machine) and `145.4 MB`
  working set during active Spotify playback.

## Implemented but requiring broader field testing

- Previous/play-pause/next/seek behavior across multiple media providers.
- Notification parsing across Telegram, Discord, and browser notification shapes.
- Notification arrival parsing/icon quality with real Telegram, Discord, and
  browser notifications. The presentation sequence itself is visually validated.
- StartupTask transitions across all Windows user-controlled startup states.
- DPI/resolution changes while the process remains active for many hours.

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
