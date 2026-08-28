# Architecture

NyKur Edge separates state and policy from Windows APIs and from WinUI. The goal
is to let future Panel Mode or hardware outputs consume the same backend state
without duplicating integrations.

## Dependency direction

```text
NyKurEdge.App
  ├─> NyKurEdge.Core
  └─> NyKurEdge.Infrastructure ─> NyKurEdge.Core
```

`NyKurEdge.Core` has no Windows or WinUI dependency. It owns event contracts,
settings, media/notification/glance models, interaction state, accent selection,
and small policies. Its tests run without a desktop session.

`NyKurEdge.Infrastructure` implements the operating-system boundaries:

- `WindowsMediaSessionService` wraps
  `GlobalSystemMediaTransportControlsSessionManager`.
- `WindowsNotificationListenerService` wraps `UserNotificationListener`.
- `WindowsDisplayService` enumerates monitor/work-area/DPI data through supported
  Win32 APIs.
- `WindowsStartupService` wraps packaged `StartupTask` state.
- `JsonSettingsStore` performs recoverable local persistence.
- `WindowsArtworkAccentExtractor` decodes a bounded artwork sample before handing
  pixels to the pure OKLab selector.

`NyKurEdge.App` is the composition root and presentation layer. `AppServices`
constructs the small service graph explicitly; there is no container or service
locator. `EdgeViewModel` translates backend events into bindable presentation
state. `MainPage` owns only UI behavior and composition animations.

## Event flow

The typed event bus currently carries:

- `MediaChanged`
- `PlaybackStateChanged`
- `NotificationReceived`
- `NotificationDismissed`
- `AccentChanged`
- `GlanceRequested`
- `GlanceEnded`
- `SettingsChanged`

Subscriptions return `IDisposable` handles. Long-running integrations implement
async disposal, unsubscribe from WinRT events, and release timers/cancellation
sources during shutdown.

## Edge window

`EdgeWindowController` owns window policy independently of page content:

- `OverlappedPresenter` removes border/title bar and disables resizing.
- `WS_EX_TOOLWINDOW` and `AppWindow.IsShownInSwitchers = false` keep the normal
  Edge surface out of the taskbar and Alt+Tab.
- `WS_EX_NOACTIVATE`, `AppWindow.Show(false)`, and `SetWindowPos(...SWP_NOACTIVATE)`
  prevent ambient appearance from taking focus.
- Settings interaction temporarily permits activation so keyboard/color controls
  remain usable.
- Bounds are derived from the selected display's work area and effective DPI.
- `EdgeWindowLayout` sizes the collapsed field independently from the contextual
  surface (`112 × 720` DIPs and `432 × 318` DIPs respectively). Taskbar-adjusted
  work-area bounds and effective DPI remain the source of physical placement.
- The production collapsed phenomenon is drawn by the transparent Win2D surface.
  The WinUI HWND region follows the visible fluid field and orb instead of exposing
  an interactive full-height rectangle.
- `NativeEdgeCompositionHost` contains an experimental separate
  `WS_EX_NOREDIRECTIONBITMAP` no-activation tool window. It is enabled only when
  `NYKUR_EDGE_NATIVE_COMPOSITION=1`; this prevents a native target that accepts
  frames but presents no pixels on some systems from making the product invisible.
- While that native target is explicitly enabled, the WinUI HWND retains one
  transparent keep-alive pixel so Win2D can continue producing frame geometry
  without painting a duplicate surface.
- The native class procedure explicitly accepts `WM_NCCREATE`; returning zero at
  that point aborts `CreateWindowEx` and previously caused a silent Win2D fallback.
- Desktop acrylic belongs only to the expanded WinUI shell. The transparent idle
  renderer fades while the shell establishes itself, preserving a continuous
  orb-to-panel transition.
- A four-second low-frequency display poll catches resolution, work-area, and DPI
  changes without an idle render loop.

The physical anchor setting is reduced to a sub-two-DIP seam in presentation, so
legacy preferences cannot turn the idle identity back into a thick bar.
Expansion is a short timer-driven transition only while movement is in progress;
no high-rate layout timer runs while idle.

## Edge rendering

`EdgeWaveRenderer` separates sparse signal simulation from continuous visual
presentation. Fifteen damped control nodes drift toward low-frequency procedural
targets, and a bounded spring integrator interpolates them during compositor render
ticks. Seventy-three sampled points form four separated structural contours plus
seventeen harmonized fine strands, with a broad center envelope and long, naturally
dissipating upper/lower tails. Notification travel, pressure displacement, playing
energy, and expansion progress all enter the same field model instead of being
unrelated overlay effects.

`EdgeWaveRenderer` draws those points on the production Win2D surface as faint
pressure fields, accent filament families, a neutral edge anchor, atmospheric
bloom, and a layered optical half-lens. The experimental
`NativeEdgeCompositionHost` consumes the same bounded frame model and converts it
to system-composition shapes, so it can be compatibility-tested without forking the
simulation or presentation state. When explicitly active, the Win2D mirror is not
redundantly drawn.
The orb center and outer radius stay geometrically fixed in idle—only low-amplitude
internal refraction changes—so ambient breathing does not introduce positional
micro-jitter. The Win2D canvas is the visible default until native presentation
compatibility is demonstrated across the target hardware matrix.

`EdgeInteractionStateMachine` distinguishes transient pointer preview from an
intentional pinned-open launcher state. The visible half-orb is the click target;
pinning temporarily permits window activation, while passive hover retains the
normal no-activation behavior.

`IEdgeMotionSource` supplies normalized energy/band values. The current
`ProceduralEdgeMotionSource` is deterministic and explicitly non-audio-reactive;
a future loopback analyzer can replace it without changing geometry or media
discovery. Notification timing remains coordinated by `EdgeBubbleController`,
while its pulse, orb displacement, and ripple are rendered by the same field path.

## Media

The media adapter observes the Windows global media-session manager instead of a
Spotify-specific API. Session and playback events publish immutable snapshots.
Artwork reads are bounded to 8 MB, and decoded color analysis is downsampled to
64 pixels on the longest working dimension.

The initial edge signal is procedural and does not claim audio reactivity. The
renderer contract is ready for a later loopback-audio analyzer without changing
media discovery or presentation state.

## Accent engine

The accent selector:

1. samples non-transparent pixels;
2. converts sRGB into OKLab;
3. rejects near-black, near-white, and weakly chromatic candidates;
4. bins candidates perceptually and scores population, chroma, and usable
   lightness;
5. constrains lightness/chroma and maps the result back into the sRGB gamut.

Presentation interpolates between accents in OKLab over a short composition-era
transition. The same mutable brushes feed the edge, buttons, sliders, and toggles
so Windows' unrelated system accent does not leak into the visual identity.

## Notifications

The listener is permission-gated and uses only the public Windows notification
listener contract. A source policy rejects globally disabled or individually
disabled applications before presentation. Privacy is applied before assigning
preview text:

- app only;
- sender/title;
- full preview.

The public API cannot reliably prevent another application's banner before it is
shown. NyKur Edge documents the supported per-app banner configuration and does
not use registry interception or race-based toast deletion.

## Glances

`GlanceCoordinator` serializes temporary presentations through a semaphore and
publishes begin/end events. The clock scheduler is only the first producer. This
keeps timing policy separate from the Edge UI and lets later modules request the
same temporary surface.
