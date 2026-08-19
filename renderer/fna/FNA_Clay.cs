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
    }

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
    private static void AppendRoundedRect(FNA_ClayDeviceResources r, in Clay_BoundingBox rect, float cornerRadius, Color color, bool textured)
    {
        float minRadius = Math.Min(rect.width, rect.height) / 2f;
        float clampedRadius = Math.Min(cornerRadius, minRadius);
        if (clampedRadius <= 0f)
        {
            AppendPlainRect(r, rect, color, textured);
            return;
        }

        int numCircleSegments = Math.Max(NUM_CIRCLE_SEGMENTS, (int)(clampedRadius * 0.5f));
        if (numCircleSegments > MAX_CIRCLE_SEGMENTS_FILL)
        {
            numCircleSegments = MAX_CIRCLE_SEGMENTS_FILL;
        }

        int totalVertices = 4 + 4 * (numCircleSegments * 2) + 8;
        int totalIndices = 6 + 4 * (numCircleSegments * 3) + 24;
        r.EnsureScratchCapacity(totalVertices, totalIndices);

        ushort b = (ushort)r.VertexCount;
        Color vc = textured ? Color.White : color;
        float x0 = rect.x, y0 = rect.y, x1 = rect.x + rect.width, y1 = rect.y + rect.height;

        // Center rectangle (TL, TR, BR, BL).
        AddVertex(r, x0 + clampedRadius, y0 + clampedRadius, vc, Uv(rect, textured, x0 + clampedRadius, y0 + clampedRadius));
        AddVertex(r, x1 - clampedRadius, y0 + clampedRadius, vc, Uv(rect, textured, x1 - clampedRadius, y0 + clampedRadius));
        AddVertex(r, x1 - clampedRadius, y1 - clampedRadius, vc, Uv(rect, textured, x1 - clampedRadius, y1 - clampedRadius));
        AddVertex(r, x0 + clampedRadius, y1 - clampedRadius, vc, Uv(rect, textured, x0 + clampedRadius, y1 - clampedRadius));

        AddTriangle(r, b, (ushort)(b + 1), (ushort)(b + 3));
        AddTriangle(r, (ushort)(b + 1), (ushort)(b + 2), (ushort)(b + 3));

        // Rounded corners as triangle fans.
        float step = (MathF.PI / 2f) / numCircleSegments;
        for (int i = 0; i < numCircleSegments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1f) * step;

            for (int j = 0; j < 4; j++)
            {
                float cx, cy, sx, sy;
                switch (j)
                {
                    case 0: cx = x0 + clampedRadius; cy = y0 + clampedRadius; sx = -1; sy = -1; break; // TL
                    case 1: cx = x1 - clampedRadius; cy = y0 + clampedRadius; sx = 1; sy = -1; break; // TR
                    case 2: cx = x1 - clampedRadius; cy = y1 - clampedRadius; sx = 1; sy = 1; break; // BR
                    default: cx = x0 + clampedRadius; cy = y1 - clampedRadius; sx = -1; sy = 1; break; // BL
                }

                float p1x = cx + MathF.Cos(angle1) * clampedRadius * sx;
                float p1y = cy + MathF.Sin(angle1) * clampedRadius * sy;
                float p2x = cx + MathF.Cos(angle2) * clampedRadius * sx;
                float p2y = cy + MathF.Sin(angle2) * clampedRadius * sy;

                ushort i1 = (ushort)r.VertexCount;
                AddVertex(r, p1x, p1y, vc, Uv(rect, textured, p1x, p1y));
                ushort i2 = (ushort)r.VertexCount;
                AddVertex(r, p2x, p2y, vc, Uv(rect, textured, p2x, p2y));

                AddTriangle(r, (ushort)(b + j), i1, i2);
            }
        }

        // Edge rectangles (top, right, bottom, left), each tied to the center rect.
        ushort topTL = (ushort)r.VertexCount;
        AddVertex(r, x0 + clampedRadius, y0, vc, Uv(rect, textured, x0 + clampedRadius, y0));
        ushort topTR = (ushort)r.VertexCount;
        AddVertex(r, x1 - clampedRadius, y0, vc, Uv(rect, textured, x1 - clampedRadius, y0));
        AddTriangle(r, b, topTL, topTR);
        AddTriangle(r, (ushort)(b + 1), b, topTR);

        ushort rightT = (ushort)r.VertexCount;
        AddVertex(r, x1, y0 + clampedRadius, vc, Uv(rect, textured, x1, y0 + clampedRadius));
        ushort rightB = (ushort)r.VertexCount;
        AddVertex(r, x1, y1 - clampedRadius, vc, Uv(rect, textured, x1, y1 - clampedRadius));
        AddTriangle(r, (ushort)(b + 1), rightT, rightB);
        AddTriangle(r, (ushort)(b + 2), (ushort)(b + 1), rightB);

        ushort bottomR = (ushort)r.VertexCount;
        AddVertex(r, x1 - clampedRadius, y1, vc, Uv(rect, textured, x1 - clampedRadius, y1));
        ushort bottomL = (ushort)r.VertexCount;
        AddVertex(r, x0 + clampedRadius, y1, vc, Uv(rect, textured, x0 + clampedRadius, y1));
        AddTriangle(r, (ushort)(b + 2), bottomR, bottomL);
        AddTriangle(r, (ushort)(b + 3), (ushort)(b + 2), bottomL);

        ushort leftB = (ushort)r.VertexCount;
        AddVertex(r, x0, y1 - clampedRadius, vc, Uv(rect, textured, x0, y1 - clampedRadius));
        ushort leftT = (ushort)r.VertexCount;
        AddVertex(r, x0, y0 + clampedRadius, vc, Uv(rect, textured, x0, y0 + clampedRadius));
        AddTriangle(r, (ushort)(b + 3), leftB, leftT);
        AddTriangle(r, b, (ushort)(b + 3), leftT);
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

        device.BlendState = BlendState.AlphaBlend;
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

                    AppendRoundedRect(r, bb, rcmd.renderData.rectangle.cornerRadius.topLeft, ToColor(rcmd.renderData.rectangle.backgroundColor), false);
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
                        SpriteFontBase font = rendererData.fonts[tc.fontId].GetFont(tc.fontSize);
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
                        font.DrawText(rendererData.spriteBatch, textSegment, new Vector2(bb.x, bb.y), ToColor(tc.textColor));
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

                            AppendRoundedRect(r, bb, ic.cornerRadius.topLeft, Color.White, true);
                            if (r.VertexCount >= MAX_BATCH_VERTICES)
                            {
                                FlushGeometry(device, r);
                            }
                        }
                    }

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
