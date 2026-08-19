using System;
using System.Numerics;
using ClaySharp;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NvgSharp;

namespace ClaySharp.Renderer.NvgSharp;

/// <summary>
/// NvgSharp (NanoVG-style) renderer for Clay, ported from <see cref="ClaySharp.Renderer.FNA.FNA_Clay"/>.
///
/// Where the FNA renderer batches geometry into a dynamic vertex buffer, this renderer maps
/// each Clay render command to the NvgSharp immediate-mode path API (<see cref="NvgContext"/>).
/// Rounded rectangles, borders, images and scissor clipping are all expressed as NanoVG paths,
/// giving anti-aliased corners for free. Text is drawn through NvgSharp.Text (FontStashSharp
/// backed) into the same render cache.
///
/// Usage mirrors the FNA renderer: construct a <see cref="NvgContext"/> once (e.g.
/// <c>new NvgContext(graphicsDevice, true)</c>), store it in <see cref="NvgRendererData.Context"/>,
/// then call <see cref="RenderClayCommands"/> once per frame. The renderer resets state and
/// flushes the accumulated geometry around the command loop (NvgSharp has no BeginFrame/EndFrame).
/// </summary>
public static class Nvg_Clay
{
    /// <summary>
    /// Optional per-font text styling (text shadow/outline effects), applied when the text
    /// command's userData implements this interface. Mirrors <c>FNA_Clay.IFnaFontEffect</c>.
    /// </summary>
    public interface INvgFontEffect
    {
        public TextStyle TextStyle => TextStyle.None;
        public FontSystemEffect Effect => FontSystemEffect.None;
        public int EffectAmount => 0;
    }

    /// <summary>
    /// Resources the renderer needs. The caller owns these (they are not created by the
    /// renderer) — parity with <c>FNA_Clay.FnaRendererData</c>.
    /// </summary>
    public struct NvgRendererData
    {
        /// <summary>The NvgSharp context used to record and draw the UI. Owned by the caller.</summary>
        public NvgContext Context;

        /// <summary>Fonts indexed by Clay fontId.</summary>
        public FontSystem[] Fonts;

        /// <summary>Resolves imageData into a <see cref="Texture2D"/> when imageData is not already one.</summary>
        public Func<object, Texture2D?>? TextureResolver;

        /// <summary>Renders images when imageData is not a <see cref="Texture2D"/> and cannot be resolved.</summary>
        public ImageRenderHandler? ImageRenderer;

        /// <summary>Renders custom elements.</summary>
        public CustomRenderHandler? CustomRenderer;

        /// <summary>Resolves a <see cref="FontSystem"/> from the text command + userData instead of fontId.</summary>
        public FontGetHandler? FontGetter;

        public NvgRendererData(NvgContext context, FontSystem[]? fonts = null)
        {
            Context = context;
            Fonts = fonts ?? [];
        }
    }

    public delegate void ImageRenderHandler(ref Clay.ImageRenderData data);
    public delegate void CustomRenderHandler(ref Clay.CustomRenderData data);
    public delegate FontSystem FontGetHandler(ref Clay.TextRenderData data, object? userData);

    private const float DegToRad = MathF.PI / 180f;

    private static Color ToColor(in Clay.Color c) => new Color((int)c.R, (int)c.G, (int)c.B, (int)c.A);

    // ---------------------------------------------------------------
    // Shape helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Filled rounded rectangle. When all corner radii are zero, <see cref="NvgContext.RoundedRectVarying"/>
    /// falls back to a plain rect internally.
    /// </summary>
    private static void RenderRectangle(NvgContext context, in Clay.BoundingBox rect, in Clay.CornerRadiusValues cornerRadius, Color color)
    {
        context.BeginPath();
        context.RoundedRectVarying(
            rect.X, rect.Y, rect.Width, rect.Height,
            cornerRadius.TopLeft, cornerRadius.TopRight,
            cornerRadius.BottomRight, cornerRadius.BottomLeft);
        context.FillColor(color);
        context.Fill();
    }

    /// <summary>
    /// Filled image clipped to a rounded rectangle. The image is stretched across the destination
    /// rect (matching the FNA renderer, which maps UVs 0..1 across the rect).
    /// </summary>
    private static void RenderImage(NvgContext context, in Clay.BoundingBox rect, in Clay.CornerRadiusValues cornerRadius, Texture2D texture)
    {
        context.BeginPath();
        context.RoundedRectVarying(
            rect.X, rect.Y, rect.Width, rect.Height,
            cornerRadius.TopLeft, cornerRadius.TopRight,
            cornerRadius.BottomRight, cornerRadius.BottomLeft);
        context.FillPaint(context.ImagePattern(rect.X, rect.Y, rect.Width, rect.Height, 0f, texture, 1f));
        context.Fill();
    }

    /// <summary>
    /// Quarter-ring corner of a border. A NanoVG stroke is centered on the path, so the arc path
    /// radius is offset inward by half the stroke width — putting the stroked band's outer edge at
    /// <paramref name="radius"/> and its inner edge at <c>radius - thickness</c>, exactly like the
    /// FNA renderer's annulus-sector <c>AppendArc</c>.
    /// </summary>
    private static void RenderArc(NvgContext context, float cx, float cy, float radius, float startAngleDeg, float endAngleDeg, float thickness, Color color)
    {
        float arcRadius = Math.Max(radius - thickness * 0.5f, 0.5f);
        context.BeginPath();
        context.Arc(cx, cy, arcRadius, startAngleDeg * DegToRad, endAngleDeg * DegToRad, Winding.ClockWise);
        context.StrokeColor(color);
        context.StrokeWidth(thickness);
        context.LineCap(LineCap.Butt);
        context.Stroke();
    }

    /// <summary>
    /// Border drawn as 4 edge rectangles + 4 corner quarter-ring arcs, mirroring the FNA/SDL3
    /// reference. Top corners use <c>Width.Top</c> and bottom corners use <c>Width.Bottom</c>.
    /// </summary>
    private static void RenderBorder(NvgContext context, in Clay.BoundingBox rect, in Clay.BorderRenderData config)
    {
        Color color = ToColor(config.Color);
        float minRadius = Math.Min(rect.Width, rect.Height) / 2f;
        float tl = Math.Min(config.CornerRadius.TopLeft, minRadius);
        float tr = Math.Min(config.CornerRadius.TopRight, minRadius);
        float bl = Math.Min(config.CornerRadius.BottomLeft, minRadius);
        float br = Math.Min(config.CornerRadius.BottomRight, minRadius);

        float x0 = rect.X, y0 = rect.Y, x1 = rect.X + rect.Width, y1 = rect.Y + rect.Height;

        context.FillColor(color);

        // Edges (filled rects), inset by the clamped corner radii.
        if (config.Width.Left > 0)
        {
            context.BeginPath();
            context.Rect(x0, y0 + tl, config.Width.Left, rect.Height - tl - bl);
            context.Fill();
        }

        if (config.Width.Right > 0)
        {
            context.BeginPath();
            context.Rect(x1 - config.Width.Right, y0 + tr, config.Width.Right, rect.Height - tr - br);
            context.Fill();
        }

        if (config.Width.Top > 0)
        {
            context.BeginPath();
            context.Rect(x0 + tl, y0, rect.Width - tl - tr, config.Width.Top);
            context.Fill();
        }

        if (config.Width.Bottom > 0)
        {
            context.BeginPath();
            context.Rect(x0 + bl, y1 - config.Width.Bottom, rect.Width - bl - br, config.Width.Bottom);
            context.Fill();
        }

        // Corners (quarter-ring arcs).
        if (config.CornerRadius.TopLeft > 0)
        {
            RenderArc(context, x0 + tl, y0 + tl, tl, 180f, 270f, config.Width.Top, color);
        }

        if (config.CornerRadius.TopRight > 0)
        {
            RenderArc(context, x1 - tr, y0 + tr, tr, 270f, 360f, config.Width.Top, color);
        }

        if (config.CornerRadius.BottomLeft > 0)
        {
            RenderArc(context, x0 + bl, y1 - bl, bl, 90f, 180f, config.Width.Bottom, color);
        }

        if (config.CornerRadius.BottomRight > 0)
        {
            RenderArc(context, x1 - br, y1 - br, br, 0f, 90f, config.Width.Bottom, color);
        }
    }

    private static void RenderText(NvgRendererData rendererData, NvgContext context, ref Clay.TextRenderData tc, ref Clay.RenderCommand rcmd, in Clay.BoundingBox bb)
    {
        SpriteFontBase font;
        if (rendererData.Fonts.Length > tc.FontId)
        {
            font = rendererData.Fonts[tc.FontId].GetFont(tc.FontSize);
        }
        else if (rendererData.FontGetter is not null)
        {
            font = rendererData.FontGetter.Invoke(ref tc, rcmd.UserData).GetFont(tc.FontSize);
        }
        else
        {
            return; // no matching font
        }

        Microsoft.Extensions.Primitives.StringSegment contents = tc.StringContents;

        TextStyle textStyle = TextStyle.None;
        FontSystemEffect effect = FontSystemEffect.None;
        int effectAmount = 0;
        if (rcmd.UserData is INvgFontEffect customEffect)
        {
            textStyle = customEffect.TextStyle;
            effect = customEffect.Effect;
            effectAmount = customEffect.EffectAmount;
        }

        context.FillColor(ToColor(tc.TextColor));
        context.Text(
            font,
            contents.AsSpan(),
            bb.X,
            bb.Y - font.LineHeight,
            characterSpacing: tc.LetterSpacing,
            textStyle: textStyle,
            effect: effect,
            effectAmount: effectAmount);
    }

    // ---------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------

    public static void RenderClayCommands(NvgRendererData rendererData, Clay.RenderCommandArray rcommands)
    {
        NvgContext context = rendererData.Context;

        // NvgSharp has no BeginFrame/EndFrame; reset to a clean state (fill white, stroke black,
        // identity transform, no scissor) and let the caller's accumulated geometry flush at the end.
        context.ResetState();

        for (int i = 0; i < rcommands.Length; i++)
        {
            ref Clay.RenderCommand rcmd = ref rcommands.Get(i);
            Clay.BoundingBox bb = rcmd.BoundingBox;

            // Skip empty drawable commands (scissor still needs processing).
            bool isScissor = rcmd.CommandType == Clay.RenderCommandType.ScissorStart
                || rcmd.CommandType == Clay.RenderCommandType.ScissorEnd;
            if (!isScissor && (bb.Width <= 0f || bb.Height <= 0f))
            {
                continue;
            }

            switch (rcmd.CommandType)
            {
                case Clay.RenderCommandType.Rectangle:
                    RenderRectangle(context, bb, rcmd.RenderData.Rectangle.CornerRadius, ToColor(rcmd.RenderData.Rectangle.BackgroundColor));
                    break;

                case Clay.RenderCommandType.Border:
                    RenderBorder(context, bb, rcmd.RenderData.Border);
                    break;

                case Clay.RenderCommandType.Text:
                    RenderText(rendererData, context, ref rcmd.RenderData.Text, ref rcmd, bb);
                    break;

                case Clay.RenderCommandType.ScissorStart:
                    context.Scissor(bb.X, bb.Y, bb.Width, bb.Height);
                    break;

                case Clay.RenderCommandType.ScissorEnd:
                    context.ResetScissor();
                    break;

                case Clay.RenderCommandType.Image:
                    {
                        ref Clay.ImageRenderData ic = ref rcmd.RenderData.Image;
                        if (ic.ImageData is Texture2D texture)
                        {
                            RenderImage(context, bb, ic.CornerRadius, texture);
                        }
                        else if (ic.ImageData is not null)
                        {
                            Texture2D? resolved = rendererData.TextureResolver?.Invoke(ic.ImageData);
                            if (resolved is not null)
                            {
                                RenderImage(context, bb, ic.CornerRadius, resolved);
                            }
                            else
                            {
                                rendererData.ImageRenderer?.Invoke(ref ic);
                            }
                        }
                    }

                    break;

                case Clay.RenderCommandType.Custom:
                    rendererData.CustomRenderer?.Invoke(ref rcmd.RenderData.Custom);
                    break;

                default:
                    // NONE, OVERLAY_COLOR_START/END — unsupported by this renderer.
                    break;
            }
        }

        context.Flush();
    }
}
