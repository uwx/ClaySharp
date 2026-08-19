using System;
using System.IO;
using System.Numerics;
using ClaySharp;
using ClaySharp.Examples.SDL3;
using ClaySharp.Plugin.TextInput;
using ClaySharp.Plugin.TextInput.FNA;
using ClaySharp.Renderer.FNA;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ClaySharp.Examples.FNA;

/// <summary>
/// FNA port of the ClaySharp SDL3 simple demo. Exercises rectangles, rounded
/// corners, borders, scissor clipping, text, and (via Space) a rounded-corner image.
/// </summary>
public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;
    private FontSystem _fontSystem = null!;
    private Texture2D _sampleImage = null!;
    private FNA_Clay.FnaRendererData _rendererData;
    private ClayVideoDemo_Data _demoData = null!;

    private bool _showDemo = true;
    private KeyboardState _prevKeyboard;
    private MouseState _prevMouse;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1200,
            PreferredBackBufferHeight = 800,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        IsFixedTimeStep = false;
        _graphics.SynchronizeWithVerticalRetrace = false;
    }

    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "resources", name);

    private static Clay.Dimensions MeasureText(Microsoft.Extensions.Primitives.StringSegment text, Clay.TextElementConfig config, object? userData)
    {
        var fontSystem = (FontSystem)userData!;
        Bounds bounds = fontSystem.GetFont(config.FontSize).TextBounds(text.AsSpan(), Vector2.Zero);
        return new Clay.Dimensions(bounds.X2 - bounds.X, bounds.Y2 - bounds.Y);
    }

    private static void HandleClayErrors(Clay.ErrorData errorData) => Console.WriteLine(errorData.ErrorText);

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _fontSystem = new FontSystem();
        _fontSystem.AddFont(File.ReadAllBytes(Resource("Roboto-Regular.ttf")));

        using Stream stream = File.OpenRead(Resource("sample.png"));
        _sampleImage = Texture2D.FromStream(GraphicsDevice, stream);

        _rendererData = new FNA_Clay.FnaRendererData
        {
            GraphicsDevice = GraphicsDevice,
            SpriteBatch = _spriteBatch,
            Fonts = new[] { _fontSystem },
        };

        Clay.Initialize(
            new Clay.Dimensions(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight),
            new Clay.ErrorHandler { ErrorHandlerFunction = HandleClayErrors });
        Clay.SetMeasureTextFunction(MeasureText, _fontSystem);

        ClayTextInput.SetPlatform(ClayTextInputFna.Platform());
        ClayTextInputFna.HookEvents();
        global::SDL3.SDL.SDL_StartTextInput(Window.Handle);
        
        _demoData = ClayVideoDemo.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        KeyboardState keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Space) && _prevKeyboard.IsKeyUp(Keys.Space))
        {
            _showDemo = !_showDemo;
        }
        else if (keyboard.IsKeyDown(Keys.F1) && _prevKeyboard.IsKeyUp(Keys.F1))
        {
            Clay.SetDebugModeEnabled(!Clay.IsDebugModeEnabled());
        }

        _prevKeyboard = keyboard;

        // Handle window resizing.
        if (_graphics.PreferredBackBufferWidth != Window.ClientBounds.Width ||
            _graphics.PreferredBackBufferHeight != Window.ClientBounds.Height)
        {
            _graphics.PreferredBackBufferWidth = Window.ClientBounds.Width;
            _graphics.PreferredBackBufferHeight = Window.ClientBounds.Height;
            _graphics.ApplyChanges();
        }

        Clay.SetLayoutDimensions(new Clay.Dimensions(Window.ClientBounds.Width, Window.ClientBounds.Height));

        MouseState mouse = Mouse.GetState();
        Clay.SetPointerState(new Vector2(mouse.X, mouse.Y), mouse.LeftButton == ButtonState.Pressed);

        int wheelDelta = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheelDelta != 0)
        {
            Clay.UpdateScrollContainers(true, new Vector2(0f, wheelDelta / 120f), 0.01f);
        }

        _prevMouse = mouse;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(0.2f, 0.2f, 0.25f, 1f));

        Clay.RenderCommandArray renderCommands = _showDemo
            ? ClayVideoDemo.CreateLayout(_demoData)
            : CreateImageLayout();

        FNA_Clay.RenderClayCommands(_rendererData, renderCommands);

        base.Draw(gameTime);
    }

    private Clay.RenderCommandArray CreateImageLayout()
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
                CornerRadius = Clay.CornerRadius(32),
                AspectRatio = new Clay.AspectRatioElementConfig { AspectRatio = 23.0f / 42.0f },
                Image = new Clay.ImageElementConfig { ImageData = _sampleImage },
            })) { }
        }

        return Clay.EndLayout(0);
    }
}
