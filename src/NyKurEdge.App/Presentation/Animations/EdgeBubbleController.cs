using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NyKurEdge.App.Presentation.Animations;

public sealed class EdgeBubbleController : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly FrameworkElement _breathHost;
    private readonly FrameworkElement _bubbleBody;
    private readonly FrameworkElement _iconHost;
    private readonly FrameworkElement _incomingPulse;
    private readonly FrameworkElement _haloPrimary;
    private readonly FrameworkElement _haloSecondary;
    private readonly FrameworkElement _unreadRing;
    private bool _disposed;

    public EdgeBubbleController(
        FrameworkElement surface,
        FrameworkElement breathHost,
        FrameworkElement bubbleBody,
        FrameworkElement iconHost,
        FrameworkElement incomingPulse,
        FrameworkElement haloPrimary,
        FrameworkElement haloSecondary,
        FrameworkElement unreadRing)
    {
        _surface = surface;
        _breathHost = breathHost;
        _bubbleBody = bubbleBody;
        _iconHost = iconHost;
        _incomingPulse = incomingPulse;
        _haloPrimary = haloPrimary;
        _haloSecondary = haloSecondary;
        _unreadRing = unreadRing;
    }

    public void Start(bool isPlaying) => SetPlaying(isPlaying);

    public void SetPlaying(bool isPlaying)
    {
        ThrowIfDisposed();
        var visual = PrepareVisual(_breathHost);
        visual.StopAnimation("Scale");

        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.One);
        var peak = isPlaying ? 1.055f : 1.025f;
        animation.InsertKeyFrame(1, new Vector3(peak, peak, 1));
        animation.Duration = TimeSpan.FromMilliseconds(isPlaying ? 1320 : 2600);
        animation.Direction = AnimationDirection.Alternate;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", animation);
    }

    public void SetUnread(bool unread)
    {
        ThrowIfDisposed();
        var visual = PrepareVisual(_unreadRing);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, visual.Opacity);
        animation.InsertKeyFrame(1, unread ? 0.20f : 0f);
        animation.Duration = TimeSpan.FromMilliseconds(unread ? 360 : 520);
        visual.StartAnimation("Opacity", animation);
    }

    public void TriggerNotification(double timingScale = 1)
    {
        ThrowIfDisposed();
        timingScale = Math.Clamp(timingScale, 1, 4);
        AnimateIncomingPulse(timingScale);
        AnimateBubbleExpansion(timingScale);
        AnimateIcon(timingScale);
        AnimateHalo(
            _haloPrimary,
            delayMilliseconds: 190,
            scale: 2.35f,
            opacity: 0.34f,
            timingScale);
        AnimateHalo(
            _haloSecondary,
            delayMilliseconds: 360,
            scale: 2.7f,
            opacity: 0.20f,
            timingScale);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var element in new[]
                 {
                     _breathHost,
                     _bubbleBody,
                     _iconHost,
                     _incomingPulse,
                     _haloPrimary,
                     _haloSecondary,
                     _unreadRing,
                 })
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset.Y");
        }
    }

    private void AnimateIncomingPulse(double timingScale)
    {
        var visual = PrepareVisual(_incomingPulse);
        var compositor = visual.Compositor;
        var startY = visual.Offset.Y;
        var targetY = Math.Max(startY, (float)((_surface.ActualHeight - _incomingPulse.ActualHeight) / 2));

        var travel = compositor.CreateScalarKeyFrameAnimation();
        travel.InsertKeyFrame(0, startY);
        travel.InsertKeyFrame(1, targetY, EaseOut(compositor));
        travel.Duration = TimeSpan.FromMilliseconds(270 * timingScale);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(0.16f, 0.76f);
        opacity.InsertKeyFrame(0.82f, 0.62f);
        opacity.InsertKeyFrame(1, 0);
        opacity.Duration = travel.Duration;

        visual.StartAnimation("Offset.Y", travel);
        visual.StartAnimation("Opacity", opacity);
    }

    private void AnimateBubbleExpansion(double timingScale)
    {
        var visual = PrepareVisual(_bubbleBody);
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.One);
        animation.InsertKeyFrame(0.14f, new Vector3(1.56f, 1.56f, 1), EaseOut(compositor));
        animation.InsertKeyFrame(0.70f, new Vector3(1.56f, 1.56f, 1));
        animation.InsertKeyFrame(1, Vector3.One, EaseInOut(compositor));
        animation.Duration = TimeSpan.FromMilliseconds(1840 * timingScale);
        visual.StartAnimation("Scale", animation);
    }

    private void AnimateIcon(double timingScale)
    {
        var visual = PrepareVisual(_iconHost);
        var compositor = visual.Compositor;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(0.13f, 0);
        opacity.InsertKeyFrame(0.24f, 0.94f, EaseOut(compositor));
        opacity.InsertKeyFrame(0.72f, 0.94f);
        opacity.InsertKeyFrame(0.92f, 0);
        opacity.InsertKeyFrame(1, 0);
        opacity.Duration = TimeSpan.FromMilliseconds(1840 * timingScale);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.62f, 0.62f, 1));
        scale.InsertKeyFrame(0.24f, Vector3.One, EaseOut(compositor));
        scale.InsertKeyFrame(0.78f, Vector3.One);
        scale.InsertKeyFrame(1, new Vector3(0.82f, 0.82f, 1));
        scale.Duration = opacity.Duration;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    private static void AnimateHalo(
        FrameworkElement element,
        int delayMilliseconds,
        float scale,
        float opacity,
        double timingScale)
    {
        var visual = PrepareVisual(element);
        var compositor = visual.Compositor;

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.InsertKeyFrame(0, new Vector3(0.82f, 0.82f, 1));
        scaleAnimation.InsertKeyFrame(1, new Vector3(scale, scale, 1), EaseOut(compositor));
        scaleAnimation.DelayTime = TimeSpan.FromMilliseconds(delayMilliseconds * timingScale);
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(980 * timingScale);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(0.12f, opacity);
        opacityAnimation.InsertKeyFrame(1, 0);
        opacityAnimation.DelayTime = scaleAnimation.DelayTime;
        opacityAnimation.Duration = scaleAnimation.Duration;

        visual.StartAnimation("Scale", scaleAnimation);
        visual.StartAnimation("Opacity", opacityAnimation);
    }

    private static Visual PrepareVisual(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)(element.ActualWidth / 2),
            (float)(element.ActualHeight / 2),
            0);
        return visual;
    }

    private static CubicBezierEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));

    private static CubicBezierEasingFunction EaseInOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.15f, 1));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
