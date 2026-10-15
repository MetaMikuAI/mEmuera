using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class GraphicsBuiltins
{
    private sealed record Handler(string Name, string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        {
            if (Name is "GDRAWTEXT" or "GDRAWGWITHROTATE")
            {
                int first = Name == "GDRAWTEXT" ? 2 : 3;
                if (arguments[first].IsOmitted != arguments[first + 1].IsOmitted)
                    throw new ScriptException("ARGUMENT_COUNT", Name + " 坐标必须成对指定");
            }
            int matrixIndex = Name == "GDRAWG" ? 10 : Name is "SETIMAGELAYERL" or "SETIMAGELAYER" or "CBGSETSPRITE" ? 7 : 6;
            var matrix = Name is "GDRAWSPRITE" or "GDRAWG" or "SETIMAGELAYERL" or "SETIMAGELAYER" or "CBGSETSPRITE" && arguments[matrixIndex].Reference is { } reference ? context.ReadMatrix(reference) : default;
            if (Name is "SPRITECREATE" or "GCLEAR")
            {
                int count = arguments.Length;
                while (count > 0 && arguments[count - 1].IsOmitted) count--;
                if (Name == "SPRITECREATE" ? count is not (2 or 6 or 8 or 10) : count is not (2 or 6))
                    throw new ScriptException("ARGUMENT_COUNT", Name + " 参数数量错误");
                arguments = arguments[..count];
            }
            var values = arguments.Select(a => a.Value).ToImmutableArray();
            if (Name == "GFILLRECTANGLE") values = values.Add(new EraValue(0xff000000L | (uint)context.DefaultBackgroundColor));
            context.Defer(ServiceKind.Resource, new GraphicsPayload(Name, values, matrix)
            {
                AnchorLine = Name == "SETIMAGELAYERL" ? context.LineCount : null,
                FontName = context.FontName, FontSize = context.FontSize, Foreground = 0xff000000u | (uint)context.DefaultForegroundColor
            });
            return null;
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        void Add(string name, params BuiltinParameter[] parameters)
        {
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Query)); handlers.Add(new Handler(name, id));
        }
        Add("CBGCLEAR");
        Add("CBGCLEARBUTTON"); Add("CBGREMOVEBMAP");
        Add("CBGSETBMAPG", new BuiltinParameter(ValueKind.Integer));
        Add("CBGREMOVERANGE", new(ValueKind.Integer), new(ValueKind.Integer));
        Add("CBGSETBUTTONSPRITE", new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.String, Default: new EraValue("")));
        Add("CBGSETSPRITE", new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(1L)), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(255L)), new(ValueKind.Integer, Reference: true, Default: new EraValue(0L)));
        Add("SETBGIMAGE", new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(255L)));
        signatures[^1] = signatures[^1] with { ReturnType = null, Effect = BuiltinEffect.Output };
        Add("REMOVEBGIMAGE", new BuiltinParameter(ValueKind.String));
        signatures[^1] = signatures[^1] with { ReturnType = null, Effect = BuiltinEffect.Output };
        Add("CLEARBGIMAGE");
        signatures[^1] = signatures[^1] with { ReturnType = null, Effect = BuiltinEffect.Output };
        Add("GSETFONT", new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L)));
        Add("GGETFONT", new BuiltinParameter(ValueKind.Integer));
        signatures[^1] = signatures[^1] with { ReturnType = ValueKind.String };
        foreach (string name in new[] { "GGETFONTSIZE", "GGETFONTSTYLE", "GGETPEN", "GGETPENWIDTH", "GGETBRUSH", "G_POLYGON_DRAW", "G_POLYGON_FILL", "G_POLYGON_POINT_CLEAR" })
            Add(name, new BuiltinParameter(ValueKind.Integer));
        Add("CHKFONT", new BuiltinParameter(ValueKind.String));
        Add("GSETPEN", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GDASHSTYLE", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GDRAWLINE", Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer), 5).ToArray());
        Add("G_POLYGON_POINT_ADD", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GDRAWTEXT", new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)));
        Add("GGETTEXTSIZE", new(ValueKind.String), new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L)));
        Add("GDRAWGWITHROTATE", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(long.MinValue)), new(ValueKind.Integer, Default: new EraValue(long.MinValue)));
        Add("GSAVE", new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GLOAD", new(ValueKind.Integer), new(ValueKind.Integer));
        Add("$SPRITE_ACTIVE", new BuiltinParameter(ValueKind.String));
        Add("$SPRITE_ANIMATION", new BuiltinParameter(ValueKind.String));
        Add("$GDRAW_MASK_SIZE", new(ValueKind.Integer), new(ValueKind.Integer));
        Add("$SPRITE_RECT_READY", Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer), 6).ToArray());
        Add("SETIMAGELAYERL", new(ValueKind.String), new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(255L)), new(ValueKind.Integer, Reference: true, Default: new EraValue(0L)));
        signatures[^1] = signatures[^1] with { ReturnType = null, Effect = BuiltinEffect.Output };
        Add("SETIMAGELAYER", new(ValueKind.String), new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(255L)), new(ValueKind.Integer, Reference: true, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L)));
        signatures[^1] = signatures[^1] with { ReturnType = null, Effect = BuiltinEffect.Output };
        foreach (string name in new[] { "GDISPOSE", "GCREATED", "GWIDTH", "GHEIGHT" }) Add(name, new BuiltinParameter(ValueKind.Integer));
        foreach (string name in new[] { "SPRITEWIDTH", "SPRITEHEIGHT", "SPRITECREATED", "SPRITEPOSX", "SPRITEPOSY" }) Add(name, new BuiltinParameter(ValueKind.String));
        Add("SPRITEDISPOSE", new BuiltinParameter(ValueKind.String));
        Add("SPRITEDISPOSEALL", new BuiltinParameter(ValueKind.Integer));
        Add("SPRITECREATEFROMFILE", new(ValueKind.String), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)));
        foreach (string name in new[] { "SPRITEMOVE", "SPRITESETPOS", "SPRITEGETCOLOR" })
            Add(name, new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("SPRITECREATE", new[] { new BuiltinParameter(ValueKind.String), new BuiltinParameter(ValueKind.Integer) }
            .Concat(Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer, Default: new EraValue(0L)), 8)).ToArray());
        Add("SPRITEANIMECREATE", new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("SPRITEANIMEADDFRAME", new[] { new BuiltinParameter(ValueKind.String) }.Concat(Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer), 8)).ToArray());
        Add("GCLEAR", new[] { new BuiltinParameter(ValueKind.Integer), new BuiltinParameter(ValueKind.Integer) }
            .Concat(Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer, Default: new EraValue(0L)), 4)).ToArray());
        Add("GCREATE", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GCREATEFROMFILE", new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)));
        Add("GDRAWG", Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer), 10)
            .Append(new(ValueKind.Integer, Reference: true, Default: new EraValue(0L))).ToArray());
        Add("GDRAWGWITHMASK", Enumerable.Repeat(new BuiltinParameter(ValueKind.Integer), 5).ToArray());
        Add("GGETCOLOR", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GSETCOLOR", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GSETBRUSH", new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GFILLRECTANGLE", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("CBGSETG", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("GDRAWSPRITE", new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Reference: true, Default: new EraValue(0L)));
    }
}
