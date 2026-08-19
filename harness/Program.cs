using ClaySharp;
using ClaySharp.Plugin.TextInput;
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

// Shared helpers for the text-input checks.
void RenderInput(Clay_TextInputState s, Clay_TextInputConfig c)
{
    Clay.BeginLayout();
    ClayTextInput.TextInput(s, c);
    Clay.EndLayout(0f);
}

void FocusInput(Clay_TextInputState s, Clay_TextInputConfig c)
{
    Clay.SetPointerState(new System.Numerics.Vector2(50, 10), false);
    RenderInput(s, c);
    Clay.SetPointerState(new System.Numerics.Vector2(50, 10), true);
    RenderInput(s, c);
}

void TypeText(Clay_TextInputState s, string text)
{
    foreach (char ch in text) ClayTextInput.OnChar(ch);
}

void PressKey(Clay_TI_Key key, Clay_TI_Mod mods)
    => ClayTextInput.OnKey(key, Clay_TI_Action.CLAY_TI_ACTION_PRESS, mods);

Clay_TextInputConfig InputCfg() => new Clay_TextInputConfig
{
    sizing = new Clay_Sizing { width = Clay.SizingFixed(300), height = Clay.SizingFixed(36) },
    textConfig = new Clay_TextElementConfig { fontSize = 16, textColor = new Clay_Color(220, 220, 220, 255) },
    colorBackground = new Clay_Color(30, 30, 30, 255),
    colorBorder = new Clay_Color(80, 80, 80, 255),
    colorBorderFocus = new Clay_Color(100, 160, 255, 255),
    colorCursor = new Clay_Color(220, 220, 220, 255),
    borderWidth = Clay.BorderAll(2),
};

Console.WriteLine("== Text input: static state + placeholder ==");
{
    Clay.Initialize(new Clay_Dimensions(400, 200), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);
    ClayTextInput.SetPlatform(default);

    var name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
    Check(name.textLen == 0 && name.cursorPos == 0 && !name.focused, "static state starts empty and unfocused");
    Check(name.bufferSize == 128, "static state captures bufferSize");

    Clay_TextInputConfig cfg = InputCfg();
    cfg.placeholder = "Enter name";

    Clay.BeginLayout();
    ClayTextInput.TextInput(name, cfg);
    var commands = Clay.EndLayout(0f);

    bool placeholderFound = false, rectFound = false, borderFound = false;
    for (int i = 0; i < commands.length; i++)
    {
        switch (commands[i].commandType)
        {
            case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT:
                if (commands[i].renderData.text.stringContents.ToString() == "Enter name") placeholderFound = true;
                break;
            case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE: rectFound = true; break;
            case Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_BORDER: borderFound = true; break;
        }
    }
    Check(placeholderFound, "placeholder rendered when empty + unfocused");
    Check(rectFound, "background rectangle rendered");
    Check(borderFound, "border rendered");
}

Console.WriteLine("== Text input: focus, type, edit keys ==");
{
    Clay.Initialize(new Clay_Dimensions(400, 200), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);
    ClayTextInput.SetPlatform(default);

    var name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
    Clay_TextInputConfig cfg = InputCfg();

    RenderInput(name, cfg);
    Check(!name.focused, "not focused before click");

    FocusInput(name, cfg);
    Check(name.focused, "click on element focuses");

    TypeText(name, "abc");
    Check(name.text == "abc" && name.cursorPos == 3, "OnChar appends and moves cursor");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_BACKSPACE, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.text == "ab" && name.cursorPos == 2, "backspace deletes before cursor");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_HOME, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.cursorPos == 0, "home moves to start");
    PressKey(Clay_TI_Key.CLAY_TI_KEY_END, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.cursorPos == 2, "end moves to end");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_LEFT, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.cursorPos == 1, "left arrow moves back");
    PressKey(Clay_TI_Key.CLAY_TI_KEY_DELETE, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.text == "a" && name.cursorPos == 1, "delete removes after cursor");

    Clay.SetPointerState(new System.Numerics.Vector2(390, 190), false);
    RenderInput(name, cfg);
    Clay.SetPointerState(new System.Numerics.Vector2(390, 190), true);
    RenderInput(name, cfg);
    Check(!name.focused, "click outside unfocuses");
}

Console.WriteLine("== Text input: selection + clipboard ==");
{
    string? clipboard = null;
    ClayTextInput.SetPlatform(new Clay_TextInput_Platform
    {
        getClipboardText = _ => clipboard,
        setClipboardText = (_, text) => clipboard = text,
    });

    Clay.Initialize(new Clay_Dimensions(400, 200), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);

    var name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
    Clay_TextInputConfig cfg = InputCfg();

    FocusInput(name, cfg);
    TypeText(name, "hello");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_A, Clay_TI_Mod.CLAY_TI_MOD_CTRL);
    Check(name.selectionAnchor == 0 && name.cursorPos == 5, "Ctrl+A selects all");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_C, Clay_TI_Mod.CLAY_TI_MOD_CTRL);
    Check(clipboard == "hello", "Ctrl+C copies selection");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_END, Clay_TI_Mod.CLAY_TI_MOD_NONE);
    Check(name.selectionAnchor == -1 && name.cursorPos == 5, "end collapses selection");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_V, Clay_TI_Mod.CLAY_TI_MOD_CTRL);
    Check(name.text == "hellohello", "Ctrl+V pastes at cursor");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_LEFT, Clay_TI_Mod.CLAY_TI_MOD_SHIFT);
    Check(name.selectionAnchor == 10 && name.cursorPos == 9, "shift+left selects one char");

    PressKey(Clay_TI_Key.CLAY_TI_KEY_X, Clay_TI_Mod.CLAY_TI_MOD_CTRL);
    Check(name.text == "hellohell" && clipboard == "o", "Ctrl+X cuts selection");
}

Console.WriteLine("== Text input: password, caps, callbacks ==");
{
    ClayTextInput.SetPlatform(default);
    Clay.Initialize(new Clay_Dimensions(400, 200), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);

    // Password mode masks rendering but keeps the real text.
    var pw = ClayTextInput.State_Static(Clay.Id("Pw"), 128);
    Clay_TextInputConfig pwCfg = InputCfg();
    pwCfg.passwordMode = true;

    FocusInput(pw, pwCfg);
    TypeText(pw, "hi");
    Check(pw.text == "hi", "password stores real text");

    bool masked = false;
    Clay.BeginLayout();
    ClayTextInput.TextInput(pw, pwCfg);
    var cmds = Clay.EndLayout(0f);
    for (int i = 0; i < cmds.length; i++)
        if (cmds[i].commandType == Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT && cmds[i].renderData.text.stringContents.ToString() == "**")
            masked = true;
    Check(masked, "password renders '*' per codepoint");

    // maxLength caps codepoints.
    var max = ClayTextInput.State_Static(Clay.Id("Max"), 128);
    Clay_TextInputConfig maxCfg = InputCfg();
    maxCfg.maxLength = 3;
    FocusInput(max, maxCfg);
    TypeText(max, "abcdef");
    Check(max.text == "abc", "maxLength caps codepoints");

    // bufferSize caps characters (static cap).
    var cap = ClayTextInput.State_Static(Clay.Id("Cap"), 4);
    Clay_TextInputConfig capCfg = InputCfg();
    FocusInput(cap, capCfg);
    TypeText(cap, "abcdefgh");
    Check(cap.text == "abcd", "bufferSize caps characters");

    // Char filter + changed callback.
    int changedCalls = 0;
    var filtered = ClayTextInput.State_Static(Clay.Id("Filtered"), 128);
    Clay_TextInputConfig filterCfg = InputCfg();
    filterCfg.onCharFilter = (cp, ud) => cp >= '0' && cp <= '9';
    filterCfg.onChanged = (text, len, ud) => changedCalls++;
    FocusInput(filtered, filterCfg);
    ClayTextInput.OnChar('a');
    ClayTextInput.OnChar('1');
    ClayTextInput.OnChar('2');
    Check(filtered.text == "12", "onCharFilter rejects non-digits");
    Check(changedCalls == 2, "onChanged fires per accepted edit");
}

Console.WriteLine("== Text input: cursor blink + double click ==");
{
    ClayTextInput.SetPlatform(default);
    Clay.Initialize(new Clay_Dimensions(400, 200), errorHandler);
    Clay.SetMeasureTextFunction(Measure, null);

    var name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
    Clay_TextInputConfig cfg = InputCfg();

    ClayTextInput.Update(0.016);
    FocusInput(name, cfg);
    Check(name.cursorVisible, "cursor visible immediately after focus");

    ClayTextInput.Update(0.53);
    Check(!name.cursorVisible, "cursor blink toggles after period");

    TypeText(name, "hi");

    // Double click (two presses within the double-click frame window).
    Clay.SetPointerState(new System.Numerics.Vector2(50, 10), false);
    RenderInput(name, cfg);
    Clay.SetPointerState(new System.Numerics.Vector2(50, 10), true);
    RenderInput(name, cfg);
    Check(name.selectionAnchor == 0 && name.cursorPos == 2, "double click selects all");
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
