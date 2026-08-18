using System;
using System.IO;
using System.Numerics;
using ClaySharp;
using ClaySharp.Renderer.SDL3;
using Microsoft.Extensions.Primitives;
using SDL3;

namespace ClaySharp.Examples.SDL3;

// Port of clay/examples/SDL3-simple-demo/main.c using the SDL3-CS managed main-callback lifecycle.
[SDL.GenerateMain]
internal sealed partial class Game : SDL.IMainCallbacks<Game>
{
    private const int FONT_ID = 0;

    private nint window;
    private SDL_Clay.Clay_SDL3RendererData rendererData;
    private ClayVideoDemo_Data demoData = null!;

    private static nint sampleImage;
    private static bool showDemo = true;

    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "resources", name);

    private static Clay_Dimensions MeasureText(StringSegment text, Clay_TextElementConfig config, object? userData)
    {
        nint[] fonts = (nint[])userData!;
        nint font = fonts[config.fontId];
        TTF.SetFontSize(font, config.fontSize);
        int width = 0, height = 0;
        if (!TTF.GetStringSize(font, text.ToString(), (nuint)text.Length, out width, out height))
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to measure text: {SDL.GetError()}");
        }
        return new Clay_Dimensions(width, height);
    }

    private static void HandleClayErrors(Clay_ErrorData errorData)
    {
        Console.WriteLine(errorData.errorText);
    }

    private static Clay_RenderCommandArray CreateImageLayout()
    {
        Clay.BeginLayout();

        Clay_Sizing layoutExpand = new Clay_Sizing { width = Clay.SizingGrow(0), height = Clay.SizingGrow(0) };

        using (Clay.Element(Clay.Id("OuterContainer"), new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig
            {
                layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                sizing = layoutExpand,
                padding = Clay.PaddingAll(16),
                childGap = 16,
            },
        }))
        {
            using (Clay.Element(Clay.Id("SampleImage"), new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = layoutExpand },
                aspectRatio = new Clay_AspectRatioElementConfig { aspectRatio = 23.0f / 42.0f },
                image = new Clay_ImageElementConfig { imageData = sampleImage },
            })) { }
        }

        return Clay.EndLayout(0);
    }

    public static SDL.AppResult AppInit(out Game? appState, string[] args)
    {
        appState = null;

        if (!TTF.Init())
        {
            return SDL.AppResult.Failure;
        }

        var state = new Game();
        appState = state;

        if (!SDL.CreateWindowAndRenderer("Clay Demo", 640, 480, 0, out state.window, out state.rendererData.renderer))
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to create window and renderer: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }
        SDL.SetWindowResizable(state.window, true);

        state.rendererData.textEngine = TTF.CreateRendererTextEngine(state.rendererData.renderer);
        if (state.rendererData.textEngine == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to create text engine from renderer: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }

        state.rendererData.fonts = new nint[1];
        nint font = TTF.OpenFont(Resource("Roboto-Regular.ttf"), 24);
        if (font == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to load font: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }
        state.rendererData.fonts[FONT_ID] = font;

        sampleImage = Image.LoadTexture(state.rendererData.renderer, Resource("sample.png"));
        if (sampleImage == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to load image: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }

        SDL.GetWindowSize(state.window, out int width, out int height);
        Clay.Initialize(new Clay_Dimensions(width, height), new Clay_ErrorHandler { errorHandlerFunction = HandleClayErrors });
        Clay.SetMeasureTextFunction(MeasureText, state.rendererData.fonts);

        state.demoData = ClayVideoDemo.Initialize();

        return SDL.AppResult.Continue;
    }

    public SDL.AppResult AppEvent(ref SDL.Event @event)
    {
        SDL.AppResult result = SDL.AppResult.Continue;

        switch ((SDL.EventType)@event.Type)
        {
            case SDL.EventType.Quit:
                result = SDL.AppResult.Success;
                break;
            case SDL.EventType.KeyUp:
                if (@event.Key.Key == SDL.Keycode.Space)
                {
                    showDemo = !showDemo;
                }
                break;
            case SDL.EventType.WindowResized:
                Clay.SetLayoutDimensions(new Clay_Dimensions(@event.Window.Data1, @event.Window.Data2));
                break;
            case SDL.EventType.MouseWheel:
                Clay.UpdateScrollContainers(true, new Vector2(@event.Wheel.X, @event.Wheel.Y), 0.01f);
                break;
        }

        return result;
    }

    public SDL.AppResult AppIterate()
    {
        SDL.MouseButtonFlags buttons = SDL.GetMouseState(out float mouseX, out float mouseY);
        Clay.SetPointerState(new Vector2(mouseX, mouseY), (buttons & (SDL.MouseButtonFlags)SDL.ButtonMask(1)) != 0);

        Clay_RenderCommandArray renderCommands = showDemo
            ? ClayVideoDemo.CreateLayout(demoData)
            : CreateImageLayout();

        SDL.SetRenderDrawColor(rendererData.renderer, 0, 0, 0, 255);
        SDL.RenderClear(rendererData.renderer);

        SDL_Clay.SDL_Clay_RenderClayCommands(rendererData, renderCommands);

        SDL.RenderPresent(rendererData.renderer);

        return SDL.AppResult.Continue;
    }

    public void AppQuit(SDL.AppResult result)
    {
        if (result != SDL.AppResult.Success)
        {
            SDL.LogError(SDL.LogCategory.Error, "Application failed to run");
        }

        if (sampleImage != 0)
        {
            SDL.DestroyTexture(sampleImage);
        }

        if (rendererData.renderer != 0)
        {
            SDL.DestroyRenderer(rendererData.renderer);
        }

        if (window != 0)
        {
            SDL.DestroyWindow(window);
        }

        if (rendererData.fonts != null)
        {
            for (int i = 0; i < rendererData.fonts.Length; i++)
            {
                if (rendererData.fonts[i] != 0)
                {
                    TTF.CloseFont(rendererData.fonts[i]);
                }
            }
        }

        if (rendererData.textEngine != 0)
        {
            TTF.DestroyRendererTextEngine(rendererData.textEngine);
        }

        TTF.Quit();
    }
}
