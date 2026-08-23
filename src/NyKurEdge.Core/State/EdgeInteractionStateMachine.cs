namespace NyKurEdge.Core.State;

public enum EdgeVisibility
{
    Collapsed,
    Expanded,
}

public sealed record EdgeInteractionState(
    EdgeVisibility Visibility,
    bool IsPointerInside,
    bool IsGlanceActive,
    bool IsPinnedOpen,
    DateTimeOffset? CollapseDueAt)
{
    public static EdgeInteractionState Initial { get; } = new(
        EdgeVisibility.Collapsed,
        false,
        false,
        false,
        null);
}

public sealed class EdgeInteractionStateMachine(TimeSpan? collapseGrace = null)
{
    private readonly TimeSpan _collapseGrace = collapseGrace ?? TimeSpan.FromMilliseconds(420);

    public EdgeInteractionState State { get; private set; } = EdgeInteractionState.Initial;

    public EdgeInteractionState PointerEntered()
    {
        State = State with
        {
            Visibility = EdgeVisibility.Expanded,
            IsPointerInside = true,
            CollapseDueAt = null,
        };
        return State;
    }

    public EdgeInteractionState PointerExited(DateTimeOffset now)
    {
        State = State with
        {
            IsPointerInside = false,
            CollapseDueAt = State.IsGlanceActive || State.IsPinnedOpen
                ? null
                : now + _collapseGrace,
        };
        return State;
    }

    public EdgeInteractionState SetPinned(bool pinned, DateTimeOffset now)
    {
        State = State with
        {
            Visibility = pinned ? EdgeVisibility.Expanded : State.Visibility,
            IsPinnedOpen = pinned,
            CollapseDueAt = pinned || State.IsPointerInside || State.IsGlanceActive
                ? null
                : now + _collapseGrace,
        };
        return State;
    }

    public EdgeInteractionState TogglePinned(DateTimeOffset now) =>
        SetPinned(!State.IsPinnedOpen, now);

    public EdgeInteractionState BeginGlance()
    {
        State = State with
        {
            Visibility = EdgeVisibility.Expanded,
            IsGlanceActive = true,
            CollapseDueAt = null,
        };
        return State;
    }

    public EdgeInteractionState EndGlance(DateTimeOffset now)
    {
        State = State with
        {
            IsGlanceActive = false,
            CollapseDueAt = State.IsPointerInside || State.IsPinnedOpen
                ? null
                : now + _collapseGrace,
        };
        return State;
    }

    public EdgeInteractionState Advance(DateTimeOffset now)
    {
        if (!State.IsPointerInside &&
            !State.IsGlanceActive &&
            !State.IsPinnedOpen &&
            State.CollapseDueAt is { } dueAt &&
            now >= dueAt)
        {
            State = State with
            {
                Visibility = EdgeVisibility.Collapsed,
                CollapseDueAt = null,
            };
        }

        return State;
    }
}
