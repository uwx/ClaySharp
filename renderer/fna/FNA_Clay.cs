using System;
using System.Numerics;
using ClaySharp;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClaySharp.Renderer.FNA;

/// <summary>
/// FNA renderer for Clay, ported from <see cref="ClaySharp.Renderer.SDL3.SDL_Clay"/>.
///
/// Unlike the SDL3 reference (which issues one draw per command), this renderer batches
/// geometry: every RECTANGLE/BORDER/IMAGE command between two state changes (scissor
/// changes, text, or texture switches) is accumulated into a single growable dynamic
/// vertex buffer and drawn with one <see cref="GraphicsDevice.DrawIndexedPrimitives"/> call.
/// </summary>
public class FNA_Clay
{
    /// <summary>
    /// Resources the renderer needs. The caller owns these (they are not created by
    /// the renderer) — parity with <c>Clay_SDL3RendererData</c>.
    /// </summary>
    public struct Clay_FNARendererData
    {
        public GraphicsDevice graphicsDevice;
        public SpriteBatch spriteBatch;
        public FontSystem[] fonts; // indexed by Clay fontId
        public Func<object, Texture2D?>? textureResolver;
        public ImageRenderHandler? imageRenderer;
        public CustomRenderHandler? customRenderer;
        public FontGetHandler? fontGetter;
    }

    public delegate void ImageRenderHandler(ref Clay_ImageRenderData data);
    public delegate void CustomRenderHandler(ref Clay_CustomRenderData data);
    public delegate FontSystem FontGetHandler(ref Clay_TextRenderData data, object? userData);

    private const int NUM_CIRCLE_SEGMENTS = 16;
    private const int MAX_CIRCLE_SEGMENTS_FILL = 1024; // caps per-command vertex counts (16-bit indices)
    private const int MAX_CIRCLE_SEGMENTS_ARC = 1024;
    private const int MAX_BATCH_VERTICES = 60000; // keep below 65535 so 16-bit indices stay valid

    private const int INITIAL_VERTEX_CAPACITY = 4096;
    private const int INITIAL_INDEX_CAPACITY = 8192;

    // ---------------------------------------------------------------
    // Per-GraphicsDevice cached resources (effect, buffers, states)
    // ---------------------------------------------------------------
    private sealed class FNA_ClayDeviceResources
    {
        public readonly BasicEffect Effect;

        public DynamicVertexBuffer VertexBuffer;
        public DynamicIndexBuffer IndexBuffer;
        public int VertexCapacity;
        public int IndexCapacity;

        public readonly RasterizerState RasterizerPlain;
        public readonly RasterizerState RasterizerScissor;

        // Scratch geometry for the current batch.
        public VertexPositionColorTexture[] Vertices;
        public ushort[] Indices;
        public int VertexCount;
        public int IndexCount;
        public Texture2D? BatchTexture; // null = solid-color batch

        public FNA_ClayDeviceResources(GraphicsDevice device)
        {
            Effect = new BasicEffect(device)
            {
                VertexColorEnabled = true,
                TextureEnabled = false,
                World = Matrix4x4.Identity,
                View = Matrix4x4.Identity,
                Projection = Matrix4x4.Identity,
            };
            Effect.CurrentTechnique = Effect.Techniques[0];

            VertexCapacity = INITIAL_VERTEX_CAPACITY;
            IndexCapacity = INITIAL_INDEX_CAPACITY;
            VertexBuffer = new DynamicVertexBuffer(device, typeof(VertexPositionColorTexture), VertexCapacity, BufferUsage.WriteOnly);
            IndexBuffer = new DynamicIndexBuffer(device, IndexElementSize.SixteenBits, IndexCapacity, BufferUsage.WriteOnly);

            RasterizerPlain = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = false };
            RasterizerScissor = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };

            Vertices = new VertexPositionColorTexture[INITIAL_VERTEX_CAPACITY];
            Indices = new ushort[INITIAL_INDEX_CAPACITY];
        }

        public void Reset()
        {
            VertexCount = 0;
            IndexCount = 0;
            BatchTexture = null;
        }

        public void EnsureScratchCapacity(int additionalVertices, int additionalIndices)
        {
            int neededV = VertexCount + additionalVertices;
            if (neededV > Vertices.Length)
            {
                Array.Resize(ref Vertices, Math.Max(neededV, Vertices.Length * 2));
            }

            int neededI = IndexCount + additionalIndices;
            if (neededI > Indices.Length)
            {
                Array.Resize(ref Indices, Math.Max(neededI, Indices.Length * 2));
            }
        }

        public void EnsureGpuCapacity(GraphicsDevice device, int neededVertices, int neededIndices)
        {
            if (neededVertices > VertexCapacity)
            {
                VertexBuffer.Dispose();
                VertexCapacity = Math.Max(neededVertices, VertexCapacity * 2);
                VertexBuffer = new DynamicVertexBuffer(device, typeof(VertexPositionColorTexture), VertexCapacity, BufferUsage.WriteOnly);
            }

            if (neededIndices > IndexCapacity)
            {
                IndexBuffer.Dispose();
                IndexCapacity = Math.Max(neededIndices, IndexCapacity * 2);
                IndexBuffer = new DynamicIndexBuffer(device, IndexElementSize.SixteenBits, IndexCapacity, BufferUsage.WriteOnly);
            }
        }
    }

    // Single-device cache. If a different GraphicsDevice is used (or the device is
    // reset), the previous buffers/effect are abandoned and rebuilt on next use.
    private static FNA_ClayDeviceResources? _resources;
    private static GraphicsDevice? _resourcesDevice;

    private static FNA_ClayDeviceResources GetResources(GraphicsDevice device)
    {
        if (_resources == null || _resourcesDevice != device)
        {
            _resources = new FNA_ClayDeviceResources(device);
            _resourcesDevice = device;
        }

        return _resources;
    }

    // ---------------------------------------------------------------
    // Geometry building (appends into the current batch)
    // ---------------------------------------------------------------

    private static Color ToColor(in Clay_Color c) => new Color((int)c.r, (int)c.g, (int)c.b, (int)c.a);

    private static Vector2 Uv(in Clay_BoundingBox rect, bool textured, float px, float py)
        => textured ? new Vector2((px - rect.x) / rect.width, (py - rect.y) / rect.height) : Vector2.Zero;

    private static void AddVertex(FNA_ClayDeviceResources r, float x, float y, Color color, Vector2 uv)
    {
        r.Vertices[r.VertexCount++] = new VertexPositionColorTexture(new Vector3(x, y, 0f), color, uv);
    }

    private static void AddTriangle(FNA_ClayDeviceResources r, ushort a, ushort b, ushort c)
    {
        r.Indices[r.IndexCount++] = a;
        r.Indices[r.IndexCount++] = b;
        r.Indices[r.IndexCount++] = c;
    }

    private static void AppendPlainRect(FNA_ClayDeviceResources r, in Clay_BoundingBox rect, Color color, bool textured)
    {
        r.EnsureScratchCapacity(4, 6);

        ushort b = (ushort)r.VertexCount;
        Color vc = textured ? Color.White : color;
        float x0 = rect.x, y0 = rect.y, x1 = rect.x + rect.width, y1 = rect.y + rect.height;

        AddVertex(r, x0, y0, vc, Uv(rect, textured, x0, y0));
        AddVertex(r, x1, y0, vc, Uv(rect, textured, x1, y0));
        AddVertex(r, x1, y1, vc, Uv(rect, textured, x1, y1));
        AddVertex(r, x0, y1, vc, Uv(rect, textured, x0, y1));

        AddTriangle(r, b, (ushort)(b + 1), (ushort)(b + 2));
        AddTriangle(r, b, (ushort)(b + 2), (ushort)(b + 3));
    }

    /// <summary>
    /// Ports the SDL3 rounded-rect tessellation (center quad + 4 corner fans + 4 edge
    /// quads). When <paramref name="textured"/> is true, vertex UVs are mapped across
    /// the destination rect and vertex color is forced to white, so the same geometry
    /// can clip a texture to the rounded shape.
    /// </summary>
    private static void AppendRoundedRect(FNA_ClayDeviceResources r, in Clay_BoundingBox rect, in Clay_CornerRadius cornerRadius, Color color, bool textured)
    {
        float minRadius = Math.Min(rect.width, rect.height) / 2f;
        float tl = Math.Clamp(cornerRadius.topLeft, 0f, minRadius);
        float tr = Math.Clamp(cornerRadius.topRight, 0f, minRadius);
        float br = Math.Clamp(cornerRadius.bottomRight, 0f, minRadius);
        float bl = Math.Clamp(cornerRadius.bottomLeft, 0f, minRadius);

        if (tl <= 0f && tr <= 0f && br <= 0f && bl <= 0f)
        {
            AppendPlainRect(r, rect, color, textured);
            return;
        }

        float maxRadius = Math.Max(Math.Max(tl, tr), Math.Max(br, bl));
        int numSegments = Math.Max(NUM_CIRCLE_SEGMENTS, (int)(maxRadius * 0.5f));
        if (numSegments > MAX_CIRCLE_SEGMENTS_FILL)
        {
            numSegments = MAX_CIRCLE_SEGMENTS_FILL;
        }

        int roundedCorners = (tl > 0f ? 1 : 0) + (tr > 0f ? 1 : 0) + (br > 0f ? 1 : 0) + (bl > 0f ? 1 : 0);
        r.EnsureScratchCapacity(4 + roundedCorners * (numSegments + 1) + 8, 6 + roundedCorners * numSegments * 3 + 24);

        Color vc = textured ? Color.White : color;
        float x0 = rect.x, y0 = rect.y, x1 = rect.x + rect.width, y1 = rect.y + rect.height;

        // Inner corners of the center quad (also the arc centers).
        Vector2 itl = new Vector2(x0 + tl, y0 + tl);
        Vector2 itr = new Vector2(x1 - tr, y0 + tr);
        Vector2 ibr = new Vector2(x1 - br, y1 - br);
        Vector2 ibl = new Vector2(x0 + bl, y1 - bl);

        ushort cTl = (ushort)r.VertexCount;
        AddVertex(r, itl.X, itl.Y, vc, Uv(rect, textured, itl.X, itl.Y));
        ushort cTr = (ushort)r.VertexCount;
        AddVertex(r, itr.X, itr.Y, vc, Uv(rect, textured, itr.X, itr.Y));
        ushort cBr = (ushort)r.VertexCount;
        AddVertex(r, ibr.X, ibr.Y, vc, Uv(rect, textured, ibr.X, ibr.Y));
        ushort cBl = (ushort)r.VertexCount;
        AddVertex(r, ibl.X, ibl.Y, vc, Uv(rect, textured, ibl.X, ibl.Y));

        AddTriangle(r, cTl, cTr, cBl);
        AddTriangle(r, cTr, cBr, cBl);

        // Corner fans, each with its own radius.
        AppendCornerFan(r, cTl, itl, tl, 180f, 270f, vc, rect, textured, numSegments);
        AppendCornerFan(r, cTr, itr, tr, 270f, 360f, vc, rect, textured, numSegments);
        AppendCornerFan(r, cBr, ibr, br, 0f, 90f, vc, rect, textured, numSegments);
        AppendCornerFan(r, cBl, ibl, bl, 90f, 180f, vc, rect, textured, numSegments);

        // Edge quads (top, right, bottom, left).
        AppendEdge(r, cTl, cTr, new Vector2(x0 + tl, y0), new Vector2(x1 - tr, y0), vc, rect, textured);
        AppendEdge(r, cTr, cBr, new Vector2(x1, y0 + tr), new Vector2(x1, y1 - br), vc, rect, textured);
        AppendEdge(r, cBr, cBl, new Vector2(x1 - br, y1), new Vector2(x0 + bl, y1), vc, rect, textured);
        AppendEdge(r, cBl, cTl, new Vector2(x0, y1 - bl), new Vector2(x0, y0 + tl), vc, rect, textured);
    }

    private static void AppendCornerFan(FNA_ClayDeviceResources r, ushort centerIndex, Vector2 center, float radius, float startAngleDeg, float endAngleDeg, Color color, in Clay_BoundingBox rect, bool textured, int numSegments)
    {
        if (radius <= 0f)
        {
            return;
        }

        float radStart = startAngleDeg * MathF.PI / 180f;
        float radEnd = endAngleDeg * MathF.PI / 180f;
        float step = (radEnd - radStart) / numSegments;

        ushort prev = 0;
        for (int i = 0; i <= numSegments; i++)
        {
            float angle = radStart + i * step;
            float px = center.X + MathF.Cos(angle) * radius;
            float py = center.Y + MathF.Sin(angle) * radius;

            ushort idx = (ushort)r.VertexCount;
            AddVertex(r, px, py, color, Uv(rect, textured, px, py));

            if (i > 0)
            {
                AddTriangle(r, centerIndex, prev, idx);
            }

            prev = idx;
        }
    }

    private static void AppendEdge(FNA_ClayDeviceResources r, ushort innerA, ushort innerB, Vector2 outerA, Vector2 outerB, Color color, in Clay_BoundingBox rect, bool textured)
    {
        ushort oa = (ushort)r.VertexCount;
        AddVertex(r, outerA.X, outerA.Y, color, Uv(rect, textured, outerA.X, outerA.Y));
        ushort ob = (ushort)r.VertexCount;
        AddVertex(r, outerB.X, outerB.Y, color, Uv(rect, textured, outerB.X, outerB.Y));

        AddTriangle(r, innerA, innerB, ob);
        AddTriangle(r, innerA, ob, oa);
    }

    /// <summary>
    /// Filled quarter-ring (annulus sector) band between the outer radius and
    /// (radius - thickness), rendered as a triangle strip. This gives true thickness,
    /// unlike the SDL3 reference which draws concentric 1px lines.
    /// </summary>
    private static void AppendArc(FNA_ClayDeviceResources r, Vector2 center, float radius, float startAngleDeg, float endAngleDeg, float thickness, Color color)
    {
        float radStart = startAngleDeg * MathF.PI / 180f;
        float radEnd = endAngleDeg * MathF.PI / 180f;

        int numSegments = Math.Max(NUM_CIRCLE_SEGMENTS, (int)(radius * 1.5f));
        if (numSegments > MAX_CIRCLE_SEGMENTS_ARC)
        {
            numSegments = MAX_CIRCLE_SEGMENTS_ARC;
        }

        float innerRadius = Math.Max(radius - thickness, 0f);
        int pointCount = numSegments + 1;
        r.EnsureScratchCapacity(pointCount * 2, numSegments * 6);

        ushort b = (ushort)r.VertexCount;
        float angleStep = (radEnd - radStart) / numSegments;
        for (int i = 0; i <= numSegments; i++)
        {
            float angle = radStart + i * angleStep;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            AddVertex(r, center.X + cos * radius, center.Y + sin * radius, color, Vector2.Zero);
            AddVertex(r, center.X + cos * innerRadius, center.Y + sin * innerRadius, color, Vector2.Zero);
        }

        for (int i = 0; i < numSegments; i++)
        {
            ushort o0 = (ushort)(b + i * 2);
            ushort i0 = (ushort)(b + i * 2 + 1);
            ushort o1 = (ushort)(b + (i + 1) * 2);
            ushort i1 = (ushort)(b + (i + 1) * 2 + 1);
            AddTriangle(r, o0, i0, o1);
            AddTriangle(r, i0, i1, o1);
        }
    }

    private static void AppendBorder(FNA_ClayDeviceResources r, in Clay_BoundingBox rect, in Clay_BorderRenderData config)
    {
        Color color = ToColor(config.color);
        float minRadius = Math.Min(rect.width, rect.height) / 2f;
        float tl = Math.Min(config.cornerRadius.topLeft, minRadius);
        float tr = Math.Min(config.cornerRadius.topRight, minRadius);
        float bl = Math.Min(config.cornerRadius.bottomLeft, minRadius);
        float br = Math.Min(config.cornerRadius.bottomRight, minRadius);

        float x0 = rect.x, y0 = rect.y, x1 = rect.x + rect.width, y1 = rect.y + rect.height;

        // Edges (filled rects), inset by the clamped corner radii.
        if (config.width.left > 0)
        {
            AppendPlainRect(r, new Clay_BoundingBox(x0, y0 + tl, config.width.left, rect.height - tl - bl), color, false);
        }

        if (config.width.right > 0)
        {
            AppendPlainRect(r, new Clay_BoundingBox(x1 - config.width.right, y0 + tr, config.width.right, rect.height - tr - br), color, false);
        }

        if (config.width.top > 0)
        {
            AppendPlainRect(r, new Clay_BoundingBox(x0 + tl, y0, rect.width - tl - tr, config.width.top), color, false);
        }

        if (config.width.bottom > 0)
        {
            AppendPlainRect(r, new Clay_BoundingBox(x0 + bl, y1 - config.width.bottom, rect.width - bl - br, config.width.bottom), color, false);
        }

        // Corners (quarter-ring arcs). Top corners use width.top, bottom use width.bottom,
        // matching the SDL3 reference.
        if (config.cornerRadius.topLeft > 0)
        {
            AppendArc(r, new Vector2(x0 + tl, y0 + tl), tl, 180f, 270f, config.width.top, color);
        }

        if (config.cornerRadius.topRight > 0)
        {
            AppendArc(r, new Vector2(x1 - tr, y0 + tr), tr, 270f, 360f, config.width.top, color);
        }

        if (config.cornerRadius.bottomLeft > 0)
        {
            AppendArc(r, new Vector2(x0 + bl, y1 - bl), bl, 90f, 180f, config.width.bottom, color);
        }

        if (config.cornerRadius.bottomRight > 0)
        {
            AppendArc(r, new Vector2(x1 - br, y1 - br), br, 0f, 90f, config.width.bottom, color);
        }
    }

    // ---------------------------------------------------------------
    // Batching / flush
    // ---------------------------------------------------------------

    private static void FlushGeometry(GraphicsDevice device, FNA_ClayDeviceResources r)
    {
        if (r.VertexCount == 0)
        {
            return;
        }

        r.EnsureGpuCapacity(device, r.VertexCount, r.IndexCount);
        r.VertexBuffer.SetData(r.Vertices, 0, r.VertexCount, SetDataOptions.Discard);
        r.IndexBuffer.SetData(r.Indices, 0, r.IndexCount, SetDataOptions.Discard);

        r.Effect.TextureEnabled = r.BatchTexture != null;
        if (r.BatchTexture != null)
        {
            r.Effect.Texture = r.BatchTexture;
        }

        r.Effect.CurrentTechnique.Passes[0].Apply();

        // Vertex colors are straight (non-premultiplied) RGBA. FNA's BlendState.AlphaBlend
        // is premultiplied (One, InverseSourceAlpha); use NonPremultiplied instead, and reset it
        // here because SpriteBatch leaves its own (premultiplied) blend state behind after End().
        device.BlendState = BlendState.NonPremultiplied;

        device.SetVertexBuffer(r.VertexBuffer);
        device.Indices = r.IndexBuffer;
        device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, r.VertexCount, 0, r.IndexCount / 3);

        r.VertexCount = 0;
        r.IndexCount = 0;
        r.BatchTexture = null;
    }

    private static void CloseTextBatch(Clay_FNARendererData rendererData, ref bool textBatchOpen)
    {
        if (textBatchOpen)
        {
            rendererData.spriteBatch.End();
            textBatchOpen = false;
        }
    }

    // ---------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------

    public static void FNA_Clay_RenderClayCommands(Clay_FNARendererData rendererData, Clay_RenderCommandArray rcommands)
    {
        GraphicsDevice device = rendererData.graphicsDevice;
        FNA_ClayDeviceResources r = GetResources(device);

        // Save device state; restore on exit (polite overlay renderer).
        BlendState oldBlend = device.BlendState;
        DepthStencilState oldDepth = device.DepthStencilState;
        RasterizerState oldRasterizer = device.RasterizerState;
        SamplerState oldSampler = device.SamplerStates[0];
        Rectangle oldScissor = device.ScissorRectangle;

        device.BlendState = BlendState.NonPremultiplied;
        device.DepthStencilState = DepthStencilState.None;
        device.SamplerStates[0] = SamplerState.LinearClamp;
        device.RasterizerState = r.RasterizerPlain;

        Viewport viewport = device.Viewport;
        r.Effect.Projection = Matrix4x4.CreateOrthographicOffCenter(0f, viewport.Width, viewport.Height, 0f, 0f, 1f);

        r.Reset();
        bool textBatchOpen = false;

        for (int i = 0; i < rcommands.length; i++)
        {
            ref Clay_RenderCommand rcmd = ref rcommands.Get(i);
            Clay_BoundingBox bb = rcmd.boundingBox;

            // Skip empty drawable commands (scissor still needs processing).
            bool isScissor = rcmd.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START
                || rcmd.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_END;
            if (!isScissor && (bb.width <= 0f || bb.height <= 0f))
            {
                continue;
            }

            switch (rcmd.commandType)
            {
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    if (r.BatchTexture != null)
                    {
                        FlushGeometry(device, r); // switch to solid-color batch
                    }

                    AppendRoundedRect(r, bb, rcmd.renderData.rectangle.cornerRadius, ToColor(rcmd.renderData.rectangle.backgroundColor), false);
                    if (r.VertexCount >= MAX_BATCH_VERTICES)
                    {
                        FlushGeometry(device, r);
                    }

                    break;

                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_BORDER:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    if (r.BatchTexture != null)
                    {
                        FlushGeometry(device, r);
                    }

                    AppendBorder(r, bb, rcmd.renderData.border);
                    if (r.VertexCount >= MAX_BATCH_VERTICES)
                    {
                        FlushGeometry(device, r);
                    }

                    break;

                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT:
                    FlushGeometry(device, r);
                    {
                        ref Clay_TextRenderData tc = ref rcmd.renderData.text;
                        SpriteFontBase font;
                        if (rendererData.fonts.Length >= tc.fontId) 
                            font = rendererData.fonts[tc.fontId].GetFont(tc.fontSize);
                        else if (rendererData.fontGetter is not null)
                            font = rendererData.fontGetter.Invoke(ref tc, rcmd.userData).GetFont(tc.fontSize);
                        else
                            break; // TODO emit warning, there's no matching font!

                        if (!textBatchOpen)
                        {
                            rendererData.spriteBatch.Begin(
                                SpriteSortMode.Deferred,
                                BlendState.AlphaBlend,
                                SamplerState.LinearClamp,
                                DepthStencilState.None,
                                device.RasterizerState,
                                null);
                            textBatchOpen = true;
                        }

                        Microsoft.Extensions.Primitives.StringSegment contents = tc.stringContents;
                        var textSegment = new FontStashSharp.StringSegment(
                            contents.Buffer ?? string.Empty,
                            contents.Offset,
                            contents.Length);

                        // FontStashSharp's MeasureString returns (maxX, maxY) — the glyph's bottom from the
                        // line top — but DrawText positions glyphs relative to the baseline, so short glyphs
                        // (e.g. "x", "-") sit below the line-box top. Offset by the tight bounds so the drawn
                        // text aligns with Clay's bounding box.
                        Bounds bounds = font.TextBounds(textSegment, Vector2.Zero);
                        font.DrawText(
                            rendererData.spriteBatch,
                            textSegment,
                            new Vector2(bb.x - bounds.X, bb.y - bounds.Y),
                            ToColor(tc.textColor));
                    }

                    break;

                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    FlushGeometry(device, r);
                    device.ScissorRectangle = new Rectangle(
                        (int)MathF.Round(bb.x),
                        (int)MathF.Round(bb.y),
                        (int)MathF.Round(bb.width),
                        (int)MathF.Round(bb.height));
                    device.RasterizerState = r.RasterizerScissor;
                    break;

                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_END:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    FlushGeometry(device, r);
                    device.RasterizerState = r.RasterizerPlain;
                    break;

                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_IMAGE:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    {
                        ref Clay_ImageRenderData ic = ref rcmd.renderData.image;
                        if (ic.imageData is Texture2D texture)
                        {
                            if (r.BatchTexture != texture)
                            {
                                FlushGeometry(device, r);
                                r.BatchTexture = texture;
                            }

                            AppendRoundedRect(r, bb, ic.cornerRadius, Color.White, true);
                            if (r.VertexCount >= MAX_BATCH_VERTICES)
                            {
                                FlushGeometry(device, r);
                            }
                        }
                        else if (ic.imageData is not null)
                        {
                            var texture1 = rendererData.textureResolver?.Invoke(ic.imageData);
                            if (texture1 is not null)
                            {
                                if (r.BatchTexture != texture1)
                                {
                                    FlushGeometry(device, r);
                                    r.BatchTexture = texture1;
                                }

                                AppendRoundedRect(r, bb, ic.cornerRadius, Color.White, true);
                                if (r.VertexCount >= MAX_BATCH_VERTICES)
                                {
                                    FlushGeometry(device, r);
                                }
                            }
                            else
                            {
                                rendererData.imageRenderer?.Invoke(ref ic);
                            }
                        }
                    }

                    break;
                
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_CUSTOM:
                    CloseTextBatch(rendererData, ref textBatchOpen);
                    FlushGeometry(device, r);
                    rendererData.customRenderer?.Invoke(ref rcmd.renderData.custom);
                    break;

                default:
                    // NONE, OVERLAY_COLOR_START/END, CUSTOM — unsupported by this renderer.
                    break;
            }
        }

        CloseTextBatch(rendererData, ref textBatchOpen);
        FlushGeometry(device, r);

        // Restore device state.
        device.BlendState = oldBlend;
        device.DepthStencilState = oldDepth;
        device.RasterizerState = oldRasterizer;
        device.SamplerStates[0] = oldSampler;
        device.ScissorRectangle = oldScissor;
    }
}
