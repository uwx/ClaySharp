// Clay Text Input — C# port of clay_text_input.h.
//
// A managed, idiomatic C# port of the official clay_text_input.h text-input
// widget that keeps the public API faithful to the original C header.
// Differences from the C implementation:
//   * The text buffer is a managed, immutable `string`, so `bufferSize` is a
//     hard character cap (0 = unlimited) rather than a byte capacity, and
//     dynamic growth is automatic — `onResize` / `resizeUserData` are retained
//     for API parity but never invoked.
//   * `Clay_String` becomes `string` and `Clay_StringSlice` becomes
//     Microsoft.Extensions.Primitives.StringSegment. Byte offsets become UTF-16
//     code-unit offsets (surrogate-pair aware); `maxLength` still counts
//     codepoints.
//   * The caller-owned calc/display scratch buffer becomes a managed string, so
//     the static initialiser no longer takes calcBuf/calcBufferSize and the
//     destroy function needs no free callback.
//   * `void*` user data becomes `object?`.
//   * The C macros (CLAY_TEXT_INPUT / CLAY_TEXT_INPUT_WITH_STATE / ...) are
//     replaced by the static `ClayTextInput` facade.
//
// Usage:
//   ClayTextInput.SetPlatform(ClayTextInputSdl3.Platform()); // or fill the struct yourself
//   ClayTextInputState name = ClayTextInput.State_Static(Clay.Id("Name"), 128);
//   // each frame, before Clay.BeginLayout():
//   ClayTextInput.Update(deltaTime);
//   // feed platform events via ClayTextInput.OnChar / ClayTextInput.OnKey
//   // inside layout:
//   ClayTextInput.TextInput(Clay.Id("Name"), name, new Clay_TextInputConfig { ... });

using System;
using System.Numerics;
using System.Text;
using ClaySharp;
using Microsoft.Extensions.Primitives;

namespace ClaySharp.Plugin.TextInput
{
    // -----------------------------------------
    // KEY ENUMS -------------------------------
    // -----------------------------------------

    public enum Clay_TI_Action
    {
        CLAY_TI_ACTION_RELEASE = 0,
        CLAY_TI_ACTION_PRESS,
        CLAY_TI_ACTION_REPEAT,
    }

    public enum Clay_TI_Key
    {
        CLAY_TI_KEY_UNKNOWN = 0,
        CLAY_TI_KEY_LEFT,
        CLAY_TI_KEY_RIGHT,
        CLAY_TI_KEY_HOME,
        CLAY_TI_KEY_END,
        CLAY_TI_KEY_BACKSPACE,
        CLAY_TI_KEY_DELETE,
        CLAY_TI_KEY_ENTER,
        CLAY_TI_KEY_ESCAPE,
        CLAY_TI_KEY_A,
        CLAY_TI_KEY_C,
        CLAY_TI_KEY_V,
        CLAY_TI_KEY_X,
    }

    [Flags]
    public enum Clay_TI_Mod
    {
        CLAY_TI_MOD_NONE = 0,
        CLAY_TI_MOD_SHIFT = 1 << 0,
        CLAY_TI_MOD_CTRL = 1 << 1,
        CLAY_TI_MOD_ALT = 1 << 2,
        CLAY_TI_MOD_SUPER = 1 << 3,
    }

    // -----------------------------------------
    // CALLBACK DELEGATES ----------------------
    // -----------------------------------------

    // Only clipboard I/O and cursor swapping need platform help; all other events
    // are pushed in by the caller via ClayTextInput.OnChar / OnKey.
    public delegate string? Clay_TextInput_GetClipboardFn(object? userData);
    public delegate void Clay_TextInput_SetClipboardFn(object? userData, string text);
    public delegate void Clay_TextInput_SetIbeamCursorFn(object? userData);
    public delegate void Clay_TextInput_ResetCursorFn(object? userData);

    // Retained for API parity with the C header. The managed port never invokes
    // the resize callback — managed strings grow automatically and are bounded by
    // bufferSize (chars) and maxLength (codepoints).
    public delegate Clay_TI_ResizeResult Clay_TI_ResizeFn(string? oldBuf, int oldCapacity, int minCapacity, object? userData);

    public delegate bool Clay_TI_CharFilterFn(uint codepoint, object? userData);
    public delegate void Clay_TI_ChangedFn(string text, int textLen, object? userData);

    // -----------------------------------------
    // STRUCTS ---------------------------------
    // -----------------------------------------

    public struct Clay_TI_ResizeResult
    {
        public string? buf;
        public int capacity;
    }

    public struct Clay_TextInput_Platform
    {
        // Return clipboard contents; null if empty.
        public Clay_TextInput_GetClipboardFn? getClipboardText;
        // Write to the system clipboard.
        public Clay_TextInput_SetClipboardFn? setClipboardText;
        // Set the cursor to I-beam.
        public Clay_TextInput_SetIbeamCursorFn? setIbeamCursor;
        // Reset the cursor back to normal.
        public Clay_TextInput_ResetCursorFn? resetCursor;
        // Passed verbatim to all four callbacks; may be null.
        public object? userData;
    }

    // Passed to ClayTextInput.TextInput() each frame (immediate-mode style), so
    // any field can change between frames — including toggling callbacks on/off.
    public struct Clay_TextInputConfig
    {
        // Sizing & layout.
        public Clay_Sizing sizing;
        public Clay_Padding padding;

        // Text.
        public Clay_TextElementConfig textConfig;
        // Shown when the buffer is empty and the element is unfocused. Null or
        // empty disables the placeholder.
        public string? placeholder;
        // Render '*' per codepoint.
        public bool passwordMode;

        // Colours (RGBA 0–255).
        public Clay_Color colorPlaceholder;
        public Clay_Color colorBackground;
        public Clay_Color colorBorder;
        public Clay_Color colorBorderFocus;
        public Clay_Color colorSelection;
        public Clay_Color colorCursor;

        // Shape.
        public Clay_CornerRadius cornerRadius;
        public Clay_BorderWidth borderWidth;

        // Extra layout.
        public Clay_FloatingElementConfig floating;

        // Behaviour.
        // Maximum codepoints; 0 = unlimited.
        public int maxLength;
        // Cursor blink half-period in seconds; 0 → default 0.53 s.
        public float cursorBlinkPeriod;

        // Callbacks (all optional; NULL disables).
        // Dynamic buffer growth — unused in the managed port (strings grow
        // automatically); retained for API parity.
        public Clay_TI_ResizeFn? onResize;
        public object? resizeUserData;
        // Per-character filter; NULL → accept all.
        public Clay_TI_CharFilterFn? onCharFilter;
        public object? charFilterUserData;
        // Post-edit notification.
        public Clay_TI_ChangedFn? onChanged;
        public object? changedUserData;
    }

    // Retains all per-input mutable data across frames. A class (like
    // Clay_LayoutElement) because the C implementation takes it by pointer and
    // the focused state is tracked module-wide by reference.
    public sealed class Clay_TextInputState
    {
        // Buffer — a managed string; null marks the state as uninitialised.
        // Never cache this; edits reassign the whole string.
        public string? text;
        // Maximum UTF-16 chars; 0 = unlimited.
        public int bufferSize;
        // Current string length in UTF-16 code units.
        public int textLen => text?.Length ?? 0;

        // Editing.
        // UTF-16 code-unit offset of the insert point.
        public int cursorPos;
        // UTF-16 code-unit offset; -1 = no selection.
        public int selectionAnchor = -1;

        // Focus / blink.
        public bool focused;
        public double cursorBlinkTimer;
        public bool cursorVisible = true;

        // Internal.
        public bool _isDynamic;
        public Clay_ElementId _elementId;
        // Display scratch string (password-masked when enabled).
        public string _calcText = "";
        // Cached copy of the most recent Clay_TextInputConfig so OnChar / OnKey
        // can read the callbacks without extra arguments.
        public Clay_TextInputConfig _cfg;
        public ulong _lastClickFrame;
        public bool _lastClickWasDouble;
        public bool _dragSelecting;
        public int _dragAnchor;
    }

    // -----------------------------------------
    // FACADE ----------------------------------
    // -----------------------------------------

    public static class ClayTextInput
    {
        private static Clay_TextInput_Platform g_platform;
        private static bool g_cursorIsIbeam;
        private static Clay_TextInputState? g_focused;
        private static ulong g_frameCount;

        // ── Module lifecycle ──────────────────────────────────────────────

        public static void SetPlatform(Clay_TextInput_Platform platform)
        {
            g_platform = platform;
            g_focused = null;
            g_cursorIsIbeam = false;
        }

        public static void Update(double dt)
        {
            g_frameCount++;
            if (g_focused == null) return;
            g_focused.cursorBlinkTimer += dt;
            double period = g_focused._cfg.cursorBlinkPeriod > 0f
                            ? g_focused._cfg.cursorBlinkPeriod
                            : 0.53;
            while (g_focused.cursorBlinkTimer >= period)
            {
                g_focused.cursorBlinkTimer -= period;
                g_focused.cursorVisible = !g_focused.cursorVisible;
            }
        }

        // ── Event feed ────────────────────────────────────────────────────

        public static void OnChar(uint codepoint)
        {
            if (g_focused == null) return;
            var cfg = g_focused._cfg;
            if (cfg.onCharFilter != null && !cfg.onCharFilter(codepoint, cfg.charFilterUserData)) return;

            // Reject values that would not form a valid UTF-16 scalar.
            if (codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF)) return;

            var rune = new Rune((int)codepoint);
            Span<char> buf = stackalloc char[2];
            int n = rune.EncodeToUtf16(buf);
            Insert(g_focused, new string(buf.Slice(0, n)), n);

            g_focused.cursorBlinkTimer = 0.0;
            g_focused.cursorVisible = true;
        }

        public static void OnKey(Clay_TI_Key key, Clay_TI_Action action, Clay_TI_Mod mods)
        {
            if (g_focused == null) return;
            if (action != Clay_TI_Action.CLAY_TI_ACTION_PRESS && action != Clay_TI_Action.CLAY_TI_ACTION_REPEAT) return;

            var s = g_focused;
            bool ctrl = (mods & Clay_TI_Mod.CLAY_TI_MOD_CTRL) != 0;
            bool shift = (mods & Clay_TI_Mod.CLAY_TI_MOD_SHIFT) != 0;

            switch (key)
            {
                case Clay_TI_Key.CLAY_TI_KEY_LEFT:
                {
                    bool hadSelection = s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos;
                    int oldAnchor = s.selectionAnchor;
                    ShiftAnchor(s, shift);
                    if (!shift && hadSelection)
                    {
                        s.cursorPos = Math.Min(s.cursorPos, oldAnchor);
                        s.selectionAnchor = -1;
                    }
                    else
                    {
                        s.cursorPos = ctrl ? WordLeft(s.text ?? "", s.cursorPos)
                                           : Utf16Prev(s.text ?? "", s.cursorPos);
                    }
                    break;
                }
                case Clay_TI_Key.CLAY_TI_KEY_RIGHT:
                {
                    bool hadSelection = s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos;
                    int oldAnchor = s.selectionAnchor;
                    ShiftAnchor(s, shift);
                    if (!shift && hadSelection)
                    {
                        s.cursorPos = Math.Max(s.cursorPos, oldAnchor);
                        s.selectionAnchor = -1;
                    }
                    else
                    {
                        s.cursorPos = ctrl ? WordRight(s.text ?? "", s.cursorPos, s.textLen)
                                           : Utf16Next(s.text ?? "", s.cursorPos, s.textLen);
                    }
                    break;
                }
                case Clay_TI_Key.CLAY_TI_KEY_HOME:
                    ShiftAnchor(s, shift);
                    s.cursorPos = 0;
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_END:
                    ShiftAnchor(s, shift);
                    s.cursorPos = s.textLen;
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_BACKSPACE:
                {
                    int lo, hi;
                    if (s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos)
                    {
                        lo = Math.Min(s.cursorPos, s.selectionAnchor);
                        hi = Math.Max(s.cursorPos, s.selectionAnchor);
                        s.cursorPos = lo;
                    }
                    else if (s.cursorPos > 0)
                    {
                        lo = ctrl ? WordLeft(s.text ?? "", s.cursorPos) : Utf16Prev(s.text ?? "", s.cursorPos);
                        hi = s.cursorPos;
                        s.cursorPos = lo;
                    }
                    else break;
                    DeleteNotify(s, lo, hi);
                    s.selectionAnchor = -1;
                    break;
                }
                case Clay_TI_Key.CLAY_TI_KEY_DELETE:
                {
                    int lo, hi;
                    if (s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos)
                    {
                        lo = Math.Min(s.cursorPos, s.selectionAnchor);
                        hi = Math.Max(s.cursorPos, s.selectionAnchor);
                        s.cursorPos = lo;
                    }
                    else if (s.cursorPos < s.textLen)
                    {
                        lo = s.cursorPos;
                        hi = ctrl ? WordRight(s.text ?? "", s.cursorPos, s.textLen)
                                  : Utf16Next(s.text ?? "", s.cursorPos, s.textLen);
                    }
                    else break;
                    DeleteNotify(s, lo, hi);
                    s.selectionAnchor = -1;
                    break;
                }
                case Clay_TI_Key.CLAY_TI_KEY_A:
                    if (ctrl) { s.selectionAnchor = 0; s.cursorPos = s.textLen; }
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_C:
                    if (ctrl) CopySelection(s);
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_X:
                    if (ctrl)
                    {
                        CopySelection(s);
                        if (s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos)
                        {
                            int lo = Math.Min(s.cursorPos, s.selectionAnchor);
                            int hi = Math.Max(s.cursorPos, s.selectionAnchor);
                            DeleteNotify(s, lo, hi);
                            s.cursorPos = lo;
                        }
                    }
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_V:
                    if (ctrl && g_platform.getClipboardText != null)
                    {
                        string? clip = g_platform.getClipboardText(g_platform.userData);
                        if (clip != null) Insert(s, clip, clip.Length);
                    }
                    break;
                case Clay_TI_Key.CLAY_TI_KEY_ESCAPE:
                case Clay_TI_Key.CLAY_TI_KEY_ENTER:
                    Unfocus(s);
                    break;
            }

            s.cursorBlinkTimer = 0.0;
            s.cursorVisible = true;
        }

        // ── State initialisers ────────────────────────────────────────────

        public static Clay_TextInputState State_Static(Clay_ElementId elementId, int bufferSize)
        {
            return new Clay_TextInputState
            {
                text = "",
                bufferSize = bufferSize,
                cursorPos = 0,
                selectionAnchor = -1,
                cursorVisible = true,
                _elementId = elementId,
                _isDynamic = false,
                _calcText = "",
            };
        }

        public static bool State_InitDynamic(
            Clay_TextInputState state,
            int initialCapacity,
            Clay_ElementId elementId,
            Clay_TI_ResizeFn? resizeFn,
            object? resizeUserData)
        {
            state.text = "";
            state.bufferSize = 0;
            state.cursorPos = 0;
            state.selectionAnchor = -1;
            state.focused = false;
            state.cursorVisible = true;
            state._elementId = elementId;
            state._isDynamic = true;
            state._calcText = "";
            // initialCapacity / resizeFn are unused: managed strings grow automatically.
            return true;
        }

        public static void State_Destroy(Clay_TextInputState state)
        {
            state.text = null;
            state.bufferSize = 0;
            state.selectionAnchor = -1;
            state.focused = false;
            state._isDynamic = false;
            state._dragSelecting = false;
            if (ReferenceEquals(g_focused, state)) g_focused = null;
        }

        public static void State_Insert(Clay_TextInputState state, int at, string s)
        {
            if (at < 0) at = 0;
            else if (at > state.textLen) at = state.textLen;
            state.cursorPos = at;
            Insert(state, s, s.Length);
        }

        // ── Element DSL ───────────────────────────────────────────────────

        // CLAY_TEXT_INPUT(id, state, cfg) — auto-initialises an uninitialised state.
        public static void TextInput(Clay_ElementId id, Clay_TextInputState state, Clay_TextInputConfig cfg)
        {
            if (state.text == null)
            {
                state.text = "";
                state._elementId = id;
                state._isDynamic = true;
            }
            __Element(state, cfg);
        }

        // CLAY_TEXT_INPUT_WITH_STATE(state, cfg) — uses the state's stored element id.
        public static void TextInput(Clay_TextInputState state, Clay_TextInputConfig cfg) => __Element(state, cfg);

        // ── Internals ─────────────────────────────────────────────────────

        private static void __Element(Clay_TextInputState state, Clay_TextInputConfig cfg)
        {
            state._cfg = cfg;
            state.text ??= string.Empty;

            Clay_PointerData pointerData = Clay.GetPointerState();

            // Click → focus / unfocus.
            if (pointerData.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
            {
                if (Clay.PointerOver(state._elementId))
                {
                    Clay_ElementData elementData = Clay.GetElementData(state._elementId);
                    Focus(state);

                    bool isDoubleClick = state._lastClickFrame > 0
                                         && !state._lastClickWasDouble
                                         && (g_frameCount - state._lastClickFrame) <= 20;
                    if (isDoubleClick)
                    {
                        state.selectionAnchor = 0;
                        state.cursorPos = state.textLen;
                        state._dragSelecting = false;
                        state._lastClickWasDouble = true;
                    }
                    else
                    {
                        bool hasClickPos = false;
                        int clickPos = state.cursorPos;
                        if (elementData.found)
                        {
                            float clickX = pointerData.position.X - elementData.boundingBox.x - cfg.padding.left;
                            hasClickPos = ByteAtXIfInBounds(state, cfg, clickX, out clickPos);
                        }

                        if (hasClickPos) state.cursorPos = clickPos;
                        state.selectionAnchor = -1;
                        state._dragAnchor = state.cursorPos;
                        state._dragSelecting = hasClickPos;
                        state._lastClickWasDouble = false;
                    }

                    state._lastClickFrame = g_frameCount;
                    state.cursorBlinkTimer = 0.0;
                    state.cursorVisible = true;
                }
                else if (ReferenceEquals(g_focused, state))
                {
                    Unfocus(state);
                }
            }

            // Drag selection.
            if (state.focused && state._dragSelecting
                && pointerData.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED)
            {
                Clay_ElementData elementData = Clay.GetElementData(state._elementId);
                if (elementData.found)
                {
                    float dragX = pointerData.position.X - elementData.boundingBox.x - cfg.padding.left;
                    int dragPos = ByteAtX(state, cfg, dragX);
                    state.cursorPos = dragPos;
                    state.selectionAnchor = (dragPos == state._dragAnchor) ? -1 : state._dragAnchor;
                    state.cursorBlinkTimer = 0.0;
                    state.cursorVisible = true;
                }
            }

            if (pointerData.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_RELEASED_THIS_FRAME)
            {
                state._dragSelecting = false;
            }

            // I-beam cursor.
            if (g_platform.setIbeamCursor != null && !g_cursorIsIbeam && Clay.PointerOver(state._elementId))
            {
                g_cursorIsIbeam = true;
                g_platform.setIbeamCursor(g_platform.userData);
            }
            else if (g_platform.resetCursor != null && g_cursorIsIbeam && !Clay.PointerOver(state._elementId))
            {
                g_cursorIsIbeam = false;
                g_platform.resetCursor(g_platform.userData);
            }

            Clay_Color borderColor = state.focused ? cfg.colorBorderFocus : cfg.colorBorder;

            using (Clay.Element(state._elementId, new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig
                {
                    sizing = cfg.sizing,
                    padding = cfg.padding,
                    childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                },
                backgroundColor = cfg.colorBackground,
                cornerRadius = cfg.cornerRadius,
                floating = cfg.floating,
                border = new Clay_BorderElementConfig { color = borderColor, width = cfg.borderWidth },
            }))
            {
                const string testWidthChar = " ";
                float visualXBias = MeasureWidth(new StringSegment(testWidthChar), cfg) * 0.33f;
                float cursorH = cfg.textConfig.fontSize;
                const float cursorW = 2.0f;

                float textOffsetX = cfg.padding.left;
                float cursorX = 0f;
                float selectionX = 0f;
                float selectionW = 0f;
                bool hasSelection = false;

                if (state.focused)
                {
                    cursorX = MeasureTo(state, cfg, state.cursorPos);
                    if (state.selectionAnchor >= 0 && state.selectionAnchor != state.cursorPos)
                    {
                        int lo = Math.Min(state.cursorPos, state.selectionAnchor);
                        int hi = Math.Max(state.cursorPos, state.selectionAnchor);
                        float sx = MeasureTo(state, cfg, lo);
                        selectionX = sx;
                        selectionW = MeasureTo(state, cfg, hi) - sx;
                        hasSelection = true;
                    }
                }

                // Placeholder or text content.
                if (state.textLen == 0 && !string.IsNullOrEmpty(cfg.placeholder) && !state.focused)
                {
                    Clay_TextElementConfig placeholderConfig = cfg.textConfig;
                    placeholderConfig.textColor = cfg.colorPlaceholder;
                    Clay.Text(cfg.placeholder, placeholderConfig);
                }
                else
                {
                    Clay.Text(DisplayString(state, cfg), cfg.textConfig);
                }

                // Cursor / selection (focused only).
                if (state.focused)
                {
                    if (hasSelection)
                    {
                        using (Clay.AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(selectionW), height = Clay.SizingFixed(cursorH) } },
                            backgroundColor = cfg.colorSelection,
                            floating = new Clay_FloatingElementConfig
                            {
                                offset = new Vector2(textOffsetX + selectionX + visualXBias, 0),
                                attachPoints = new Clay_FloatingAttachPoints
                                {
                                    element = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER,
                                    parent = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER,
                                },
                                attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT,
                            },
                        })) { }
                    }

                    if (state.cursorVisible && !hasSelection)
                    {
                        using (Clay.AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingFixed(cursorW), height = Clay.SizingFixed(cursorH) } },
                            backgroundColor = cfg.colorCursor,
                            floating = new Clay_FloatingElementConfig
                            {
                                offset = new Vector2(textOffsetX + cursorX + 0.5f, 0),
                                attachPoints = new Clay_FloatingAttachPoints
                                {
                                    element = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER,
                                    parent = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER,
                                },
                                attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT,
                            },
                        })) { }
                    }
                }
            }
        }

        // ── Editing primitives ────────────────────────────────────────────

        private static void Insert(Clay_TextInputState s, string str, int strLen)
        {
            if (strLen > str.Length) strLen = str.Length;

            // Replace selection.
            if (s.selectionAnchor >= 0 && s.selectionAnchor != s.cursorPos)
            {
                int lo = Math.Min(s.cursorPos, s.selectionAnchor);
                int hi = Math.Max(s.cursorPos, s.selectionAnchor);
                DeleteRange(s, lo, hi);
                s.cursorPos = lo;
            }
            s.selectionAnchor = -1;

            // Trim to maxLength (codepoints).
            if (s._cfg.maxLength > 0)
            {
                int rem = s._cfg.maxLength - RuneCount(s.text ?? "", s.textLen);
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
            if (s.bufferSize > 0)
            {
                int rem = s.bufferSize - s.textLen;
                if (rem <= 0) return;
                if (strLen > rem) strLen = rem;
                // Never split a surrogate pair.
                if (strLen > 0 && strLen < str.Length && char.IsLowSurrogate(str[strLen])) strLen--;
                if (strLen <= 0) return;
            }

            var current = s.text ?? "";
            s.text = current.Substring(0, s.cursorPos) + str.Substring(0, strLen) + current.Substring(s.cursorPos);
            s.cursorPos += strLen;

            if (s._cfg.onChanged != null)
                s._cfg.onChanged(s.text, s.textLen, s._cfg.changedUserData);
        }

        private static void DeleteRange(Clay_TextInputState s, int lo, int hi)
        {
            var t = s.text ?? "";
            if (lo < 0) lo = 0;
            if (hi > t.Length) hi = t.Length;
            if (lo >= hi) return;

            s.text = t.Substring(0, lo) + t.Substring(hi);
            if (s.cursorPos > hi) s.cursorPos -= hi - lo;
            else if (s.cursorPos > lo) s.cursorPos = lo;
            s.selectionAnchor = -1;
        }

        private static void DeleteNotify(Clay_TextInputState s, int lo, int hi)
        {
            int before = s.textLen;
            DeleteRange(s, lo, hi);
            if (s.textLen != before && s._cfg.onChanged != null)
                s._cfg.onChanged(s.text!, s.textLen, s._cfg.changedUserData);
        }

        private static void CopySelection(Clay_TextInputState s)
        {
            if (g_platform.setClipboardText == null) return;
            if (s.selectionAnchor < 0 || s.selectionAnchor == s.cursorPos) return;
            int lo = Math.Min(s.cursorPos, s.selectionAnchor);
            int hi = Math.Max(s.cursorPos, s.selectionAnchor);
            g_platform.setClipboardText(g_platform.userData, s.text!.Substring(lo, hi - lo));
        }

        // ── Focus helpers ─────────────────────────────────────────────────

        private static void Focus(Clay_TextInputState s)
        {
            if (g_focused != null && !ReferenceEquals(g_focused, s))
            {
                g_focused.focused = false;
                g_focused.selectionAnchor = -1;
            }
            s.focused = true;
            s.cursorBlinkTimer = 0.0;
            s.cursorVisible = true;
            g_focused = s;
        }

        private static void Unfocus(Clay_TextInputState s)
        {
            s.focused = false;
            s.selectionAnchor = -1;
            s._dragSelecting = false;
            if (ReferenceEquals(g_focused, s)) g_focused = null;
        }

        private static void ShiftAnchor(Clay_TextInputState s, bool shift)
        {
            if (shift && s.selectionAnchor < 0) s.selectionAnchor = s.cursorPos;
            else if (!shift) s.selectionAnchor = -1;
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

        private static float MeasureWidth(StringSegment text, Clay_TextInputConfig cfg)
        {
            if (Clay.s_measureText == null)
            {
                Clay.GetCurrentContext()?.Error(
                    Clay_ErrorType.CLAY_ERROR_TYPE_TEXT_MEASUREMENT_FUNCTION_NOT_PROVIDED,
                    "Clay's MeasureText function is null. Call Clay.SetMeasureTextFunction() before using ClayTextInput.");
                return 0f;
            }
            return Clay.s_measureText(text, cfg.textConfig, Clay.GetCurrentContext()?.measureTextUserData).width;
        }

        private static string DisplayString(Clay_TextInputState s, Clay_TextInputConfig cfg)
        {
            var t = s.text ?? "";
            if (cfg.passwordMode)
            {
                s._calcText = new string('*', RuneCount(t, t.Length));
                return s._calcText;
            }
            s._calcText = t;
            return s._calcText;
        }

        private static float MeasureTo(Clay_TextInputState s, Clay_TextInputConfig cfg, int charPos)
        {
            var t = s.text ?? "";
            if (charPos < 0) charPos = 0;
            if (charPos > t.Length) charPos = t.Length;

            if (cfg.passwordMode)
            {
                int dispLen = RuneCount(t, charPos);
                return MeasureWidth(new StringSegment(new string('*', dispLen)), cfg);
            }
            return MeasureWidth(new StringSegment(t, 0, charPos), cfg);
        }

        private static int ByteAtX(Clay_TextInputState s, Clay_TextInputConfig cfg, float x)
        {
            var t = s.text ?? "";
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

        private static bool ByteAtXIfInBounds(Clay_TextInputState s, Clay_TextInputConfig cfg, float x, out int outCharPos)
        {
            var t = s.text ?? "";
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
}
