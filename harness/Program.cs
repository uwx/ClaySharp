using ClaySharp;
using Microsoft.Extensions.Primitives;

// A lightweight verification harness for the ClaySharp port. Exercises the public API and
// asserts on the generated render commands and element data.

int passed = 0;
int failed = 0;

void Check(bool condition, string name)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}");
    }
}

void Approx(float actual, float expected, float eps, string name)
{
    Check(MathF.Abs(actual - expected) <= eps, $"{name} (expected ~{expected}, got {actual})");
}

Clay_ErrorType? lastError = null;
Clay_ErrorHandler errorHandler = new Clay_ErrorHandler
{
    errorHandlerFunction = data => { lastError = data.errorType; },
};

static Clay_Dimensions Measure(StringSegment text, Clay_TextElementConfig config, object? userData)
    => new Clay_Dimensions(text.Length * 8, 16);

Console.WriteLine("== Fixed sizing ==");
{
    var ctx = Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Box"), new Clay_ElementDeclaration
    {
        backgroundColor = new Clay_Color(255, 0, 0, 255),
        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(50), height = Clay.SizingFixed(50) } },
    })) { }
    var commands = Clay.EndLayout(0f);
    Check(commands.length == 1, "one rectangle command");
    Check(commands.length >= 1 && commands[0].commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE, "command is RECTANGLE");
    Check(commands.length >= 1 && commands[0].boundingBox.width == 50 && commands[0].boundingBox.height == 50, "box size 50x50");
    Check(commands.length >= 1 && commands[0].boundingBox.x == 0 && commands[0].boundingBox.y == 0, "box at 0,0");
    var data = Clay.GetElementData(Clay.Id("Box"));
    Check(data.found, "GetElementData found");
    Approx(data.boundingBox.width, 50, 0.01f, "GetElementData width");
}

Console.WriteLine("== Grow sizing ==");
{
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Row"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig
        {
            layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT,
            sizing = new Clay_Sizing { width = Clay.SizingGrow(), height = Clay.SizingGrow() },
        },
    }))
    {
        using (Clay.Element(Clay.Id("A"), new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(20), height = Clay.SizingFixed(20) } }, backgroundColor = new Clay_Color(0, 255, 0, 255) })) { }
        using (Clay.Element(Clay.Id("B"), new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingGrow(), height = Clay.SizingFixed(20) } }, backgroundColor = new Clay_Color(0, 0, 255, 255) })) { }
    }
    var commands = Clay.EndLayout(0f);
    var rects = commands.internalArray.Take(commands.length).Where(c => c.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE).ToList();
    Check(rects.Count == 2, "two rectangles");
    Check(rects.Count == 2 && MathF.Abs(rects[0].boundingBox.width - 20) <= 0.01f, "A width 20");
    Check(rects.Count == 2 && MathF.Abs(rects[1].boundingBox.width - 80) <= 0.01f, "B grows to 80");
}

Console.WriteLine("== Text ==");
{
    Clay.Initialize(new Clay_Dimensions(200, 100), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);
    Clay.BeginLayout();
    Clay.Text("Hello", new Clay_TextElementConfig { textColor = new Clay_Color(255, 255, 255, 255), fontSize = 16 });
    var commands = Clay.EndLayout(0f);
    Check(commands.length >= 1 && commands[0].commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT, "text command");
    Check(commands.length >= 1 && commands[0].renderData.text.stringContents.Length == 5, "text contents length 5");
    Check(commands.length >= 1 && MathF.Abs(commands[0].boundingBox.width - 40) <= 0.01f, "text width 40");
}

Console.WriteLine("== Floating + z-order ==");
{
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Base"), new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(100), height = Clay.SizingFixed(100) } }, backgroundColor = new Clay_Color(255, 0, 0, 255) })) { }
    using (Clay.Element(Clay.Id("Overlay"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(30), height = Clay.SizingFixed(30) } },
        floating = new Clay_FloatingElementConfig { attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ROOT, zIndex = 1 },
        backgroundColor = new Clay_Color(0, 0, 255, 255),
    })) { }
    var commands = Clay.EndLayout(0f);
    var rects = commands.internalArray.Take(commands.length).Where(c => c.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE).ToList();
    Check(rects.Count == 2, "two rectangles for base + overlay");
    Check(rects.Count == 2 && rects[0].id == Clay.Id("Base").id, "base drawn first");
    Check(rects.Count == 2 && rects[1].zIndex == 1, "overlay zIndex 1");
}

Console.WriteLine("== Border ==");
{
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Bordered"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(40), height = Clay.SizingFixed(40) } },
        border = new Clay_BorderElementConfig { color = new Clay_Color(255, 255, 255, 255), width = Clay.BorderAll(2) },
    })) { }
    var commands = Clay.EndLayout(0f);
    Check(commands.internalArray.Take(commands.length).Any(c => c.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_BORDER), "border command emitted");
}

Console.WriteLine("== Scroll container ==");
{
    var ctx = Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Scroll"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(100), height = Clay.SizingFixed(100) } },
        clip = new Clay_ClipElementConfig { vertical = true },
    }))
    {
        using (Clay.Element(Clay.Id("Content"), new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(100), height = Clay.SizingFixed(300) } },
            backgroundColor = new Clay_Color(255, 255, 255, 255),
        })) { }
    }
    var commands = Clay.EndLayout(0f);
    var scroll = Clay.GetScrollContainerData(Clay.Id("Scroll"));
    Check(scroll.found, "scroll data found");
    Approx(scroll.contentDimensions.height, 300, 0.01f, "scroll content height 300");
    Check(commands.internalArray.Take(commands.length).Any(c => c.commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START), "scissor start emitted");

    scroll.scrollPosition = new System.Numerics.Vector2(0, -50);
    var scroll2 = Clay.GetScrollContainerData(Clay.Id("Scroll"));
    Approx(scroll2.scrollPosition.Y, -50, 0.01f, "scroll position writable");
}

Console.WriteLine("== Pointer / hover ==");
{
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Hit"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(40), height = Clay.SizingFixed(40) } },
        backgroundColor = new Clay_Color(255, 255, 255, 255),
    })) { }
    Clay.EndLayout(0f);

    Clay.SetPointerState(new System.Numerics.Vector2(10, 10), true);
    Check(Clay.PointerOver(Clay.Id("Hit")), "PointerOver inside");
    Clay.SetPointerState(new System.Numerics.Vector2(90, 90), false);
    Check(!Clay.PointerOver(Clay.Id("Hit")), "PointerOver outside");
}

Console.WriteLine("== Transition (EaseOut) ==");
{
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);

    Clay_ElementDeclaration Decl(float padding) => new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig
        {
            layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
            padding = new Clay_Padding { left = (ushort)padding },
            sizing = new Clay_Sizing { width = Clay.SizingGrow(), height = Clay.SizingGrow() },
        },
    };

    float RenderFrame(float padding)
    {
        Clay.BeginLayout();
        using (Clay.Element(Clay.Id("Wrap"), Decl(padding)))
        {
            using (Clay.Element(Clay.Id("Mover"), new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(20), height = Clay.SizingFixed(20) } },
                backgroundColor = new Clay_Color(255, 0, 0, 255),
                transition = new Clay_TransitionElementConfig { handler = Clay.EaseOut, duration = 0.5f, properties = Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BOUNDING_BOX },
            })) { }
        }
        Clay.EndLayout(0.1f);
        return Clay.GetElementData(Clay.Id("Mover")).boundingBox.x;
    }

    RenderFrame(0);   // frame 1: appears at x = 0
    RenderFrame(50);  // frame 2: transition begins (ratio 0)
    float x = RenderFrame(50); // frame 3: ratio 0.2 → interpolated

    var data = Clay.GetElementData(Clay.Id("Mover"));
    Check(data.found, "moving element found");
    Check(x > 0 && x < 50, $"moving element interpolating (x = {x})");
}

Console.WriteLine("== Duplicate ID error ==");
{
    lastError = null;
    Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Dup"), new Clay_ElementDeclaration { })) { }
    using (Clay.Element(Clay.Id("Dup"), new Clay_ElementDeclaration { })) { }
    Clay.EndLayout(0f);
    Check(lastError == Clay_ErrorType.CLAY_ERROR_TYPE_DUPLICATE_ID, "duplicate ID reported");
}

Console.WriteLine("== Multi-context ==");
{
    var a = Clay.Initialize(new Clay_Dimensions(100, 100), errorHandler);
    var b = Clay.Initialize(new Clay_Dimensions(200, 200), errorHandler);
    Check(Clay.GetCurrentContext() == b, "current context is b");
    Clay.SetCurrentContext(a);
    Check(Clay.GetCurrentContext() == a, "current context restored to a");
}

Console.WriteLine("== Debug view ==");
{
    lastError = null;
    Clay.Initialize(new Clay_Dimensions(400, 300), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);

    // Baseline without the debug overlay.
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Panel"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM, sizing = new Clay_Sizing { width = Clay.SizingGrow(), height = Clay.SizingGrow() } },
        backgroundColor = new Clay_Color(40, 40, 40, 255),
    }))
    {
        Clay.Text("Hello", new Clay_TextElementConfig { textColor = new Clay_Color(255, 255, 255, 255), fontSize = 16 });
        using (Clay.Element(Clay.Id("Child"), new Clay_ElementDeclaration { backgroundColor = new Clay_Color(80, 80, 80, 255) })) { }
    }
    int noDebugCount = Clay.EndLayout(0f).length;

    // Same layout with the debug overlay enabled.
    Clay.SetDebugModeEnabled(true);
    Clay.BeginLayout();
    using (Clay.Element(Clay.Id("Panel"), new Clay_ElementDeclaration
    {
        layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM, sizing = new Clay_Sizing { width = Clay.SizingGrow(), height = Clay.SizingGrow() } },
        backgroundColor = new Clay_Color(40, 40, 40, 255),
    }))
    {
        Clay.Text("Hello", new Clay_TextElementConfig { textColor = new Clay_Color(255, 255, 255, 255), fontSize = 16 });
        using (Clay.Element(Clay.Id("Child"), new Clay_ElementDeclaration { backgroundColor = new Clay_Color(80, 80, 80, 255) })) { }
    }
    var withDebug = Clay.EndLayout(0f);
    Clay.SetDebugModeEnabled(false);

    Check(withDebug.length > noDebugCount, "debug view generates extra commands");
    Check(lastError == null, "debug view produces no errors");
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
