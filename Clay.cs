// VERSION: 0.14
// Clay (https://github.com/nicbarker/clay) — C# port.
//
// A managed, idiomatic C# port of clay.h (Clay v0.14) that keeps the public API
// faithful to the original C library. Differences from the C implementation:
//   * The arena allocator is replaced with managed arrays (no Clay_Arena /
//     Clay_MinMemorySize / Clay_CreateArenaWithCapacityAndMemory).
//   * Clay_String is replaced with `string` and Clay_StringSlice with
//     Microsoft.Extensions.Primitives.StringSegment.
//   * Clay_Vector2 is System.Numerics.Vector2.
//   * `void*` user data becomes `object?`.
//   * Hashing is built on System.HashCode (content based, stable within a run).
//   * The C macros (CLAY / CLAY_AUTO_ID / CLAY_TEXT / CLAY_ID / CLAY_SIZING_*)
//     are replaced by the static `Clay` facade: `using (Clay.Element(id, decl)) { }`,
//     `Clay.AutoId(decl)`, `Clay.Text(text, config)`, `Clay.Id("...")`, etc.
//   * The self-hosted debug inspector (Clay__RenderDebugView) lives in Clay.DebugView.cs.
//
// Licensed under the zlib/libpng license (see bottom of clay.h).

using System;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Primitives;

// Several fields exist only to mirror clay.h's state (e.g. debug view / external scroll fields that are
// written only by not-yet-ported or experimental code paths). They are intentionally left unassigned.
#pragma warning disable CS0649

namespace ClaySharp
{
    // -----------------------------------------
    // UTILITY STRUCTS -------------------------
    // -----------------------------------------

    public struct Clay_Dimensions(float width, float height)
    {
        public float width = width, height = height;
    }

    // Internally clay conventionally represents colors as 0-255, but interpretation is up to the renderer.
    public struct Clay_Color(float r, float g, float b, float a)
    {
        public float r = r, g = g, b = b, a = a;
    }

    public struct Clay_BoundingBox(float x, float y, float width, float height)
    {
        public float x = x, y = y, width = width, height = height;
    }

    // Primarily created via the Clay.Id() / Clay.Idi() / Clay.IdLocal() helpers.
    // Represents a hashed string ID used for identifying and finding specific clay UI elements, required
    // by functions such as Clay.PointerOver() and Clay.GetElementData().
    public struct Clay_ElementId
    {
        public uint id;       // The resulting hash generated from the other fields.
        public uint offset;   // A numerical offset applied after computing the hash from stringId.
        public uint baseId;   // A base hash value to start from, for example the parent element ID is used when calculating CLAY_ID_LOCAL().
        public string stringId; // The string id to hash.
    }

    // A sized array of Clay_ElementId (returned from Clay.GetPointerOverIds()).
    public readonly struct Clay_ElementIdArray : IReadOnlyList<Clay_ElementId>
    {
        internal readonly ClayArray<Clay_ElementId> items;

        internal Clay_ElementIdArray(ClayArray<Clay_ElementId> items)
        {
            this.items = items;
        }

        public int capacity => items.capacity;
        public int length => items.length;
        public Clay_ElementId[] internalArray => items.internalArray;
        public Clay_ElementId this[int index] => items.internalArray[index];
        
        public IEnumerator<Clay_ElementId> GetEnumerator() => new ClayArrayEnumerator<Clay_ElementId>(items);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IReadOnlyCollection<Clay_ElementId>.Count => items.length;
    }

    public struct ClayArrayEnumerator<T> : IEnumerator<T>
    {
        private int _index = -1;
        private readonly ClayArray<T> _array;

        internal ClayArrayEnumerator(ClayArray<T> array)
        {
            _array = array;
        }

        public bool MoveNext()
        {
            return ++_index < _array.length;
        }

        public void Reset()
        {
            _index = -1;
        }

        public T Current => _array.internalArray[_index];

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
        }
    }

    // Controls the "radius", or corner rounding of elements, including rectangles, borders and images.
    public struct Clay_CornerRadius
    {
        public float topLeft;
        public float topRight;
        public float bottomLeft;
        public float bottomRight;
    }

    // -----------------------------------------
    // ELEMENT CONFIGS -------------------------
    // -----------------------------------------

    public enum Clay_LayoutDirection
    {
        // (Default) Lays out child elements from left to right with increasing x.
        CLAY_LEFT_TO_RIGHT = 0,
        // Lays out child elements from top to bottom with increasing y.
        CLAY_TOP_TO_BOTTOM = 1,
    }

    public enum Clay_LayoutAlignmentX
    {
        // (Default) Aligns child elements to the left hand side of this element, offset by padding.left
        CLAY_ALIGN_X_LEFT = 0,
        // Aligns child elements to the right hand side of this element, offset by padding.right
        CLAY_ALIGN_X_RIGHT = 1,
        // Aligns child elements horizontally to the center of this element
        CLAY_ALIGN_X_CENTER = 2,
    }

    public enum Clay_LayoutAlignmentY
    {
        // (Default) Aligns child elements to the top of this element, offset by padding.top
        CLAY_ALIGN_Y_TOP = 0,
        // Aligns child elements to the bottom of this element, offset by padding.bottom
        CLAY_ALIGN_Y_BOTTOM = 1,
        // Aligns child elements vertically to the center of this element
        CLAY_ALIGN_Y_CENTER = 2,
    }

    // Controls how the element takes up space inside its parent container.
    public enum Clay__SizingType
    {
        // (default) Wraps tightly to the size of the element's contents.
        CLAY__SIZING_TYPE_FIT = 0,
        // Expands along this axis to fill available space in the parent element, sharing it with other GROW elements.
        CLAY__SIZING_TYPE_GROW = 1,
        // Expects 0-1 range. Clamps the axis size to a percent of the parent container's axis size minus padding and child gaps.
        CLAY__SIZING_TYPE_PERCENT = 2,
        // Clamps the axis size to an exact size in pixels.
        CLAY__SIZING_TYPE_FIXED = 3,
    }

    public struct Clay_ChildAlignment
    {
        public Clay_LayoutAlignmentX x; // Controls alignment of children along the x axis.
        public Clay_LayoutAlignmentY y; // Controls alignment of children along the y axis.
    }

    // Controls the minimum and maximum size in pixels that this element is allowed to grow or shrink to,
    // overriding sizing types such as FIT or GROW.
    public struct Clay_SizingMinMax
    {
        public float min; // The smallest final size of the element on this axis will be this value in pixels.
        public float max; // The largest final size of the element on this axis will be this value in pixels.
    }

    // Controls the sizing of this element along one axis inside its parent container.
    public struct Clay_SizingAxis
    {
        // The C code overlays Clay_SizingMinMax and `float percent` in a union. In C# both fields coexist,
        // tagged by `type` (only the field relevant to `type` is meaningful).
        public Clay_SizingMinMax minMax; // min/max size in pixels for FIT / GROW / FIXED sizing.
        public float percent;             // 0-1 range, only used by CLAY__SIZING_TYPE_PERCENT.
        public Clay__SizingType type;     // Controls how the element takes up space inside its parent container.
    }

    public struct Clay_Sizing
    {
        public Clay_SizingAxis width;  // Controls the width sizing of the element, along the x axis.
        public Clay_SizingAxis height; // Controls the height sizing of the element, along the y axis.
    }

    public struct Clay_Padding
    {
        public ushort left;
        public ushort right;
        public ushort top;
        public ushort bottom;
    }

    // Controls various settings that affect the size and position of an element, as well as the sizes and
    // positions of any child elements.
    public struct Clay_LayoutConfig
    {
        public Clay_Sizing sizing; // FIT / GROW / PERCENT / FIXED sizing inside the parent container.
        public Clay_Padding padding; // "padding" in pixels, a gap between this element's bounding box and its children.
        public ushort childGap; // The gap in pixels between child elements along the layout axis.
        public Clay_ChildAlignment childAlignment; // Controls how child elements are aligned on each axis.
        public Clay_LayoutDirection layoutDirection; // Controls the direction in which child elements are laid out.
    }

    // Controls how text "wraps", that is how it is broken into multiple lines when there is insufficient horizontal space.
    public enum Clay_TextElementConfigWrapMode
    {
        // (default) breaks on whitespace characters.
        CLAY_TEXT_WRAP_WORDS = 0,
        // Don't break on space characters, only on newlines.
        CLAY_TEXT_WRAP_NEWLINES = 1,
        // Disable text wrapping entirely.
        CLAY_TEXT_WRAP_NONE = 2,
    }

    // Controls how wrapped lines of text are horizontally aligned within the outer text bounding box.
    public enum Clay_TextAlignment
    {
        // (default) Horizontally aligns wrapped lines of text to the left hand side of their bounding box.
        CLAY_TEXT_ALIGN_LEFT = 0,
        // Horizontally aligns wrapped lines of text to the center of their bounding box.
        CLAY_TEXT_ALIGN_CENTER = 1,
        // Horizontally aligns wrapped lines of text to the right hand side of their bounding box.
        CLAY_TEXT_ALIGN_RIGHT = 2,
    }

    // Controls various functionality related to text elements.
    public struct Clay_TextElementConfig
    {
        public object? userData; // A pointer that will be transparently passed through to the resulting render command.
        public Clay_Color textColor; // The RGBA color of the font to render, conventionally specified as 0-255.
        public ushort fontId; // An integer transparently passed to the measure text function to identify the font to use.
        public ushort fontSize; // Controls the size of the font.
        public ushort letterSpacing; // Controls extra horizontal spacing between characters.
        public ushort lineHeight; // Controls additional vertical space between wrapped lines of text.
        public Clay_TextElementConfigWrapMode wrapMode; // How text wraps.
        public Clay_TextAlignment textAlignment; // How wrapped lines are horizontally aligned.
    }

    // Controls various settings related to aspect ratio scaling element.
    public struct Clay_AspectRatioElementConfig
    {
        public float aspectRatio; // The target "aspect ratio", final width divided by final height.
    }

    // Controls various settings related to image elements.
    public struct Clay_ImageElementConfig
    {
        public object? imageData; // A transparent object used to pass image data through to the renderer.
    }

    // Controls where a floating element is offset relative to its parent element.
    public enum Clay_FloatingAttachPointType
    {
        CLAY_ATTACH_POINT_LEFT_TOP = 0,
        CLAY_ATTACH_POINT_LEFT_CENTER = 1,
        CLAY_ATTACH_POINT_LEFT_BOTTOM = 2,
        CLAY_ATTACH_POINT_CENTER_TOP = 3,
        CLAY_ATTACH_POINT_CENTER_CENTER = 4,
        CLAY_ATTACH_POINT_CENTER_BOTTOM = 5,
        CLAY_ATTACH_POINT_RIGHT_TOP = 6,
        CLAY_ATTACH_POINT_RIGHT_CENTER = 7,
        CLAY_ATTACH_POINT_RIGHT_BOTTOM = 8,
    }

    // Controls where a floating element is offset relative to its parent element.
    public struct Clay_FloatingAttachPoints
    {
        public Clay_FloatingAttachPointType element; // The origin point on a floating element that attaches to its parent.
        public Clay_FloatingAttachPointType parent;  // The origin point on the parent element that the floating element attaches to.
    }

    // Controls how mouse pointer events like hover and click are captured or passed through to elements underneath.
    public enum Clay_PointerCaptureMode
    {
        // (default) "Capture" the pointer event and don't allow events like hover and click to pass through.
        CLAY_POINTER_CAPTURE_MODE_CAPTURE = 0,
        // Transparently pass through pointer events like hover and click to elements underneath the floating element.
        CLAY_POINTER_CAPTURE_MODE_PASSTHROUGH = 1,
    }

    // Controls which element a floating element is "attached" to (i.e. relative offset from).
    public enum Clay_FloatingAttachToElement
    {
        // (default) Disables floating for this element.
        CLAY_ATTACH_TO_NONE = 0,
        // Attaches this floating element to its parent.
        CLAY_ATTACH_TO_PARENT = 1,
        // Attaches this floating element to an element with a specific ID (.parentId).
        CLAY_ATTACH_TO_ELEMENT_WITH_ID = 2,
        // Attaches this floating element to the root of the layout.
        CLAY_ATTACH_TO_ROOT = 3,
    }

    // Controls whether or not a floating element is clipped to the same clipping rectangle as the element it's attached to.
    public enum Clay_FloatingClipToElement
    {
        // (default) - The floating element does not inherit clipping.
        CLAY_CLIP_TO_NONE = 0,
        // The floating element is clipped to the same clipping rectangle as the element it's attached to.
        CLAY_CLIP_TO_ATTACHED_PARENT = 1,
    }

    // Controls various settings related to "floating" elements.
    public struct Clay_FloatingElementConfig
    {
        public Vector2 offset; // Offsets this floating element by the provided x,y coordinates from its attachPoints.
        public Clay_Dimensions expand; // Expands the boundaries of the outer floating element without affecting its children.
        public uint parentId; // For CLAY_ATTACH_TO_ELEMENT_WITH_ID: the element to attach to.
        public short zIndex; // Controls the z index of this floating element and all its children.
        public Clay_FloatingAttachPoints attachPoints; // How pointer events are captured / passed through.
        public Clay_PointerCaptureMode pointerCaptureMode; // How pointer events are captured / passed through.
        public Clay_FloatingAttachToElement attachTo; // Which element this floating element is attached to.
        public Clay_FloatingClipToElement clipTo; // Whether this floating element inherits clipping.
    }

    // Controls various settings related to custom elements.
    public struct Clay_CustomElementConfig
    {
        public object? customData; // Transparent custom data passed through to the renderer (generates CUSTOM commands).
    }

    // Controls the axis on which an element switches to "scrolling", which clips the contents and allows scrolling.
    public struct Clay_ClipElementConfig
    {
        public bool horizontal; // Clip overflowing elements on the X axis.
        public bool vertical;   // Clip overflowing elements on the Y axis.
        public Vector2 childOffset; // Offsets the x,y positions of all child elements (used primarily for scrolling containers).
    }

    // Controls the widths of individual element borders.
    public struct Clay_BorderWidth
    {
        public ushort left;
        public ushort right;
        public ushort top;
        public ushort bottom;
        // Creates borders between each child element, depending on the layoutDirection.
        public ushort betweenChildren;
    }

    // Controls settings related to element borders.
    public struct Clay_BorderElementConfig
    {
        public Clay_Color color; // Controls the color of all borders with width > 0.
        public Clay_BorderWidth width; // Controls the widths of individual borders.
    }

    public struct Clay_TransitionData
    {
        public Clay_BoundingBox boundingBox;
        public Clay_Color backgroundColor;
        public Clay_Color overlayColor;
        public Clay_Color borderColor;
        public Clay_BorderWidth borderWidth;
    }

    public enum Clay_TransitionState
    {
        CLAY_TRANSITION_STATE_IDLE = 0,
        CLAY_TRANSITION_STATE_ENTERING = 1,
        CLAY_TRANSITION_STATE_TRANSITIONING = 2,
        CLAY_TRANSITION_STATE_EXITING = 3,
    }

    [Flags]
    public enum Clay_TransitionProperty
    {
        CLAY_TRANSITION_PROPERTY_NONE = 0,
        CLAY_TRANSITION_PROPERTY_X = 1,
        CLAY_TRANSITION_PROPERTY_Y = 2,
        CLAY_TRANSITION_PROPERTY_POSITION = CLAY_TRANSITION_PROPERTY_X | CLAY_TRANSITION_PROPERTY_Y,
        CLAY_TRANSITION_PROPERTY_WIDTH = 4,
        CLAY_TRANSITION_PROPERTY_HEIGHT = 8,
        CLAY_TRANSITION_PROPERTY_DIMENSIONS = CLAY_TRANSITION_PROPERTY_WIDTH | CLAY_TRANSITION_PROPERTY_HEIGHT,
        CLAY_TRANSITION_PROPERTY_BOUNDING_BOX = CLAY_TRANSITION_PROPERTY_POSITION | CLAY_TRANSITION_PROPERTY_DIMENSIONS,
        CLAY_TRANSITION_PROPERTY_BACKGROUND_COLOR = 16,
        CLAY_TRANSITION_PROPERTY_OVERLAY_COLOR = 32,
        CLAY_TRANSITION_PROPERTY_CORNER_RADIUS = 64,
        CLAY_TRANSITION_PROPERTY_BORDER_COLOR = 128,
        CLAY_TRANSITION_PROPERTY_BORDER_WIDTH = 256,
        CLAY_TRANSITION_PROPERTY_BORDER = CLAY_TRANSITION_PROPERTY_BORDER_COLOR | CLAY_TRANSITION_PROPERTY_BORDER_WIDTH,
    }

    public ref struct Clay_TransitionCallbackArguments
    {
        public Clay_TransitionState transitionState;
        public Clay_TransitionData initial;
        public ref Clay_TransitionData current; // Live mutable state — the handler writes interpolated values here.
        public Clay_TransitionData target;
        public float elapsedTime;
        public float duration;
        public Clay_TransitionProperty properties;
    }

    public enum Clay_TransitionEnterTriggerType
    {
        CLAY_TRANSITION_ENTER_SKIP_ON_FIRST_PARENT_FRAME = 0,
        CLAY_TRANSITION_ENTER_TRIGGER_ON_FIRST_PARENT_FRAME = 1,
    }

    public enum Clay_TransitionExitTriggerType
    {
        CLAY_TRANSITION_EXIT_SKIP_WHEN_PARENT_EXITS = 0,
        CLAY_TRANSITION_EXIT_TRIGGER_WHEN_PARENT_EXITS = 1,
    }

    public enum Clay_TransitionInteractionHandlingType
    {
        CLAY_TRANSITION_DISABLE_INTERACTIONS_WHILE_TRANSITIONING_POSITION = 0,
        CLAY_TRANSITION_ALLOW_INTERACTIONS_WHILE_TRANSITIONING_POSITION = 1,
    }

    public enum Clay_ExitTransitionSiblingOrdering
    {
        CLAY_EXIT_TRANSITION_ORDERING_UNDERNEATH_SIBLINGS = 0,
        CLAY_EXIT_TRANSITION_ORDERING_NATURAL_ORDER = 1,
        CLAY_EXIT_TRANSITION_ORDERING_ABOVE_SIBLINGS = 2,
    }

    public struct Clay_TransitionElementConfigEnter
    {
        public Clay_TransitionSetStateFunction? setInitialState;
        public Clay_TransitionEnterTriggerType trigger;
    }

    public struct Clay_TransitionElementConfigExit
    {
        public Clay_TransitionSetStateFunction? setFinalState;
        public Clay_TransitionExitTriggerType trigger;
        public Clay_ExitTransitionSiblingOrdering siblingOrdering;
    }

    // Controls settings related to transitions.
    public struct Clay_TransitionElementConfig
    {
        public Clay_TransitionHandler? handler;
        public float duration;
        public Clay_TransitionProperty properties;
        public Clay_TransitionInteractionHandlingType interactionHandling;
        public Clay_TransitionElementConfigEnter enter;
        public Clay_TransitionElementConfigExit exit;
    }

    // -----------------------------------------
    // RENDER COMMAND DATA ---------------------
    // -----------------------------------------

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_TEXT
    public struct Clay_TextRenderData
    {
        public StringSegment stringContents; // A string slice containing the text to be rendered.
        public Clay_Color textColor;
        public ushort fontId;
        public ushort fontSize;
        public ushort letterSpacing; // Extra whitespace gap in pixels between each character.
        public ushort lineHeight;    // The height of the bounding box for this line of text.
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_RECTANGLE
    public struct Clay_RectangleRenderData
    {
        public Clay_Color backgroundColor;
        public Clay_CornerRadius cornerRadius;
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_IMAGE
    public struct Clay_ImageRenderData
    {
        public Clay_Color backgroundColor;
        public Clay_CornerRadius cornerRadius;
        public object? imageData;
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_CUSTOM
    public struct Clay_CustomRenderData
    {
        public Clay_Color backgroundColor;
        public Clay_CornerRadius cornerRadius;
        public object? customData;
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_SCISSOR_START || SCISSOR_END
    public struct Clay_ClipRenderData
    {
        public bool horizontal;
        public bool vertical;
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_OVERLAY_COLOR_START || OVERLAY_COLOR_END
    public struct Clay_OverlayColorRenderData
    {
        public Clay_Color color;
    }

    // Render command data when commandType == CLAY_RENDER_COMMAND_TYPE_BORDER
    public struct Clay_BorderRenderData
    {
        public Clay_Color color;
        public Clay_CornerRadius cornerRadius;
        public Clay_BorderWidth width;
    }

    // The C library uses a union here. In C# this is a flat struct holding all render data variants;
    // only the member matching `Clay_RenderCommand.commandType` is meaningful.
    public struct Clay_RenderData
    {
        public Clay_RectangleRenderData rectangle;
        public Clay_TextRenderData text;
        public Clay_ImageRenderData image;
        public Clay_CustomRenderData custom;
        public Clay_BorderRenderData border;
        public Clay_ClipRenderData clip;
        public Clay_OverlayColorRenderData overlayColor;
    }

    // -----------------------------------------
    // MISCELLANEOUS STRUCTS & ENUMS -----------
    // -----------------------------------------

    // Data representing the current internal state of a scrolling element.
    public ref struct Clay_ScrollContainerData
    {
        private ref Clay__ScrollContainerDataInternal internalData;

        public Vector2 scrollPosition
        {
            get
            {
                if (Unsafe.IsNullRef(in internalData)) return default;
                return internalData.scrollPosition;
            }
            set
            {
                if (Unsafe.IsNullRef(in internalData)) return;
                internalData.scrollPosition = value;
            }
        }

        public Clay_Dimensions scrollContainerDimensions; // The bounding box of the scroll element.
        public Clay_Dimensions contentDimensions; // The outer dimensions of the inner scroll container content.
        public Clay_ClipElementConfig config; // The config that was originally passed to the clip element.
        public bool found; // Indicates whether an actual scroll container matched the provided ID.

        internal static Clay_ScrollContainerData Create(ref Clay__ScrollContainerDataInternal internalData)
        {
            return new Clay_ScrollContainerData
            {
                internalData = ref internalData,
                scrollContainerDimensions = new Clay_Dimensions(internalData.boundingBox.width, internalData.boundingBox.height),
                contentDimensions = internalData.contentSize,
                config = internalData.layoutElement.config.clip,
                found = true,
            };
        }
    }

    // Bounding box and other data for a specific UI element.
    public struct Clay_ElementData
    {
        public Clay_BoundingBox boundingBox; // The rectangle that encloses this UI element, relative to the layout root.
        public bool found; // Indicates whether an actual element matched the provided ID.
    }

    // Used by renderers to determine specific handling for each render command.
    public enum Clay_RenderCommandType
    {
        CLAY_RENDER_COMMAND_TYPE_NONE = 0,
        CLAY_RENDER_COMMAND_TYPE_RECTANGLE = 1,
        CLAY_RENDER_COMMAND_TYPE_BORDER = 2,
        CLAY_RENDER_COMMAND_TYPE_TEXT = 3,
        CLAY_RENDER_COMMAND_TYPE_IMAGE = 4,
        CLAY_RENDER_COMMAND_TYPE_SCISSOR_START = 5,
        CLAY_RENDER_COMMAND_TYPE_SCISSOR_END = 6,
        CLAY_RENDER_COMMAND_TYPE_OVERLAY_COLOR_START = 7,
        CLAY_RENDER_COMMAND_TYPE_OVERLAY_COLOR_END = 8,
        CLAY_RENDER_COMMAND_TYPE_CUSTOM = 9,
    }

    public struct Clay_RenderCommand
    {
        public Clay_BoundingBox boundingBox; // A rectangular box that fully encloses this UI element.
        public Clay_RenderData renderData; // Data specific to this command's commandType.
        public object? userData; // Transparently passed through from the original element declaration.
        public uint id; // The id of this element, transparently passed through from the original element declaration.
        public short zIndex; // The z order required for drawing this command correctly.
        public Clay_RenderCommandType commandType; // Specifies how to handle rendering of this command.
    }

    // A sized array of render commands (returned from Clay.EndLayout()).
    public struct Clay_RenderCommandArray : IReadOnlyList<Clay_RenderCommand>
    {
        internal ClayArray<Clay_RenderCommand> items;

        internal Clay_RenderCommandArray(ClayArray<Clay_RenderCommand> items)
        {
            this.items = items;
        }

        public int capacity => items.capacity;
        public int length => items.length;
        public Clay_RenderCommand[] internalArray => items.internalArray;
        public Clay_RenderCommand this[int index] => items.internalArray[index];

        // Bounds-checked accessor equivalent to the C Clay_RenderCommandArray_Get.
        public ref Clay_RenderCommand Get(int index) => ref items.Get(index);
        
        public IEnumerator<Clay_RenderCommand> GetEnumerator() => new ClayArrayEnumerator<Clay_RenderCommand>(items);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IReadOnlyCollection<Clay_RenderCommand>.Count => items.length;
    }

    // Represents the current state of interaction with clay this frame.
    public enum Clay_PointerDataInteractionState
    {
        // A left mouse click, or touch occurred this frame.
        CLAY_POINTER_DATA_PRESSED_THIS_FRAME = 0,
        // The left mouse button click or touch happened in the past, and is still held down this frame.
        CLAY_POINTER_DATA_PRESSED = 1,
        // The left mouse button click or touch was released this frame.
        CLAY_POINTER_DATA_RELEASED_THIS_FRAME = 2,
        // The left mouse button click or touch is not currently down / was released in the past.
        CLAY_POINTER_DATA_RELEASED = 3,
    }

    // Information on the current state of pointer interactions this frame.
    public struct Clay_PointerData
    {
        public Vector2 position; // The position of the mouse / touch / pointer relative to the root of the layout.
        public Clay_PointerDataInteractionState state; // The current state of interaction with clay this frame.
    }

    public struct Clay_ElementDeclaration
    {
        public Clay_LayoutConfig layout; // Controls the size and position of an element and its children.
        public Clay_Color backgroundColor; // Background color; generates a RECTANGLE render command (or is passed to IMAGE/CUSTOM).
        public Clay_Color overlayColor; // "Color Overlay" applied to this element and all its children.
        public Clay_CornerRadius cornerRadius; // Corner rounding of rectangles, borders and images.
        public Clay_AspectRatioElementConfig aspectRatio; // Aspect ratio scaling.
        public Clay_ImageElementConfig image; // Image element settings.
        public Clay_FloatingElementConfig floating; // Floating / absolute positioning settings.
        public Clay_CustomElementConfig custom; // CUSTOM render command settings.
        public Clay_ClipElementConfig clip; // Clip / scroll settings.
        public Clay_BorderElementConfig border; // Border settings.
        public Clay_TransitionElementConfig transition; // Transition settings.
        public object? userData; // Transparently passed through to resulting render commands.
    }

    // Represents the type of error clay encountered while computing layout.
    public enum Clay_ErrorType
    {
        CLAY_ERROR_TYPE_TEXT_MEASUREMENT_FUNCTION_NOT_PROVIDED = 0,
        CLAY_ERROR_TYPE_ARENA_CAPACITY_EXCEEDED = 1,
        CLAY_ERROR_TYPE_ELEMENTS_CAPACITY_EXCEEDED = 2,
        CLAY_ERROR_TYPE_TEXT_MEASUREMENT_CAPACITY_EXCEEDED = 3,
        CLAY_ERROR_TYPE_DUPLICATE_ID = 4,
        CLAY_ERROR_TYPE_FLOATING_CONTAINER_PARENT_NOT_FOUND = 5,
        CLAY_ERROR_TYPE_PERCENTAGE_OVER_1 = 6,
        CLAY_ERROR_TYPE_INTERNAL_ERROR = 7,
        CLAY_ERROR_TYPE_UNBALANCED_OPEN_CLOSE = 8,
        CLAY_ERROR_TYPE_HASH_MAP_CAPACITY_EXCEEDED = 9,
    }

    // Data to identify the error that clay has encountered.
    public struct Clay_ErrorData
    {
        public Clay_ErrorType errorType; // The type of error encountered.
        public string errorText; // Human-readable error text.
        public object? userData; // Transparently passed through from the error handler.
    }

    // A wrapper struct around Clay's error handler function.
    public struct Clay_ErrorHandler
    {
        public Clay_ErrorHandlerFunction? errorHandlerFunction; // A user provided function called when Clay encounters an error.
        public object? userData; // Transparently passed through to the error handler when it is called.
    }

    // -----------------------------------------
    // CALLBACK DELEGATES ----------------------
    // -----------------------------------------

    public delegate Clay_Dimensions Clay_MeasureTextFunction(StringSegment text, Clay_TextElementConfig config, object? userData);
    public delegate void Clay_ErrorHandlerFunction(Clay_ErrorData errorData);
    public delegate void Clay_OnHoverFunction(Clay_ElementId elementId, Clay_PointerData pointerData, object? userData);
    public delegate Vector2 Clay_QueryScrollOffsetFunction(uint elementId, object? userData);
    public delegate bool Clay_TransitionHandler(Clay_TransitionCallbackArguments arguments);
    public delegate Clay_TransitionData Clay_TransitionSetStateFunction(Clay_TransitionData state, Clay_TransitionProperty properties);

    // -----------------------------------------
    // INTERNAL TYPES --------------------------
    // -----------------------------------------

    // One-shot "already warned" flags per error class.
    internal struct Clay_BooleanWarnings
    {
        public bool maxElementsExceeded;
        public bool maxRenderCommandsExceeded;
        public bool maxTextMeasureCacheExceeded;
        public bool textMeasurementFunctionNotSet;
        public bool hashMapCapacityExceeded;
    }

    // A single warning entry for the debug view's warnings pane. In Clay v0.14 nothing ever adds
    // warnings, so this array stays empty; kept for parity with the C context layout.
    internal struct Clay__Warning
    {
        public string baseMessage;
        public string dynamicMessage;
    }

    // A single wrapped line of a text element.
    internal struct Clay__WrappedTextLine
    {
        public Clay_Dimensions dimensions;
        public StringSegment line; // A slice of the source text (Buffer = full text, Offset = start, Length = line length).
    }

    // Non-owning view over a region of a shared array. Mirrors the C `Array##Slice` structs.
    internal struct ClayArraySlice<T>
    {
        public int length;
        public T[] internalArray;
        public int offset;

        private static T s_default = default!;

        public ref T Get(int index)
        {
            if (Clay.__Array_RangeCheck(index, length)) return ref internalArray[offset + index];
            return ref s_default;
        }
    }

    // Layout element data for text elements (the "other half" of Clay_LayoutElement's C union).
    internal struct Clay__TextElementData
    {
        public string text;
        public Clay_Dimensions preferredDimensions;
        public ClayArraySlice<Clay__WrappedTextLine> wrappedLines;
    }

    // In C this holds an `int32_t *elements` pointer into Clay_Context.layoutElementChildren.
    internal struct Clay__LayoutElementChildren
    {
        public Clay_LayoutElement[] elements; // The shared layoutElementChildren backing array (element references, not indices).
        public int offset;     // Start offset within that array.
        public ushort length;  // Number of children.
    }

    // Mutable reference type (the C implementation takes it by pointer everywhere).
    internal sealed class Clay_LayoutElement
    {
        public Clay__LayoutElementChildren children;
        public Clay_Dimensions dimensions;
        public Clay_Dimensions minDimensions;

        // The C union of `Clay_ElementDeclaration config` vs `{ textConfig, textElementData }` becomes two
        // coexisting fields, gated by `isTextElement`.
        public Clay_ElementDeclaration config;
        public Clay_TextElementConfig textConfig;
        public Clay__TextElementData textElementData;

        public uint id;
        public ushort floatingChildrenCount;
        public bool isTextElement;
        public bool exiting; // True if the element is in an exit transition ("synthetic" element).

        // Index of this element in Clay_Context.layoutElements — replaces C pointer arithmetic
        // (`element - context->layoutElements.internalArray`).
        public int index;

        // Shallow clone: copies value-type fields and shares reference fields (children array, text string),
        // matching C's bitwise struct copy semantics for cloned exiting subtrees.
        internal Clay_LayoutElement Clone() => (Clay_LayoutElement)MemberwiseClone();
    }

    // Internal state of a scrolling container.
    internal struct Clay__ScrollContainerDataInternal
    {
        public Clay_LayoutElement layoutElement;
        public Clay_BoundingBox boundingBox;
        public Clay_Dimensions contentSize;
        public Vector2 scrollOrigin;
        public Vector2 pointerOrigin;
        public Vector2 scrollMomentum;
        public Vector2 scrollPosition;
        public Vector2 previousDelta;
        public float momentumTime;
        public uint elementId;
        public bool openThisFrame;
        public bool pointerScrollActive;
    }

    // Internal state of a transition element.
    internal struct Clay__TransitionDataInternal
    {
        public Clay_TransitionData initialState;
        public Clay_TransitionData currentState;
        public Clay_TransitionData targetState;
        public Clay_LayoutElement elementThisFrame;
        public Vector2 oldParentRelativePosition;
        public uint elementId;
        public uint parentId;
        public uint siblingIndex;
        public float elapsedTime;
        public Clay_TransitionState state;
        public bool transitionOut;
        public bool reparented;
        public Clay_TransitionProperty activeProperties;
    }

    // Hash map item for element ID -> element lookups.
    internal struct Clay_LayoutElementHashMapItem
    {
        public Clay_BoundingBox boundingBox;
        public Clay_ElementId elementId;
        public Clay_LayoutElement layoutElement;
        public int layoutElementIndex; // Index into Clay_Context.layoutElements (replaces C pointer arithmetic).
        public Clay_OnHoverFunction? onHoverFunction;
        public object? hoverFunctionUserData;
        public int nextIndex;
        public uint generation;
        public bool appearedThisFrame;
        public Clay__DebugData debugData;

        internal struct Clay__DebugData
        {
            public bool collision;
            public bool collapsed;
        }
    }

    // A measured "word" in the text measurement cache, linked via `next`.
    internal struct Clay__MeasuredWord
    {
        public int startOffset;
        public int length;
        public float width;
        public int next;
    }

    // Hash map item for the text measurement cache.
    internal struct Clay__MeasureTextCacheItem
    {
        public Clay_Dimensions unwrappedDimensions;
        public int measuredWordsStartIndex;
        public float minWidth;
        public bool containsNewlines;
        public uint id;
        public int nextIndex;
        public uint generation;
    }

    // A node used by the DFS layout passes.
    internal struct Clay__LayoutElementTreeNode
    {
        public Clay_LayoutElement layoutElement;
        public Vector2 position;
        public Vector2 nextChildOffset;
        public bool parentMovedThisFramed; // Used to relativise transitions.
    }

    // The root of a layout tree (the main tree plus each floating subtree).
    internal struct Clay__LayoutElementTreeRoot
    {
        public int layoutElementIndex;
        public uint parentId; // 0 in the case of the root layout tree.
        public uint clipElementId; // 0 if there is no clip element.
        public short zIndex;
        public Vector2 pointerOffset; // Only used when scroll containers are managed externally.
    }

    // The entire per-context state, mirroring the C `struct Clay_Context`.
    // A class (mutable reference) because it is frequently taken as a reference.
    public sealed class Clay_Context
    {
        internal int maxElementCount;
        internal int maxMeasureTextCacheWordCount;
        internal int exitingElementsLength;
        internal int exitingElementsChildrenLength;
        internal bool rootResizedLastFrame;
        internal Clay_ErrorHandler errorHandler;
        internal Clay_BooleanWarnings booleanWarnings;

        internal Clay_PointerData pointerInfo;
        internal Clay_Dimensions layoutDimensions;
        internal Clay_ElementId dynamicElementIndexBaseHash;
        internal uint dynamicElementIndex;
        internal bool debugModeEnabled;
        internal bool disableCulling;
        internal bool externalScrollHandlingEnabled;
        internal bool warningsEnabled;
        internal uint debugSelectedElementId;
        internal uint generation;
        internal object? measureTextUserData;
        internal object? queryScrollOffsetUserData;

        // Layout Elements / Render Commands
        internal ClayArray<Clay_LayoutElement> layoutElements;
        internal ClayArray<Clay_RenderCommand> renderCommands;
        internal ClayArray<int> openLayoutElementStack;
        internal ClayArray<Clay_LayoutElement> layoutElementChildren;
        internal ClayArray<int> layoutElementChildrenBuffer;
        internal ClayArray<int> reusableElementIndexBuffer;
        internal ClayArray<int> layoutElementClipElementIds;

        // Misc Data Structures
        internal ClayArray<Clay__WrappedTextLine> wrappedTextLines;
        internal ClayArray<Clay__LayoutElementTreeNode> layoutElementTreeNodeArray1;
        internal ClayArray<Clay__LayoutElementTreeRoot> layoutElementTreeRoots;
        internal ClayArray<Clay_LayoutElementHashMapItem> layoutElementsHashMapInternal;
        internal ClayArray<int> layoutElementsHashMap;
        internal ClayArray<int> layoutElementsHashMapFreeList;
        internal ClayArray<Clay__MeasureTextCacheItem> measureTextHashMapInternal;
        internal ClayArray<int> measureTextHashMapInternalFreeList;
        internal ClayArray<int> measureTextHashMap;
        internal ClayArray<Clay__MeasuredWord> measuredWords;
        internal ClayArray<int> measuredWordsFreeList;
        internal ClayArray<int> openClipElementStack;
        internal ClayArray<Clay_ElementId> pointerOverIds;
        internal ClayArray<Clay__ScrollContainerDataInternal> scrollContainerDatas;
        internal ClayArray<Clay__TransitionDataInternal> transitionDatas;
        internal ClayArray<bool> treeNodeVisited;
        internal ClayArray<Clay__Warning> warnings;

        // Reports an error through the configured error handler (mirrors the C `context->errorHandler.errorHandlerFunction(...)` calls).
        internal void Error(Clay_ErrorType errorType, string errorText)
        {
            errorHandler.errorHandlerFunction?.Invoke(new Clay_ErrorData
            {
                errorType = errorType,
                errorText = errorText,
                userData = errorHandler.userData,
            });
        }

        // Persistent memory — initialized once and not reset between frames.
        internal void InitializePersistentMemory()
        {
            scrollContainerDatas = new ClayArray<Clay__ScrollContainerDataInternal>(100);
            transitionDatas = new ClayArray<Clay__TransitionDataInternal>(200);
            layoutElementsHashMapInternal = new ClayArray<Clay_LayoutElementHashMapItem>(maxElementCount);
            layoutElementsHashMap = new ClayArray<int>(maxElementCount);
            layoutElementsHashMapFreeList = new ClayArray<int>(maxElementCount);
            measureTextHashMapInternal = new ClayArray<Clay__MeasureTextCacheItem>(maxElementCount);
            measureTextHashMapInternalFreeList = new ClayArray<int>(maxElementCount);
            measuredWordsFreeList = new ClayArray<int>(maxMeasureTextCacheWordCount);
            measureTextHashMap = new ClayArray<int>(maxElementCount);
            measuredWords = new ClayArray<Clay__MeasuredWord>(maxMeasureTextCacheWordCount);
            pointerOverIds = new ClayArray<Clay_ElementId>(maxElementCount);
        }

        // Ephemeral memory — reset every frame.
        internal void InitializeEphemeralMemory()
        {
            layoutElementChildrenBuffer = new ClayArray<int>(maxElementCount);
            layoutElements = new ClayArray<Clay_LayoutElement>(maxElementCount);
            wrappedTextLines = new ClayArray<Clay__WrappedTextLine>(maxElementCount);
            layoutElementTreeNodeArray1 = new ClayArray<Clay__LayoutElementTreeNode>(maxElementCount);
            layoutElementTreeRoots = new ClayArray<Clay__LayoutElementTreeRoot>(maxElementCount);
            layoutElementChildren = new ClayArray<Clay_LayoutElement>(maxElementCount);
            openLayoutElementStack = new ClayArray<int>(maxElementCount);
            renderCommands = new ClayArray<Clay_RenderCommand>(maxElementCount);
            treeNodeVisited = new ClayArray<bool>(maxElementCount);
            treeNodeVisited.length = treeNodeVisited.capacity; // Accessed directly rather than behaving as a list.
            openClipElementStack = new ClayArray<int>(maxElementCount);
            reusableElementIndexBuffer = new ClayArray<int>(maxElementCount);
            layoutElementClipElementIds = new ClayArray<int>(maxElementCount);
            warnings = new ClayArray<Clay__Warning>(100);
        }
    }

    // Generic fixed-capacity array, a managed replacement for the C `CLAY__ARRAY_DEFINE` macro families.
    // `ref` returns replace the C `&array->internalArray[i]` pointer returns.
    internal struct ClayArray<T>
    {
        public int capacity;
        public int length;
        public T[] internalArray;

        private static T s_default = default!;

        public ClayArray(int capacity)
        {
            this.capacity = capacity;
            this.length = 0;
            this.internalArray = new T[capacity];
        }

        public ref T Get(int index)
        {
            if (Clay.__Array_RangeCheck(index, length)) return ref internalArray[index];
            return ref s_default;
        }

        public T GetValue(int index)
        {
            if (Clay.__Array_RangeCheck(index, length)) return internalArray[index];
            return default!;
        }

        public ref T GetCheckCapacity(int index)
        {
            if (Clay.__Array_RangeCheck(index, capacity)) return ref internalArray[index];
            return ref s_default;
        }

        public ref T Add(T item)
        {
            if (Clay.__Array_AddCapacityCheck(length, capacity))
            {
                internalArray[length++] = item;
                return ref internalArray[length - 1];
            }
            return ref s_default;
        }

        public T RemoveSwapback(int index)
        {
            if (Clay.__Array_RangeCheck(index, length))
            {
                length--;
                T removed = internalArray[index];
                internalArray[index] = internalArray[length];
                return removed;
            }
            return default!;
        }

        public ref T Set(int index, T value)
        {
            if (Clay.__Array_RangeCheck(index, capacity))
            {
                internalArray[index] = value;
                length = index < length ? length : index + 1;
                return ref internalArray[index];
            }
            return ref s_default;
        }

        public ref T Set_DontTouchLength(int index, T value)
        {
            if (Clay.__Array_RangeCheck(index, capacity))
            {
                internalArray[index] = value;
                return ref internalArray[index];
            }
            return ref s_default;
        }
    }

    // -----------------------------------------
    // ENGINE — the static facade + internals ----
    // -----------------------------------------

    public static partial class Clay
    {
        private const float CLAY__MAXFLOAT = 3.40282346638528859812e+38f;
        private const float CLAY__EPSILON = 0.01f;

        internal static Clay_Context? s_currentContext;
        internal static int s_defaultMaxElementCount = 8192;
        internal static int s_defaultMaxMeasureTextWordCacheCount = 16384;

        // Default layout config (matches the C `extern Clay_LayoutConfig CLAY_LAYOUT_DEFAULT`).
        public static readonly Clay_LayoutConfig CLAY_LAYOUT_DEFAULT = default;

        // Debug view globals (the inspector itself lives in Clay.DebugView.cs).
        public static uint __debugViewWidth = 400;
        public static Clay_Color __debugViewHighlightColor = new Clay_Color(168, 66, 28, 100);

        // Function-pointer globals (mirrors the C `Clay__MeasureText` / `Clay__QueryScrollOffset`).
        internal static Clay_MeasureTextFunction? s_measureText;
        internal static Clay_QueryScrollOffsetFunction? s_queryScrollOffset;

        public static Clay_Context? GetCurrentContext() => s_currentContext;
        public static void SetCurrentContext(Clay_Context? context) => s_currentContext = context;

        // -------------------------------------
        // Error helpers ------------------------
        // -------------------------------------

        internal static bool __Array_RangeCheck(int index, int length)
        {
            if (index < length && index >= 0) return true;
            GetCurrentContext()?.Error(Clay_ErrorType.CLAY_ERROR_TYPE_INTERNAL_ERROR,
                "Clay attempted to make an out of bounds array access. This is an internal error and is likely a bug.");
            return false;
        }

        internal static bool __Array_AddCapacityCheck(int length, int capacity)
        {
            if (length < capacity) return true;
            GetCurrentContext()?.Error(Clay_ErrorType.CLAY_ERROR_TYPE_INTERNAL_ERROR,
                "Clay attempted to make an out of bounds array access. This is an internal error and is likely a bug.");
            return false;
        }

        // -------------------------------------
        // Hashing ------------------------------
        // -------------------------------------

        internal static Clay_ElementId __HashNumber(uint offset, uint seed)
        {
            var hash = new HashCode();
            hash.Add(seed);
            hash.Add(offset + 48);
            uint id = unchecked((uint)hash.ToHashCode());
            return new Clay_ElementId { id = id + 1, offset = offset, baseId = seed, stringId = string.Empty }; // Reserve the hash result of zero as "null id".
        }

        internal static Clay_ElementId __HashString(string key, uint seed)
        {
            var hash = new HashCode();
            hash.Add(seed);
            hash.Add(key);
            uint id = unchecked((uint)hash.ToHashCode());
            return new Clay_ElementId { id = id + 1, offset = 0, baseId = id + 1, stringId = key }; // Reserve the hash result of zero as "null id".
        }

        internal static Clay_ElementId __HashStringWithOffset(string key, uint offset, uint seed)
        {
            var baseHash = new HashCode();
            baseHash.Add(seed);
            baseHash.Add(key);
            uint baseId = unchecked((uint)baseHash.ToHashCode());

            var hash = new HashCode();
            hash.Add(baseId);
            hash.Add(offset);
            uint id = unchecked((uint)hash.ToHashCode());

            return new Clay_ElementId { id = id + 1, offset = offset, baseId = baseId + 1, stringId = key }; // Reserve the hash result of zero as "null id".
        }

        internal static uint __HashStringContentsWithConfig(string text, Clay_TextElementConfig config)
        {
            var hash = new HashCode();
            hash.Add(text);
            hash.Add(config.fontId);
            hash.Add(config.fontSize);
            hash.Add(config.letterSpacing);
            return unchecked((uint)hash.ToHashCode()) + 1; // Reserve the hash result of zero as "null id".
        }

        // -------------------------------------
        // Element access helpers ---------------
        // -------------------------------------

        internal static Clay_LayoutElement __GetOpenLayoutElement()
        {
            var context = GetCurrentContext()!;
            return context.layoutElements.internalArray[context.openLayoutElementStack.internalArray[context.openLayoutElementStack.length - 1]];
        }

        internal static Clay_LayoutElement __GetParentElement()
        {
            var context = GetCurrentContext()!;
            return context.layoutElements.internalArray[context.openLayoutElementStack.GetValue(context.openLayoutElementStack.length - 2)];
        }

        internal static uint __GetParentElementId() => __GetParentElement().id;

        internal static bool __BorderHasAnyWidth(in Clay_BorderElementConfig borderConfig)
        {
            return borderConfig.width.betweenChildren > 0 || borderConfig.width.left > 0 || borderConfig.width.right > 0
                || borderConfig.width.top > 0 || borderConfig.width.bottom > 0;
        }

        internal static void __UpdateAspectRatioBox(Clay_LayoutElement layoutElement)
        {
            if (layoutElement.config.aspectRatio.aspectRatio != 0)
            {
                if (layoutElement.dimensions.width == 0 && layoutElement.dimensions.height != 0)
                {
                    layoutElement.dimensions.width = layoutElement.dimensions.height * layoutElement.config.aspectRatio.aspectRatio;
                }
                else if (layoutElement.dimensions.width != 0 && layoutElement.dimensions.height == 0)
                {
                    layoutElement.dimensions.height = layoutElement.dimensions.width * (1 / layoutElement.config.aspectRatio.aspectRatio);
                }
            }
        }

        internal static bool __PointIsInsideRect(Vector2 point, Clay_BoundingBox rect)
        {
            return point.X >= rect.x && point.X <= rect.x + rect.width && point.Y >= rect.y && point.Y <= rect.y + rect.height;
        }

        internal static bool __FloatEqual(float left, float right)
        {
            float subtracted = left - right;
            return subtracted < CLAY__EPSILON && subtracted > -CLAY__EPSILON;
        }

        // Equality helpers replacing the C Clay__MemCmp usage in the non-debug engine.
        internal static bool __ColorEqual(in Clay_Color a, in Clay_Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        internal static bool __BorderWidthEqual(in Clay_BorderWidth a, in Clay_BorderWidth b)
            => a.left == b.left && a.right == b.right && a.top == b.top && a.bottom == b.bottom && a.betweenChildren == b.betweenChildren;

        // -------------------------------------
        // Element ID hash map ------------------
        // -------------------------------------

        internal static ref Clay_LayoutElementHashMapItem __AddHashMapItem(Clay_ElementId elementId, Clay_LayoutElement layoutElement, int layoutElementIndex)
        {
            var context = GetCurrentContext()!;
            if (context.layoutElementsHashMapInternal.length == context.layoutElementsHashMapInternal.capacity - 1)
            {
                if (!context.booleanWarnings.hashMapCapacityExceeded)
                {
                    context.booleanWarnings.hashMapCapacityExceeded = true;
                    context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_HASH_MAP_CAPACITY_EXCEEDED,
                        "Clay has run out of space in it's internal element ID hashmap.  Try using Clay_SetMaxElementCount() with a higher value.");
                }
                return ref Unsafe.NullRef<Clay_LayoutElementHashMapItem>();
            }

            var item = new Clay_LayoutElementHashMapItem
            {
                elementId = elementId,
                layoutElement = layoutElement,
                layoutElementIndex = layoutElementIndex,
                nextIndex = -1,
                generation = context.generation + 1,
                appearedThisFrame = true,
            };

            int hashBucket = (int)(elementId.id % (uint)context.layoutElementsHashMap.capacity);
            int hashItemPrevious = -1;
            int hashItemIndex = context.layoutElementsHashMap.internalArray[hashBucket];
            while (hashItemIndex != -1) // Just replace collision, not a big deal - leave it up to the end user.
            {
                ref var hashItem = ref context.layoutElementsHashMapInternal.internalArray[hashItemIndex];
                if (hashItem.elementId.id == elementId.id) // Collision - resolve based on generation.
                {
                    item.nextIndex = hashItem.nextIndex;
                    if (hashItem.generation <= context.generation) // First collision - assume this is the "same" element.
                    {
                        hashItem.appearedThisFrame = hashItem.generation < context.generation;
                        hashItem.elementId = elementId; // If the stringId reference has changed, update the hash item to use the new one.
                        hashItem.generation = context.generation + 1;
                        hashItem.layoutElement = layoutElement;
                        hashItem.layoutElementIndex = layoutElementIndex;
                        hashItem.debugData.collision = false;
                        hashItem.onHoverFunction = null;
                        hashItem.hoverFunctionUserData = null;
                    }
                    else // Multiple collisions this frame - two elements have the same ID.
                    {
                        context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_DUPLICATE_ID,
                            "An element with this ID was already previously declared during this layout.");
                        if (context.debugModeEnabled) hashItem.debugData.collision = true;
                    }
                    return ref hashItem;
                }
                hashItemPrevious = hashItemIndex;
                hashItemIndex = hashItem.nextIndex;
            }

            int indexToUse;
            if (context.layoutElementsHashMapFreeList.length > 0)
            {
                indexToUse = context.layoutElementsHashMapFreeList.internalArray[context.layoutElementsHashMapFreeList.length - 1];
                context.layoutElementsHashMapFreeList.length--;
            }
            else
            {
                indexToUse = context.layoutElementsHashMapInternal.length;
            }
            context.layoutElementsHashMapInternal.Set(indexToUse, item);
            if (hashItemPrevious != -1)
            {
                context.layoutElementsHashMapInternal.internalArray[hashItemPrevious].nextIndex = indexToUse;
            }
            else
            {
                context.layoutElementsHashMap.internalArray[hashBucket] = indexToUse;
            }
            return ref context.layoutElementsHashMapInternal.internalArray[indexToUse];
        }

        internal static ref Clay_LayoutElementHashMapItem __GetHashMapItem(uint id)
        {
            var context = GetCurrentContext();
            if (context == null) return ref Unsafe.NullRef<Clay_LayoutElementHashMapItem>();
            int hashBucket = (int)(id % (uint)context.layoutElementsHashMap.capacity);
            int elementIndex = context.layoutElementsHashMap.internalArray[hashBucket];
            while (elementIndex != -1)
            {
                ref var hashEntry = ref context.layoutElementsHashMapInternal.internalArray[elementIndex];
                if (hashEntry.elementId.id == id) return ref hashEntry;
                elementIndex = hashEntry.nextIndex;
            }
            return ref Unsafe.NullRef<Clay_LayoutElementHashMapItem>();
        }

        // -------------------------------------
        // Text measurement cache ---------------
        // -------------------------------------

        internal static ref Clay__MeasuredWord __AddMeasuredWord(Clay__MeasuredWord word, ref Clay__MeasuredWord previousWord)
        {
            var context = GetCurrentContext()!;
            if (context.measuredWordsFreeList.length > 0)
            {
                int newItemIndex = context.measuredWordsFreeList.internalArray[context.measuredWordsFreeList.length - 1];
                context.measuredWordsFreeList.length--;
                context.measuredWords.internalArray[newItemIndex] = word;
                previousWord.next = newItemIndex;
                return ref context.measuredWords.internalArray[newItemIndex];
            }
            else
            {
                previousWord.next = context.measuredWords.length;
                return ref context.measuredWords.Add(word);
            }
        }

        internal static Clay__MeasureTextCacheItem __MeasureTextCached(string text, Clay_TextElementConfig config)
        {
            var context = GetCurrentContext()!;
            if (s_measureText == null)
            {
                if (!context.booleanWarnings.textMeasurementFunctionNotSet)
                {
                    context.booleanWarnings.textMeasurementFunctionNotSet = true;
                    context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_TEXT_MEASUREMENT_FUNCTION_NOT_PROVIDED,
                        "Clay's internal MeasureText function is null. You may have forgotten to call Clay_SetMeasureTextFunction(), or passed a NULL function pointer by mistake.");
                }
                return default;
            }

            uint id = __HashStringContentsWithConfig(text, config);
            int hashBucket = (int)(id % (uint)(context.maxMeasureTextCacheWordCount / 32));
            int elementIndexPrevious = 0;
            int elementIndex = context.measureTextHashMap.internalArray[hashBucket];
            while (elementIndex != 0)
            {
                var hashEntry = context.measureTextHashMapInternal.internalArray[elementIndex];
                if (hashEntry.id == id)
                {
                    hashEntry.generation = context.generation;
                    context.measureTextHashMapInternal.internalArray[elementIndex] = hashEntry;
                    return hashEntry;
                }

                // This element hasn't been seen in a few frames, delete the hash map item.
                if (context.generation - hashEntry.generation > 2)
                {
                    // Add all the measured words that were included in this measurement to the freelist.
                    int nextWordIndex = hashEntry.measuredWordsStartIndex;
                    while (nextWordIndex != -1)
                    {
                        var measuredWord = context.measuredWords.internalArray[nextWordIndex];
                        context.measuredWordsFreeList.Add(nextWordIndex);
                        nextWordIndex = measuredWord.next;
                    }

                    int nextIndex = hashEntry.nextIndex;
                    context.measureTextHashMapInternal.internalArray[elementIndex] = new Clay__MeasureTextCacheItem { measuredWordsStartIndex = -1 };
                    context.measureTextHashMapInternalFreeList.Add(elementIndex);
                    if (elementIndexPrevious == 0)
                    {
                        context.measureTextHashMap.internalArray[hashBucket] = nextIndex;
                    }
                    else
                    {
                        var previousHashEntry = context.measureTextHashMapInternal.internalArray[elementIndexPrevious];
                        previousHashEntry.nextIndex = nextIndex;
                        context.measureTextHashMapInternal.internalArray[elementIndexPrevious] = previousHashEntry;
                    }
                    elementIndex = nextIndex;
                }
                else
                {
                    elementIndexPrevious = elementIndex;
                    elementIndex = hashEntry.nextIndex;
                }
            }

            int newItemIndex;
            var measured = new Clay__MeasureTextCacheItem { measuredWordsStartIndex = -1, id = id, generation = context.generation };
            if (context.measureTextHashMapInternalFreeList.length > 0)
            {
                newItemIndex = context.measureTextHashMapInternalFreeList.internalArray[context.measureTextHashMapInternalFreeList.length - 1];
                context.measureTextHashMapInternalFreeList.length--;
                context.measureTextHashMapInternal.internalArray[newItemIndex] = measured;
            }
            else
            {
                if (context.measureTextHashMapInternal.length == context.measureTextHashMapInternal.capacity - 1)
                {
                    if (!context.booleanWarnings.maxTextMeasureCacheExceeded)
                    {
                        context.booleanWarnings.maxTextMeasureCacheExceeded = true;
                        context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_ELEMENTS_CAPACITY_EXCEEDED,
                            "Clay ran out of capacity while attempting to measure text elements. Try using Clay_SetMaxElementCount() with a higher value.");
                    }
                    return default;
                }
                newItemIndex = context.measureTextHashMapInternal.length;
                context.measureTextHashMapInternal.Add(measured);
            }

            int start = 0;
            int end = 0;
            float lineWidth = 0;
            float measuredWidth = 0;
            float measuredHeight = 0;
            float spaceWidth = s_measureText(new StringSegment(" "), config, context.measureTextUserData).width;

            Clay__MeasuredWord tempWord = default;
            tempWord.next = -1;
            ref Clay__MeasuredWord previousWord = ref tempWord;

            while (end < text.Length)
            {
                if (context.measuredWords.length == context.measuredWords.capacity - 1)
                {
                    if (!context.booleanWarnings.maxTextMeasureCacheExceeded)
                    {
                        context.booleanWarnings.maxTextMeasureCacheExceeded = true;
                        context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_TEXT_MEASUREMENT_CAPACITY_EXCEEDED,
                            "Clay has run out of space in it's internal text measurement cache. Try using Clay_SetMaxMeasureTextCacheWordCount() (default 16384, with 1 unit storing 1 measured word).");
                    }
                    return default;
                }

                char current = text[end];
                if (current == ' ' || current == '\n')
                {
                    int length = end - start;
                    Clay_Dimensions dimensions = default;
                    if (length > 0)
                    {
                        dimensions = s_measureText(new StringSegment(text, start, length), config, context.measureTextUserData);
                    }
                    measured.minWidth = MathF.Max(dimensions.width, measured.minWidth);
                    measuredHeight = MathF.Max(measuredHeight, dimensions.height);
                    if (current == ' ')
                    {
                        dimensions.width += spaceWidth;
                        previousWord = ref __AddMeasuredWord(new Clay__MeasuredWord { startOffset = start, length = length + 1, width = dimensions.width, next = -1 }, ref previousWord);
                        lineWidth += dimensions.width;
                    }
                    if (current == '\n')
                    {
                        if (length > 0)
                        {
                            previousWord = ref __AddMeasuredWord(new Clay__MeasuredWord { startOffset = start, length = length, width = dimensions.width, next = -1 }, ref previousWord);
                        }
                        previousWord = ref __AddMeasuredWord(new Clay__MeasuredWord { startOffset = end + 1, length = 0, width = 0, next = -1 }, ref previousWord);
                        lineWidth += dimensions.width;
                        measuredWidth = MathF.Max(lineWidth, measuredWidth);
                        measured.containsNewlines = true;
                        lineWidth = 0;
                    }
                    start = end + 1;
                }
                end++;
            }

            if (end - start > 0)
            {
                Clay_Dimensions dimensions = s_measureText(new StringSegment(text, start, end - start), config, context.measureTextUserData);
                __AddMeasuredWord(new Clay__MeasuredWord { startOffset = start, length = end - start, width = dimensions.width, next = -1 }, ref previousWord);
                lineWidth += dimensions.width;
                measuredHeight = MathF.Max(measuredHeight, dimensions.height);
                measured.minWidth = MathF.Max(dimensions.width, measured.minWidth);
            }

            measuredWidth = MathF.Max(lineWidth, measuredWidth) - config.letterSpacing;

            measured.measuredWordsStartIndex = tempWord.next;
            measured.unwrappedDimensions.width = measuredWidth;
            measured.unwrappedDimensions.height = measuredHeight;

            // In C the `measured` pointer aliases the array slot; write the computed values back.
            context.measureTextHashMapInternal.internalArray[newItemIndex] = measured;

            if (elementIndexPrevious != 0)
            {
                var previousHashEntry = context.measureTextHashMapInternal.internalArray[elementIndexPrevious];
                previousHashEntry.nextIndex = newItemIndex;
                context.measureTextHashMapInternal.internalArray[elementIndexPrevious] = previousHashEntry;
            }
            else
            {
                context.measureTextHashMap.internalArray[hashBucket] = newItemIndex;
            }
            return measured;
        }

        // -------------------------------------
        // Element declaration ------------------
        // -------------------------------------

        internal static Clay_SizingAxis __GetElementSizing(Clay_LayoutElement element, bool xAxis)
        {
            if (element.isTextElement) return default;
            return xAxis ? element.config.layout.sizing.width : element.config.layout.sizing.height;
        }

        internal static void __OpenElement()
        {
            var context = GetCurrentContext()!;
            if (context.layoutElements.length == context.layoutElements.capacity - 1 || context.booleanWarnings.maxElementsExceeded)
            {
                context.booleanWarnings.maxElementsExceeded = true;
                return;
            }

            var openLayoutElement = new Clay_LayoutElement();
            context.layoutElements.Add(openLayoutElement);
            openLayoutElement.index = context.layoutElements.length - 1;
            context.openLayoutElementStack.Add(context.layoutElements.length - 1);

            // Generate an ID.
            Clay_LayoutElement parentElement = context.layoutElements.internalArray[context.openLayoutElementStack.GetValue(context.openLayoutElementStack.length - 2)];
            uint offset = (uint)(parentElement.children.length + parentElement.floatingChildrenCount);
            Clay_ElementId elementId = __HashNumber(offset, parentElement.id);
            openLayoutElement.id = elementId.id;
            __AddHashMapItem(elementId, openLayoutElement, openLayoutElement.index);

            if (context.openClipElementStack.length > 0)
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, context.openClipElementStack.GetValue(context.openClipElementStack.length - 1));
            }
            else
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, 0);
            }
        }

        internal static void __OpenElementWithId(Clay_ElementId elementId)
        {
            var context = GetCurrentContext()!;
            if (context.layoutElements.length == context.layoutElements.capacity - 1 || context.booleanWarnings.maxElementsExceeded)
            {
                context.booleanWarnings.maxElementsExceeded = true;
                return;
            }

            var openLayoutElement = new Clay_LayoutElement { id = elementId.id };
            context.layoutElements.Add(openLayoutElement);
            openLayoutElement.index = context.layoutElements.length - 1;
            context.openLayoutElementStack.Add(context.layoutElements.length - 1);
            __AddHashMapItem(elementId, openLayoutElement, openLayoutElement.index);

            if (context.openClipElementStack.length > 0)
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, context.openClipElementStack.GetValue(context.openClipElementStack.length - 1));
            }
            else
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, 0);
            }
        }

        internal static void __OpenTextElement(string text, Clay_TextElementConfig textConfig)
        {
            var context = GetCurrentContext()!;
            if (context.layoutElements.length == context.layoutElements.capacity - 1 || context.booleanWarnings.maxElementsExceeded)
            {
                context.booleanWarnings.maxElementsExceeded = true;
                return;
            }

            Clay_LayoutElement parentElement = __GetOpenLayoutElement();

            var textElement = new Clay_LayoutElement { textConfig = textConfig, isTextElement = true };
            context.layoutElements.Add(textElement);
            textElement.index = context.layoutElements.length - 1;

            if (context.openClipElementStack.length > 0)
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, context.openClipElementStack.GetValue(context.openClipElementStack.length - 1));
            }
            else
            {
                context.layoutElementClipElementIds.Set(context.layoutElements.length - 1, 0);
            }

            context.layoutElementChildrenBuffer.Add(context.layoutElements.length - 1);

            Clay__MeasureTextCacheItem textMeasured = __MeasureTextCached(text, textConfig);
            Clay_ElementId elementId = __HashNumber((uint)(parentElement.children.length + parentElement.floatingChildrenCount), parentElement.id);
            textElement.id = elementId.id;
            __AddHashMapItem(elementId, textElement, textElement.index);

            Clay_Dimensions textDimensions = new Clay_Dimensions
            {
                width = textMeasured.unwrappedDimensions.width,
                height = textConfig.lineHeight > 0 ? textConfig.lineHeight : textMeasured.unwrappedDimensions.height,
            };
            textElement.dimensions = textDimensions;
            textElement.minDimensions = new Clay_Dimensions { width = textMeasured.minWidth, height = textDimensions.height };
            textElement.textElementData = new Clay__TextElementData { text = text, preferredDimensions = textMeasured.unwrappedDimensions };
            parentElement.children.length++;
        }

        internal static void __ConfigureOpenElementPtr(in Clay_ElementDeclaration declaration)
        {
            var context = GetCurrentContext()!;
            Clay_LayoutElement openLayoutElement = __GetOpenLayoutElement();
            openLayoutElement.config = declaration;

            if ((declaration.layout.sizing.width.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT && declaration.layout.sizing.width.percent > 1)
                || (declaration.layout.sizing.height.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT && declaration.layout.sizing.height.percent > 1))
            {
                context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_PERCENTAGE_OVER_1,
                    "An element was configured with CLAY_SIZING_PERCENT, but the provided percentage value was over 1.0. Clay expects a value between 0 and 1, i.e. 20% is 0.2.");
            }

            if (declaration.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
            {
                ref Clay_FloatingElementConfig floatingConfig = ref openLayoutElement.config.floating;
                // The depth of the tree will always be at least 2 here (auto generated root element).
                Clay_LayoutElement hierarchicalParent = context.layoutElements.internalArray[context.openLayoutElementStack.GetValue(context.openLayoutElementStack.length - 2)];
                if (hierarchicalParent != null)
                {
                    int clipElementId = 0;
                    if (declaration.floating.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT)
                    {
                        // Attach to the element's direct hierarchical parent.
                        floatingConfig.parentId = hierarchicalParent.id;
                        if (context.openClipElementStack.length > 0)
                        {
                            clipElementId = context.openClipElementStack.GetValue(context.openClipElementStack.length - 1);
                        }
                    }
                    else if (declaration.floating.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ELEMENT_WITH_ID)
                    {
                        ref Clay_LayoutElementHashMapItem parentItem = ref __GetHashMapItem(floatingConfig.parentId);
                        if (Unsafe.IsNullRef(in parentItem))
                        {
                            context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_FLOATING_CONTAINER_PARENT_NOT_FOUND,
                                "A floating element was declared with a parentId, but no element with that ID was found.");
                        }
                        else
                        {
                            clipElementId = context.layoutElementClipElementIds.GetValue(parentItem.layoutElementIndex);
                        }
                    }
                    else if (declaration.floating.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ROOT)
                    {
                        floatingConfig.parentId = __HashString("Clay__RootContainer", 0).id;
                    }

                    if (declaration.floating.clipTo == Clay_FloatingClipToElement.CLAY_CLIP_TO_NONE)
                    {
                        clipElementId = 0;
                    }

                    int currentElementIndex = context.openLayoutElementStack.GetValue(context.openLayoutElementStack.length - 1);
                    context.layoutElementClipElementIds.Set(currentElementIndex, clipElementId);
                    context.openClipElementStack.Add(clipElementId);
                    context.layoutElementTreeRoots.Add(new Clay__LayoutElementTreeRoot
                    {
                        layoutElementIndex = context.openLayoutElementStack.GetValue(context.openLayoutElementStack.length - 1),
                        parentId = floatingConfig.parentId,
                        clipElementId = (uint)clipElementId,
                        zIndex = floatingConfig.zIndex,
                    });
                }
            }

            if (declaration.clip.horizontal || declaration.clip.vertical)
            {
                context.openClipElementStack.Add((int)openLayoutElement.id);
                // Retrieve or create cached data to track scroll position across frames.
                ref Clay__ScrollContainerDataInternal scrollOffset = ref Unsafe.NullRef<Clay__ScrollContainerDataInternal>();
                for (int i = 0; i < context.scrollContainerDatas.length; i++)
                {
                    ref Clay__ScrollContainerDataInternal mapping = ref context.scrollContainerDatas.internalArray[i];
                    if (openLayoutElement.id == mapping.elementId)
                    {
                        scrollOffset = ref mapping;
                        scrollOffset.layoutElement = openLayoutElement;
                        scrollOffset.openThisFrame = true;
                    }
                }
                if (Unsafe.IsNullRef(in scrollOffset))
                {
                    scrollOffset = ref context.scrollContainerDatas.Add(new Clay__ScrollContainerDataInternal
                    {
                        layoutElement = openLayoutElement,
                        scrollOrigin = new Vector2(-1, -1),
                        elementId = openLayoutElement.id,
                        openThisFrame = true,
                    });
                }
                if (context.externalScrollHandlingEnabled)
                {
                    scrollOffset.scrollPosition = s_queryScrollOffset!(scrollOffset.elementId, context.queryScrollOffsetUserData);
                }
            }

            // Setup data to track transitions across frames.
            if (declaration.transition.handler != null)
            {
                ref Clay__TransitionDataInternal transitionData = ref Unsafe.NullRef<Clay__TransitionDataInternal>();
                Clay_LayoutElement parentElement = __GetParentElement();
                for (int i = 0; i < context.transitionDatas.length; i++)
                {
                    ref Clay__TransitionDataInternal existingData = ref context.transitionDatas.internalArray[i];
                    if (openLayoutElement.id == existingData.elementId)
                    {
                        if (existingData.state == Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                        {
                            existingData.state = Clay_TransitionState.CLAY_TRANSITION_STATE_IDLE;
                            ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(openLayoutElement.id);
                            if (!Unsafe.IsNullRef(in hashMapItem)) hashMapItem.appearedThisFrame = false;
                        }
                        transitionData = ref existingData;
                        transitionData.elementThisFrame = openLayoutElement;
                        if (transitionData.parentId != parentElement.id)
                        {
                            transitionData.reparented = true;
                        }
                        transitionData.parentId = parentElement.id;
                        transitionData.siblingIndex = parentElement.children.length;
                        transitionData.transitionOut = declaration.transition.exit.setFinalState != null;
                    }
                }
                if (!Unsafe.IsNullRef(in transitionData))
                {
                    transitionData = ref context.transitionDatas.Add(new Clay__TransitionDataInternal
                    {
                        elementThisFrame = openLayoutElement,
                        elementId = openLayoutElement.id,
                        parentId = parentElement.id,
                        siblingIndex = parentElement.children.length,
                        transitionOut = declaration.transition.exit.setFinalState != null,
                    });
                }
            }
        }

        internal static void __ConfigureOpenElement(Clay_ElementDeclaration declaration) => __ConfigureOpenElementPtr(in declaration);

        internal static void __CloseElement()
        {
            var context = GetCurrentContext()!;
            if (context.booleanWarnings.maxElementsExceeded) return;

            Clay_LayoutElement openLayoutElement = __GetOpenLayoutElement();
            ref Clay_LayoutConfig layoutConfig = ref openLayoutElement.config.layout;
            bool elementHasClipHorizontal = openLayoutElement.config.clip.horizontal;
            bool elementHasClipVertical = openLayoutElement.config.clip.vertical;
            if (elementHasClipHorizontal || elementHasClipVertical || openLayoutElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
            {
                context.openClipElementStack.length--;
            }

            float leftRightPadding = layoutConfig.padding.left + layoutConfig.padding.right;
            float topBottomPadding = layoutConfig.padding.top + layoutConfig.padding.bottom;

            // Attach children to the current open element.
            openLayoutElement.children.elements = context.layoutElementChildren.internalArray;
            openLayoutElement.children.offset = context.layoutElementChildren.length;

            if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
            {
                openLayoutElement.dimensions.width = leftRightPadding;
                openLayoutElement.minDimensions.width = leftRightPadding;
                for (int i = 0; i < openLayoutElement.children.length; i++)
                {
                    int childIndex = context.layoutElementChildrenBuffer.GetValue(context.layoutElementChildrenBuffer.length - openLayoutElement.children.length + i);
                    Clay_LayoutElement child = context.layoutElements.internalArray[childIndex];
                    openLayoutElement.dimensions.width += child.dimensions.width;
                    openLayoutElement.dimensions.height = MathF.Max(openLayoutElement.dimensions.height, child.dimensions.height + topBottomPadding);
                    // Minimum size of child elements doesn't matter to clip containers as they can shrink and hide their contents.
                    if (!elementHasClipHorizontal)
                    {
                        openLayoutElement.minDimensions.width += child.minDimensions.width;
                    }
                    if (!elementHasClipVertical)
                    {
                        openLayoutElement.minDimensions.height = MathF.Max(openLayoutElement.minDimensions.height, child.minDimensions.height + topBottomPadding);
                    }
                    context.layoutElementChildren.Add(child);
                }
                float childGap = MathF.Max(openLayoutElement.children.length - 1, 0) * layoutConfig.childGap;
                openLayoutElement.dimensions.width += childGap;
                if (!elementHasClipHorizontal)
                {
                    openLayoutElement.minDimensions.width += childGap;
                }
            }
            else if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM)
            {
                openLayoutElement.dimensions.height = topBottomPadding;
                openLayoutElement.minDimensions.height = topBottomPadding;
                for (int i = 0; i < openLayoutElement.children.length; i++)
                {
                    int childIndex = context.layoutElementChildrenBuffer.GetValue(context.layoutElementChildrenBuffer.length - openLayoutElement.children.length + i);
                    Clay_LayoutElement child = context.layoutElements.internalArray[childIndex];
                    openLayoutElement.dimensions.height += child.dimensions.height;
                    openLayoutElement.dimensions.width = MathF.Max(openLayoutElement.dimensions.width, child.dimensions.width + leftRightPadding);
                    if (!elementHasClipVertical)
                    {
                        openLayoutElement.minDimensions.height += child.minDimensions.height;
                    }
                    if (!elementHasClipHorizontal)
                    {
                        openLayoutElement.minDimensions.width = MathF.Max(openLayoutElement.minDimensions.width, child.minDimensions.width + leftRightPadding);
                    }
                    context.layoutElementChildren.Add(child);
                }
                float childGap = MathF.Max(openLayoutElement.children.length - 1, 0) * layoutConfig.childGap;
                openLayoutElement.dimensions.height += childGap;
                if (!elementHasClipVertical)
                {
                    openLayoutElement.minDimensions.height += childGap;
                }
            }

            context.layoutElementChildrenBuffer.length -= openLayoutElement.children.length;

            // Clamp element min and max width to the values configured in the layout.
            if (layoutConfig.sizing.width.type != Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
            {
                if (layoutConfig.sizing.width.minMax.max <= 0) layoutConfig.sizing.width.minMax.max = CLAY__MAXFLOAT;
                openLayoutElement.dimensions.width = MathF.Min(MathF.Max(openLayoutElement.dimensions.width, layoutConfig.sizing.width.minMax.min), layoutConfig.sizing.width.minMax.max);
                openLayoutElement.minDimensions.width = MathF.Min(MathF.Max(openLayoutElement.minDimensions.width, layoutConfig.sizing.width.minMax.min), layoutConfig.sizing.width.minMax.max);
            }
            else
            {
                openLayoutElement.dimensions.width = 0;
            }

            // Clamp element min and max height to the values configured in the layout.
            if (layoutConfig.sizing.height.type != Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
            {
                if (layoutConfig.sizing.height.minMax.max <= 0) layoutConfig.sizing.height.minMax.max = CLAY__MAXFLOAT;
                openLayoutElement.dimensions.height = MathF.Min(MathF.Max(openLayoutElement.dimensions.height, layoutConfig.sizing.height.minMax.min), layoutConfig.sizing.height.minMax.max);
                openLayoutElement.minDimensions.height = MathF.Min(MathF.Max(openLayoutElement.minDimensions.height, layoutConfig.sizing.height.minMax.min), layoutConfig.sizing.height.minMax.max);
            }
            else
            {
                openLayoutElement.dimensions.height = 0;
            }

            __UpdateAspectRatioBox(openLayoutElement);

            bool elementIsFloating = openLayoutElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE;

            // Close the currently open element.
            int closingElementIndex = context.openLayoutElementStack.RemoveSwapback(context.openLayoutElementStack.length - 1);

            // Get the currently open parent.
            openLayoutElement = __GetOpenLayoutElement();

            if (context.openLayoutElementStack.length > 1)
            {
                if (elementIsFloating)
                {
                    openLayoutElement.floatingChildrenCount++;
                    return;
                }
                openLayoutElement.children.length++;
                context.layoutElementChildrenBuffer.Add(closingElementIndex);
            }
        }

        // -------------------------------------
        // Layout engine ------------------------
        // -------------------------------------

        internal static void __SizeContainersAlongAxis(bool xAxis, bool collectElements, ref ClayArray<int> textElementsOut, ref ClayArray<int> aspectRatioElementsOut)
        {
            var context = GetCurrentContext()!;
            ClayArray<int> bfsBuffer = context.layoutElementChildrenBuffer;
            ClayArray<int> resizableContainerBuffer = context.openLayoutElementStack;

            for (int rootIndex = 0; rootIndex < context.layoutElementTreeRoots.length; ++rootIndex)
            {
                bfsBuffer.length = 0;
                Clay__LayoutElementTreeRoot root = context.layoutElementTreeRoots.internalArray[rootIndex];
                Clay_LayoutElement rootElement = context.layoutElements.internalArray[root.layoutElementIndex];
                bfsBuffer.Add(root.layoutElementIndex);

                // Size floating containers to their parents.
                if (rootElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
                {
                    ref Clay_FloatingElementConfig floatingElementConfig = ref rootElement.config.floating;
                    ref Clay_LayoutElementHashMapItem parentItem = ref __GetHashMapItem(floatingElementConfig.parentId);
                    if (!Unsafe.IsNullRef(in parentItem))
                    {
                        Clay_LayoutElement parentLayoutElement = parentItem.layoutElement;
                        switch (rootElement.config.layout.sizing.width.type)
                        {
                            case Clay__SizingType.CLAY__SIZING_TYPE_GROW:
                                rootElement.dimensions.width = parentLayoutElement.dimensions.width;
                                break;
                            case Clay__SizingType.CLAY__SIZING_TYPE_PERCENT:
                                rootElement.dimensions.width = parentLayoutElement.dimensions.width * rootElement.config.layout.sizing.width.percent;
                                break;
                            default: break;
                        }
                        switch (rootElement.config.layout.sizing.height.type)
                        {
                            case Clay__SizingType.CLAY__SIZING_TYPE_GROW:
                                rootElement.dimensions.height = parentLayoutElement.dimensions.height;
                                break;
                            case Clay__SizingType.CLAY__SIZING_TYPE_PERCENT:
                                rootElement.dimensions.height = parentLayoutElement.dimensions.height * rootElement.config.layout.sizing.height.percent;
                                break;
                            default: break;
                        }
                    }
                }

                if (rootElement.config.layout.sizing.width.type != Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
                {
                    rootElement.dimensions.width = MathF.Min(MathF.Max(rootElement.dimensions.width, rootElement.config.layout.sizing.width.minMax.min), rootElement.config.layout.sizing.width.minMax.max);
                }
                if (rootElement.config.layout.sizing.height.type != Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
                {
                    rootElement.dimensions.height = MathF.Min(MathF.Max(rootElement.dimensions.height, rootElement.config.layout.sizing.height.minMax.min), rootElement.config.layout.sizing.height.minMax.max);
                }

                for (int i = 0; i < bfsBuffer.length; ++i)
                {
                    int parentIndex = bfsBuffer.internalArray[i];
                    Clay_LayoutElement parent = context.layoutElements.internalArray[parentIndex];
                    ref Clay_LayoutConfig parentLayoutConfig = ref parent.config.layout;
                    int growContainerCount = 0;
                    float parentSize = xAxis ? parent.dimensions.width : parent.dimensions.height;
                    float parentPadding = xAxis
                        ? parentLayoutConfig.padding.left + parentLayoutConfig.padding.right
                        : parentLayoutConfig.padding.top + parentLayoutConfig.padding.bottom;
                    float innerContentSize = 0;
                    float totalPaddingAndChildGaps = parentPadding;
                    bool sizingAlongAxis = (xAxis && parentLayoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                                           || (!xAxis && parentLayoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM);
                    resizableContainerBuffer.length = 0;
                    float parentChildGap = parentLayoutConfig.childGap;
                    bool isFirstChild = true;

                    for (int childOffset = 0; childOffset < parent.children.length; childOffset++)
                    {
                        Clay_LayoutElement childElement = parent.children.elements[parent.children.offset + childOffset];
                        int childElementIndex = childElement.index;
                        Clay_SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                        float childSize = xAxis ? childElement.dimensions.width : childElement.dimensions.height;

                        if (collectElements && childElement.isTextElement)
                        {
                            textElementsOut.Add(childElementIndex);
                        }
                        else if (childElement.children.length > 0)
                        {
                            bfsBuffer.Add(childElementIndex);
                        }

                        if (!childElement.isTextElement && collectElements && childElement.config.aspectRatio.aspectRatio != 0)
                        {
                            aspectRatioElementsOut.Add(childElementIndex);
                        }

                        // Note: setting isFirstChild = false is skipped here.
                        if (childElement.exiting)
                        {
                            continue;
                        }

                        if (childSizing.type != Clay__SizingType.CLAY__SIZING_TYPE_PERCENT
                            && childSizing.type != Clay__SizingType.CLAY__SIZING_TYPE_FIXED
                            && (!childElement.isTextElement || childElement.textConfig.wrapMode == Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_WORDS))
                        {
                            resizableContainerBuffer.Add(childElementIndex);
                        }

                        if (sizingAlongAxis)
                        {
                            innerContentSize += (childSizing.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT ? 0 : childSize);
                            if (childSizing.type == Clay__SizingType.CLAY__SIZING_TYPE_GROW)
                            {
                                growContainerCount++;
                            }
                            if (!isFirstChild)
                            {
                                // For children after index 0, the childAxisOffset is the gap from the previous child.
                                innerContentSize += parentChildGap;
                                totalPaddingAndChildGaps += parentChildGap;
                            }
                        }
                        else
                        {
                            innerContentSize = MathF.Max(childSize, innerContentSize);
                        }
                        isFirstChild = false;
                    }

                    // Expand percentage containers to size.
                    for (int childOffset = 0; childOffset < parent.children.length; childOffset++)
                    {
                        Clay_LayoutElement childElement = parent.children.elements[parent.children.offset + childOffset];
                        Clay_SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                        if (childSizing.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
                        {
                            float percentSize = (parentSize - totalPaddingAndChildGaps) * childSizing.percent;
                            if (xAxis) childElement.dimensions.width = percentSize;
                            else childElement.dimensions.height = percentSize;
                            if (sizingAlongAxis)
                            {
                                innerContentSize += percentSize;
                            }
                            __UpdateAspectRatioBox(childElement);
                        }
                    }

                    if (sizingAlongAxis)
                    {
                        float sizeToDistribute = parentSize - parentPadding - innerContentSize;
                        // The content is too large, compress the children as much as possible.
                        if (sizeToDistribute < 0)
                        {
                            // If the parent clips content in this axis direction, don't compress children.
                            if ((xAxis && parent.config.clip.horizontal) || (!xAxis && parent.config.clip.vertical))
                            {
                                continue;
                            }
                            // Scrolling containers preferentially compress before others.
                            while (sizeToDistribute < -CLAY__EPSILON && resizableContainerBuffer.length > 0)
                            {
                                float largest = 0;
                                float secondLargest = 0;
                                float widthToAdd = sizeToDistribute;
                                for (int childIndex = 0; childIndex < resizableContainerBuffer.length; childIndex++)
                                {
                                    Clay_LayoutElement child = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childIndex]];
                                    float childSize = xAxis ? child.dimensions.width : child.dimensions.height;
                                    if (__FloatEqual(childSize, largest)) continue;
                                    if (childSize > largest)
                                    {
                                        secondLargest = largest;
                                        largest = childSize;
                                    }
                                    if (childSize < largest)
                                    {
                                        secondLargest = MathF.Max(secondLargest, childSize);
                                        widthToAdd = secondLargest - largest;
                                    }
                                }

                                widthToAdd = MathF.Max(widthToAdd, sizeToDistribute / resizableContainerBuffer.length);

                                for (int childIndex = 0; childIndex < resizableContainerBuffer.length; childIndex++)
                                {
                                    Clay_LayoutElement child = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childIndex]];
                                    float minSize = xAxis ? child.minDimensions.width : child.minDimensions.height;
                                    float previousWidth = xAxis ? child.dimensions.width : child.dimensions.height;
                                    if (__FloatEqual(previousWidth, largest))
                                    {
                                        float newSize = previousWidth + widthToAdd;
                                        if (newSize <= minSize)
                                        {
                                            newSize = minSize;
                                            resizableContainerBuffer.RemoveSwapback(childIndex--);
                                        }
                                        if (xAxis) child.dimensions.width = newSize;
                                        else child.dimensions.height = newSize;
                                        sizeToDistribute -= (newSize - previousWidth);
                                    }
                                }
                            }
                        }
                        // The content is too small, allow SIZING_GROW containers to expand.
                        else if (sizeToDistribute > 0 && growContainerCount > 0)
                        {
                            for (int childIndex = 0; childIndex < resizableContainerBuffer.length; childIndex++)
                            {
                                Clay_LayoutElement child = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childIndex]];
                                if (__GetElementSizing(child, xAxis).type != Clay__SizingType.CLAY__SIZING_TYPE_GROW)
                                {
                                    resizableContainerBuffer.RemoveSwapback(childIndex--);
                                }
                            }
                            while (sizeToDistribute > CLAY__EPSILON && resizableContainerBuffer.length > 0)
                            {
                                float smallest = CLAY__MAXFLOAT;
                                float secondSmallest = CLAY__MAXFLOAT;
                                float widthToAdd = sizeToDistribute;
                                for (int childIndex = 0; childIndex < resizableContainerBuffer.length; childIndex++)
                                {
                                    Clay_LayoutElement child = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childIndex]];
                                    float childSize = xAxis ? child.dimensions.width : child.dimensions.height;
                                    if (__FloatEqual(childSize, smallest)) continue;
                                    if (childSize < smallest)
                                    {
                                        secondSmallest = smallest;
                                        smallest = childSize;
                                    }
                                    if (childSize > smallest)
                                    {
                                        secondSmallest = MathF.Min(secondSmallest, childSize);
                                        widthToAdd = secondSmallest - smallest;
                                    }
                                }

                                widthToAdd = MathF.Min(widthToAdd, sizeToDistribute / resizableContainerBuffer.length);

                                for (int childIndex = 0; childIndex < resizableContainerBuffer.length; childIndex++)
                                {
                                    Clay_LayoutElement child = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childIndex]];
                                    Clay_SizingAxis childSizing = __GetElementSizing(child, xAxis);
                                    float maxSize = childSizing.minMax.max;
                                    float previousWidth = xAxis ? child.dimensions.width : child.dimensions.height;
                                    if (__FloatEqual(previousWidth, smallest))
                                    {
                                        float newSize = previousWidth + widthToAdd;
                                        if (newSize >= maxSize)
                                        {
                                            newSize = maxSize;
                                            resizableContainerBuffer.RemoveSwapback(childIndex--);
                                        }
                                        if (xAxis) child.dimensions.width = newSize;
                                        else child.dimensions.height = newSize;
                                        sizeToDistribute -= (newSize - previousWidth);
                                    }
                                }
                            }
                        }
                    }
                    // Sizing along the non layout axis ("off axis").
                    else
                    {
                        for (int childOffset = 0; childOffset < resizableContainerBuffer.length; childOffset++)
                        {
                            Clay_LayoutElement childElement = context.layoutElements.internalArray[resizableContainerBuffer.internalArray[childOffset]];
                            Clay_SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                            float minSize = xAxis ? childElement.minDimensions.width : childElement.minDimensions.height;
                            float maxSize = parentSize - parentPadding;
                            // If we're laying out the children of a scroll panel, grow containers expand to the size of the inner content.
                            if ((xAxis && parent.config.clip.horizontal) || (!xAxis && parent.config.clip.vertical))
                            {
                                maxSize = MathF.Max(maxSize, innerContentSize);
                            }
                            if (childSizing.type == Clay__SizingType.CLAY__SIZING_TYPE_GROW)
                            {
                                float growSize = MathF.Min(maxSize, childSizing.minMax.max);
                                if (xAxis) childElement.dimensions.width = growSize;
                                else childElement.dimensions.height = growSize;
                            }
                            float clamped = MathF.Max(minSize, MathF.Min(xAxis ? childElement.dimensions.width : childElement.dimensions.height, maxSize));
                            if (xAxis) childElement.dimensions.width = clamped;
                            else childElement.dimensions.height = clamped;
                        }
                    }
                }
            }
        }

        internal static void __AddRenderCommand(Clay_RenderCommand renderCommand)
        {
            var context = GetCurrentContext()!;
            if (context.renderCommands.length < context.renderCommands.capacity - 1)
            {
                context.renderCommands.Add(renderCommand);
            }
            else
            {
                if (!context.booleanWarnings.maxRenderCommandsExceeded)
                {
                    context.booleanWarnings.maxRenderCommandsExceeded = true;
                    context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_ELEMENTS_CAPACITY_EXCEEDED,
                        "Clay ran out of capacity while attempting to create render commands. This is usually caused by a large amount of wrapping text elements while close to the max element capacity. Try using Clay_SetMaxElementCount() with a higher value.");
                }
            }
        }

        internal static bool __ElementIsOffscreen(in Clay_BoundingBox boundingBox)
        {
            var context = GetCurrentContext()!;
            if (context.disableCulling) return false;

            return (boundingBox.x > context.layoutDimensions.width)
                || (boundingBox.y > context.layoutDimensions.height)
                || (boundingBox.x + boundingBox.width < 0)
                || (boundingBox.y + boundingBox.height < 0);
        }

        internal static void __CalculateFinalLayout(float deltaTime, bool useStoredBoundingBoxes, bool generateRenderCommands)
        {
            var context = GetCurrentContext()!;

            // Calculate sizing along the X axis.
            ClayArray<int> textElements = context.openClipElementStack;
            textElements.length = 0;
            ClayArray<int> aspectRatioElements = context.reusableElementIndexBuffer;
            aspectRatioElements.length = 0;
            __SizeContainersAlongAxis(true, true, ref textElements, ref aspectRatioElements);

            // Wrap text.
            for (int textElementIndex = 0; textElementIndex < textElements.length; ++textElementIndex)
            {
                Clay_LayoutElement element = context.layoutElements.internalArray[textElements.internalArray[textElementIndex]];
                ref Clay__TextElementData textElementData = ref element.textElementData;
                textElementData.wrappedLines = new ClayArraySlice<Clay__WrappedTextLine>
                {
                    length = 0,
                    internalArray = context.wrappedTextLines.internalArray,
                    offset = context.wrappedTextLines.length,
                };

                Clay__MeasureTextCacheItem measureTextCacheItem = __MeasureTextCached(textElementData.text, element.textConfig);
                float lineWidth = 0;
                float lineHeight = element.textConfig.lineHeight > 0 ? element.textConfig.lineHeight : textElementData.preferredDimensions.height;
                int lineLengthChars = 0;
                int lineStartOffset = 0;

                if (!measureTextCacheItem.containsNewlines && textElementData.preferredDimensions.width <= element.dimensions.width)
                {
                    context.wrappedTextLines.Add(new Clay__WrappedTextLine
                    {
                        dimensions = element.dimensions,
                        line = new StringSegment(textElementData.text),
                    });
                    textElementData.wrappedLines.length++;
                    continue;
                }

                float spaceWidth = s_measureText!(new StringSegment(" "), element.textConfig, context.measureTextUserData).width;
                int wordIndex = measureTextCacheItem.measuredWordsStartIndex;
                while (wordIndex != -1)
                {
                    if (context.wrappedTextLines.length > context.wrappedTextLines.capacity - 1) break;

                    Clay__MeasuredWord measuredWord = context.measuredWords.internalArray[wordIndex];
                    // Only word on the line is too large, just render it anyway.
                    if (lineLengthChars == 0 && lineWidth + measuredWord.width > element.dimensions.width)
                    {
                        context.wrappedTextLines.Add(new Clay__WrappedTextLine
                        {
                            dimensions = new Clay_Dimensions { width = measuredWord.width, height = lineHeight },
                            line = new StringSegment(textElementData.text, measuredWord.startOffset, measuredWord.length),
                        });
                        textElementData.wrappedLines.length++;
                        wordIndex = measuredWord.next;
                        lineStartOffset = measuredWord.startOffset + measuredWord.length;
                    }
                    // measuredWord.length == 0 means a newline character.
                    else if (measuredWord.length == 0 || lineWidth + measuredWord.width > element.dimensions.width)
                    {
                        bool finalCharIsSpace = textElementData.text[Math.Max(lineStartOffset + lineLengthChars - 1, 0)] == ' ';
                        // Clamp to 0 to avoid a negative-length StringSegment in a pathological case.
                        int lineLength = Math.Max(lineLengthChars + (finalCharIsSpace ? -1 : 0), 0);
                        context.wrappedTextLines.Add(new Clay__WrappedTextLine
                        {
                            dimensions = new Clay_Dimensions { width = lineWidth + (finalCharIsSpace ? -spaceWidth : 0), height = lineHeight },
                            line = new StringSegment(textElementData.text, lineStartOffset, lineLength),
                        });
                        textElementData.wrappedLines.length++;
                        if (lineLengthChars == 0 || measuredWord.length == 0)
                        {
                            wordIndex = measuredWord.next;
                        }
                        lineWidth = 0;
                        lineLengthChars = 0;
                        lineStartOffset = measuredWord.startOffset;
                    }
                    else
                    {
                        lineWidth += measuredWord.width + element.textConfig.letterSpacing;
                        lineLengthChars += measuredWord.length;
                        wordIndex = measuredWord.next;
                    }
                }

                if (lineLengthChars > 0)
                {
                    context.wrappedTextLines.Add(new Clay__WrappedTextLine
                    {
                        dimensions = new Clay_Dimensions { width = lineWidth - element.textConfig.letterSpacing, height = lineHeight },
                        line = new StringSegment(textElementData.text, lineStartOffset, lineLengthChars),
                    });
                    textElementData.wrappedLines.length++;
                }
                element.dimensions.height = lineHeight * textElementData.wrappedLines.length;
            }

            // Scale vertical heights according to aspect ratio.
            for (int i = 0; i < aspectRatioElements.length; ++i)
            {
                Clay_LayoutElement aspectElement = context.layoutElements.internalArray[aspectRatioElements.internalArray[i]];
                aspectElement.dimensions.height = (1 / aspectElement.config.aspectRatio.aspectRatio) * aspectElement.dimensions.width;
                aspectElement.config.layout.sizing.height.minMax.max = aspectElement.dimensions.height;
            }

            // Propagate the effect of text wrapping / aspect scaling on the height of parents.
            ClayArray<Clay__LayoutElementTreeNode> dfsBuffer = context.layoutElementTreeNodeArray1;
            dfsBuffer.length = 0;
            for (int i = 0; i < context.layoutElementTreeRoots.length; ++i)
            {
                Clay__LayoutElementTreeRoot root = context.layoutElementTreeRoots.internalArray[i];
                context.treeNodeVisited.internalArray[dfsBuffer.length] = false;
                dfsBuffer.Add(new Clay__LayoutElementTreeNode { layoutElement = context.layoutElements.internalArray[root.layoutElementIndex] });
            }
            while (dfsBuffer.length > 0)
            {
                Clay__LayoutElementTreeNode currentElementTreeNode = dfsBuffer.internalArray[dfsBuffer.length - 1];
                Clay_LayoutElement currentElement = currentElementTreeNode.layoutElement;
                if (!context.treeNodeVisited.internalArray[dfsBuffer.length - 1])
                {
                    context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = true;
                    // If the element has no children or is a text element, don't bother inspecting it.
                    if (currentElement.isTextElement || currentElement.children.length == 0)
                    {
                        dfsBuffer.length--;
                        continue;
                    }
                    // Add the children to the DFS buffer.
                    for (int i = 0; i < currentElement.children.length; i++)
                    {
                        context.treeNodeVisited.internalArray[dfsBuffer.length] = false;
                        dfsBuffer.Add(new Clay__LayoutElementTreeNode
                        {
                            layoutElement = currentElement.children.elements[currentElement.children.offset + i],
                        });
                    }
                    continue;
                }
                dfsBuffer.length--;

                // DFS node has been visited, this is on the way back up to the root.
                ref Clay_LayoutConfig layoutConfig = ref currentElement.config.layout;
                if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                {
                    // Resize any parent containers that have grown in height along their non layout axis.
                    for (int j = 0; j < currentElement.children.length; ++j)
                    {
                        Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + j];
                        float childHeightWithPadding = MathF.Max(childElement.dimensions.height + layoutConfig.padding.top + layoutConfig.padding.bottom, currentElement.dimensions.height);
                        currentElement.dimensions.height = MathF.Min(MathF.Max(childHeightWithPadding, layoutConfig.sizing.height.minMax.min), layoutConfig.sizing.height.minMax.max);
                    }
                }
                else if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM)
                {
                    // Resizing along the layout axis.
                    float contentHeight = layoutConfig.padding.top + layoutConfig.padding.bottom;
                    for (int j = 0; j < currentElement.children.length; ++j)
                    {
                        Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + j];
                        contentHeight += childElement.dimensions.height;
                    }
                    contentHeight += MathF.Max(currentElement.children.length - 1, 0) * layoutConfig.childGap;
                    currentElement.dimensions.height = MathF.Min(MathF.Max(contentHeight, layoutConfig.sizing.height.minMax.min), layoutConfig.sizing.height.minMax.max);
                }
            }

            // Calculate sizing along the Y axis.
            ClayArray<int> noTextElements = default;
            ClayArray<int> noAspectElements = default;
            __SizeContainersAlongAxis(false, false, ref noTextElements, ref noAspectElements);

            // Scale horizontal widths according to aspect ratio.
            for (int i = 0; i < aspectRatioElements.length; ++i)
            {
                Clay_LayoutElement aspectElement = context.layoutElements.internalArray[aspectRatioElements.internalArray[i]];
                aspectElement.dimensions.width = aspectElement.config.aspectRatio.aspectRatio * aspectElement.dimensions.height;
            }

            // Sort tree roots by z-index.
            int sortMax = context.layoutElementTreeRoots.length - 1;
            while (sortMax > 0) // todo dumb bubble sort.
            {
                for (int i = 0; i < sortMax; ++i)
                {
                    Clay__LayoutElementTreeRoot current = context.layoutElementTreeRoots.internalArray[i];
                    Clay__LayoutElementTreeRoot next = context.layoutElementTreeRoots.internalArray[i + 1];
                    if (next.zIndex < current.zIndex)
                    {
                        context.layoutElementTreeRoots.internalArray[i] = next;
                        context.layoutElementTreeRoots.internalArray[i + 1] = current;
                    }
                }
                sortMax--;
            }

            // Calculate final positions and generate render commands.
            context.renderCommands.length = 0;
            dfsBuffer.length = 0;

            for (int rootIndex = 0; rootIndex < context.layoutElementTreeRoots.length; ++rootIndex)
            {
                dfsBuffer.length = 0;
                Clay__LayoutElementTreeRoot root = context.layoutElementTreeRoots.internalArray[rootIndex];
                Clay_LayoutElement rootElement = context.layoutElements.internalArray[root.layoutElementIndex];
                Vector2 rootPosition = default;
                ref Clay_LayoutElementHashMapItem parentHashMapItem = ref __GetHashMapItem(root.parentId);

                // Position root floating containers.
                if (rootElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE && !Unsafe.IsNullRef(in parentHashMapItem))
                {
                    ref Clay_FloatingElementConfig config = ref rootElement.config.floating;
                    Clay_Dimensions rootDimensions = rootElement.dimensions;
                    Clay_BoundingBox parentBoundingBox = parentHashMapItem.boundingBox;
                    Vector2 targetAttachPosition = default;

                    switch (config.attachPoints.parent)
                    {
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM:
                            targetAttachPosition.X = parentBoundingBox.x; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM:
                            targetAttachPosition.X = parentBoundingBox.x + parentBoundingBox.width / 2; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM:
                            targetAttachPosition.X = parentBoundingBox.x + parentBoundingBox.width; break;
                    }
                    switch (config.attachPoints.element)
                    {
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM: break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM:
                            targetAttachPosition.X -= rootDimensions.width / 2; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM:
                            targetAttachPosition.X -= rootDimensions.width; break;
                    }
                    switch (config.attachPoints.parent)
                    {
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP:
                            targetAttachPosition.Y = parentBoundingBox.y; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER:
                            targetAttachPosition.Y = parentBoundingBox.y + parentBoundingBox.height / 2; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM:
                            targetAttachPosition.Y = parentBoundingBox.y + parentBoundingBox.height; break;
                    }
                    switch (config.attachPoints.element)
                    {
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP: break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER:
                            targetAttachPosition.Y -= rootDimensions.height / 2; break;
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM:
                        case Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM:
                            targetAttachPosition.Y -= rootDimensions.height; break;
                    }
                    targetAttachPosition.X += config.offset.X;
                    targetAttachPosition.Y += config.offset.Y;
                    rootPosition = targetAttachPosition;
                }

                if (root.clipElementId != 0)
                {
                    ref Clay_LayoutElementHashMapItem clipHashMapItem = ref __GetHashMapItem(root.clipElementId);
                    if (!Unsafe.IsNullRef(in clipHashMapItem) && !__ElementIsOffscreen(in clipHashMapItem.boundingBox))
                    {
                        // Floating elements attached to scrolling contents won't be correctly positioned if external scroll handling is enabled; fix here.
                        if (context.externalScrollHandlingEnabled)
                        {
                            if (clipHashMapItem.layoutElement.config.clip.horizontal)
                            {
                                rootPosition.X += clipHashMapItem.layoutElement.config.clip.childOffset.X;
                            }
                            if (clipHashMapItem.layoutElement.config.clip.vertical)
                            {
                                rootPosition.Y += clipHashMapItem.layoutElement.config.clip.childOffset.Y;
                            }
                        }
                        if (generateRenderCommands)
                        {
                            __AddRenderCommand(new Clay_RenderCommand
                            {
                                boundingBox = clipHashMapItem.boundingBox,
                                userData = null,
                                id = __HashNumber(rootElement.id, (uint)(rootElement.children.length + 10)).id, // TODO need a better strategy for managing derived ids.
                                zIndex = root.zIndex,
                                commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START,
                            });
                        }
                    }
                }

                dfsBuffer.Add(new Clay__LayoutElementTreeNode
                {
                    layoutElement = rootElement,
                    position = rootPosition,
                    nextChildOffset = new Vector2(rootElement.config.layout.padding.left, rootElement.config.layout.padding.top),
                });

                context.treeNodeVisited.internalArray[0] = false;
                while (dfsBuffer.length > 0)
                {
                    ref Clay__LayoutElementTreeNode currentElementTreeNode = ref dfsBuffer.internalArray[dfsBuffer.length - 1];
                    Clay_LayoutElement currentElement = currentElementTreeNode.layoutElement;
                    Clay_LayoutConfig layoutConfig = currentElement.isTextElement ? CLAY_LAYOUT_DEFAULT : currentElement.config.layout;
                    Vector2 scrollOffset = default;

                    // DFS is returning back upwards.
                    if (context.treeNodeVisited.internalArray[dfsBuffer.length - 1])
                    {
                        if (currentElement.isTextElement)
                        {
                            dfsBuffer.length--;
                            continue;
                        }
                        ref Clay_LayoutElementHashMapItem currentElementData = ref __GetHashMapItem(currentElement.id);
                        if (generateRenderCommands && !Unsafe.IsNullRef(in currentElementData) && !__ElementIsOffscreen(in currentElementData.boundingBox))
                        {
                            bool closeClipElement = false;
                            if (currentElement.config.clip.horizontal || currentElement.config.clip.vertical)
                            {
                                closeClipElement = true;
                                for (int i = 0; i < context.scrollContainerDatas.length; i++)
                                {
                                    Clay__ScrollContainerDataInternal mapping = context.scrollContainerDatas.internalArray[i];
                                    if (mapping.layoutElement == currentElement)
                                    {
                                        scrollOffset = currentElement.config.clip.childOffset;
                                        if (context.externalScrollHandlingEnabled)
                                        {
                                            scrollOffset = default;
                                        }
                                        break;
                                    }
                                }
                            }

                            if (__BorderHasAnyWidth(in currentElement.config.border))
                            {
                                Clay_BoundingBox borderBoundingBox = currentElementData.boundingBox;
                                ref Clay_BorderElementConfig borderConfig = ref currentElement.config.border;
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = borderBoundingBox,
                                    renderData = new Clay_RenderData
                                    {
                                        border = new Clay_BorderRenderData
                                        {
                                            color = borderConfig.color,
                                            cornerRadius = currentElement.config.cornerRadius,
                                            width = borderConfig.width,
                                        },
                                    },
                                    userData = currentElement.config.userData,
                                    id = __HashNumber(currentElement.id, currentElement.children.length).id,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_BORDER,
                                });

                                if (borderConfig.width.betweenChildren > 0 && borderConfig.color.a > 0)
                                {
                                    float halfGap = layoutConfig.childGap / 2;
                                    float halfWidth = borderConfig.width.betweenChildren / 2;
                                    Vector2 borderOffset = new Vector2(layoutConfig.padding.left - halfGap, layoutConfig.padding.top - halfGap);
                                    if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                                    {
                                        for (int i = 0; i < currentElement.children.length; ++i)
                                        {
                                            Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + i];
                                            if (i > 0)
                                            {
                                                __AddRenderCommand(new Clay_RenderCommand
                                                {
                                                    boundingBox = new Clay_BoundingBox(
                                                        borderBoundingBox.x + borderOffset.X + scrollOffset.X - halfWidth,
                                                        borderBoundingBox.y + scrollOffset.Y,
                                                        borderConfig.width.betweenChildren,
                                                        currentElement.dimensions.height),
                                                    renderData = new Clay_RenderData
                                                    {
                                                        rectangle = new Clay_RectangleRenderData { backgroundColor = borderConfig.color },
                                                    },
                                                    userData = currentElement.config.userData,
                                                    id = __HashNumber(currentElement.id, (uint)(currentElement.children.length + 1 + i)).id,
                                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE,
                                                });
                                            }
                                            borderOffset.X += childElement.dimensions.width + layoutConfig.childGap;
                                        }
                                    }
                                    else
                                    {
                                        for (int i = 0; i < currentElement.children.length; ++i)
                                        {
                                            Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + i];
                                            if (i > 0)
                                            {
                                                __AddRenderCommand(new Clay_RenderCommand
                                                {
                                                    boundingBox = new Clay_BoundingBox(
                                                        borderBoundingBox.x + scrollOffset.X,
                                                        borderBoundingBox.y + borderOffset.Y + scrollOffset.Y - halfWidth,
                                                        currentElement.dimensions.width,
                                                        borderConfig.width.betweenChildren),
                                                    renderData = new Clay_RenderData
                                                    {
                                                        rectangle = new Clay_RectangleRenderData { backgroundColor = borderConfig.color },
                                                    },
                                                    userData = currentElement.config.userData,
                                                    id = __HashNumber(currentElement.id, (uint)(currentElement.children.length + 1 + i)).id,
                                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE,
                                                });
                                            }
                                            borderOffset.Y += childElement.dimensions.height + layoutConfig.childGap;
                                        }
                                    }
                                }
                            }

                            if (currentElement.config.overlayColor.a > 0)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_OVERLAY_COLOR_END,
                                });
                            }
                            // This exists because the scissor needs to end _after_ borders between elements.
                            if (closeClipElement)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    id = __HashNumber(currentElement.id, (uint)(rootElement.children.length + 11)).id,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_END,
                                });
                            }
                        }

                        dfsBuffer.length--;
                        continue;
                    }

                    // This will only be run a single time for each element in downwards DFS order.
                    context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = true;
                    Clay_BoundingBox currentElementBoundingBox = new Clay_BoundingBox(currentElementTreeNode.position.X, currentElementTreeNode.position.Y, currentElement.dimensions.width, currentElement.dimensions.height);
                    ref Clay__ScrollContainerDataInternal scrollContainerData = ref Unsafe.NullRef<Clay__ScrollContainerDataInternal>();

                    if (!currentElement.isTextElement)
                    {
                        if (useStoredBoundingBoxes && currentElement.config.transition.handler != null)
                        {
                            bool found = false;
                            for (int j = 0; j < context.transitionDatas.length; ++j)
                            {
                                ref Clay__TransitionDataInternal transitionData = ref context.transitionDatas.internalArray[j];
                                if (transitionData.elementId == currentElement.id)
                                {
                                    found = true;
                                    if (transitionData.state != Clay_TransitionState.CLAY_TRANSITION_STATE_IDLE)
                                    {
                                        if ((transitionData.activeProperties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_X) != 0) currentElementBoundingBox.x = transitionData.currentState.boundingBox.x;
                                        if ((transitionData.activeProperties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_Y) != 0) currentElementBoundingBox.y = transitionData.currentState.boundingBox.y;
                                        if ((transitionData.activeProperties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_WIDTH) != 0) currentElementBoundingBox.width = transitionData.currentState.boundingBox.width;
                                        if ((transitionData.activeProperties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_HEIGHT) != 0) currentElementBoundingBox.height = transitionData.currentState.boundingBox.height;
                                    }
                                    break;
                                }
                            }
                            // An exiting element that completed its transition this frame - skip tree.
                            if (!found && currentElement.config.transition.exit.setFinalState != null)
                            {
                                dfsBuffer.length--;
                                continue;
                            }
                        }

                        if (currentElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
                        {
                            ref Clay_FloatingElementConfig floatingElementConfig = ref currentElement.config.floating;
                            Clay_Dimensions expand = floatingElementConfig.expand;
                            currentElementBoundingBox.x -= expand.width;
                            currentElementBoundingBox.width += expand.width * 2;
                            currentElementBoundingBox.y -= expand.height;
                            currentElementBoundingBox.height += expand.height * 2;
                        }

                        // Apply scroll offsets to container.
                        if (currentElement.config.clip.horizontal || currentElement.config.clip.vertical)
                        {
                            // This linear scan could theoretically be slow under very strange conditions.
                            for (int i = 0; i < context.scrollContainerDatas.length; i++)
                            {
                                ref Clay__ScrollContainerDataInternal mapping = ref context.scrollContainerDatas.internalArray[i];
                                if (mapping.layoutElement == currentElement)
                                {
                                    scrollContainerData = ref mapping;
                                    mapping.boundingBox = currentElementBoundingBox;
                                    scrollOffset = currentElement.config.clip.childOffset;
                                    if (context.externalScrollHandlingEnabled)
                                    {
                                        scrollOffset = default;
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    bool offscreen = __ElementIsOffscreen(in currentElementBoundingBox);

                    // Generate render commands for current element.
                    if (generateRenderCommands && !offscreen)
                    {
                        if (currentElement.isTextElement)
                        {
                            ref Clay_TextElementConfig textElementConfig = ref currentElement.textConfig;
                            float naturalLineHeight = currentElement.textElementData.preferredDimensions.height;
                            float finalLineHeight = textElementConfig.lineHeight > 0 ? textElementConfig.lineHeight : naturalLineHeight;
                            float lineHeightOffset = (finalLineHeight - naturalLineHeight) / 2;
                            float yPosition = lineHeightOffset;
                            for (int lineIndex = 0; lineIndex < currentElement.textElementData.wrappedLines.length; ++lineIndex)
                            {
                                Clay__WrappedTextLine wrappedLine = currentElement.textElementData.wrappedLines.internalArray[currentElement.textElementData.wrappedLines.offset + lineIndex];
                                if (wrappedLine.line.Length == 0)
                                {
                                    yPosition += finalLineHeight;
                                    continue;
                                }
                                float offset = currentElementBoundingBox.width - wrappedLine.dimensions.width;
                                if (textElementConfig.textAlignment == Clay_TextAlignment.CLAY_TEXT_ALIGN_LEFT)
                                {
                                    offset = 0;
                                }
                                if (textElementConfig.textAlignment == Clay_TextAlignment.CLAY_TEXT_ALIGN_CENTER)
                                {
                                    offset /= 2;
                                }
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = new Clay_BoundingBox(currentElementBoundingBox.x + offset, currentElementBoundingBox.y + yPosition, wrappedLine.dimensions.width, wrappedLine.dimensions.height),
                                    renderData = new Clay_RenderData
                                    {
                                        text = new Clay_TextRenderData
                                        {
                                            stringContents = wrappedLine.line,
                                            textColor = textElementConfig.textColor,
                                            fontId = textElementConfig.fontId,
                                            fontSize = textElementConfig.fontSize,
                                            letterSpacing = textElementConfig.letterSpacing,
                                            lineHeight = textElementConfig.lineHeight,
                                        },
                                    },
                                    userData = textElementConfig.userData,
                                    id = __HashNumber((uint)lineIndex, currentElement.id).id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT,
                                });
                                yPosition += finalLineHeight;

                                if (!context.disableCulling && currentElementBoundingBox.y + yPosition > context.layoutDimensions.height)
                                {
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (currentElement.config.overlayColor.a > 0)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    renderData = new Clay_RenderData
                                    {
                                        overlayColor = new Clay_OverlayColorRenderData { color = currentElement.config.overlayColor },
                                    },
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_OVERLAY_COLOR_START,
                                });
                            }
                            if (currentElement.config.image.imageData != null)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = currentElementBoundingBox,
                                    renderData = new Clay_RenderData
                                    {
                                        image = new Clay_ImageRenderData
                                        {
                                            backgroundColor = currentElement.config.backgroundColor,
                                            cornerRadius = currentElement.config.cornerRadius,
                                            imageData = currentElement.config.image.imageData,
                                        },
                                    },
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_IMAGE,
                                });
                            }
                            if (currentElement.config.custom.customData != null)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = currentElementBoundingBox,
                                    renderData = new Clay_RenderData
                                    {
                                        custom = new Clay_CustomRenderData
                                        {
                                            backgroundColor = currentElement.config.backgroundColor,
                                            cornerRadius = currentElement.config.cornerRadius,
                                            customData = currentElement.config.custom.customData,
                                        },
                                    },
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_CUSTOM,
                                });
                            }
                            if (currentElement.config.clip.horizontal || currentElement.config.clip.vertical)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = currentElementBoundingBox,
                                    renderData = new Clay_RenderData
                                    {
                                        clip = new Clay_ClipRenderData
                                        {
                                            horizontal = currentElement.config.clip.horizontal,
                                            vertical = currentElement.config.clip.vertical,
                                        },
                                    },
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_START,
                                });
                            }
                            if (currentElement.config.backgroundColor.a > 0)
                            {
                                __AddRenderCommand(new Clay_RenderCommand
                                {
                                    boundingBox = currentElementBoundingBox,
                                    renderData = new Clay_RenderData
                                    {
                                        rectangle = new Clay_RectangleRenderData
                                        {
                                            backgroundColor = currentElement.config.backgroundColor,
                                            cornerRadius = currentElement.config.cornerRadius,
                                        },
                                    },
                                    userData = currentElement.config.userData,
                                    id = currentElement.id,
                                    zIndex = root.zIndex,
                                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_RECTANGLE,
                                });
                            }
                        }
                    }

                    ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(currentElement.id);
                    if (!Unsafe.IsNullRef(in hashMapItem)) hashMapItem.boundingBox = currentElementBoundingBox;

                    if (currentElement.isTextElement) continue;

                    // Setup positions for child elements and add to DFS buffer.

                    // On-axis alignment.
                    Clay_Dimensions contentSizeCurrent = default;
                    if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                    {
                        for (int i = 0; i < currentElement.children.length; ++i)
                        {
                            Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + i];
                            if (childElement.exiting) continue;
                            contentSizeCurrent.width += childElement.dimensions.width;
                            contentSizeCurrent.height = MathF.Max(contentSizeCurrent.height, childElement.dimensions.height);
                        }
                        contentSizeCurrent.width += MathF.Max(currentElement.children.length - 1, 0) * layoutConfig.childGap;
                        float extraSpace = currentElement.dimensions.width - (layoutConfig.padding.left + layoutConfig.padding.right) - contentSizeCurrent.width;
                        switch (layoutConfig.childAlignment.x)
                        {
                            case Clay_LayoutAlignmentX.CLAY_ALIGN_X_LEFT: extraSpace = 0; break;
                            case Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER: extraSpace /= 2; break;
                            default: break;
                        }
                        extraSpace = MathF.Max(0, extraSpace);
                        currentElementTreeNode.nextChildOffset.X += extraSpace;
                    }
                    else if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM)
                    {
                        for (int i = 0; i < currentElement.children.length; ++i)
                        {
                            Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + i];
                            if (childElement.exiting) continue;
                            contentSizeCurrent.width = MathF.Max(contentSizeCurrent.width, childElement.dimensions.width);
                            contentSizeCurrent.height += childElement.dimensions.height;
                        }
                        contentSizeCurrent.height += MathF.Max(currentElement.children.length - 1, 0) * layoutConfig.childGap;
                        float extraSpace = currentElement.dimensions.height - (layoutConfig.padding.top + layoutConfig.padding.bottom) - contentSizeCurrent.height;
                        switch (layoutConfig.childAlignment.y)
                        {
                            case Clay_LayoutAlignmentY.CLAY_ALIGN_Y_TOP: extraSpace = 0; break;
                            case Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER: extraSpace /= 2; break;
                            default: break;
                        }
                        extraSpace = MathF.Max(0, extraSpace);
                        currentElementTreeNode.nextChildOffset.Y += extraSpace;
                    }

                    if (!Unsafe.IsNullRef(in scrollContainerData))
                    {
                        scrollContainerData.contentSize = new Clay_Dimensions
                        {
                            width = contentSizeCurrent.width + layoutConfig.padding.left + layoutConfig.padding.right,
                            height = contentSizeCurrent.height + layoutConfig.padding.top + layoutConfig.padding.bottom,
                        };
                    }

                    // Add children to the DFS buffer.
                    dfsBuffer.length += currentElement.children.length;
                    for (int i = 0; i < currentElement.children.length; ++i)
                    {
                        Clay_LayoutElement childElement = currentElement.children.elements[currentElement.children.offset + i];

                        // Alignment along non layout axis.
                        if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                        {
                            currentElementTreeNode.nextChildOffset.Y = currentElement.config.layout.padding.top;
                            float whiteSpaceAroundChild = currentElement.dimensions.height - (layoutConfig.padding.top + layoutConfig.padding.bottom) - childElement.dimensions.height;
                            switch (layoutConfig.childAlignment.y)
                            {
                                case Clay_LayoutAlignmentY.CLAY_ALIGN_Y_TOP: break;
                                case Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER: currentElementTreeNode.nextChildOffset.Y += whiteSpaceAroundChild / 2; break;
                                case Clay_LayoutAlignmentY.CLAY_ALIGN_Y_BOTTOM: currentElementTreeNode.nextChildOffset.Y += whiteSpaceAroundChild; break;
                            }
                        }
                        else
                        {
                            currentElementTreeNode.nextChildOffset.X = currentElement.config.layout.padding.left;
                            float whiteSpaceAroundChild = currentElement.dimensions.width - (layoutConfig.padding.left + layoutConfig.padding.right) - childElement.dimensions.width;
                            switch (layoutConfig.childAlignment.x)
                            {
                                case Clay_LayoutAlignmentX.CLAY_ALIGN_X_LEFT: break;
                                case Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER: currentElementTreeNode.nextChildOffset.X += whiteSpaceAroundChild / 2; break;
                                case Clay_LayoutAlignmentX.CLAY_ALIGN_X_RIGHT: currentElementTreeNode.nextChildOffset.X += whiteSpaceAroundChild; break;
                            }
                        }

                        Vector2 childPosition = new Vector2(
                            currentElementBoundingBox.x + currentElementTreeNode.nextChildOffset.X + scrollOffset.X,
                            currentElementBoundingBox.y + currentElementTreeNode.nextChildOffset.Y + scrollOffset.Y);

                        // DFS buffer elements need to be added in reverse because stack traversal happens backwards.
                        int newNodeIndex = dfsBuffer.length - 1 - i;
                        dfsBuffer.internalArray[newNodeIndex] = new Clay__LayoutElementTreeNode
                        {
                            layoutElement = childElement,
                            position = childPosition,
                            nextChildOffset = new Vector2(childElement.config.layout.padding.left, childElement.config.layout.padding.top),
                        };
                        context.treeNodeVisited.internalArray[newNodeIndex] = false;

                        // Update parent offsets.
                        if (!childElement.exiting)
                        {
                            if (layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT)
                            {
                                currentElementTreeNode.nextChildOffset.X += childElement.dimensions.width + layoutConfig.childGap;
                            }
                            else
                            {
                                currentElementTreeNode.nextChildOffset.Y += childElement.dimensions.height + layoutConfig.childGap;
                            }
                        }
                    }
                }

                if (root.clipElementId != 0)
                {
                    ref Clay_LayoutElementHashMapItem clipHashMapItem = ref __GetHashMapItem(root.clipElementId);
                    if (!Unsafe.IsNullRef(in clipHashMapItem) && !__ElementIsOffscreen(in clipHashMapItem.boundingBox))
                    {
                        __AddRenderCommand(new Clay_RenderCommand
                        {
                            id = __HashNumber(rootElement.id, (uint)(rootElement.children.length + 11)).id,
                            commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_SCISSOR_END,
                        });
                    }
                }
            }
        }

        // -------------------------------------
        // PUBLIC API ---------------------------
        // -------------------------------------

        private static float Lerp(float from, float to, float mix) => from + (to - from) * mix;

        public static Clay_Context Initialize(Clay_Dimensions layoutDimensions, Clay_ErrorHandler errorHandler)
        {
            int maxElementCount = s_currentContext != null ? s_currentContext.maxElementCount : s_defaultMaxElementCount;
            int maxMeasureTextCacheWordCount = s_currentContext != null ? s_currentContext.maxMeasureTextCacheWordCount : s_defaultMaxMeasureTextWordCacheCount;

            var context = new Clay_Context
            {
                maxElementCount = maxElementCount,
                maxMeasureTextCacheWordCount = maxMeasureTextCacheWordCount,
                errorHandler = errorHandler.errorHandlerFunction != null ? errorHandler : default,
                layoutDimensions = layoutDimensions,
            };
            SetCurrentContext(context);
            context.InitializePersistentMemory();
            context.InitializeEphemeralMemory();

            for (int i = 0; i < context.layoutElementsHashMap.capacity; ++i)
            {
                context.layoutElementsHashMap.internalArray[i] = -1;
            }
            for (int i = 0; i < context.measureTextHashMap.capacity; ++i)
            {
                context.measureTextHashMap.internalArray[i] = 0;
            }
            context.measureTextHashMapInternal.length = 1; // Reserve the 0 value to mean "no next element".
            context.layoutDimensions = layoutDimensions;
            return context;
        }

        public static void SetMeasureTextFunction(Clay_MeasureTextFunction measureTextFunction, object? userData)
        {
            var context = GetCurrentContext()!;
            s_measureText = measureTextFunction;
            context.measureTextUserData = userData;
        }

        public static void SetQueryScrollOffsetFunction(Clay_QueryScrollOffsetFunction queryScrollOffsetFunction, object? userData)
        {
            var context = GetCurrentContext()!;
            s_queryScrollOffset = queryScrollOffsetFunction;
            context.queryScrollOffsetUserData = userData;
        }

        public static void SetLayoutDimensions(Clay_Dimensions dimensions)
        {
            var context = GetCurrentContext()!;
            context.rootResizedLastFrame = !__FloatEqual(context.layoutDimensions.width, dimensions.width) || !__FloatEqual(context.layoutDimensions.height, dimensions.height);
            context.layoutDimensions = dimensions;
        }

        public static Clay_Dimensions GetLayoutDimensions() => GetCurrentContext()!.layoutDimensions;

        public static void SetPointerState(Vector2 position, bool isPointerDown)
        {
            var context = GetCurrentContext()!;
            if (context.booleanWarnings.maxElementsExceeded) return;

            context.pointerInfo.position = position;
            context.pointerOverIds.length = 0;

            ClayArray<int> dfsBuffer = context.layoutElementChildrenBuffer;

            for (int rootIndex = context.layoutElementTreeRoots.length - 1; rootIndex >= 0; --rootIndex)
            {
                dfsBuffer.length = 0;
                Clay__LayoutElementTreeRoot root = context.layoutElementTreeRoots.internalArray[rootIndex];
                dfsBuffer.Add(root.layoutElementIndex);
                context.treeNodeVisited.internalArray[0] = false;
                bool found = false;
                bool skipTree = false;

                while (dfsBuffer.length > 0)
                {
                    if (context.treeNodeVisited.internalArray[dfsBuffer.length - 1])
                    {
                        dfsBuffer.length--;
                        continue;
                    }
                    context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = true;

                    int currentElementIndex = dfsBuffer.internalArray[dfsBuffer.length - 1];
                    Clay_LayoutElement currentElement = context.layoutElements.internalArray[currentElementIndex];

                    ref Clay_LayoutElementHashMapItem mapItem = ref __GetHashMapItem(currentElement.id); // TODO think of a way around this.
                    int clipElementId = context.layoutElementClipElementIds.GetValue(currentElementIndex);
                    ref Clay_LayoutElementHashMapItem clipItem = ref __GetHashMapItem((uint)clipElementId);

                    // This check skips mouse interactions for elements that are currently "exit transitioning".
                    if (!Unsafe.IsNullRef(in mapItem) && mapItem.generation > context.generation)
                    {
                        // Conditionally skip mouse interactions on non-exit transitions, based on user config.
                        if (!currentElement.isTextElement && currentElement.config.transition.handler != null)
                        {
                            for (int I = 0; I < context.transitionDatas.length; ++I)
                            {
                                ref Clay__TransitionDataInternal data = ref context.transitionDatas.internalArray[I];
                                if (data.elementId == currentElement.id)
                                {
                                    if (currentElement.config.transition.interactionHandling == Clay_TransitionInteractionHandlingType.CLAY_TRANSITION_DISABLE_INTERACTIONS_WHILE_TRANSITIONING_POSITION)
                                    {
                                        if (data.state == Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING || data.state == Clay_TransitionState.CLAY_TRANSITION_STATE_ENTERING
                                            || ((data.activeProperties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_POSITION) != 0 && data.state == Clay_TransitionState.CLAY_TRANSITION_STATE_TRANSITIONING))
                                        {
                                            skipTree = true;
                                        }
                                    }
                                    else if (currentElement.config.transition.interactionHandling == Clay_TransitionInteractionHandlingType.CLAY_TRANSITION_ALLOW_INTERACTIONS_WHILE_TRANSITIONING_POSITION)
                                    {
                                        if (data.state == Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                                        {
                                            skipTree = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (skipTree)
                        {
                            dfsBuffer.length--;
                            continue;
                        }

                        Clay_BoundingBox elementBox = mapItem.boundingBox;
                        elementBox.x -= root.pointerOffset.X;
                        elementBox.y -= root.pointerOffset.Y;
                        if (__PointIsInsideRect(position, elementBox)
                            && (clipElementId == 0 || (!Unsafe.IsNullRef(in clipItem) && __PointIsInsideRect(position, clipItem.boundingBox)) || context.externalScrollHandlingEnabled))
                        {
                            mapItem.onHoverFunction?.Invoke(mapItem.elementId, context.pointerInfo, mapItem.hoverFunctionUserData);
                            context.pointerOverIds.Add(mapItem.elementId);
                            found = true;
                        }

                        for (int i = currentElement.children.length - 1; i >= 0; --i)
                        {
                            dfsBuffer.Add(currentElement.children.elements[currentElement.children.offset + i].index);
                            context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = false; // TODO needs to be ranged checked.
                        }
                    }
                    else
                    {
                        dfsBuffer.length--;
                    }
                }

                Clay_LayoutElement rootElement = context.layoutElements.internalArray[root.layoutElementIndex];
                if (found && rootElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE
                    && rootElement.config.floating.pointerCaptureMode == Clay_PointerCaptureMode.CLAY_POINTER_CAPTURE_MODE_CAPTURE)
                {
                    break;
                }
            }

            if (isPointerDown)
            {
                if (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
                {
                    context.pointerInfo.state = Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED;
                }
                else if (context.pointerInfo.state != Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED)
                {
                    context.pointerInfo.state = Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME;
                }
            }
            else
            {
                if (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_RELEASED_THIS_FRAME)
                {
                    context.pointerInfo.state = Clay_PointerDataInteractionState.CLAY_POINTER_DATA_RELEASED;
                }
                else if (context.pointerInfo.state != Clay_PointerDataInteractionState.CLAY_POINTER_DATA_RELEASED)
                {
                    context.pointerInfo.state = Clay_PointerDataInteractionState.CLAY_POINTER_DATA_RELEASED_THIS_FRAME;
                }
            }
        }

        public static Clay_PointerData GetPointerState() => GetCurrentContext()!.pointerInfo;

        public static Vector2 GetScrollOffset()
        {
            var context = GetCurrentContext()!;
            if (context.booleanWarnings.maxElementsExceeded) return default;
            Clay_LayoutElement openLayoutElement = __GetOpenLayoutElement();
            for (int i = 0; i < context.scrollContainerDatas.length; i++)
            {
                Clay__ScrollContainerDataInternal mapping = context.scrollContainerDatas.internalArray[i];
                if (mapping.elementId == openLayoutElement.id) return mapping.scrollPosition;
            }
            return default;
        }

        public static void UpdateScrollContainers(bool enableDragScrolling, Vector2 scrollDelta, float deltaTime)
        {
            var context = GetCurrentContext()!;
            bool isPointerActive = enableDragScrolling && (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED
                || context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME);

            // Don't apply scroll events to ancestors of the inner element.
            int highestPriorityElementIndex = -1;
            ref Clay__ScrollContainerDataInternal highestPriorityScrollData = ref Unsafe.NullRef<Clay__ScrollContainerDataInternal>();

            for (int i = 0; i < context.scrollContainerDatas.length; i++)
            {
                ref Clay__ScrollContainerDataInternal scrollData = ref context.scrollContainerDatas.internalArray[i];
                if (!scrollData.openThisFrame)
                {
                    context.scrollContainerDatas.RemoveSwapback(i);
                    continue;
                }
                scrollData.openThisFrame = false;
                ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(scrollData.elementId);
                // Element isn't rendered this frame but scroll offset has been retained.
                if (Unsafe.IsNullRef(in hashMapItem))
                {
                    context.scrollContainerDatas.RemoveSwapback(i);
                    continue;
                }

                // Touch / click is released.
                if (!isPointerActive && scrollData.pointerScrollActive)
                {
                    float xDiff = scrollData.scrollPosition.X - scrollData.scrollOrigin.X;
                    if (xDiff < -10 || xDiff > 10)
                    {
                        scrollData.scrollMomentum.X = (scrollData.scrollPosition.X - scrollData.scrollOrigin.X) / (scrollData.momentumTime * 25);
                    }
                    float yDiff = scrollData.scrollPosition.Y - scrollData.scrollOrigin.Y;
                    if (yDiff < -10 || yDiff > 10)
                    {
                        scrollData.scrollMomentum.Y = (scrollData.scrollPosition.Y - scrollData.scrollOrigin.Y) / (scrollData.momentumTime * 25);
                    }
                    scrollData.pointerScrollActive = false;
                    scrollData.pointerOrigin = default;
                    scrollData.scrollOrigin = default;
                    scrollData.momentumTime = 0;
                }

                // Apply existing momentum.
                scrollData.scrollPosition.X += scrollData.scrollMomentum.X;
                scrollData.scrollMomentum.X *= 0.95f;
                bool scrollOccurred = scrollDelta.X != 0 || scrollDelta.Y != 0;
                if ((scrollData.scrollMomentum.X > -0.1f && scrollData.scrollMomentum.X < 0.1f) || scrollOccurred)
                {
                    scrollData.scrollMomentum.X = 0;
                }
                scrollData.scrollPosition.X = MathF.Min(MathF.Max(scrollData.scrollPosition.X, -MathF.Max(scrollData.contentSize.width - scrollData.layoutElement.dimensions.width, 0)), 0);

                scrollData.scrollPosition.Y += scrollData.scrollMomentum.Y;
                scrollData.scrollMomentum.Y *= 0.95f;
                if ((scrollData.scrollMomentum.Y > -0.1f && scrollData.scrollMomentum.Y < 0.1f) || scrollOccurred)
                {
                    scrollData.scrollMomentum.Y = 0;
                }
                scrollData.scrollPosition.Y = MathF.Min(MathF.Max(scrollData.scrollPosition.Y, -MathF.Max(scrollData.contentSize.height - scrollData.layoutElement.dimensions.height, 0)), 0);

                for (int j = 0; j < context.pointerOverIds.length; ++j) // TODO n & m are small here but n*m gives me the creeps.
                {
                    if (scrollData.layoutElement.id == context.pointerOverIds.internalArray[j].id)
                    {
                        highestPriorityElementIndex = j;
                        highestPriorityScrollData = ref scrollData;
                    }
                }
            }

            if (highestPriorityElementIndex > -1 && !Unsafe.IsNullRef(in highestPriorityScrollData))
            {
                Clay_LayoutElement scrollElement = highestPriorityScrollData.layoutElement;
                ref Clay_ClipElementConfig clipConfig = ref scrollElement.config.clip;
                bool canScrollVertically = clipConfig.vertical && highestPriorityScrollData.contentSize.height > scrollElement.dimensions.height;
                bool canScrollHorizontally = clipConfig.horizontal && highestPriorityScrollData.contentSize.width > scrollElement.dimensions.width;

                // Handle wheel scroll.
                if (canScrollVertically)
                {
                    highestPriorityScrollData.scrollPosition.Y = highestPriorityScrollData.scrollPosition.Y + scrollDelta.Y * 10;
                }
                if (canScrollHorizontally)
                {
                    highestPriorityScrollData.scrollPosition.X = highestPriorityScrollData.scrollPosition.X + scrollDelta.X * 10;
                }

                // Handle click / touch scroll.
                if (isPointerActive)
                {
                    highestPriorityScrollData.scrollMomentum = default;
                    if (!highestPriorityScrollData.pointerScrollActive)
                    {
                        highestPriorityScrollData.pointerOrigin = context.pointerInfo.position;
                        highestPriorityScrollData.scrollOrigin = highestPriorityScrollData.scrollPosition;
                        highestPriorityScrollData.pointerScrollActive = true;
                    }
                    else
                    {
                        float scrollDeltaX = 0, scrollDeltaY = 0;
                        if (canScrollHorizontally)
                        {
                            float oldXScrollPosition = highestPriorityScrollData.scrollPosition.X;
                            highestPriorityScrollData.scrollPosition.X = highestPriorityScrollData.scrollOrigin.X + (context.pointerInfo.position.X - highestPriorityScrollData.pointerOrigin.X);
                            highestPriorityScrollData.scrollPosition.X = MathF.Max(MathF.Min(highestPriorityScrollData.scrollPosition.X, 0), -(highestPriorityScrollData.contentSize.width - highestPriorityScrollData.boundingBox.width));
                            scrollDeltaX = highestPriorityScrollData.scrollPosition.X - oldXScrollPosition;
                        }
                        if (canScrollVertically)
                        {
                            float oldYScrollPosition = highestPriorityScrollData.scrollPosition.Y;
                            highestPriorityScrollData.scrollPosition.Y = highestPriorityScrollData.scrollOrigin.Y + (context.pointerInfo.position.Y - highestPriorityScrollData.pointerOrigin.Y);
                            highestPriorityScrollData.scrollPosition.Y = MathF.Max(MathF.Min(highestPriorityScrollData.scrollPosition.Y, 0), -(highestPriorityScrollData.contentSize.height - highestPriorityScrollData.boundingBox.height));
                            scrollDeltaY = highestPriorityScrollData.scrollPosition.Y - oldYScrollPosition;
                        }
                        if (scrollDeltaX > -0.1f && scrollDeltaX < 0.1f && scrollDeltaY > -0.1f && scrollDeltaY < 0.1f && highestPriorityScrollData.momentumTime > 0.15f)
                        {
                            highestPriorityScrollData.momentumTime = 0;
                            highestPriorityScrollData.pointerOrigin = context.pointerInfo.position;
                            highestPriorityScrollData.scrollOrigin = highestPriorityScrollData.scrollPosition;
                        }
                        else
                        {
                            highestPriorityScrollData.momentumTime += deltaTime;
                        }
                    }
                }

                // Clamp any changes to scroll position to the maximum size of the contents.
                if (canScrollVertically)
                {
                    highestPriorityScrollData.scrollPosition.Y = MathF.Max(MathF.Min(highestPriorityScrollData.scrollPosition.Y, 0), -(highestPriorityScrollData.contentSize.height - scrollElement.dimensions.height));
                }
                if (canScrollHorizontally)
                {
                    highestPriorityScrollData.scrollPosition.X = MathF.Max(MathF.Min(highestPriorityScrollData.scrollPosition.X, 0), -(highestPriorityScrollData.contentSize.width - scrollElement.dimensions.width));
                }
            }
        }

        public static void BeginLayout()
        {
            var context = GetCurrentContext()!;
            context.InitializeEphemeralMemory();
            context.generation++;
            context.dynamicElementIndex = 0;

            // Set up the root container that covers the entire window.
            Clay_Dimensions rootDimensions = new Clay_Dimensions { width = context.layoutDimensions.width, height = context.layoutDimensions.height };
            if (context.debugModeEnabled)
            {
                // The debug inspector consumes the right-hand strip, so keep the root width reduction for parity with C.
                rootDimensions.width -= __debugViewWidth;
            }
            context.booleanWarnings = default;
            __OpenElementWithId(Id("Clay__RootContainer"));
            __ConfigureOpenElement(new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig
                {
                    sizing = new Clay_Sizing
                    {
                        width = SizingFixed(rootDimensions.width),
                        height = SizingFixed(rootDimensions.height),
                    },
                },
            });
            context.openLayoutElementStack.Add(0);
            context.layoutElementTreeRoots.Add(new Clay__LayoutElementTreeRoot { layoutElementIndex = 0 });
        }

        internal static void __ApplyTransitionedPropertiesToElement(Clay_LayoutElement currentElement, Clay_TransitionProperty properties, Clay_TransitionData currentTransitionData, ref Clay_BoundingBox boundingBox, bool reparented)
        {
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_WIDTH) != 0)
            {
                if (!reparented)
                {
                    currentElement.dimensions.width = currentTransitionData.boundingBox.width;
                    currentElement.config.layout.sizing.width = SizingFixed(currentTransitionData.boundingBox.width);
                }
                else
                {
                    boundingBox.width = currentTransitionData.boundingBox.width;
                }
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_HEIGHT) != 0)
            {
                if (!reparented)
                {
                    currentElement.dimensions.height = currentTransitionData.boundingBox.height;
                    currentElement.config.layout.sizing.height = SizingFixed(currentTransitionData.boundingBox.height);
                }
                else
                {
                    boundingBox.height = currentTransitionData.boundingBox.height;
                }
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_X) != 0)
            {
                boundingBox.x = currentTransitionData.boundingBox.x;
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_Y) != 0)
            {
                boundingBox.y = currentTransitionData.boundingBox.y;
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_OVERLAY_COLOR) != 0)
            {
                currentElement.config.overlayColor = currentTransitionData.overlayColor;
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BACKGROUND_COLOR) != 0)
            {
                currentElement.config.backgroundColor = currentTransitionData.backgroundColor;
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_COLOR) != 0)
            {
                currentElement.config.border.color = currentTransitionData.borderColor;
            }
            if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_WIDTH) != 0)
            {
                currentElement.config.border.width = currentTransitionData.borderWidth;
            }
        }

        public static Clay_RenderCommandArray EndLayout(float deltaTime)
        {
            var context = GetCurrentContext()!;
            __CloseElement();

            if (context.openLayoutElementStack.length > 1)
            {
                context.Error(Clay_ErrorType.CLAY_ERROR_TYPE_UNBALANCED_OPEN_CLOSE,
                    "There were still open layout elements when EndLayout was called. This results from an unequal number of calls to Clay__OpenElement and Clay__CloseElement.");
            }

            // Prune non exiting transitions.
            for (int i = 0; i < context.transitionDatas.length; ++i)
            {
                ref Clay__TransitionDataInternal data = ref context.transitionDatas.internalArray[i];
                ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(data.elementId);
                // Transition element exited and doesn't have an exit handler defined,
                // or the user deleted the transition handler from one frame to the next.
                if (!data.transitionOut
                    && (Unsafe.IsNullRef(in hashMapItem) || hashMapItem.generation <= context.generation || hashMapItem.layoutElement == null || hashMapItem.layoutElement.config.transition.handler == null))
                {
                    context.transitionDatas.RemoveSwapback(i);
                    i--;
                    continue;
                }
            }

            ClayArray<int> elementIdsToRemoveTransitions = context.reusableElementIndexBuffer;
            elementIdsToRemoveTransitions.length = 0;

            for (int i = 0; i < context.transitionDatas.length; ++i)
            {
                ref Clay__TransitionDataInternal data = ref context.transitionDatas.internalArray[i];
                ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(data.elementId);
                if (data.transitionOut)
                {
                    Clay_TransitionElementConfig config = data.elementThisFrame.config.transition;
                    // Element wasn't found this frame - either delete transition data or transition out.
                    if (!Unsafe.IsNullRef(in hashMapItem) && hashMapItem.generation <= context.generation)
                    {
                        ref Clay_LayoutElementHashMapItem parentHashMapItem = ref __GetHashMapItem(data.parentId);
                        // Don't exit transition if the parent has also exited and SKIP_WHEN_PARENT_EXITS is used.
                        if (config.exit.trigger == Clay_TransitionExitTriggerType.CLAY_TRANSITION_EXIT_TRIGGER_WHEN_PARENT_EXITS
                            || Unsafe.IsNullRef(in parentHashMapItem) || parentHashMapItem.generation > context.generation)
                        {
                            // This if only runs one single time when the element first starts exiting.
                            if (data.state != Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                            {
                                if (Unsafe.IsNullRef(in parentHashMapItem) || parentHashMapItem.generation <= context.generation)
                                {
                                    data.elementThisFrame.config.floating.attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ROOT;
                                    data.elementThisFrame.config.floating.offset = new Vector2(hashMapItem.boundingBox.x, hashMapItem.boundingBox.y);
                                    data.elementThisFrame.config.floating.parentId = __HashString("Clay__RootContainer", 0).id;
                                }
                                hashMapItem.appearedThisFrame = false;
                                data.elementThisFrame.exiting = true;
                                data.elementThisFrame.config.layout.sizing.width = SizingFixed(data.elementThisFrame.dimensions.width);
                                data.elementThisFrame.config.layout.sizing.height = SizingFixed(data.elementThisFrame.dimensions.height);
                                data.state = Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING;
                                data.activeProperties = config.properties;
                                data.elapsedTime = 0;
                                data.targetState = config.exit.setFinalState!(data.targetState, config.properties);
                            }

                            // Below this line runs every frame while element is exiting.

                            // Clone the entire subtree back into the main UI layout tree.
                            ClayArray<int> bfsBuffer = context.openLayoutElementStack;
                            bfsBuffer.length = 0;
                            int oldElementIndex = data.elementThisFrame.index;
                            Clay_LayoutElement exitingElement = data.elementThisFrame.Clone();
                            context.layoutElements.Add(exitingElement);
                            int exitingElementIndex = context.layoutElements.length - 1;
                            exitingElement.index = exitingElementIndex;
                            context.layoutElementClipElementIds.Set(exitingElementIndex, context.layoutElementClipElementIds.GetValue(oldElementIndex));
                            data.elementThisFrame = exitingElement;
                            bfsBuffer.Add(exitingElementIndex);

                            int bufferIndex = 0;
                            while (bufferIndex < bfsBuffer.length)
                            {
                                Clay_LayoutElement layoutElement = context.layoutElements.internalArray[bfsBuffer.internalArray[bufferIndex]];
                                ref Clay_LayoutElementHashMapItem bfsMapItem = ref __GetHashMapItem(layoutElement.id);
                                // Children of exiting elements may have been moved elsewhere in the layout; this prevents a duplicate ID error.
                                if (Unsafe.IsNullRef(in bfsMapItem) || bfsMapItem.generation <= context.generation)
                                {
                                    __AddHashMapItem(new Clay_ElementId { id = layoutElement.id }, layoutElement, layoutElement.index);
                                    int firstChildSlot = context.layoutElementChildren.length;
                                    ushort newChildrenLength = layoutElement.children.length;
                                    for (int j = 0; j < layoutElement.children.length; ++j)
                                    {
                                        Clay_LayoutElement childElement = layoutElement.children.elements[layoutElement.children.offset + j];
                                        ref Clay_LayoutElementHashMapItem childMapItem = ref __GetHashMapItem(childElement.id);
                                        if (Unsafe.IsNullRef(in childMapItem) || childMapItem.generation <= context.generation)
                                        {
                                            // Remove any nested transitions inside exiting trees.
                                            if (!childElement.isTextElement && childElement.config.transition.handler != null)
                                            {
                                                elementIdsToRemoveTransitions.Add((int)childElement.id);
                                            }
                                            int oldChildIndex = childElement.index;
                                            Clay_LayoutElement newChildElement = childElement.Clone();
                                            context.layoutElements.Add(newChildElement);
                                            int newChildIndex = context.layoutElements.length - 1;
                                            newChildElement.index = newChildIndex;
                                            context.layoutElementClipElementIds.Set(newChildIndex, context.layoutElementClipElementIds.GetValue(oldChildIndex));
                                            bfsBuffer.Add(newChildIndex);
                                            if (newChildElement.isTextElement)
                                            {
                                                newChildElement.textElementData.wrappedLines.length = 0;
                                            }
                                            context.layoutElementChildren.Add(newChildElement);
                                        }
                                        else
                                        {
                                            newChildrenLength--;
                                        }
                                    }
                                    layoutElement.children = new Clay__LayoutElementChildren
                                    {
                                        elements = context.layoutElementChildren.internalArray,
                                        offset = firstChildSlot,
                                        length = newChildrenLength,
                                    };
                                }
                                bufferIndex++;
                            }
                            hashMapItem.layoutElement = exitingElement;
                            hashMapItem.layoutElementIndex = exitingElementIndex;

                            // Reattach the inserted subtree to its previous parent if it still exists and the exiting element is not floating.
                            Clay_FloatingElementConfig floatingConfig = hashMapItem.layoutElement.config.floating;
                            if (!Unsafe.IsNullRef(in parentHashMapItem) && parentHashMapItem.generation > context.generation && floatingConfig.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
                            {
                                Clay_LayoutElement parentElement = parentHashMapItem.layoutElement;
                                int newChildrenStartIndex = context.layoutElementChildren.length;
                                bool found = false;
                                if (config.exit.siblingOrdering == Clay_ExitTransitionSiblingOrdering.CLAY_EXIT_TRANSITION_ORDERING_UNDERNEATH_SIBLINGS)
                                {
                                    context.layoutElementChildren.Add(exitingElement);
                                    found = true;
                                }
                                for (int j = 0; j < parentElement.children.length; ++j)
                                {
                                    if (config.exit.siblingOrdering == Clay_ExitTransitionSiblingOrdering.CLAY_EXIT_TRANSITION_ORDERING_NATURAL_ORDER && j == data.siblingIndex)
                                    {
                                        context.layoutElementChildren.Add(exitingElement);
                                        found = true;
                                    }
                                    context.layoutElementChildren.Add(parentElement.children.elements[parentElement.children.offset + j]);
                                }
                                if (!found)
                                {
                                    context.layoutElementChildren.Add(exitingElement);
                                }
                                parentElement.children.length++;
                                parentElement.children.elements = context.layoutElementChildren.internalArray;
                                parentElement.children.offset = newChildrenStartIndex;
                            }
                            // Otherwise, create the tree root for the floating element (needs to be created every frame).
                            else
                            {
                                context.layoutElementTreeRoots.Add(new Clay__LayoutElementTreeRoot
                                {
                                    layoutElementIndex = exitingElementIndex,
                                    parentId = floatingConfig.parentId,
                                    zIndex = floatingConfig.zIndex,
                                });
                            }
                        }
                        // Parent exited, just delete child without exit transition.
                        else
                        {
                            context.transitionDatas.RemoveSwapback(i);
                            i--;
                            continue;
                        }
                    }
                }
            }

            // Remove nested transitions.
            for (int i = 0; i < elementIdsToRemoveTransitions.length; ++i)
            {
                for (int j = 0; j < context.transitionDatas.length; ++j)
                {
                    if (context.transitionDatas.internalArray[j].elementId == (uint)elementIdsToRemoveTransitions.internalArray[i])
                    {
                        context.transitionDatas.RemoveSwapback(j);
                        break;
                    }
                }
            }

            if (context.booleanWarnings.maxElementsExceeded)
            {
                const string message = "Clay Error: Layout elements exceeded Clay__maxElementCount";
                __AddRenderCommand(new Clay_RenderCommand
                {
                    boundingBox = new Clay_BoundingBox(context.layoutDimensions.width / 2 - 59 * 4, context.layoutDimensions.height / 2, 0, 0),
                    renderData = new Clay_RenderData
                    {
                        text = new Clay_TextRenderData
                        {
                            stringContents = new StringSegment(message),
                            textColor = new Clay_Color(255, 0, 0, 255),
                            fontSize = 16,
                        },
                    },
                    commandType = Clay_RenderCommandType.CLAY_RENDER_COMMAND_TYPE_TEXT,
                });
            }
            else
            {
                if (context.transitionDatas.length > 0)
                {
                    __CalculateFinalLayout(deltaTime, false, false);

                    for (int i = 0; i < context.transitionDatas.length; ++i)
                    {
                        ref Clay__TransitionDataInternal transitionData = ref context.transitionDatas.internalArray[i];
                        Clay_LayoutElement currentElement = transitionData.elementThisFrame;
                        ref Clay_LayoutElementHashMapItem mapItem = ref __GetHashMapItem(transitionData.elementId);
                        if (Unsafe.IsNullRef(in mapItem)) continue;
                        ref Clay_LayoutElementHashMapItem parentMapItem = ref __GetHashMapItem(transitionData.parentId);

                        Clay_TransitionData targetState = transitionData.targetState;
                        if (transitionData.state != Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                        {
                            targetState = new Clay_TransitionData
                            {
                                boundingBox = mapItem.boundingBox,
                                backgroundColor = currentElement.config.backgroundColor,
                                overlayColor = currentElement.config.overlayColor,
                                borderColor = currentElement.config.border.color,
                                borderWidth = currentElement.config.border.width,
                            };
                        }
                        Clay_TransitionData oldTargetState = transitionData.targetState;
                        transitionData.targetState = targetState;

                        if (mapItem.appearedThisFrame)
                        {
                            if (currentElement.config.transition.enter.setInitialState != null
                                && !(!Unsafe.IsNullRef(in parentMapItem) && parentMapItem.appearedThisFrame && currentElement.config.transition.enter.trigger == Clay_TransitionEnterTriggerType.CLAY_TRANSITION_ENTER_SKIP_ON_FIRST_PARENT_FRAME))
                            {
                                transitionData.state = Clay_TransitionState.CLAY_TRANSITION_STATE_ENTERING;
                                transitionData.initialState = currentElement.config.transition.enter.setInitialState(transitionData.targetState, currentElement.config.transition.properties);
                                transitionData.currentState = transitionData.initialState;
                                transitionData.activeProperties = currentElement.config.transition.properties;
                                __ApplyTransitionedPropertiesToElement(currentElement, currentElement.config.transition.properties, transitionData.initialState, ref mapItem.boundingBox, transitionData.reparented);
                            }
                            else
                            {
                                transitionData.initialState = targetState;
                                transitionData.currentState = targetState;
                                transitionData.activeProperties = Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_NONE;
                            }
                        }
                        else
                        {
                            if (transitionData.state != Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                            {
                                Vector2 parentScrollOffset = !Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.layoutElement.config.clip.childOffset : default;
                                Vector2 newRelativePosition = new Vector2(
                                    mapItem.boundingBox.x - (!Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.boundingBox.x : 0) - parentScrollOffset.X,
                                    mapItem.boundingBox.y - (!Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.boundingBox.y : 0) - parentScrollOffset.Y);
                                Vector2 oldRelativePosition = transitionData.oldParentRelativePosition;
                                transitionData.oldParentRelativePosition = newRelativePosition;

                                Clay_TransitionProperty properties = currentElement.config.transition.properties;
                                Clay_TransitionProperty newActiveProperties = Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_NONE;
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_X) != 0)
                                {
                                    if (!__FloatEqual(oldTargetState.boundingBox.x, targetState.boundingBox.x)
                                        && (!__FloatEqual(oldRelativePosition.X, newRelativePosition.X) || transitionData.reparented)
                                        && !context.rootResizedLastFrame)
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_X;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_Y) != 0)
                                {
                                    if (!__FloatEqual(oldTargetState.boundingBox.y, targetState.boundingBox.y)
                                        && (!__FloatEqual(oldRelativePosition.Y, newRelativePosition.Y) || transitionData.reparented)
                                        && !context.rootResizedLastFrame)
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_Y;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_WIDTH) != 0)
                                {
                                    if (!__FloatEqual(oldTargetState.boundingBox.width, targetState.boundingBox.width) && !context.rootResizedLastFrame)
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_WIDTH;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_HEIGHT) != 0)
                                {
                                    if (!__FloatEqual(oldTargetState.boundingBox.height, targetState.boundingBox.height) && !context.rootResizedLastFrame)
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_HEIGHT;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BACKGROUND_COLOR) != 0)
                                {
                                    if (!__ColorEqual(oldTargetState.backgroundColor, targetState.backgroundColor))
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BACKGROUND_COLOR;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_OVERLAY_COLOR) != 0)
                                {
                                    if (!__ColorEqual(oldTargetState.overlayColor, targetState.overlayColor))
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_OVERLAY_COLOR;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_COLOR) != 0)
                                {
                                    if (!__ColorEqual(oldTargetState.borderColor, targetState.borderColor))
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_COLOR;
                                    }
                                }
                                if ((properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_WIDTH) != 0)
                                {
                                    if (!__BorderWidthEqual(oldTargetState.borderWidth, targetState.borderWidth))
                                    {
                                        newActiveProperties |= Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_WIDTH;
                                    }
                                }

                                if (newActiveProperties != 0)
                                {
                                    transitionData.elapsedTime = 0;
                                    transitionData.initialState = transitionData.currentState;
                                    transitionData.state = Clay_TransitionState.CLAY_TRANSITION_STATE_TRANSITIONING;
                                    transitionData.activeProperties |= newActiveProperties;
                                }
                            }

                            if (transitionData.state == Clay_TransitionState.CLAY_TRANSITION_STATE_IDLE)
                            {
                                transitionData.initialState = targetState;
                                transitionData.currentState = targetState;
                                transitionData.targetState = targetState;
                                transitionData.activeProperties = Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_NONE;
                            }
                            else
                            {
                                bool transitionComplete = currentElement.config.transition.handler!(new Clay_TransitionCallbackArguments
                                {
                                    transitionState = transitionData.state,
                                    initial = transitionData.initialState,
                                    current = ref transitionData.currentState,
                                    target = targetState,
                                    elapsedTime = transitionData.elapsedTime,
                                    duration = currentElement.config.transition.duration,
                                    properties = transitionData.activeProperties,
                                });
                                __ApplyTransitionedPropertiesToElement(currentElement, transitionData.activeProperties, transitionData.currentState, ref mapItem.boundingBox, transitionData.reparented);
                                transitionData.elapsedTime += deltaTime;

                                if (transitionComplete)
                                {
                                    if (transitionData.state == Clay_TransitionState.CLAY_TRANSITION_STATE_ENTERING || transitionData.state == Clay_TransitionState.CLAY_TRANSITION_STATE_TRANSITIONING)
                                    {
                                        transitionData.state = Clay_TransitionState.CLAY_TRANSITION_STATE_IDLE;
                                        transitionData.elapsedTime = 0;
                                        transitionData.reparented = false;
                                        transitionData.activeProperties = Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_NONE;
                                    }
                                    else if (transitionData.state == Clay_TransitionState.CLAY_TRANSITION_STATE_EXITING)
                                    {
                                        context.transitionDatas.RemoveSwapback(i);
                                        i--;
                                    }
                                }
                            }
                        }
                    }

                    if (context.debugModeEnabled)
                    {
                        context.warningsEnabled = false;
                        __RenderDebugView();
                        context.warningsEnabled = true;
                    }

                    if (context.booleanWarnings.maxElementsExceeded)
                    {
                        __AddDebugViewElementsExceededError();
                    }
                    else
                    {
                        __CalculateFinalLayout(deltaTime, true, true);
                    }
                    // Note: C calls Clay__CloneElementsWithExitTransition() here to persist exiting subtrees in reused
                    // arena memory. In C#, object references already keep `elementThisFrame` alive across frames.
                }
                else
                {
                    if (context.debugModeEnabled)
                    {
                        context.warningsEnabled = false;
                        __RenderDebugView();
                        context.warningsEnabled = true;
                    }

                    if (context.booleanWarnings.maxElementsExceeded)
                    {
                        __AddDebugViewElementsExceededError();
                    }
                    else
                    {
                        __CalculateFinalLayout(deltaTime, false, true);
                    }
                }
            }

            // Hash map GC — evict items not seen this frame.
            for (int i = 0; i < context.layoutElementsHashMap.capacity; ++i)
            {
                int currentElementIndex = context.layoutElementsHashMap.internalArray[i];
                int previousElementIndex = -1;
                while (currentElementIndex != -1)
                {
                    Clay_LayoutElementHashMapItem currentItem = context.layoutElementsHashMapInternal.internalArray[currentElementIndex];
                    int nextIndex = currentItem.nextIndex;
                    if (currentItem.generation <= context.generation)
                    {
                        // Delete the underlying item and add it to the freelist.
                        context.layoutElementsHashMapInternal.internalArray[currentElementIndex] = new Clay_LayoutElementHashMapItem { nextIndex = -1 };
                        context.layoutElementsHashMapFreeList.Add(currentElementIndex);
                        if (previousElementIndex == -1)
                        {
                            context.layoutElementsHashMap.internalArray[i] = nextIndex;
                            currentElementIndex = nextIndex;
                            previousElementIndex = -1;
                        }
                        else
                        {
                            Clay_LayoutElementHashMapItem previousItem = context.layoutElementsHashMapInternal.internalArray[previousElementIndex];
                            previousItem.nextIndex = nextIndex;
                            context.layoutElementsHashMapInternal.internalArray[previousElementIndex] = previousItem;
                            currentElementIndex = nextIndex;
                        }
                    }
                    else
                    {
                        previousElementIndex = currentElementIndex;
                        currentElementIndex = nextIndex;
                    }
                }
            }

            return new Clay_RenderCommandArray(context.renderCommands);
        }

        public static uint GetOpenElementId() => __GetOpenLayoutElement().id;

        public static Clay_ElementId GetElementId(string idString) => __HashString(idString, 0);

        public static Clay_ElementId GetElementIdWithIndex(string idString, uint index) => __HashStringWithOffset(idString, index, 0);

        public static bool Hovered()
        {
            var context = GetCurrentContext()!;
            if (context.booleanWarnings.maxElementsExceeded) return false;
            Clay_LayoutElement openLayoutElement = __GetOpenLayoutElement();
            for (int i = 0; i < context.pointerOverIds.length; ++i)
            {
                if (context.pointerOverIds.internalArray[i].id == openLayoutElement.id) return true;
            }
            return false;
        }

        public static void OnHover(Clay_OnHoverFunction onHoverFunction, object? userData)
        {
            var context = GetCurrentContext()!;
            if (context.booleanWarnings.maxElementsExceeded) return;
            Clay_LayoutElement openLayoutElement = __GetOpenLayoutElement();
            ref Clay_LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(openLayoutElement.id);
            if (!Unsafe.IsNullRef(in hashMapItem))
            {
                hashMapItem.onHoverFunction = onHoverFunction;
                hashMapItem.hoverFunctionUserData = userData;
            }
        }

        public static bool PointerOver(Clay_ElementId elementId) // TODO return priority for separating multiple results.
        {
            var context = GetCurrentContext()!;
            for (int i = 0; i < context.pointerOverIds.length; ++i)
            {
                if (context.pointerOverIds.internalArray[i].id == elementId.id) return true;
            }
            return false;
        }

        public static Clay_ElementIdArray GetPointerOverIds() => new Clay_ElementIdArray(GetCurrentContext()!.pointerOverIds);

        public static Clay_ScrollContainerData GetScrollContainerData(Clay_ElementId id)
        {
            var context = GetCurrentContext()!;
            for (int i = 0; i < context.scrollContainerDatas.length; ++i)
            {
                ref Clay__ScrollContainerDataInternal scrollContainerData = ref context.scrollContainerDatas.internalArray[i];
                if (scrollContainerData.elementId == id.id)
                {
                    if (scrollContainerData.layoutElement == null)
                    {
                        // This can happen on the first frame before a scroll container is declared.
                        return default;
                    }
                    return Clay_ScrollContainerData.Create(ref scrollContainerData);
                }
            }
            return default;
        }

        public static Clay_ElementData GetElementData(Clay_ElementId id)
        {
            ref Clay_LayoutElementHashMapItem item = ref __GetHashMapItem(id.id);
            if (Unsafe.IsNullRef(in item)) return default;
            return new Clay_ElementData { boundingBox = item.boundingBox, found = true };
        }

        public static void SetDebugModeEnabled(bool enabled) => GetCurrentContext()!.debugModeEnabled = enabled;
        public static bool IsDebugModeEnabled() => GetCurrentContext()!.debugModeEnabled;

        public static void SetCullingEnabled(bool enabled) => GetCurrentContext()!.disableCulling = !enabled;

        public static void SetExternalScrollHandlingEnabled(bool enabled) => GetCurrentContext()!.externalScrollHandlingEnabled = enabled;

        public static int GetMaxElementCount() => GetCurrentContext()!.maxElementCount;

        public static void SetMaxElementCount(int maxElementCount)
        {
            var context = GetCurrentContext();
            if (context != null)
            {
                context.maxElementCount = maxElementCount;
            }
            else
            {
                s_defaultMaxElementCount = maxElementCount;
                s_defaultMaxMeasureTextWordCacheCount = maxElementCount * 2;
            }
        }

        public static int GetMaxMeasureTextCacheWordCount() => GetCurrentContext()!.maxMeasureTextCacheWordCount;

        public static void SetMaxMeasureTextCacheWordCount(int maxMeasureTextCacheWordCount)
        {
            var context = GetCurrentContext();
            if (context != null)
            {
                context.maxMeasureTextCacheWordCount = maxMeasureTextCacheWordCount;
            }
            else
            {
                s_defaultMaxMeasureTextWordCacheCount = maxMeasureTextCacheWordCount;
            }
        }

        public static void ResetMeasureTextCache()
        {
            var context = GetCurrentContext()!;
            context.measureTextHashMapInternal.length = 0;
            context.measureTextHashMapInternalFreeList.length = 0;
            context.measureTextHashMap.length = 0;
            context.measuredWords.length = 0;
            context.measuredWordsFreeList.length = 0;

            for (int i = 0; i < context.measureTextHashMap.capacity; ++i)
            {
                context.measureTextHashMap.internalArray[i] = 0;
            }
            context.measureTextHashMapInternal.length = 1; // Reserve the 0 value to mean "no next element".
        }

        public static bool EaseOut(Clay_TransitionCallbackArguments arguments)
        {
            float ratio = 1;
            if (arguments.duration > 0)
            {
                ratio = MathF.Min(arguments.elapsedTime / arguments.duration, 1);
            }
            float inverse = 1f - ratio;
            float lerpAmount = 1f - (inverse * inverse * inverse);

            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_X) != 0)
            {
                arguments.current.boundingBox.x = Lerp(arguments.initial.boundingBox.x, arguments.target.boundingBox.x, lerpAmount);
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_Y) != 0)
            {
                arguments.current.boundingBox.y = Lerp(arguments.initial.boundingBox.y, arguments.target.boundingBox.y, lerpAmount);
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_WIDTH) != 0)
            {
                arguments.current.boundingBox.width = Lerp(arguments.initial.boundingBox.width, arguments.target.boundingBox.width, lerpAmount);
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_HEIGHT) != 0)
            {
                arguments.current.boundingBox.height = Lerp(arguments.initial.boundingBox.height, arguments.target.boundingBox.height, lerpAmount);
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BACKGROUND_COLOR) != 0)
            {
                arguments.current.backgroundColor = new Clay_Color(
                    Lerp(arguments.initial.backgroundColor.r, arguments.target.backgroundColor.r, lerpAmount),
                    Lerp(arguments.initial.backgroundColor.g, arguments.target.backgroundColor.g, lerpAmount),
                    Lerp(arguments.initial.backgroundColor.b, arguments.target.backgroundColor.b, lerpAmount),
                    Lerp(arguments.initial.backgroundColor.a, arguments.target.backgroundColor.a, lerpAmount));
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_OVERLAY_COLOR) != 0)
            {
                arguments.current.overlayColor = new Clay_Color(
                    Lerp(arguments.initial.overlayColor.r, arguments.target.overlayColor.r, lerpAmount),
                    Lerp(arguments.initial.overlayColor.g, arguments.target.overlayColor.g, lerpAmount),
                    Lerp(arguments.initial.overlayColor.b, arguments.target.overlayColor.b, lerpAmount),
                    Lerp(arguments.initial.overlayColor.a, arguments.target.overlayColor.a, lerpAmount));
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_COLOR) != 0)
            {
                arguments.current.borderColor = new Clay_Color(
                    Lerp(arguments.initial.borderColor.r, arguments.target.borderColor.r, lerpAmount),
                    Lerp(arguments.initial.borderColor.g, arguments.target.borderColor.g, lerpAmount),
                    Lerp(arguments.initial.borderColor.b, arguments.target.borderColor.b, lerpAmount),
                    Lerp(arguments.initial.borderColor.a, arguments.target.borderColor.a, lerpAmount));
            }
            if ((arguments.properties & Clay_TransitionProperty.CLAY_TRANSITION_PROPERTY_BORDER_WIDTH) != 0)
            {
                arguments.current.borderWidth = new Clay_BorderWidth
                {
                    left = (ushort)Lerp(arguments.initial.borderWidth.left, arguments.target.borderWidth.left, lerpAmount),
                    right = (ushort)Lerp(arguments.initial.borderWidth.right, arguments.target.borderWidth.right, lerpAmount),
                    top = (ushort)Lerp(arguments.initial.borderWidth.top, arguments.target.borderWidth.top, lerpAmount),
                    bottom = (ushort)Lerp(arguments.initial.borderWidth.bottom, arguments.target.borderWidth.bottom, lerpAmount),
                    betweenChildren = (ushort)Lerp(arguments.initial.borderWidth.betweenChildren, arguments.target.borderWidth.betweenChildren, lerpAmount),
                };
            }
            return ratio >= 1;
        }

        // -------------------------------------
        // DSL (replaces the C macros) ---------
        // -------------------------------------

        private sealed class ElementScope : IDisposable
        {
            public void Dispose() => __CloseElement();
        }

        private static readonly ElementScope s_elementScope = new ElementScope();

        // CLAY(id, ...) { ... }  →  using (Clay.Element(id, decl)) { ... }
        public static IDisposable Element(Clay_ElementId id, Clay_ElementDeclaration declaration) => Element(id, () => declaration);

        // Overload that evaluates the declaration _after_ the element is opened, so expressions like
        // Clay.Hovered() or Clay.GetScrollOffset() inside the declaration observe the newly opened element
        // (matching the C macro's evaluation order).
        public static IDisposable Element(Clay_ElementId id, Func<Clay_ElementDeclaration> declaration)
        {
            __OpenElementWithId(id);
            __ConfigureOpenElement(declaration());
            return s_elementScope;
        }

        // CLAY_AUTO_ID(...) { ... }  →  using (Clay.AutoId(decl)) { ... }
        public static IDisposable AutoId(Clay_ElementDeclaration declaration) => AutoId(() => declaration);

        public static IDisposable AutoId(Func<Clay_ElementDeclaration> declaration)
        {
            __OpenElement();
            __ConfigureOpenElement(declaration());
            return s_elementScope;
        }

        // CLAY_TEXT(text, ...)  →  Clay.Text(text, config)
        public static void Text(string text, Clay_TextElementConfig textConfig) => __OpenTextElement(text, textConfig);

        // ID helpers (CLAY_ID / CLAY_SID / CLAY_IDI / CLAY_SIDI / CLAY_ID_LOCAL / ...)
        public static Clay_ElementId Id(string label) => __HashString(label, 0);
        public static Clay_ElementId SId(string label) => __HashString(label, 0);
        public static Clay_ElementId Idi(string label, uint index) => __HashStringWithOffset(label, index, 0);
        public static Clay_ElementId SIdi(string label, uint index) => __HashStringWithOffset(label, index, 0);
        public static Clay_ElementId IdLocal(string label) => __HashString(label, GetOpenElementId());
        public static Clay_ElementId SIdLocal(string label) => __HashString(label, GetOpenElementId());
        public static Clay_ElementId IdiLocal(string label, uint index) => __HashStringWithOffset(label, index, GetOpenElementId());
        public static Clay_ElementId SIdiLocal(string label, uint index) => __HashStringWithOffset(label, index, GetOpenElementId());

        // Sizing / padding / corner / border helpers (CLAY_SIZING_* / CLAY_PADDING_ALL / ...).
        public static Clay_SizingAxis SizingFixed(float fixedSize) => new Clay_SizingAxis
        {
            minMax = new Clay_SizingMinMax { min = fixedSize, max = fixedSize },
            type = Clay__SizingType.CLAY__SIZING_TYPE_FIXED,
        };

        public static Clay_SizingAxis SizingGrow() => new Clay_SizingAxis { minMax = default, type = Clay__SizingType.CLAY__SIZING_TYPE_GROW };
        public static Clay_SizingAxis SizingGrow(float min) => new Clay_SizingAxis { minMax = new Clay_SizingMinMax { min = min, max = 0 }, type = Clay__SizingType.CLAY__SIZING_TYPE_GROW };
        public static Clay_SizingAxis SizingGrow(float min, float max) => new Clay_SizingAxis { minMax = new Clay_SizingMinMax { min = min, max = max }, type = Clay__SizingType.CLAY__SIZING_TYPE_GROW };

        public static Clay_SizingAxis SizingFit() => new Clay_SizingAxis { minMax = default, type = Clay__SizingType.CLAY__SIZING_TYPE_FIT };
        public static Clay_SizingAxis SizingFit(float min) => new Clay_SizingAxis { minMax = new Clay_SizingMinMax { min = min, max = 0 }, type = Clay__SizingType.CLAY__SIZING_TYPE_FIT };
        public static Clay_SizingAxis SizingFit(float min, float max) => new Clay_SizingAxis { minMax = new Clay_SizingMinMax { min = min, max = max }, type = Clay__SizingType.CLAY__SIZING_TYPE_FIT };

        public static Clay_SizingAxis SizingPercent(float percentOfParent) => new Clay_SizingAxis { percent = percentOfParent, type = Clay__SizingType.CLAY__SIZING_TYPE_PERCENT };

        public static Clay_Padding PaddingAll(ushort padding) => new Clay_Padding { left = padding, right = padding, top = padding, bottom = padding };
        public static Clay_CornerRadius CornerRadius(float radius) => new Clay_CornerRadius { topLeft = radius, topRight = radius, bottomLeft = radius, bottomRight = radius };
        public static Clay_BorderWidth BorderAll(ushort widthValue) => new Clay_BorderWidth { left = widthValue, right = widthValue, top = widthValue, bottom = widthValue, betweenChildren = widthValue };
        public static Clay_BorderWidth BorderOutside(ushort widthValue) => new Clay_BorderWidth { left = widthValue, right = widthValue, top = widthValue, bottom = widthValue, betweenChildren = 0 };
    }
}
