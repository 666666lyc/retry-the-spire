// Shared "pennant" banner button. Used by the run-history "View Acts"
// button and the game-over "View Acts" button. The shape mirrors the
// game's NBackButton atlas, recolored via a luminance×tint shader so
// any hue keeps the texture's painterly grain.
//
// Anchor side controls which way the pennant tail hangs:
//   • Right (default): banner sits at the screen's right edge with
//     the notches pointing inward; FlipH on the texture mirrors the
//     left-edge source art horizontally.
//   • Left:  banner sits at the left edge, source orientation.
//
// Returns the root Control so callers can adjust positioning (e.g.
// align to a sibling's row, fix Y offset for a game-over button).
using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace Retry;

public enum BannerSide
{
    Left,
    Right
}

public sealed class BannerButtonOptions
{
    public string Name = "BannerButton";
    public string Text = "Button";
    public BannerSide Side = BannerSide.Right;
    public Color Tint = new(0.20f, 0.40f, 0.78f, 1f);
    public float Brightness = 2.1f;
    public float OffsetTop = 280; // local position when not aligned
    public Action? OnClick;
}

public static class BannerButton
{
    public const string BannerTexPath = "res://images/atlases/ui_atlas.sprites/back_button.tres";
    public const string OutlineTexPath = "res://images/atlases/compressed.sprites/back_button_outline.tres";
    public const string KreonBoldTooltipPath = "res://themes/kreon_bold_glyph_space_one.tres";
    public static Control Create(BannerButtonOptions opts)
    {
        var bannerTex = ResourceLoader.Load<Texture2D>(BannerTexPath);
        var outlineTex = ResourceLoader.Load<Texture2D>(OutlineTexPath);
        var font = ResourceLoader.Load<FontVariation>(KreonBoldTooltipPath);
        var root = new Control
        {
            Name = opts.Name
        };
        const float bannerW = 460f;
        const float slidePast = 150f;
        if (opts.Side == BannerSide.Right)
        {
            root.AnchorLeft = 1;
            root.AnchorRight = 1;
            root.OffsetLeft = -bannerW + slidePast;
            root.OffsetRight = slidePast;
        }
        else
        {
            root.AnchorLeft = 0;
            root.AnchorRight = 0;
            root.OffsetLeft = -slidePast;
            root.OffsetRight = bannerW - slidePast;
        }

        root.AnchorTop = 0;
        root.AnchorBottom = 0;
        root.OffsetTop = opts.OffsetTop;
        root.OffsetBottom = opts.OffsetTop + 100;
        root.MouseFilter = Control.MouseFilterEnum.Pass;
        static void Fill(Control c)
        {
            c.AnchorLeft = 0;
            c.AnchorRight = 1;
            c.AnchorTop = 0;
            c.AnchorBottom = 1;
            c.OffsetLeft = 0;
            c.OffsetRight = 0;
            c.OffsetTop = 0;
            c.OffsetBottom = 0;
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        // Source texture notches point LEFT. Right-anchored buttons
        // mirror horizontally so the notches point at the screen edge.
        bool flip = opts.Side == BannerSide.Right;
        if (bannerTex != null)
        {
            var shadow = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = new Color(0f, 0f, 0f, 0.55f),
                FlipH = flip,
            };
            Fill(shadow);
            shadow.OffsetLeft += 4;
            shadow.OffsetRight += 4;
            shadow.OffsetTop += 5;
            shadow.OffsetBottom += 5;
            root.AddChild(shadow);
        }

        TextureRect? outlineNode = null;
        if (outlineTex != null)
        {
            outlineNode = new TextureRect
            {
                Texture = outlineTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = Colors.Transparent,
                FlipH = flip,
            };
            Fill(outlineNode);
            root.AddChild(outlineNode);
        }

        if (bannerTex != null)
        {
            var banner = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                FlipH = flip,
            };
            Fill(banner);
            banner.Material = BuildRecolorMaterial(opts.Tint, opts.Brightness);
            root.AddChild(banner);
        }

        var label = new MegaLabel
        {
            Text = opts.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinFontSize = 28,
            MaxFontSize = 28,
        };
        if (font != null)
            label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", new Color(0.96f, 0.90f, 0.78f, 1f));
        label.AddThemeColorOverride("font_outline_color", new Color(0.18f, 0.07f, 0.04f, 1f));
        label.AddThemeConstantOverride("outline_size", 6);
        Fill(label);
        label.OffsetTop -= 6;
        label.OffsetBottom -= 6;
        // Nudge text toward the visible half of the banner so it
        // isn't centered over the pennant tail.
        int nudge = opts.Side == BannerSide.Right ? -15 : 15;
        label.OffsetLeft += nudge;
        label.OffsetRight += nudge;
        root.AddChild(label);
        var hit = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None
        };
        Fill(hit);
        hit.MouseFilter = Control.MouseFilterEnum.Stop;
        hit.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var outlineForHover = outlineNode;
        var glow = new Color(0.95f, 0.74f, 0.10f, 0.75f);
        hit.MouseEntered += () =>
        {
            try
            {
                if (outlineForHover != null && GodotObject.IsInstanceValid(outlineForHover))
                    outlineForHover.CreateTween().TweenProperty(outlineForHover, "modulate", glow, 0.10);
            }
            catch
            {
            }
        };
        hit.MouseExited += () =>
        {
            try
            {
                if (outlineForHover != null && GodotObject.IsInstanceValid(outlineForHover))
                    outlineForHover.CreateTween().TweenProperty(outlineForHover, "modulate", Colors.Transparent, 0.25);
            }
            catch
            {
            }
        };
        var captured = opts.OnClick;
        if (captured != null)
            hit.Pressed += () => captured();
        root.AddChild(hit);
        return root;
    }

    private static ShaderMaterial BuildRecolorMaterial(Color tint, float brightness)
    {
        var shader = new Shader();
        shader.Code = @"
shader_type canvas_item;
uniform vec3 tint : source_color = vec3(1.0);
uniform float brightness : hint_range(0.0, 4.0) = 1.0;
void fragment() {
    vec4 tex = texture(TEXTURE, UV);
    float lum = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    COLOR = vec4(tint * lum * brightness, tex.a);
}
";
        var mat = new ShaderMaterial
        {
            Shader = shader
        };
        mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        mat.SetShaderParameter("brightness", brightness);
        return mat;
    }
}
