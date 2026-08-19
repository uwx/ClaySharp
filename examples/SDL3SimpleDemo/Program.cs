using System;
using System.IO;
using System.Numerics;
using ClaySharp;
using ClaySharp.Plugin.TextInput;
using ClaySharp.Plugin.TextInput.SDL3;
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
    private SDL_Clay.Sdl3RendererData rendererData;
    private ClayVideoDemo_Data demoData = null!;

    private static nint sampleImage;
    private static bool showDemo = true;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrameSeconds;

    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "resources", name);

    private static Clay.Dimensions MeasureText(StringSegment text, Clay.TextElementConfig config, object? userData)
    {
        nint[] fonts = (nint[])userData!;
        nint font = fonts[config.FontId];
        TTF.SetFontSize(font, config.FontSize);
        int width = 0, height = 0;
        if (!TTF.GetStringSize(font, text.ToString(), (nuint)text.Length, out width, out height))
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to measure text: {SDL.GetError()}");
        }
        return new Clay.Dimensions(width, height);
    }

    private static void HandleClayErrors(Clay.ErrorData errorData)
    {
        Console.WriteLine(errorData.ErrorText);
    }

    private static Clay.RenderCommandArray CreateImageLayout()
    {
        Clay.BeginLayout();

        Clay.Sizing layoutExpand = new Clay.Sizing { Width = Clay.SizingGrow(0), Height = Clay.SizingGrow(0) };

        using (Clay.Element(Clay.Id("OuterContainer"), new Clay.ElementDeclaration
        {
            Layout = new Clay.LayoutConfig
            {
                LayoutDirection = Clay.LayoutDirection.TopToBottom,
                Sizing = layoutExpand,
                Padding = Clay.PaddingAll(16),
                ChildGap = 16,
            },
        }))
        {
            using (Clay.Element(Clay.Id("SampleImage"), new Clay.ElementDeclaration
            {
                Layout = new Clay.LayoutConfig { Sizing = layoutExpand },
                AspectRatio = new Clay.AspectRatioElementConfig { AspectRatio = 23.0f / 42.0f },
                Image = new Clay.ImageElementConfig { ImageData = sampleImage },
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

        if (!SDL.CreateWindowAndRenderer("Clay Demo", 640, 480, 0, out state.window, out state.rendererData.Renderer))
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to create window and renderer: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }
        SDL.SetWindowResizable(state.window, true);

        state.rendererData.TextEngine = TTF.CreateRendererTextEngine(state.rendererData.Renderer);
        if (state.rendererData.TextEngine == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to create text engine from renderer: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }

        state.rendererData.Fonts = new nint[1];
        nint font = TTF.OpenFont(Resource("Roboto-Regular.ttf"), 24);
        if (font == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to load font: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }
        state.rendererData.Fonts[FONT_ID] = font;

        sampleImage = Image.LoadTexture(state.rendererData.Renderer, Resource("sample.png"));
        if (sampleImage == 0)
        {
            SDL.LogError(SDL.LogCategory.Error, $"Failed to load image: {SDL.GetError()}");
            return SDL.AppResult.Failure;
        }

        SDL.GetWindowSize(state.window, out int width, out int height);
        Clay.Initialize(new Clay.Dimensions(width, height), new Clay.ErrorHandler { ErrorHandlerFunction = HandleClayErrors });
        Clay.SetMeasureTextFunction(MeasureText, state.rendererData.Fonts);

        ClayTextInput.SetPlatform(ClayTextInputSdl3.Platform());
        SDL.StartTextInput(state.window);

        state.demoData = ClayVideoDemo.Initialize();
        state._lastFrameSeconds = state._clock.Elapsed.TotalSeconds;

        return SDL.AppResult.Continue;
    }

    public SDL.AppResult AppEvent(ref SDL.Event @event)
    {
        SDL.AppResult result = SDL.AppResult.Continue;

        ClayTextInputSdl3.ProcessEvent(ref @event);

        switch ((SDL.EventType)@event.Type)
        {
            case SDL.EventType.Quit:
                result = SDL.AppResult.Success;
                break;
            case SDL.EventType.KeyUp:
                if (@event.Key.Key == SDL.Keycode.Space && !demoData.textInput.Focused)
                {
                    showDemo = !showDemo;
                }
                else if (@event.Key.Key == SDL.Keycode.F1)
                {
                    Clay.SetDebugModeEnabled(!Clay.IsDebugModeEnabled());
                }
                break;
            case SDL.EventType.WindowResized:
                Clay.SetLayoutDimensions(new Clay.Dimensions(@event.Window.Data1, @event.Window.Data2));
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

        double now = _clock.Elapsed.TotalSeconds;
        ClayTextInput.Update(now - _lastFrameSeconds);
        _lastFrameSeconds = now;

        Clay.RenderCommandArray renderCommands = showDemo
            ? ClayVideoDemo.CreateLayout(demoData)
            : CreateImageLayout();

        SDL.SetRenderDrawColor(rendererData.Renderer, 0, 0, 0, 255);
        SDL.RenderClear(rendererData.Renderer);

        SDL_Clay.RenderClayCommands(rendererData, renderCommands);

        SDL.RenderPresent(rendererData.Renderer);

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

        if (rendererData.Renderer != 0)
        {
            SDL.DestroyRenderer(rendererData.Renderer);
        }

        if (window != 0)
        {
            SDL.DestroyWindow(window);
        }

        if (rendererData.Fonts != null)
        {
            for (int i = 0; i < rendererData.Fonts.Length; i++)
            {
                if (rendererData.Fonts[i] != 0)
                {
                    TTF.CloseFont(rendererData.Fonts[i]);
                }
            }
        }

        if (rendererData.TextEngine != 0)
        {
            TTF.DestroyRendererTextEngine(rendererData.TextEngine);
        }

        TTF.Quit();
    }
}
