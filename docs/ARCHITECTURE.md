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
- `WindowsLoopbackAudioAnalyzer` reduces the default render endpoint to normalized
  energy and low/mid/high-band snapshots. It retains only a small circular sample
  buffer, never writes audio, and uses bounded recovery after capture startup or
  stream failures.

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
- `EdgeWindowLayout` keeps the Edge HWND at the taskbar-adjusted primary work-area
  height in both states. Its collapsed width is `152` DIPs; the compact contextual
  bloom remains approximately `432 × 318` DIPs and is centered vertically inside
  that full-height surface. Effective DPI remains the source of physical placement.
- The production collapsed phenomenon is drawn by the transparent Win2D surface.
  The full-height visual HWND remains visually transparent and input-transparent
  while collapsed. A separate no-redirection native launcher HWND owns only the
  small edge-embedded orb region, so unrelated desktop applications keep their
  pointer and wheel input. As expansion begins, the WinUI surface regains input
  and its DPI-aware `WM_NCHITTEST` geometry narrows interaction to the organic
  panel silhouette.
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
presentation. Seventeen damped control nodes drift toward low-frequency procedural
targets, and a bounded spring integrator interpolates them during compositor render
ticks. Presentation uses an accumulated 60 Hz deadline in every state so a 60 Hz
compositor cannot alias idle motion down to 30 Hz; sparse target sampling still
keeps simulation work bounded. Seventy-three sampled points form four separated
structural contours plus thirty-two harmonized fine strands. Three separated moving vertical
energy/pressure zones travel through a low-amplitude full-height floor, allowing
activity to migrate above and below the fixed orb instead of repeatedly peaking at
one center envelope. Notification travel, pressure displacement, playing energy,
and expansion progress all enter the same field model instead of being unrelated
overlay effects.

`EdgeWaveRenderer` draws those points on the production Win2D surface as faint
pressure fields, accent filament families, a neutral edge anchor, brighter
distributed transparent bloom, and a layered optical half-lens. The experimental
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
normal no-activation behavior. A compact native launcher HWND and a matching WinUI
button own only the orb-sized activation footprint; entering the full-height render
surface cannot initiate expansion while the Edge is collapsed. The visual HWND uses
layered `WS_EX_TRANSPARENT` pass-through while collapsed, because `HTTRANSPARENT`
alone is limited to windows on the same UI thread.

`NyKurNotificationAreaIcon` is a separate native shell host for the Windows
notification area. It exposes only Open Settings and Exit, restores itself after an
Explorer restart, and is disposed with the main window. Keeping this affordance out
of the ambient Edge preserves its no-taskbar/no-Alt+Tab window behavior.

`IEdgeMotionSource` supplies normalized energy/band values.
`AudioReactiveEdgeMotionSource` applies a noise-gated concave response curve and
asymmetric attack/release smoothing to fresh memory-only WASAPI spectrum snapshots,
then eases back to `ProceduralEdgeMotionSource` for idle, pause, stale input, or
capture unavailability. This makes quiet and medium program material visibly more
responsive without turning endpoint noise into motion. Notification timing remains
coordinated by `EdgeBubbleController`, while its pulse, orb displacement, and ripple
are rendered by the same field path.

## Media

The media adapter observes the Windows global media-session manager instead of a
Spotify-specific API. Session and playback events publish immutable snapshots.
Artwork reads are bounded to 8 MB, and decoded color analysis is downsampled to
64 pixels on the longest working dimension.

While global media reports playback, a separate default-output loopback analyzer
provides real energy and low/mid/high-band motion input. This is deliberately
system-output analysis rather than provider-specific integration, so other audible
system sounds can influence the field during playback. Process-specific capture is
a future refinement and does not affect media discovery or presentation state.

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
