using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.Graphics;

public sealed partial class RasterService
{
    private readonly List<RasterLayer> spriteBackgrounds = [];
    private long? buttonMapId;
    private long nextBackgroundId = -1;
    private sealed record BackgroundBinding(string Name, string? Selected, ScriptImageLayer Frame);
    private readonly Dictionary<long, BackgroundBinding> backgroundBindings = [];
    private async Task RefreshBackgrounds(CancellationToken ct)
    {
        foreach (long id in backgroundBindings.Keys.Where(id => !spriteBackgrounds.Any(l => l.Id == id)).ToArray()) backgroundBindings.Remove(id);
        for (int i = 0; i < spriteBackgrounds.Count; i++)
        {
            var layer = spriteBackgrounds[i];
            if (!backgroundBindings.TryGetValue(layer.Id, out var binding)) continue;
            async Task<RasterImage?> Frame(string? name)
            {
                if (name is null || !sprites.TryGetValue(name, out var sprite) || !Created(sprite)) return null;
                using var bitmap = await Snapshot(sprite, ct);
                return binding.Frame.WithFrame(new(bitmap.Width, bitmap.Height, bitmap.Bitmap.Pixels.Select(Argb).ToImmutableArray(), sprite.Animated)).Image;
            }
            var normal = await Frame(binding.Name);
            spriteBackgrounds[i] = layer with { Pixels = normal?.Pixels ?? Enumerable.Repeat(0u, layer.Width * layer.Height).ToImmutableArray(), SelectedImage = await Frame(binding.Selected) };
        }
    }
    private async ValueTask<GraphicsData?> ExecuteBackgroundAsync(GraphicsPayload request, CancellationToken ct)
    {
        string op = request.Operation; var a = request.Arguments;
        GraphicsData Result(long value) => new(new(value), Layers());
        if (op == "CBGSETBMAPG")
        {
            long id = a[0].AsInt();
            if (id is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
            if (!graphics.ContainsKey(id) || buttonMapId == id) return Result(0);
            buttonMapId = id; return Result(1);
        }
        if (op == "CBGREMOVEBMAP") { buttonMapId = null; return Result(1); }
        if (op == "CBGCLEARBUTTON") { spriteBackgrounds.RemoveAll(l => l.ButtonValue is not null); buttonMapId = null; return Result(1); }
        if (op == "CBGREMOVERANGE")
        {
            int min = unchecked((int)a[0].AsInt()), max = unchecked((int)a[1].AsInt());
            backgrounds.RemoveAll(l => l.Z >= min && l.Z <= max);
            spriteBackgrounds.RemoveAll(l => l.BackgroundName is null && l.Z >= min && l.Z <= max); return Result(1);
        }
        if (op == "CLEARBGIMAGE") { spriteBackgrounds.RemoveAll(l => l.BackgroundName is not null); return Result(1); }
        if (op == "REMOVEBGIMAGE")
        {
            int index = spriteBackgrounds.FindIndex(l => l.BackgroundName == a[0].AsString());
            if (index >= 0) spriteBackgrounds.RemoveAt(index); return Result(1);
        }
        if (op is not ("CBGSETSPRITE" or "CBGSETBUTTONSPRITE" or "SETBGIMAGE")) return null;
        await IndexSprites(ct);
        bool button = op == "CBGSETBUTTONSPRITE", background = op == "SETBGIMAGE";
        int nameIndex = button ? 1 : 0;
        if (button && a[0].AsInt() is < 0 or > 0xffffff) return Result(0);
        async Task<RasterImage?> Image(string name)
        {
            if (!sprites.TryGetValue(name, out var sprite) || !Created(sprite)) return null;
            using var bitmap = await Snapshot(sprite, ct);
            return new(bitmap.Width, bitmap.Height, bitmap.Bitmap.Pixels.Select(Argb).ToImmutableArray(), sprite.Animated);
        }
        string name = a[nameIndex].AsString();
        var image = await Image(name);
        var selected = button ? await Image(a[2].AsString()) : null;
        if (image is null && selected is null) return Result(0);
        image ??= new(selected!.Width, selected.Height, Enumerable.Repeat(0u, selected.Width * selected.Height).ToImmutableArray());
        int x = background ? 0 : checked((int)a[button ? 3 : 1].AsInt()), y = background ? 0 : checked((int)a[button ? 4 : 2].AsInt());
        int z = background ? int.MaxValue : checked((int)a[button ? 5 : 3].AsInt());
        if (!background && z == 0) throw new ScriptException("GRAPHICS_DEPTH", "图层深度不能为零");
        int width = !background && !button && a[4].AsInt() > 0 ? checked((int)a[4].AsInt()) : image.Width;
        int height = !background && !button && a[5].AsInt() > 0 ? checked((int)a[5].AsInt()) : image.Height;
        Size(width, height);
        int opacity = button ? 255 : (int)Math.Clamp(a[background ? 2 : 6].AsInt(), 0, 255);
        var resized = new ScriptImageLayer(z, x, y, 0, new(width, height, [])) { Opacity = opacity, Matrix = request.Matrix };
        var pixels = resized.WithFrame(image).Image;
        long layerId = nextBackgroundId--;
        backgroundBindings.Add(layerId, new(name, button ? a[2].AsString() : null, resized));
        spriteBackgrounds.Add(new(layerId, x, y, z, width, height, pixels.Pixels)
        {
            ButtonValue = button ? (int)a[0].AsInt() : null, SelectedImage = selected,
            Tooltip = button ? a[6].AsString() : null,
            Source = name, SelectedSource = button ? a[2].AsString() : null, Animated = image.Animated || selected?.Animated == true,
            Opacity = opacity, Matrix = request.Matrix.IsDefault ? [] : request.Matrix,
            BackgroundName = background ? name : null, BackgroundDepth = background ? a[1].AsInt() : 0
        });
        return Result(1);
    }
}
