using SDL3;

namespace ClaySharp.Renderer.SDL3;

public class SDL_Clay
{
    public struct Clay_SDL3RendererData {
        public nint renderer; // SDL_Renderer
        public nint textEngine; // TTF_TextEngine
        public nint[] fonts; // array of TTF_Font
    }

    /* Global for convenience. Even in 4K this is enough for smooth curves (low radius or rect size coupled with
     * no AA or low resolution might make it appear as jagged curves) */
    private const int NUM_CIRCLE_SEGMENTS = 16;

    //all rendering is performed by a single SDL call, avoiding multiple RenderRect + plumbing choice for circles.
    private static void SDL_Clay_RenderFillRoundedRect(Clay_SDL3RendererData rendererData, SDL.FRect rect, float cornerRadius, Clay_Color _color) {
        SDL.FColor color = new SDL.FColor() { R = _color.r/255, G = _color.g/255, B = _color.b/255, A = _color.a/255 };

        int indexCount = 0, vertexCount = 0;

        float minRadius = Math.Min(rect.W, rect.H) / 2.0f;
        float clampedRadius = Math.Min(cornerRadius, minRadius);

        int numCircleSegments = Math.Max(NUM_CIRCLE_SEGMENTS, (int) (clampedRadius * 0.5f));

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
        SDL.RenderGeometry(rendererData.renderer, 0, vertices, vertexCount, indices, indexCount);
    }

    private static void SDL_Clay_RenderArc(Clay_SDL3RendererData rendererData, SDL.FPoint center, float radius, float startAngle, float endAngle, float thickness, Clay_Color color) {
        SDL.SetRenderDrawColor(rendererData.renderer, (byte)color.r, (byte)color.g, (byte)color.b,(byte) color.a);

        float radStart = startAngle * (float.Pi / 180.0f);
        float radEnd = endAngle * (float.Pi / 180.0f);

        int numCircleSegments = Math.Max(NUM_CIRCLE_SEGMENTS, (int)(radius * 1.5f)); //increase circle segments for larger circles, 1.5 is arbitrary.

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
            SDL.RenderLines(rendererData.renderer, points, numCircleSegments + 1);
        }
    }

    private static SDL.Rect currentClippingRectangle;

    public static void SDL_Clay_RenderClayCommands(Clay_SDL3RendererData rendererData, Clay_RenderCommandArray rcommands)
    {
        for (var i = 0; i < rcommands.length; i++) {
            ref Clay_RenderCommand rcmd = ref rcommands.Get(i);
            Clay_BoundingBox bounding_box = rcmd.boundingBox;
            SDL.FRect rect = new SDL.FRect() { X = (int)bounding_box.x, Y = (int)bounding_box.y, W = (int)bounding_box.width, H = (int)bounding_box.height };

            switch (rcmd.commandType) {
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE: {
                    ref Clay_RectangleRenderData config = ref rcmd.renderData.rectangle;
                    SDL.SetRenderDrawBlendMode(rendererData.renderer, SDL.BlendMode.Blend);
                    SDL.SetRenderDrawColor(rendererData.renderer, (byte)config.backgroundColor.r, (byte)config.backgroundColor.g, (byte)config.backgroundColor.b, (byte)config.backgroundColor.a);
                    if (config.cornerRadius.topLeft > 0) {
                        SDL_Clay_RenderFillRoundedRect(rendererData, rect, config.cornerRadius.topLeft, config.backgroundColor);
                    } else {
                        SDL.RenderFillRect(rendererData.renderer, rect);
                    }
                } break;
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT: {
                    ref Clay_TextRenderData config = ref rcmd.renderData.text;
                    nint font = rendererData.fonts[config.fontId];
                    TTF.SetFontSize(font, config.fontSize);
                    nint text = TTF.CreateText(rendererData.textEngine, font, config.stringContents.ToString(), (nuint)config.stringContents.Length);
                    TTF.SetTextColor(text, (byte)config.textColor.r, (byte)config.textColor.g, (byte)config.textColor.b, (byte)config.textColor.a);
                    TTF.DrawRendererText(text, rect.X, rect.Y);
                    TTF.DestroyText(text);
                } break;
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_BORDER: {
                    ref Clay_BorderRenderData config = ref rcmd.renderData.border;

                    float minRadius = Math.Min(rect.W, rect.H) / 2.0f;
                    Clay_CornerRadius clampedRadii = new Clay_CornerRadius() {
                        topLeft = Math.Min(config.cornerRadius.topLeft, minRadius),
                        topRight = Math.Min(config.cornerRadius.topRight, minRadius),
                        bottomLeft = Math.Min(config.cornerRadius.bottomLeft, minRadius),
                        bottomRight = Math.Min(config.cornerRadius.bottomRight, minRadius)
                    };
                    //edges
                    SDL.SetRenderDrawColor(rendererData.renderer, (byte)config.color.r, (byte)config.color.g, (byte)config.color.b, (byte)config.color.a);
                    if (config.width.left > 0) {
                        float starting_y = rect.Y + clampedRadii.topLeft;
                        float length = rect.H - clampedRadii.topLeft - clampedRadii.bottomLeft;
                        SDL.FRect line = new SDL.FRect(){ X = rect.X - 1, Y = starting_y, W = config.width.left, H = length };
                        SDL.RenderFillRect(rendererData.renderer, line);
                    }
                    if (config.width.right > 0) {
                        float starting_x = rect.X + rect.W - (float)config.width.right + 1;
                        float starting_y = rect.Y + clampedRadii.topRight;
                        float length = rect.H - clampedRadii.topRight - clampedRadii.bottomRight;
                        SDL.FRect line = new SDL.FRect() { X = starting_x, Y = starting_y, W = config.width.right, H = length };
                        SDL.RenderFillRect(rendererData.renderer, line);
                    }
                    if (config.width.top > 0) {
                        float starting_x = rect.X + clampedRadii.topLeft;
                        float length = rect.W - clampedRadii.topLeft - clampedRadii.topRight;
                        SDL.FRect line = new SDL.FRect() { X = starting_x, Y = rect.Y - 1, W = length, H = config.width.top };
                        SDL.RenderFillRect(rendererData.renderer, line);
                    }
                    if (config.width.bottom > 0) {
                        float starting_x = rect.X + clampedRadii.bottomLeft;
                        float starting_y = rect.Y + rect.H - (float)config.width.bottom + 1;
                        float length = rect.W - clampedRadii.bottomLeft - clampedRadii.bottomRight;
                        SDL.FRect line = new SDL.FRect() { X = starting_x, Y = starting_y, W = length, H = config.width.bottom };
                        SDL.SetRenderDrawColor(rendererData.renderer, (byte)config.color.r, (byte)config.color.g, (byte)config.color.b, (byte)config.color.a);
                        SDL.RenderFillRect(rendererData.renderer, line);
                    }
                    //corners
                    if (config.cornerRadius.topLeft > 0) {
                        float centerX = rect.X + clampedRadii.topLeft -1;
                        float centerY = rect.Y + clampedRadii.topLeft - 1;
                        SDL_Clay_RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.topLeft,
                            180.0f, 270.0f, config.width.top, config.color);
                    }
                    if (config.cornerRadius.topRight > 0) {
                        float centerX = rect.X + rect.W - clampedRadii.topRight;
                        float centerY = rect.Y + clampedRadii.topRight - 1;
                        SDL_Clay_RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.topRight,
                            270.0f, 360.0f, config.width.top, config.color);
                    }
                    if (config.cornerRadius.bottomLeft > 0) {
                        float centerX = rect.X + clampedRadii.bottomLeft -1;
                        float centerY = rect.Y + rect.H - clampedRadii.bottomLeft;
                        SDL_Clay_RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.bottomLeft,
                            90.0f, 180.0f, config.width.bottom, config.color);
                    }
                    if (config.cornerRadius.bottomRight > 0) {
                        float centerX = rect.X + rect.W - clampedRadii.bottomRight;
                        float centerY = rect.Y + rect.H - clampedRadii.bottomRight;
                        SDL_Clay_RenderArc(rendererData, new SDL.FPoint() { X = centerX, Y = centerY }, clampedRadii.bottomRight,
                            0.0f, 90.0f, config.width.bottom, config.color);
                    }

                } break;
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START: {
                    Clay_BoundingBox boundingBox = rcmd.boundingBox;
                    currentClippingRectangle = new SDL.Rect() {
                            X = (int)MathF.Round(boundingBox.x),
                            Y = (int)MathF.Round(boundingBox.y),
                            W = (int)MathF.Round(boundingBox.width),
                            H = (int)MathF.Round(boundingBox.height),
                    };
                    SDL.SetRenderClipRect(rendererData.renderer, currentClippingRectangle);
                    break;
                }
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_END: {
                    SDL.SetRenderClipRect(rendererData.renderer, 0);
                    break;
                }
                case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_IMAGE: {
                    nint texture = (nint)rcmd.renderData.image.imageData!;
                    SDL.FRect dest = new SDL.FRect() { X = rect.X, Y = rect.Y, W = rect.W, H = rect.H };
                    SDL.RenderTexture(rendererData.renderer, texture, 0, dest);
                    break;
                }
                default:
                    Console.WriteLine("Unknown render command type: {0}", rcmd.commandType);
                    break;
            }
        }
    }

}
