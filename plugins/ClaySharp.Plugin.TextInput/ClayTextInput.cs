// Clay Text Input — C# port of clay_text_input.h.
//
// A managed, idiomatic C# port of the official clay_text_input.h text-input
// widget that keeps the public API faithful to the original C header.
// Differences from the C implementation:
//   * The text buffer is a managed, immutable `string`, so `bufferSize` is a
//     hard character cap (0 = unlimited) rather than a byte capacity, and
//     dynamic growth is automatic — `onResize` / `resizeUserData` are retained
//     for API parity but never invoked.
//   * `Clay.String` becomes `string` and `Clay.StringSlice` becomes
//     Microsoft.Extensions.Primitives.StringSegment. Byte offsets become UTF-16
//     code-unit offsets (surrogate-pair aware); `maxLength` still counts
//     codepoints.
//   * The caller-owned calc/display scratch buffer becomes a managed string, so
//     the static initialiser no longer takes calcBuf/calcBufferSize and the
//     destroy function needs no free callback.
//   * `void*` user data becomes `object?`.
//   * The C macros (TEXT_INPUT / TEXT_INPUT_WITH_STATE / ...) are
//     replaced by the static `ClayTextInput` facade.
//
// Usage:
//   ClayTextInput.SetPlatform(ClayTextInputSdl3.Platform()); // or fill the struct yourself
//   ClayTextInputState name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
//   // each frame, before Clay.BeginLayout():
//   ClayTextInput.Update(deltaTime);
//   // feed platform events via ClayTextInput.OnChar / ClayTextInput.OnKey
//   // inside layout:
//   ClayTextInput.TextInput(Clay.Id("Name"), name, new TextInputConfig { ... });

using System.Numerics;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace ClaySharp.Plugin.TextInput;
public static class ClayTextInput
{
    // -----------------------------------------
    // KEY ENUMS -------------------------------
    // -----------------------------------------

    public enum Action
    {
        Release = 0,
        Press,
        Repeat,
    }

    public enum Key
    {
        Unknown = 0,
        Left,
        Right,
        Home,
        End,
        Backspace,
        Delete,
        Enter,
        Escape,
        A,
        C,
        V,
        X,
    }

    [Flags]
    public enum Mod
    {
        None = 0,
        Shift = 1 << 0,
        Ctrl = 1 << 1,
        Alt = 1 << 2,
        Super = 1 << 3,
    }

    // -----------------------------------------
    // CALLBACK DELEGATES ----------------------
    // -----------------------------------------

    // Only clipboard I/O and cursor swapping need platform help; all other events
    // are pushed in by the caller via ClayTextInput.OnChar / OnKey.
    public delegate string? GetClipboardFn(object? userData);
    public delegate void SetClipboardFn(object? userData, string text);
    public delegate void SetIbeamCursorFn(object? userData);
    public delegate void ResetCursorFn(object? userData);

    // Retained for API parity with the C header. The managed port never invokes
    // the resize callback — managed strings grow automatically and are bounded by
    // bufferSize (chars) and maxLength (codepoints).
    public delegate ResizeResult ResizeFn(string? oldBuf, int oldCapacity, int minCapacity, object? userData);

    public delegate bool CharFilterFn(uint codepoint, object? userData);
    public delegate void ChangedFn(string text, int textLen, object? userData);

    // -----------------------------------------
    // STRUCTS ---------------------------------
    // -----------------------------------------

    public struct ResizeResult
    {
        public string? Buf;
        public int Capacity;
    }

    public struct Platform
    {
        // Return clipboard contents; null if empty.
        public GetClipboardFn? GetClipboardText;
        // Write to the system clipboard.
        public SetClipboardFn? SetClipboardText;
        // Set the cursor to I-beam.
        public SetIbeamCursorFn? SetIbeamCursor;
        // Reset the cursor back to normal.
        public ResetCursorFn? ResetCursor;
        // Passed verbatim to all four callbacks; may be null.
        public object? UserData;
    }
    
    /*
        using (Clay.Element(state.ElementId, new Clay.ElementDeclaration
               {
                   Layout = new Clay.LayoutConfig
                   {
                       Sizing = cfg.Sizing,
                       Padding = cfg.Padding,
                       ChildAlignment = new Clay.ChildAlignment
                       {
                           Y = Clay.LayoutAlignmentY.Center
                       },
                   },
                   BackgroundColor = cfg.ColorBackground,
                   CornerRadius = cfg.CornerRadius,
                   Floating = cfg.Floating,
                   Border = new Clay.BorderElementConfig
                   {
                       Color = borderColor,
                       Width = cfg.BorderWidth
                   },
               }))
     */
    public struct TextInputLayoutConfig
    {
        public Clay.Sizing Sizing; // FIT / GROW / PERCENT / FIXED sizing inside the parent container.
        public Clay.Padding Padding; // "padding" in pixels, a gap between this element's bounding box and its children.
        public ushort ChildGap; // The gap in pixels between child elements along the layout axis.
    }

    // Passed to ClayTextInput.TextInput() each frame (immediate-mode style), so
    // any field can change between frames — including toggling callbacks on/off.
    public struct TextInputConfig
    {
        public TextInputLayoutConfig Layout; // Controls the size and position of an element and its children.
        public Clay.Color BackgroundColor; // Background color; generates a RECTANGLE render command (or is passed to IMAGE/CUSTOM).
        public Clay.Color OverlayColor; // "Color Overlay" applied to this element and all its children.
        public Clay.CornerRadiusValues CornerRadius; // Corner rounding of rectangles, borders and images.
        public Clay.AspectRatioElementConfig AspectRatio; // Aspect ratio scaling.
        public Clay.FloatingElementConfig Floating; // Floating / absolute positioning settings.
        public Clay.ClipElementConfig Clip; // Clip / scroll settings.
        public Clay.BorderElementConfig Border; // Border settings.
        public Clay.TransitionElementConfig Transition; // Transition settings.
        public object? UserData; // Transparently passed through to resulting render commands.
        
        // Text.
        public Clay.TextElementConfig TextConfig;
        // Shown when the buffer is empty and the element is unfocused. Null or
        // empty disables the placeholder.
        public string? Placeholder;
        // Render '*' per codepoint.
        public bool PasswordMode;

        // Colours (RGBA 0–255).
        public Clay.Color PlaceholderColor;
        public Clay.Color BorderFocusColor;
        public Clay.Color SelectionColor;
        public Clay.Color CursorColor;

        // Behaviour.
        // Maximum codepoints; 0 = unlimited.
        public int MaxLength;
        // Cursor blink half-period in seconds; 0 → default 0.53 s.
        public float CursorBlinkPeriod;

        // Callbacks (all optional; NULL disables).
        // Dynamic buffer growth — unused in the managed port (strings grow
        // automatically); retained for API parity.
        public ResizeFn? OnResize;
        public object? ResizeUserData;
        // Per-character filter; NULL → accept all.
        public CharFilterFn? OnCharFilter;
        public object? CharFilterUserData;
        // Post-edit notification.
        public ChangedFn? OnChanged;
        public object? ChangedUserData;
    }

    // Retains all per-input mutable data across frames. A class (like
    // Clay.LayoutElement) because the C implementation takes it by pointer and
    // the focused state is tracked module-wide by reference.
    public sealed class TextInputState
    {
        // Buffer — a managed string; null marks the state as uninitialised.
        // Never cache this; edits reassign the whole string.
        public string? Text;
        // Maximum UTF-16 chars; 0 = unlimited.
        public int BufferSize;
        // Current string length in UTF-16 code units.
        public int TextLen => Text?.Length ?? 0;

        // Editing.
        // UTF-16 code-unit offset of the insert point.
        public int CursorPos;
        // UTF-16 code-unit offset; -1 = no selection.
        public int SelectionAnchor = -1;

        // Focus / blink.
        public bool Focused;
        public double CursorBlinkTimer;
        public bool CursorVisible = true;

        // Internal.
        internal bool IsDynamic;
        internal Clay.ElementId ElementId;
        // Display scratch string (password-masked when enabled).
        internal string CalcText = "";
        // Cached copy of the most recent TextInputConfig so OnChar / OnKey
        // can read the callbacks without extra arguments.
        internal TextInputConfig Cfg;
        internal ulong LastClickFrame;
        internal bool LastClickWasDouble;
        internal bool DragSelecting;
        internal int DragAnchor;
    }

    // -----------------------------------------
    // FACADE ----------------------------------
    // -----------------------------------------

    private static Platform _platform;
    private static bool _cursorIsIbeam;
    private static TextInputState? _focused;
    private static ulong _frameCount;

    // ── Module lifecycle ──────────────────────────────────────────────

    public static void SetPlatform(Platform platform)
    {
        _platform = platform;
        _focused = null;
        _cursorIsIbeam = false;
    }

    public static void Update(double dt)
    {
        _frameCount++;
        if (_focused == null) return;
        _focused.CursorBlinkTimer += dt;
        double period = _focused.Cfg.CursorBlinkPeriod > 0f
            ? _focused.Cfg.CursorBlinkPeriod
            : 0.53;
        while (_focused.CursorBlinkTimer >= period)
        {
            _focused.CursorBlinkTimer -= period;
            _focused.CursorVisible = !_focused.CursorVisible;
        }
    }

    // ── Event feed ────────────────────────────────────────────────────

    public static void OnChar(uint codepoint)
    {
        if (_focused == null) return;
        var cfg = _focused.Cfg;
        if (cfg.OnCharFilter != null && !cfg.OnCharFilter(codepoint, cfg.CharFilterUserData)) return;

        // Reject values that would not form a valid UTF-16 scalar.
        if (codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF)) return;

        var rune = new Rune((int)codepoint);
        Span<char> buf = stackalloc char[2];
        int n = rune.EncodeToUtf16(buf);
        Insert(_focused, new string(buf.Slice(0, n)), n);

        _focused.CursorBlinkTimer = 0.0;
        _focused.CursorVisible = true;
    }

    public static void OnKey(Key key, Action action, Mod mods)
    {
        if (_focused == null) return;
        if (action != Action.Press && action != Action.Repeat) return;

        var s = _focused;
        bool ctrl = (mods & Mod.Ctrl) != 0;
        bool shift = (mods & Mod.Shift) != 0;

        switch (key)
        {
            case Key.Left:
            {
                bool hadSelection = s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos;
                int oldAnchor = s.SelectionAnchor;
                ShiftAnchor(s, shift);
                if (!shift && hadSelection)
                {
                    s.CursorPos = Math.Min(s.CursorPos, oldAnchor);
                    s.SelectionAnchor = -1;
                }
                else
                {
                    s.CursorPos = ctrl ? WordLeft(s.Text ?? "", s.CursorPos)
                        : Utf16Prev(s.Text ?? "", s.CursorPos);
                }
                break;
            }
            case Key.Right:
            {
                bool hadSelection = s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos;
                int oldAnchor = s.SelectionAnchor;
                ShiftAnchor(s, shift);
                if (!shift && hadSelection)
                {
                    s.CursorPos = Math.Max(s.CursorPos, oldAnchor);
                    s.SelectionAnchor = -1;
                }
                else
                {
                    s.CursorPos = ctrl ? WordRight(s.Text ?? "", s.CursorPos, s.TextLen)
                        : Utf16Next(s.Text ?? "", s.CursorPos, s.TextLen);
                }
                break;
            }
            case Key.Home:
                ShiftAnchor(s, shift);
                s.CursorPos = 0;
                break;
            case Key.End:
                ShiftAnchor(s, shift);
                s.CursorPos = s.TextLen;
                break;
            case Key.Backspace:
            {
                int lo, hi;
                if (s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos)
                {
                    lo = Math.Min(s.CursorPos, s.SelectionAnchor);
                    hi = Math.Max(s.CursorPos, s.SelectionAnchor);
                    s.CursorPos = lo;
                }
                else if (s.CursorPos > 0)
                {
                    lo = ctrl ? WordLeft(s.Text ?? "", s.CursorPos) : Utf16Prev(s.Text ?? "", s.CursorPos);
                    hi = s.CursorPos;
                    s.CursorPos = lo;
                }
                else break;
                DeleteNotify(s, lo, hi);
                s.SelectionAnchor = -1;
                break;
            }
            case Key.Delete:
            {
                int lo, hi;
                if (s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos)
                {
                    lo = Math.Min(s.CursorPos, s.SelectionAnchor);
                    hi = Math.Max(s.CursorPos, s.SelectionAnchor);
                    s.CursorPos = lo;
                }
                else if (s.CursorPos < s.TextLen)
                {
                    lo = s.CursorPos;
                    hi = ctrl ? WordRight(s.Text ?? "", s.CursorPos, s.TextLen)
                        : Utf16Next(s.Text ?? "", s.CursorPos, s.TextLen);
                }
                else break;
                DeleteNotify(s, lo, hi);
                s.SelectionAnchor = -1;
                break;
            }
            case Key.A:
                if (ctrl) { s.SelectionAnchor = 0; s.CursorPos = s.TextLen; }
                break;
            case Key.C:
                if (ctrl) CopySelection(s);
                break;
            case Key.X:
                if (ctrl)
                {
                    CopySelection(s);
                    if (s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos)
                    {
                        int lo = Math.Min(s.CursorPos, s.SelectionAnchor);
                        int hi = Math.Max(s.CursorPos, s.SelectionAnchor);
                        DeleteNotify(s, lo, hi);
                        s.CursorPos = lo;
                    }
                }
                break;
            case Key.V:
                if (ctrl && _platform.GetClipboardText != null)
                {
                    string? clip = _platform.GetClipboardText(_platform.UserData);
                    if (clip != null) Insert(s, clip, clip.Length);
                }
                break;
            case Key.Escape:
            case Key.Enter:
                Unfocus(s);
                break;
        }

        s.CursorBlinkTimer = 0.0;
        s.CursorVisible = true;
    }

    // ── State initialisers ────────────────────────────────────────────

    public static TextInputState State_Static(Clay.ElementId elementId, int bufferSize)
    {
        return new TextInputState
        {
            Text = "",
            BufferSize = bufferSize,
            CursorPos = 0,
            SelectionAnchor = -1,
            CursorVisible = true,
            ElementId = elementId,
            IsDynamic = false,
            CalcText = "",
        };
    }

    public static bool State_InitDynamic(
        TextInputState state,
        int initialCapacity,
        Clay.ElementId elementId,
        ResizeFn? resizeFn,
        object? resizeUserData)
    {
        state.Text = "";
        state.BufferSize = 0;
        state.CursorPos = 0;
        state.SelectionAnchor = -1;
        state.Focused = false;
        state.CursorVisible = true;
        state.ElementId = elementId;
        state.IsDynamic = true;
        state.CalcText = "";
        // initialCapacity / resizeFn are unused: managed strings grow automatically.
        return true;
    }

    public static void State_Destroy(TextInputState state)
    {
        state.Text = null;
        state.BufferSize = 0;
        state.SelectionAnchor = -1;
        state.Focused = false;
        state.IsDynamic = false;
        state.DragSelecting = false;
        if (ReferenceEquals(_focused, state)) _focused = null;
    }

    public static void State_Insert(TextInputState state, int at, string s)
    {
        if (at < 0) at = 0;
        else if (at > state.TextLen) at = state.TextLen;
        state.CursorPos = at;
        Insert(state, s, s.Length);
    }

    // ── Element DSL ───────────────────────────────────────────────────

    // TEXT_INPUT(id, state, cfg) — auto-initialises an uninitialised state.
    public static void TextInput(Clay.ElementId id, TextInputState state, TextInputConfig cfg)
    {
        if (state.Text == null)
        {
            state.Text = "";
            state.ElementId = id;
            state.IsDynamic = true;
        }
        __Element(state, cfg);
    }

    // TEXT_INPUT_WITH_STATE(state, cfg) — uses the state's stored element id.
    public static void TextInput(TextInputState state, TextInputConfig cfg) => __Element(state, cfg);

    // ── Internals ─────────────────────────────────────────────────────

    private static void __Element(TextInputState state, TextInputConfig cfg)
    {
        state.Cfg = cfg;
        state.Text ??= string.Empty;

        Clay.PointerData pointerData = Clay.GetPointerState();

        // Click → focus / unfocus.
        if (pointerData.State == Clay.PointerDataInteractionState.PressedThisFrame)
        {
            if (Clay.PointerOver(state.ElementId))
            {
                Clay.ElementData elementData = Clay.GetElementData(state.ElementId);
                Focus(state);

                bool isDoubleClick = state.LastClickFrame > 0
                                     && !state.LastClickWasDouble
                                     && (_frameCount - state.LastClickFrame) <= 20;
                if (isDoubleClick)
                {
                    state.SelectionAnchor = 0;
                    state.CursorPos = state.TextLen;
                    state.DragSelecting = false;
                    state.LastClickWasDouble = true;
                }
                else
                {
                    bool hasClickPos = false;
                    int clickPos = state.CursorPos;
                    if (elementData.Found)
                    {
                        float clickX = pointerData.Position.X - elementData.BoundingBox.X - cfg.Layout.Padding.Left;
                        hasClickPos = ByteAtXIfInBounds(state, cfg, clickX, out clickPos);
                    }

                    if (hasClickPos) state.CursorPos = clickPos;
                    state.SelectionAnchor = -1;
                    state.DragAnchor = state.CursorPos;
                    state.DragSelecting = hasClickPos;
                    state.LastClickWasDouble = false;
                }

                state.LastClickFrame = _frameCount;
                state.CursorBlinkTimer = 0.0;
                state.CursorVisible = true;
            }
            else if (ReferenceEquals(_focused, state))
            {
                Unfocus(state);
            }
        }

        // Drag selection.
        if (state.Focused && state.DragSelecting
                          && pointerData.State == Clay.PointerDataInteractionState.Pressed)
        {
            Clay.ElementData elementData = Clay.GetElementData(state.ElementId);
            if (elementData.Found)
            {
                float dragX = pointerData.Position.X - elementData.BoundingBox.X - cfg.Layout.Padding.Left;
                int dragPos = ByteAtX(state, cfg, dragX);
                state.CursorPos = dragPos;
                state.SelectionAnchor = (dragPos == state.DragAnchor) ? -1 : state.DragAnchor;
                state.CursorBlinkTimer = 0.0;
                state.CursorVisible = true;
            }
        }

        if (pointerData.State == Clay.PointerDataInteractionState.ReleasedThisFrame)
        {
            state.DragSelecting = false;
        }

        // I-beam cursor.
        if (_platform.SetIbeamCursor != null && !_cursorIsIbeam && Clay.PointerOver(state.ElementId))
        {
            _cursorIsIbeam = true;
            _platform.SetIbeamCursor(_platform.UserData);
        }
        else if (_platform.ResetCursor != null && _cursorIsIbeam && !Clay.PointerOver(state.ElementId))
        {
            _cursorIsIbeam = false;
            _platform.ResetCursor(_platform.UserData);
        }

        Clay.Color borderColor = state.Focused ? cfg.BorderFocusColor : cfg.Border.Color;
        
        // public struct TextInputConfig
        // {
        //     public TextInputLayoutConfig Layout; // Controls the size and position of an element and its children.
        //     public Clay.Color BackgroundColor; // Background color; generates a RECTANGLE render command (or is passed to IMAGE/CUSTOM).
        //     public Clay.Color OverlayColor; // "Color Overlay" applied to this element and all its children.
        //     public Clay.CornerRadiusValues CornerRadius; // Corner rounding of rectangles, borders and images.
        //     public Clay.AspectRatioElementConfig AspectRatio; // Aspect ratio scaling.
        //     public Clay.FloatingElementConfig Floating; // Floating / absolute positioning settings.
        //     public Clay.ClipElementConfig Clip; // Clip / scroll settings.
        //     public Clay.BorderElementConfig Border; // Border settings.
        //     public Clay.TransitionElementConfig Transition; // Transition settings.
        //     public object? UserData; // Transparently passed through to resulting render commands.
        //     
        //     // Text.
        //     public Clay.TextElementConfig TextConfig;
        //     // Shown when the buffer is empty and the element is unfocused. Null or
        //     // empty disables the placeholder.
        //     public string? Placeholder;
        //     // Render '*' per codepoint.
        //     public bool PasswordMode;
        //
        //     // Colours (RGBA 0–255).
        //     public Clay.Color PlaceholderColor;
        //     public Clay.Color BorderColor;
        //     public Clay.Color BorderFocusColor;
        //     public Clay.Color SelectionColor;
        //     public Clay.Color CursorColor;
        //
        //     // Behaviour.
        //     // Maximum codepoints; 0 = unlimited.
        //     public int MaxLength;
        //     // Cursor blink half-period in seconds; 0 → default 0.53 s.
        //     public float CursorBlinkPeriod;
        //
        //     // Callbacks (all optional; NULL disables).
        //     // Dynamic buffer growth — unused in the managed port (strings grow
        //     // automatically); retained for API parity.
        //     public ResizeFn? OnResize;
        //     public object? ResizeUserData;
        //     // Per-character filter; NULL → accept all.
        //     public CharFilterFn? OnCharFilter;
        //     public object? CharFilterUserData;
        //     // Post-edit notification.
        //     public ChangedFn? OnChanged;
        //     public object? ChangedUserData;
        // }
        
        using (Clay.Element(state.ElementId, new Clay.ElementDeclaration
               {
                   Layout = new Clay.LayoutConfig
                   {
                       Sizing = cfg.Layout.Sizing,
                       Padding = cfg.Layout.Padding,
                       ChildGap = cfg.Layout.ChildGap,
                       ChildAlignment = new Clay.ChildAlignment
                       {
                           Y = Clay.LayoutAlignmentY.Center
                       },
                   },
                   BackgroundColor = cfg.BackgroundColor,
                   OverlayColor = cfg.OverlayColor,
                   CornerRadius = cfg.CornerRadius,
                   AspectRatio = cfg.AspectRatio,
                   Floating = cfg.Floating,
                   Clip = cfg.Clip,
                   Border = cfg.Border with { Color = borderColor },
                   Transition = cfg.Transition,
                   UserData = cfg.UserData
               }))
        {
            const string testWidthChar = " ";
            float visualXBias = MeasureWidth(new StringSegment(testWidthChar), cfg) * 0.33f;
            float cursorH = cfg.TextConfig.FontSize;
            const float cursorW = 2.0f;

            float textOffsetX = cfg.Layout.Padding.Left;
            float cursorX = 0f;
            float selectionX = 0f;
            float selectionW = 0f;
            bool hasSelection = false;

            if (state.Focused)
            {
                cursorX = MeasureTo(state, cfg, state.CursorPos);
                if (state.SelectionAnchor >= 0 && state.SelectionAnchor != state.CursorPos)
                {
                    int lo = Math.Min(state.CursorPos, state.SelectionAnchor);
                    int hi = Math.Max(state.CursorPos, state.SelectionAnchor);
                    float sx = MeasureTo(state, cfg, lo);
                    selectionX = sx;
                    selectionW = MeasureTo(state, cfg, hi) - sx;
                    hasSelection = true;
                }
            }

            // Placeholder or text content.
            if (state.TextLen == 0 && !string.IsNullOrEmpty(cfg.Placeholder) && !state.Focused)
            {
                Clay.TextElementConfig placeholderConfig = cfg.TextConfig;
                placeholderConfig.TextColor = cfg.PlaceholderColor;
                Clay.Text(cfg.Placeholder, placeholderConfig);
            }
            else
            {
                Clay.Text(DisplayString(state, cfg), cfg.TextConfig);
            }

            // Cursor / selection (focused only).
            if (state.Focused)
            {
                if (hasSelection)
                {
                    using (Clay.AutoId(new Clay.ElementDeclaration
                           {
                               Layout = new Clay.LayoutConfig { Sizing = new Clay.Sizing { Width = Clay.SizingFixed(selectionW), Height = Clay.SizingFixed(cursorH) } },
                               BackgroundColor = cfg.SelectionColor,
                               Floating = new Clay.FloatingElementConfig
                               {
                                   Offset = new Vector2(textOffsetX + selectionX + visualXBias, 0),
                                   AttachPoints = new Clay.FloatingAttachPoints
                                   {
                                       Element = Clay.FloatingAttachPointType.LeftCenter,
                                       Parent = Clay.FloatingAttachPointType.LeftCenter,
                                   },
                                   AttachTo = Clay.FloatingAttachToElement.Parent,
                               },
                           })) { }
                }

                if (state.CursorVisible && !hasSelection)
                {
                    using (Clay.AutoId(new Clay.ElementDeclaration
                           {
                               Layout = new Clay.LayoutConfig { Sizing = new Clay.Sizing { Width = Clay.SizingFixed(cursorW), Height = Clay.SizingFixed(cursorH) } },
                               BackgroundColor = cfg.CursorColor,
                               Floating = new Clay.FloatingElementConfig
                               {
                                   Offset = new Vector2(textOffsetX + cursorX + 0.5f, 0),
                                   AttachPoints = new Clay.FloatingAttachPoints
                                   {
                                       Element = Clay.FloatingAttachPointType.LeftCenter,
                                       Parent = Clay.FloatingAttachPointType.LeftCenter,
                                   },
                                   AttachTo = Clay.FloatingAttachToElement.Parent,
                               },
                           })) { }
                }
            }
        }
    }

    // ── Editing primitives ────────────────────────────────────────────

    private static void Insert(TextInputState s, string str, int strLen)
    {
        if (strLen > str.Length) strLen = str.Length;

        // Replace selection.
        if (s.SelectionAnchor >= 0 && s.SelectionAnchor != s.CursorPos)
        {
            int lo = Math.Min(s.CursorPos, s.SelectionAnchor);
            int hi = Math.Max(s.CursorPos, s.SelectionAnchor);
            DeleteRange(s, lo, hi);
            s.CursorPos = lo;
        }
        s.SelectionAnchor = -1;

        // Trim to maxLength (codepoints).
        if (s.Cfg.MaxLength > 0)
        {
            int rem = s.Cfg.MaxLength - RuneCount(s.Text ?? "", s.TextLen);
            if (rem <= 0) return;
            int cp = 0, i = 0;
            while (i < strLen && cp < rem)
            {
                i = Utf16Next(str, i, strLen);
                cp++;
            }
            strLen = i;
        }
        if (strLen <= 0) return;

        // Enforce the character cap (bufferSize > 0 → static mode).
        if (s.BufferSize > 0)
        {
            int rem = s.BufferSize - s.TextLen;
            if (rem <= 0) return;
            if (strLen > rem) strLen = rem;
            // Never split a surrogate pair.
            if (strLen > 0 && strLen < str.Length && char.IsLowSurrogate(str[strLen])) strLen--;
            if (strLen <= 0) return;
        }

        var current = s.Text ?? "";
        s.Text = current.Substring(0, s.CursorPos) + str.Substring(0, strLen) + current.Substring(s.CursorPos);
        s.CursorPos += strLen;

        if (s.Cfg.OnChanged != null)
            s.Cfg.OnChanged(s.Text, s.TextLen, s.Cfg.ChangedUserData);
    }

    private static void DeleteRange(TextInputState s, int lo, int hi)
    {
        var t = s.Text ?? "";
        if (lo < 0) lo = 0;
        if (hi > t.Length) hi = t.Length;
        if (lo >= hi) return;

        s.Text = t.Substring(0, lo) + t.Substring(hi);
        if (s.CursorPos > hi) s.CursorPos -= hi - lo;
        else if (s.CursorPos > lo) s.CursorPos = lo;
        s.SelectionAnchor = -1;
    }

    private static void DeleteNotify(TextInputState s, int lo, int hi)
    {
        int before = s.TextLen;
        DeleteRange(s, lo, hi);
        if (s.TextLen != before && s.Cfg.OnChanged != null)
            s.Cfg.OnChanged(s.Text!, s.TextLen, s.Cfg.ChangedUserData);
    }

    private static void CopySelection(TextInputState s)
    {
        if (_platform.SetClipboardText == null) return;
        if (s.SelectionAnchor < 0 || s.SelectionAnchor == s.CursorPos) return;
        int lo = Math.Min(s.CursorPos, s.SelectionAnchor);
        int hi = Math.Max(s.CursorPos, s.SelectionAnchor);
        _platform.SetClipboardText(_platform.UserData, s.Text!.Substring(lo, hi - lo));
    }

    // ── Focus helpers ─────────────────────────────────────────────────

    private static void Focus(TextInputState s)
    {
        if (_focused != null && !ReferenceEquals(_focused, s))
        {
            _focused.Focused = false;
            _focused.SelectionAnchor = -1;
        }
        s.Focused = true;
        s.CursorBlinkTimer = 0.0;
        s.CursorVisible = true;
        _focused = s;
    }

    private static void Unfocus(TextInputState s)
    {
        s.Focused = false;
        s.SelectionAnchor = -1;
        s.DragSelecting = false;
        if (ReferenceEquals(_focused, s)) _focused = null;
    }

    private static void ShiftAnchor(TextInputState s, bool shift)
    {
        if (shift && s.SelectionAnchor < 0) s.SelectionAnchor = s.CursorPos;
        else if (!shift) s.SelectionAnchor = -1;
    }

    // ── UTF-16 / text helpers ─────────────────────────────────────────

    private static int Utf16Next(string t, int pos, int len)
    {
        if (pos >= len) return len;
        pos++;
        if (pos < len && char.IsLowSurrogate(t[pos])) pos++;
        return pos;
    }

    private static int Utf16Prev(string t, int pos)
    {
        if (pos <= 0) return 0;
        pos--;
        if (pos > 0 && char.IsLowSurrogate(t[pos]) && char.IsHighSurrogate(t[pos - 1])) pos--;
        return pos;
    }

    private static int RuneCount(string t, int charLen)
    {
        int n = 0;
        for (int i = 0; i < charLen; )
        {
            i = Utf16Next(t, i, charLen);
            n++;
        }
        return n;
    }

    private static int WordLeft(string t, int pos)
    {
        while (pos > 0 && !char.IsLetterOrDigit(t[pos - 1])) pos--;
        while (pos > 0 && char.IsLetterOrDigit(t[pos - 1])) pos--;
        return pos;
    }

    private static int WordRight(string t, int pos, int len)
    {
        while (pos < len && !char.IsLetterOrDigit(t[pos])) pos++;
        while (pos < len && char.IsLetterOrDigit(t[pos])) pos++;
        return pos;
    }

    // ── Display / measurement ─────────────────────────────────────────

    private static float MeasureWidth(StringSegment text, TextInputConfig cfg)
    {
        if (Clay.MeasureText == null)
        {
            Clay.GetCurrentContext()?.Error(
                Clay.ErrorType.TextMeasurementFunctionNotProvided,
                "Clay's MeasureText function is null. Call Clay.SetMeasureTextFunction() before using ClayTextInput.");
            return 0f;
        }
        return Clay.MeasureText(text, cfg.TextConfig, Clay.GetCurrentContext()?.MeasureTextUserData).Width;
    }

    private static string DisplayString(TextInputState s, TextInputConfig cfg)
    {
        var t = s.Text ?? "";
        if (cfg.PasswordMode)
        {
            s.CalcText = new string('*', RuneCount(t, t.Length));
            return s.CalcText;
        }
        s.CalcText = t;
        return s.CalcText;
    }

    private static float MeasureTo(TextInputState s, TextInputConfig cfg, int charPos)
    {
        var t = s.Text ?? "";
        if (charPos < 0) charPos = 0;
        if (charPos > t.Length) charPos = t.Length;

        if (cfg.PasswordMode)
        {
            int dispLen = RuneCount(t, charPos);
            return MeasureWidth(new StringSegment(new string('*', dispLen)), cfg);
        }
        return MeasureWidth(new StringSegment(t, 0, charPos), cfg);
    }

    private static int ByteAtX(TextInputState s, TextInputConfig cfg, float x)
    {
        var t = s.Text ?? "";
        if (t.Length <= 0 || x <= 0f) return 0;

        float totalWidth = MeasureTo(s, cfg, t.Length);
        if (x >= totalWidth) return t.Length;

        int prev = 0;
        while (prev < t.Length)
        {
            int next = Utf16Next(t, prev, t.Length);
            float prevWidth = MeasureTo(s, cfg, prev);
            float nextWidth = MeasureTo(s, cfg, next);
            float mid = (prevWidth + nextWidth) * 0.5f;
            if (x < mid) return prev;
            prev = next;
        }
        return t.Length;
    }

    private static bool ByteAtXIfInBounds(TextInputState s, TextInputConfig cfg, float x, out int outCharPos)
    {
        var t = s.Text ?? "";
        float totalWidth = MeasureTo(s, cfg, t.Length);
        if (x < 0f || x > totalWidth)
        {
            outCharPos = 0;
            return false;
        }
        outCharPos = ByteAtX(s, cfg, x);
        return true;
    }
}