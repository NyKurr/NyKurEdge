namespace NyKurEdge.Core.Display;

public readonly record struct DisplayRect(int X, int Y, int Width, int Height);

public sealed record DisplayInfo(
    string Id,
    string Name,
    DisplayRect Bounds,
    DisplayRect WorkArea,
    uint Dpi,
    bool IsPrimary);

public interface IDisplayService
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    DisplayInfo GetPrimaryDisplay();
}
