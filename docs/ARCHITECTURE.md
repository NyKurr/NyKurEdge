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
- `EdgeWindowLayout` keeps the idle window at `82 x 320` DIPs and the current
  contextual surface at `388 x 560` DIPs, centered with an offset seam for later
  upper/lower/custom placement.
- The collapsed HWND receives a tapered polygon region after XAML has loaded, so
  painting and mouse input follow the localized fluid envelope instead of a
  rectangle or the full monitor height.
- A four-second low-frequency display poll catches resolution, work-area, and DPI
  changes without an idle render loop.

The narrow physical anchor remains configurable and normalized to `10-24` DIPs.
Expansion is a short timer-driven transition only while movement is in progress;
no high-rate layout timer runs while idle.

## Edge rendering

`EdgeWaveRenderer` owns four reusable XAML `PathGeometry` layers: a broad bloom,
an outer accent trace, a neutral glass trace, and a brighter core trace. Each path
allocates its Bézier segments once and mutates only point structs while running;
the former per-tick `Polyline.Points.Clear()` churn is gone. Idle updates run at
roughly 8 Hz and playing updates at 30 Hz. The renderer computes one point set for
the bloom/outer pair but keeps an independent mutable geometry per `Path`; WinUI
dependency objects are never shared between visual parents.

`IEdgeMotionSource` supplies normalized energy/band values. The current
`ProceduralEdgeMotionSource` is deterministic and explicitly non-audio-reactive;
a future loopback analyzer can replace it without changing geometry or media
discovery. `EdgeBubbleController` leaves continuous breathing and notification
expansion/ripple timing on the Windows compositor.

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
