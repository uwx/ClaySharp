using SDL3;

namespace ClaySharp.Renderer.SDL3;

public class SDL_Clay
{
    public struct Sdl3RendererData {
        public nint Renderer; // SDL_Renderer
        public nint TextEngine; // TTF_TextEngine
        public nint[] Fonts; // array of TTF_Font
    }

    /* Global for convenience. Even in 4K this is enough for smooth curves (low radius or rect size coupled with
     * no AA or low resolution might make it appear as jagged curves) */
    private const int NumCircleSegments = 16;

    //all rendering is performed by a single SDL call, avoiding multiple RenderRect + plumbing choice for circles.
    private static void RenderFillRoundedRect(Sdl3RendererData rendererData, SDL.FRect rect, float cornerRadius, Clay.Color _color) {
        SDL.FColor color = new SDL.FColor() { R = _color.R/255, G = _color.G/255, B = _color.B/255, A = _color.A/255 };

        int indexCount = 0, vertexCount = 0;

        float minRadius = Math.Min(rect.W, rect.H) / 2.0f;
        float clampedRadius = Math.Min(cornerRadius, minRadius);

        int numCircleSegments = Math.Max(NumCircleSegments, (int) (clampedRadius * 0.5f));

        int totalVertices = 4 + (4 * (numCircleSegments * 2)) + 2*4;
        int totalIndices = 6 + (4 * (numCircleSegments * 3)) + 6*4;

        SDL.Vertex[] vertices = new SDL.Vertex[totalVertices];
        int[] indices = new int[totalIndices];

        //define center rectangle
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint() { X = rect.X + clampedRadius, Y = rect.Y + clampedRadius }, Color = color, TexCoord = new SDL.FPoint {X = 0, Y = 0} }; //0 center TL
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint() { X = rect.X + rect.W - clampedRadius, Y = rect.Y + clampedRadius }, Color = color, TexCoord = new SDL.FPoint{X = 1, Y = 0} }; //1 center TR
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint() { X = rect.X + rect.W - clampedRadius, Y = rect.Y + rect.H - clampedRadius }, Color = color, TexCoord = new SDL.FPoint{X = 1, Y = 1} }; //2 center BR
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint() { X = rect.X + clampedRadius, Y = rect.Y + rect.H - clampedRadius }, Color = color, TexCoord = new SDL.FPoint{X = 0, Y = 1} }; //3 center BL

        indices[indexCount++] = 0;
        indices[indexCount++] = 1;
        indices[indexCount++] = 3;
        indices[indexCount++] = 1;
        indices[indexCount++] = 2;
        indices[indexCount++] = 3;

        //define rounded corners as triangle fans
        float step = (float.Pi/2) / numCircleSegments;
        for (int i = 0; i < numCircleSegments; i++) {
            float angle1 = (float)i * step;
            float angle2 = ((float)i + 1.0f) * step;

            for (int j = 0; j < 4; j++) {  // Iterate over four corners
                float cx, cy, signX, signY;

                switch (j) {
                    case 0: cx = rect.X + clampedRadius; cy = rect.Y + clampedRadius; signX = -1; signY = -1; break; // Top-left
                    case 1: cx = rect.X + rect.W - clampedRadius; cy = rect.Y + clampedRadius; signX = 1; signY = -1; break; // Top-right
                    case 2: cx = rect.X + rect.W - clampedRadius; cy = rect.Y + rect.H - clampedRadius; signX = 1; signY = 1; break; // Bottom-right
                    case 3: cx = rect.X + clampedRadius; cy = rect.Y + rect.H - clampedRadius; signX = -1; signY = 1; break; // Bottom-left
                    default: return;
                }

                vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint { X = cx + MathF.Cos(angle1) * clampedRadius * signX, Y = cy + MathF.Sin(angle1) * clampedRadius * signY }, Color = color, TexCoord = new SDL.FPoint {X = 0, Y = 0} };
                vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint { X = cx + MathF.Cos(angle2) * clampedRadius * signX, Y = cy + MathF.Sin(angle2) * clampedRadius * signY }, Color = color, TexCoord = new SDL.FPoint {X = 0, Y = 0} };

                indices[indexCount++] = j;  // Connect to corresponding central rectangle vertex
                indices[indexCount++] = vertexCount - 2;
                indices[indexCount++] = vertexCount - 1;
            }
        }

        //Define edge rectangles
        // Top edge
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + clampedRadius, Y = rect.Y}, Color = color, TexCoord = new SDL.FPoint(){X = 0, Y = 0} }; //TL
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + rect.W - clampedRadius, Y = rect.Y}, Color = color, TexCoord = new SDL.FPoint(){X = 1, Y = 0} }; //TR

        indices[indexCount++] = 0;
        indices[indexCount++] = vertexCount - 2; //TL
        indices[indexCount++] = vertexCount - 1; //TR
        indices[indexCount++] = 1;
        indices[indexCount++] = 0;
        indices[indexCount++] = vertexCount - 1; //TR
        // Right edge
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + rect.W, Y = rect.Y + clampedRadius}, Color = color, TexCoord = new SDL.FPoint(){X = 1, Y = 0} }; //RT
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + rect.W, Y = rect.Y + rect.H - clampedRadius}, Color = color, TexCoord = new SDL.FPoint(){X = 1, Y = 1} }; //RB

        indices[indexCount++] = 1;
        indices[indexCount++] = vertexCount - 2; //RT
        indices[indexCount++] = vertexCount - 1; //RB
        indices[indexCount++] = 2;
        indices[indexCount++] = 1;
        indices[indexCount++] = vertexCount - 1; //RB
        // Bottom edge
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + rect.W - clampedRadius, Y = rect.Y + rect.H}, Color = color, TexCoord = new SDL.FPoint(){X = 1, Y = 1} }; //BR
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X + clampedRadius, Y = rect.Y + rect.H}, Color = color, TexCoord = new SDL.FPoint(){X = 0, Y = 1} }; //BL

        indices[indexCount++] = 2;
        indices[indexCount++] = vertexCount - 2; //BR
        indices[indexCount++] = vertexCount - 1; //BL
        indices[indexCount++] = 3;
        indices[indexCount++] = 2;
        indices[indexCount++] = vertexCount - 1; //BL
        // Left edge
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X, Y = rect.Y + rect.H - clampedRadius}, Color = color, TexCoord = new SDL.FPoint(){X = 0, Y = 1} }; //LB
        vertices[vertexCount++] = new SDL.Vertex() { Position = new SDL.FPoint {X = rect.X, Y = rect.Y + clampedRadius}, Color = color, TexCoord = new SDL.FPoint(){X = 0, Y = 0} }; //LT

        indices[indexCount++] = 3;
        indices[indexCount++] = vertexCount - 2; //LB
        indices[indexCount++] = vertexCount - 1; //LT
        indices[indexCount++] = 0;
        indices[indexCount++] = 3;
        indices[indexCount++] = vertexCount - 1; //LT

        // Render everything
        SDL.RenderGeometry(rendererData.Renderer, 0, vertices, vertexCount, indices, indexCount);
    }

    private static void RenderArc(Sdl3RendererData rendererData, SDL.FPoint center, float radius, float startAngle, float endAngle, float thickness, Clay.Color color) {
        SDL.SetRenderDrawColor(rendererData.Renderer, (byte)color.R, (byte)color.G, (byte)color.B,(byte) color.A);

        float radStart = startAngle * (float.Pi / 180.0f);
        float radEnd = endAngle * (float.Pi / 180.0f);

        int numCircleSegments = Math.Max(NumCircleSegments, (int)(radius * 1.5f)); //increase circle segments for larger circles, 1.5 is arbitrary.

        float angleStep = (radEnd - radStart) / (float)numCircleSegments;
        float thicknessStep = 0.4f; //arbitrary value to avoid overlapping lines. Changing THICKNESS_STEP or numCircleSegments might cause artifacts.

        for (float t = thicknessStep; t < thickness - thicknessStep; t += thicknessStep) {
            SDL.FPoint[] points = new SDL.FPoint[numCircleSegments + 1];
            float clampedRadius = Math.Max(radius - t, 1.0f);

            for (int i = 0; i <= numCircleSegments; i++) {
                float angle = radStart + i * angleStep;
                points[i] = new SDL.FPoint() {
                        X = MathF.Round(center.X + float.Cos(angle) * clampedRadius),
                        Y = MathF.Round(center.Y + float.Sin(angle) * clampedRadius)
                };
            }
            SDL.RenderLines(rendererData.Renderer, points, numCircleSegments + 1);
        }
    }

    private static SDL.Rect _currentClippingRectangle;

    public static void RenderClayCommands(Sdl3RendererData rendererData, Clay.RenderCommandArray rcommands)
    {
        for (var i = 0; i < rcommands.Length; i++) {
            ref Clay.RenderCommand rcmd = ref rcommands.Get(i);
            Clay.BoundingBox boundingBox = rcmd.BoundingBox;
            SDL.FRect rect = new SDL.FRect() { X = (int)boundingBox.X, Y = (int)boundingBox.Y, W = (int)boundingBox.Width, H = (int)boundingBox.Height };

            switch (rcmd.CommandType) {
                case Clay.RenderCommandType.Rectangle: {
                    ref Clay.RectangleRenderData config = ref rcmd.RenderData.Rectangle;
                    SDL.SetRenderDrawBlendMode(rendererData.Renderer, SDL.BlendMode.Blend);
                    SDL.SetRenderDrawColor(rendererData.Renderer, (byte)config.BackgroundColor.R, (byte)config.BackgroundColor.G, (byte)config.BackgroundColor.B, (byte)config.BackgroundColor.A);
                    if (config.CornerRadius.TopLeft > 0) {
                        RenderFillRoundedRect(rendererData, rect, config.CornerRadius.TopLeft, config.BackgroundColor);
                    } else {
                        SDL.RenderFillRect(rendererData.Renderer, rect);
                    }
                } break;
                case Clay.RenderCommandType.Text: {
                    ref Clay.TextRenderData config = ref rcmd.RenderData.Text;
                    nint font = rendererData.Fonts[config.FontId];
                    TTF.SetFontSize(font, config.FontSize);
                    nint text = TTF.CreateText(rendererData.TextEngine, font, config.StringContents.ToString(), (nuint)config.StringContents.Length);
                    TTF.SetTextColor(text, (byte)config.TextColor.R, (byte)config.TextColor.G, (byte)config.TextColor.B, (byte)config.TextColor.A);
                    TTF.DrawRendererText(text, rect.X, rect.Y);
                    TTF.DestroyText(text);
                } break;
                case Clay.RenderCommandType.Border: {
                    ref Clay.BorderRenderData config = ref rcmd.RenderData.Border;

                    float minRadius = Math.Min(rect.W, rect.H) / 2.0f;
                    Clay.CornerRadiusValues clampedRadii = new Clay.CornerRadiusValues {
                        TopLeft = Math.Min(config.CornerRadius.TopLeft, minRadius),
                        TopRight = Math.Min(config.CornerRadius.TopRight, minRadius),
                        BottomLeft = Math.Min(config.CornerRadius.BottomLeft, minRadius),
                        BottomRight = Math.Min(config.CornerRadius.BottomRight, minRadius)
                    };
                    //edges
                    SDL.SetRenderDrawColor(rendererData.Renderer, (byte)config.Color.R, (byte)config.Color.G, (byte)config.Color.B, (byte)config.Color.A);
                    if (config.Width.Left > 0) {
                        float startingY = rect.Y + clampedRadii.TopLeft;
                        float length = rect.H - clampedRadii.TopLeft - clampedRadii.BottomLeft;
                        SDL.FRect line = new SDL.FRect(){ X = rect.X - 1, Y = startingY, W = config.Width.Left, H = length };
                        SDL.RenderFillRect(rendererData.Renderer, line);
                    }
                    if (config.Width.Right > 0) {
                        float startingX = rect.X + rect.W - (float)config.Width.Right + 1;
                        float startingY = rect.Y + clampedRadii.TopRight;
                        float length = rect.H - clampedRadii.TopRight - clampedRadii.BottomRight;
                        SDL.FRect line = new SDL.FRect() { X = startingX, Y = startingY, W = config.Width.Right, H = length };
                        SDL.RenderFillRect(rendererData.Renderer, line);
                    }
                    if (config.Width.Top > 0) {
                        float startingX = rect.X + clampedRadii.TopLeft;
                        float length = rect.W - clampedRadii.TopLeft - clampedRadii.TopRight;
                        SDL.FRect line = new SDL.FRect() { X = startingX, Y = rect.Y - 1, W = length, H = config.Width.Top };
                        SDL.RenderFillRect(rendererData.Renderer, line);
                    }
                    if (config.Width.Bottom > 0) {
                        float startingX = rect.X + clampedRadii.BottomLeft;
                        float startingY = rect.Y + rect.H - (float)config.Width.Bottom + 1;
                        float length = rect.W - clampedRadii.BottomLeft - clampedRadii.BottomRight;
                        SDL.FRect line = new SDL.FRect() { X = startingX, Y = startingY, W = length, H = config.Width.Bottom };
                        SDL.SetRenderDrawColor(rendererData.Renderer, (byte)config.Color.R, (byte)config.Color.G, (byte)config.Color.B, (byte)config.Color.A);
                        SDL.RenderFillRect(rendererData.Renderer, line);
                    }
                    //corners
                    if (config.CornerRadius.TopLeft > 0) {
                        float centerX = rect.X + clampedRadii.TopLeft -1;
                        float centerY = rect.Y + clampedRadii.TopLeft - 1;
                        RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.TopLeft,
                            180.0f, 270.0f, config.Width.Top, config.Color);
                    }
                    if (config.CornerRadius.TopRight > 0) {
                        float centerX = rect.X + rect.W - clampedRadii.TopRight;
                        float centerY = rect.Y + clampedRadii.TopRight - 1;
                        RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.TopRight,
                            270.0f, 360.0f, config.Width.Top, config.Color);
                    }
                    if (config.CornerRadius.BottomLeft > 0) {
                        float centerX = rect.X + clampedRadii.BottomLeft -1;
                        float centerY = rect.Y + rect.H - clampedRadii.BottomLeft;
                        RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.BottomLeft,
                            90.0f, 180.0f, config.Width.Bottom, config.Color);
                    }
                    if (config.CornerRadius.BottomRight > 0) {
                        float centerX = rect.X + rect.W - clampedRadii.BottomRight;
                        float centerY = rect.Y + rect.H - clampedRadii.BottomRight;
                        RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.BottomRight,
                            0.0f, 90.0f, config.Width.Bottom, config.Color);
                    }

                } break;
                case Clay.RenderCommandType.ScissorStart: {
                    Clay.BoundingBox boundingBox1 = rcmd.BoundingBox;
                    _currentClippingRectangle = new SDL.Rect() {
                            X = (int)MathF.Round(boundingBox1.X),
                            Y = (int)MathF.Round(boundingBox1.Y),
                            W = (int)MathF.Round(boundingBox1.Width),
                            H = (int)MathF.Round(boundingBox1.Height),
                    };
                    SDL.SetRenderClipRect(rendererData.Renderer, _currentClippingRectangle);
                    break;
                }
                case Clay.RenderCommandType.ScissorEnd: {
                    SDL.SetRenderClipRect(rendererData.Renderer, 0);
                    break;
                }
                case Clay.RenderCommandType.Image: {
                    nint texture = (nint)rcmd.RenderData.Image.ImageData!;
                    SDL.FRect dest = new SDL.FRect() { X = rect.X, Y = rect.Y, W = rect.W, H = rect.H };
                    SDL.RenderTexture(rendererData.Renderer, texture, 0, dest);
                    break;
                }
                default:
                    Console.WriteLine("Unknown render command type: {0}", rcmd.CommandType);
                    break;
            }
        }
    }

}
