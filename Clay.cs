// VERSION: 0.14
// Clay (https://github.com/nicbarker/clay) — C# port.
//
// A managed, idiomatic C# port of clay.h (Clay v0.14) that keeps the public API
// faithful to the original C library. Differences from the C implementation:
//   * The arena allocator is replaced with managed arrays (no Arena /
//     MinMemorySize / CreateArenaWithCapacityAndMemory).
//   * String is replaced with `string` and StringSlice with
//     Microsoft.Extensions.Primitives.StringSegment.
//   * Vector2 is System.Numerics.Vector2.
//   * `void*` user data becomes `object?`.
//   * Hashing is built on System.HashCode (content based, stable within a run).
//   * The C macros (CLAY / AUTO_ID / TEXT / ID / SIZING_*)
//     are replaced by the static `Clay` facade: `using (Clay.Element(id, decl)) { }`,
//     `Clay.AutoId(decl)`, `Clay.Text(text, config)`, `Clay.Id("...")`, etc.
//   * The self-hosted debug inspector (_RenderDebugView) lives in Clay.DebugView.cs.
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

namespace ClaySharp;

public static partial class Clay
{
    // -----------------------------------------
    // UTILITY STRUCTS -------------------------
    // -----------------------------------------

    public struct Dimensions(float width, float height)
    {
        public float Width = width, Height = height;
    }

    // Internally clay conventionally represents colors as 0-255, but interpretation is up to the renderer.
    public struct Color(float r, float g, float b, float a)
    {
        public float R = r, G = g, B = b, A = a;
    }

    public struct BoundingBox(float x, float y, float width, float height)
    {
        public float X = x, Y = y, Width = width, Height = height;
    }

    // Primarily created via the Clay.Id() / Clay.Idi() / Clay.IdLocal() helpers.
    // Represents a hashed string ID used for identifying and finding specific clay UI elements, required
    // by functions such as Clay.PointerOver() and Clay.GetElementData().
    public struct ElementId
    {
        public uint Id;       // The resulting hash generated from the other fields.
        public uint Offset;   // A numerical offset applied after computing the hash from stringId.
        public uint BaseId;   // A base hash value to start from, for example the parent element ID is used when calculating ID_LOCAL().
        public string StringId; // The string id to hash.
    }

    // A sized array of ElementId (returned from Clay.GetPointerOverIds()).
    public readonly struct ElementIdArray : IReadOnlyList<ElementId>
    {
        internal readonly Array<ElementId> Items;

        internal ElementIdArray(Array<ElementId> items)
        {
            Items = items;
        }

        public int Capacity => Items.Capacity;
        public int Length => Items.Length;
        public ElementId[] InternalArray => Items.InternalArray;
        public ElementId this[int index] => Items.InternalArray[index];

        public IEnumerator<ElementId> GetEnumerator() => new ArrayEnumerator<ElementId>(Items);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IReadOnlyCollection<ElementId>.Count => Items.Length;
    }

    public struct ArrayEnumerator<T> : IEnumerator<T>
    {
        private int _index = -1;
        private readonly Array<T> _array;

        internal ArrayEnumerator(Array<T> array)
        {
            _array = array;
        }

        public bool MoveNext()
        {
            return ++_index < _array.Length;
        }

        public void Reset()
        {
            _index = -1;
        }

        public T Current => _array.InternalArray[_index];

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
        }
    }

    // Controls the "radius", or corner rounding of elements, including rectangles, borders and images.
    public struct CornerRadiusValues
    {
        public float TopLeft;
        public float TopRight;
        public float BottomLeft;
        public float BottomRight;
    }

    // -----------------------------------------
    // ELEMENT CONFIGS -------------------------
    // -----------------------------------------

    public enum LayoutDirection
    {
        // (Default) Lays out child elements from left to right with increasing x.
        LeftToRight = 0,
        // Lays out child elements from top to bottom with increasing y.
        TopToBottom = 1,
    }

    public enum LayoutAlignmentX
    {
        // (Default) Aligns child elements to the left hand side of this element, offset by padding.left
        Left = 0,
        // Aligns child elements to the right hand side of this element, offset by padding.right
        Right = 1,
        // Aligns child elements horizontally to the center of this element
        Center = 2,
    }

    public enum LayoutAlignmentY
    {
        // (Default) Aligns child elements to the top of this element, offset by padding.top
        Top = 0,
        // Aligns child elements to the bottom of this element, offset by padding.bottom
        Bottom = 1,
        // Aligns child elements vertically to the center of this element
        Center = 2,
    }

    // Controls how the element takes up space inside its parent container.
    public enum SizingType
    {
        // (default) Wraps tightly to the size of the element's contents.
        Fit = 0,
        // Expands along this axis to fill available space in the parent element, sharing it with other GROW elements.
        Grow = 1,
        // Expects 0-1 range. Clamps the axis size to a percent of the parent container's axis size minus padding and child gaps.
        Percent = 2,
        // Clamps the axis size to an exact size in pixels.
        Fixed = 3,
    }

    public struct ChildAlignment
    {
        public LayoutAlignmentX X; // Controls alignment of children along the x axis.
        public LayoutAlignmentY Y; // Controls alignment of children along the y axis.
    }

    // Controls the minimum and maximum size in pixels that this element is allowed to grow or shrink to,
    // overriding sizing types such as FIT or GROW.
    public struct SizingMinMax
    {
        public float Min; // The smallest final size of the element on this axis will be this value in pixels.
        public float Max; // The largest final size of the element on this axis will be this value in pixels.
    }

    // Controls the sizing of this element along one axis inside its parent container.
    public struct SizingAxis
    {
        // The C code overlays SizingMinMax and `float percent` in a union. In C# both fields coexist,
        // tagged by `type` (only the field relevant to `type` is meaningful).
        public SizingMinMax MinMax; // min/max size in pixels for FIT / GROW / FIXED sizing.
        public float Percent;             // 0-1 range, only used by PERCENT.
        public SizingType Type;     // Controls how the element takes up space inside its parent container.
    }

    public struct Sizing
    {
        public SizingAxis Width;  // Controls the width sizing of the element, along the x axis.
        public SizingAxis Height; // Controls the height sizing of the element, along the y axis.
    }

    public struct Padding
    {
        public ushort Left;
        public ushort Right;
        public ushort Top;
        public ushort Bottom;
    }

    // Controls various settings that affect the size and position of an element, as well as the sizes and
    // positions of any child elements.
    public struct LayoutConfig
    {
        public Sizing Sizing; // FIT / GROW / PERCENT / FIXED sizing inside the parent container.
        public Padding Padding; // "padding" in pixels, a gap between this element's bounding box and its children.
        public ushort ChildGap; // The gap in pixels between child elements along the layout axis.
        public ChildAlignment ChildAlignment; // Controls how child elements are aligned on each axis.
        public LayoutDirection LayoutDirection; // Controls the direction in which child elements are laid out.
    }

    // Controls how text "wraps", that is how it is broken into multiple lines when there is insufficient horizontal space.
    public enum TextElementConfigWrapMode
    {
        // (default) breaks on whitespace characters.
        Words = 0,
        // Don't break on space characters, only on newlines.
        Newlines = 1,
        // Disable text wrapping entirely.
        None = 2,
    }

    // Controls how wrapped lines of text are horizontally aligned within the outer text bounding box.
    public enum TextAlignment
    {
        // (default) Horizontally aligns wrapped lines of text to the left hand side of their bounding box.
        Left = 0,
        // Horizontally aligns wrapped lines of text to the center of their bounding box.
        Center = 1,
        // Horizontally aligns wrapped lines of text to the right hand side of their bounding box.
        Right = 2,
    }

    // Controls various functionality related to text elements.
    public struct TextElementConfig
    {
        public object? UserData; // A pointer that will be transparently passed through to the resulting render command.
        public Color TextColor; // The RGBA color of the font to render, conventionally specified as 0-255.
        public ushort FontId; // An integer transparently passed to the measure text function to identify the font to use.
        public ushort FontSize; // Controls the size of the font.
        public ushort LetterSpacing; // Controls extra horizontal spacing between characters.
        public ushort LineHeight; // Controls additional vertical space between wrapped lines of text.
        public TextElementConfigWrapMode WrapMode; // How text wraps.
        public TextAlignment TextAlignment; // How wrapped lines are horizontally aligned.
    }

    // Controls various settings related to aspect ratio scaling element.
    public struct AspectRatioElementConfig
    {
        public float AspectRatio; // The target "aspect ratio", final width divided by final height.
    }

    // Controls various settings related to image elements.
    public struct ImageElementConfig
    {
        public object? ImageData; // A transparent object used to pass image data through to the renderer.
    }

    // Controls where a floating element is offset relative to its parent element.
    public enum FloatingAttachPointType
    {
        LeftTop = 0,
        LeftCenter = 1,
        LeftBottom = 2,
        CenterTop = 3,
        CenterCenter = 4,
        CenterBottom = 5,
        RightTop = 6,
        RightCenter = 7,
        RightBottom = 8,
    }

    // Controls where a floating element is offset relative to its parent element.
    public struct FloatingAttachPoints
    {
        public FloatingAttachPointType Element; // The origin point on a floating element that attaches to its parent.
        public FloatingAttachPointType Parent;  // The origin point on the parent element that the floating element attaches to.
    }

    // Controls how mouse pointer events like hover and click are captured or passed through to elements underneath.
    public enum PointerCaptureMode
    {
        // (default) "Capture" the pointer event and don't allow events like hover and click to pass through.
        Capture = 0,
        // Transparently pass through pointer events like hover and click to elements underneath the floating element.
        Passthrough = 1,
    }

    // Controls which element a floating element is "attached" to (i.e. relative offset from).
    public enum FloatingAttachToElement
    {
        // (default) Disables floating for this element.
        None = 0,
        // Attaches this floating element to its parent.
        Parent = 1,
        // Attaches this floating element to an element with a specific ID (.parentId).
        ElementWithId = 2,
        // Attaches this floating element to the root of the layout.
        Root = 3,
    }

    // Controls whether or not a floating element is clipped to the same clipping rectangle as the element it's attached to.
    public enum FloatingClipToElement
    {
        // (default) - The floating element does not inherit clipping.
        None = 0,
        // The floating element is clipped to the same clipping rectangle as the element it's attached to.
        AttachedParent = 1,
    }

    // Controls various settings related to "floating" elements.
    public struct FloatingElementConfig
    {
        public Vector2 Offset; // Offsets this floating element by the provided x,y coordinates from its attachPoints.
        public Dimensions Expand; // Expands the boundaries of the outer floating element without affecting its children.
        public uint ParentId; // For ELEMENT_WITH_ID: the element to attach to.
        public short ZIndex; // Controls the z index of this floating element and all its children.
        public FloatingAttachPoints AttachPoints; // How pointer events are captured / passed through.
        public PointerCaptureMode PointerCaptureMode; // How pointer events are captured / passed through.
        public FloatingAttachToElement AttachTo; // Which element this floating element is attached to.
        public FloatingClipToElement ClipTo; // Whether this floating element inherits clipping.
    }

    // Controls various settings related to custom elements.
    public struct CustomElementConfig
    {
        public object? CustomData; // Transparent custom data passed through to the renderer (generates CUSTOM commands).
    }

    // Controls the axis on which an element switches to "scrolling", which clips the contents and allows scrolling.
    public struct ClipElementConfig
    {
        public bool Horizontal; // Clip overflowing elements on the X axis.
        public bool Vertical;   // Clip overflowing elements on the Y axis.
        public Vector2 ChildOffset; // Offsets the x,y positions of all child elements (used primarily for scrolling containers).
    }

    // Controls the widths of individual element borders.
    public struct BorderWidth
    {
        public ushort Left;
        public ushort Right;
        public ushort Top;
        public ushort Bottom;
        // Creates borders between each child element, depending on the layoutDirection.
        public ushort BetweenChildren;
    }

    // Controls settings related to element borders.
    public struct BorderElementConfig
    {
        public Color Color; // Controls the color of all borders with width > 0.
        public BorderWidth Width; // Controls the widths of individual borders.
    }

    public struct TransitionData
    {
        public BoundingBox BoundingBox;
        public Color BackgroundColor;
        public Color OverlayColor;
        public Color BorderColor;
        public BorderWidth BorderWidth;
    }

    public enum TransitionState
    {
        Idle = 0,
        Entering = 1,
        Transitioning = 2,
        Exiting = 3,
    }

    [Flags]
    public enum TransitionProperty
    {
        None = 0,
        X = 1,
        Y = 2,
        Position = X | Y,
        Width = 4,
        Height = 8,
        Dimensions = Width | Height,
        BoundingBox = Position | Dimensions,
        BackgroundColor = 16,
        OverlayColor = 32,
        CornerRadius = 64,
        BorderColor = 128,
        BorderWidth = 256,
        Border = BorderColor | BorderWidth,
    }

    public ref struct TransitionCallbackArguments
    {
        public TransitionState TransitionState;
        public TransitionData Initial;
        public ref TransitionData Current; // Live mutable state — the handler writes interpolated values here.
        public TransitionData Target;
        public float ElapsedTime;
        public float Duration;
        public TransitionProperty Properties;
    }

    public enum TransitionEnterTriggerType
    {
        TransitionEnterSkipOnFirstParentFrame = 0,
        TransitionEnterTriggerOnFirstParentFrame = 1,
    }

    public enum TransitionExitTriggerType
    {
        TransitionExitSkipWhenParentExits = 0,
        TransitionExitTriggerWhenParentExits = 1,
    }

    public enum TransitionInteractionHandlingType
    {
        TransitionDisableInteractionsWhileTransitioningPosition = 0,
        TransitionAllowInteractionsWhileTransitioningPosition = 1,
    }

    public enum ExitTransitionSiblingOrdering
    {
        UnderneathSiblings = 0,
        NaturalOrder = 1,
        AboveSiblings = 2,
    }

    public struct TransitionElementConfigEnter
    {
        public TransitionSetStateFunction? SetInitialState;
        public TransitionEnterTriggerType Trigger;
    }

    public struct TransitionElementConfigExit
    {
        public TransitionSetStateFunction? SetFinalState;
        public TransitionExitTriggerType Trigger;
        public ExitTransitionSiblingOrdering SiblingOrdering;
    }

    // Controls settings related to transitions.
    public struct TransitionElementConfig
    {
        public TransitionHandler? Handler;
        public float Duration;
        public TransitionProperty Properties;
        public TransitionInteractionHandlingType InteractionHandling;
        public TransitionElementConfigEnter Enter;
        public TransitionElementConfigExit Exit;
    }

    // -----------------------------------------
    // RENDER COMMAND DATA ---------------------
    // -----------------------------------------

    // Render command data when commandType == TEXT
    public struct TextRenderData
    {
        public StringSegment StringContents; // A string slice containing the text to be rendered.
        public Color TextColor;
        public ushort FontId;
        public ushort FontSize;
        public ushort LetterSpacing; // Extra whitespace gap in pixels between each character.
        public ushort LineHeight;    // The height of the bounding box for this line of text.
    }

    // Render command data when commandType == RECTANGLE
    public struct RectangleRenderData
    {
        public Color BackgroundColor;
        public CornerRadiusValues CornerRadius;
    }

    // Render command data when commandType == IMAGE
    public struct ImageRenderData
    {
        public Color BackgroundColor;
        public CornerRadiusValues CornerRadius;
        public object? ImageData;
    }

    // Render command data when commandType == CUSTOM
    public struct CustomRenderData
    {
        public Color BackgroundColor;
        public CornerRadiusValues CornerRadius;
        public object? CustomData;
    }

    // Render command data when commandType == SCISSOR_START || SCISSOR_END
    public struct ClipRenderData
    {
        public bool Horizontal;
        public bool Vertical;
    }

    // Render command data when commandType == OVERLAY_COLOR_START || OVERLAY_COLOR_END
    public struct OverlayColorRenderData
    {
        public Color Color;
    }

    // Render command data when commandType == BORDER
    public struct BorderRenderData
    {
        public Color Color;
        public CornerRadiusValues CornerRadius;
        public BorderWidth Width;
    }

    // The C library uses a union here. In C# this is a flat struct holding all render data variants;
    // only the member matching `RenderCommand.commandType` is meaningful.
    public struct RenderData
    {
        public RectangleRenderData Rectangle;
        public TextRenderData Text;
        public ImageRenderData Image;
        public CustomRenderData Custom;
        public BorderRenderData Border;
        public ClipRenderData Clip;
        public OverlayColorRenderData OverlayColor;
    }

    // -----------------------------------------
    // MISCELLANEOUS STRUCTS & ENUMS -----------
    // -----------------------------------------

    // Data representing the current internal state of a scrolling element.
    public ref struct ScrollContainerData
    {
        private ref ScrollContainerDataInternal _internalData;

        public Vector2 ScrollPosition
        {
            get
            {
                if (Unsafe.IsNullRef(in _internalData)) return default;
                return _internalData.ScrollPosition;
            }
            set
            {
                if (Unsafe.IsNullRef(in _internalData)) return;
                _internalData.ScrollPosition = value;
            }
        }

        public Dimensions ScrollContainerDimensions; // The bounding box of the scroll element.
        public Dimensions ContentDimensions; // The outer dimensions of the inner scroll container content.
        public ClipElementConfig Config; // The config that was originally passed to the clip element.
        public bool Found; // Indicates whether an actual scroll container matched the provided ID.

        internal static ScrollContainerData Create(ref ScrollContainerDataInternal internalData)
        {
            return new ScrollContainerData
            {
                _internalData = ref internalData,
                ScrollContainerDimensions = new Dimensions(internalData.BoundingBox.Width, internalData.BoundingBox.Height),
                ContentDimensions = internalData.ContentSize,
                Config = internalData.LayoutElement.Config.Clip,
                Found = true,
            };
        }
    }

    // Bounding box and other data for a specific UI element.
    public struct ElementData
    {
        public BoundingBox BoundingBox; // The rectangle that encloses this UI element, relative to the layout root.
        public bool Found; // Indicates whether an actual element matched the provided ID.
    }

    // Used by renderers to determine specific handling for each render command.
    public enum RenderCommandType
    {
        None = 0,
        Rectangle = 1,
        Border = 2,
        Text = 3,
        Image = 4,
        ScissorStart = 5,
        ScissorEnd = 6,
        OverlayColorStart = 7,
        OverlayColorEnd = 8,
        Custom = 9,
    }

    public struct RenderCommand
    {
        public BoundingBox BoundingBox; // A rectangular box that fully encloses this UI element.
        public RenderData RenderData; // Data specific to this command's commandType.
        public object? UserData; // Transparently passed through from the original element declaration.
        public uint Id; // The id of this element, transparently passed through from the original element declaration.
        public short ZIndex; // The z order required for drawing this command correctly.
        public RenderCommandType CommandType; // Specifies how to handle rendering of this command.
    }

    // A sized array of render commands (returned from Clay.EndLayout()).
    public struct RenderCommandArray : IReadOnlyList<RenderCommand>
    {
        internal Array<RenderCommand> Items;

        internal RenderCommandArray(Array<RenderCommand> items)
        {
            Items = items;
        }

        public int Capacity => Items.Capacity;
        public int Length => Items.Length;
        public RenderCommand[] InternalArray => Items.InternalArray;
        public RenderCommand this[int index] => Items.InternalArray[index];

        // Bounds-checked accessor equivalent to the C RenderCommandArray_Get.
        public ref RenderCommand Get(int index) => ref Items.Get(index);

        public IEnumerator<RenderCommand> GetEnumerator() => new ArrayEnumerator<RenderCommand>(Items);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IReadOnlyCollection<RenderCommand>.Count => Items.Length;
    }

    // Represents the current state of interaction with clay this frame.
    public enum PointerDataInteractionState
    {
        // A left mouse click, or touch occurred this frame.
        PressedThisFrame = 0,
        // The left mouse button click or touch happened in the past, and is still held down this frame.
        Pressed = 1,
        // The left mouse button click or touch was released this frame.
        ReleasedThisFrame = 2,
        // The left mouse button click or touch is not currently down / was released in the past.
        Released = 3,
    }

    // Information on the current state of pointer interactions this frame.
    public struct PointerData
    {
        public Vector2 Position; // The position of the mouse / touch / pointer relative to the root of the layout.
        public PointerDataInteractionState State; // The current state of interaction with clay this frame.
    }

    public struct ElementDeclaration
    {
        public LayoutConfig Layout; // Controls the size and position of an element and its children.
        public Color BackgroundColor; // Background color; generates a RECTANGLE render command (or is passed to IMAGE/CUSTOM).
        public Color OverlayColor; // "Color Overlay" applied to this element and all its children.
        public CornerRadiusValues CornerRadius; // Corner rounding of rectangles, borders and images.
        public AspectRatioElementConfig AspectRatio; // Aspect ratio scaling.
        public ImageElementConfig Image; // Image element settings.
        public FloatingElementConfig Floating; // Floating / absolute positioning settings.
        public CustomElementConfig Custom; // CUSTOM render command settings.
        public ClipElementConfig Clip; // Clip / scroll settings.
        public BorderElementConfig Border; // Border settings.
        public TransitionElementConfig Transition; // Transition settings.
        public object? UserData; // Transparently passed through to resulting render commands.
    }

    // Represents the type of error clay encountered while computing layout.
    public enum ErrorType
    {
        TextMeasurementFunctionNotProvided = 0,
        ArenaCapacityExceeded = 1,
        ElementsCapacityExceeded = 2,
        TextMeasurementCapacityExceeded = 3,
        DuplicateId = 4,
        FloatingContainerParentNotFound = 5,
        PercentageOver1 = 6,
        InternalError = 7,
        UnbalancedOpenClose = 8,
        HashMapCapacityExceeded = 9,
    }

    // Data to identify the error that clay has encountered.
    public struct ErrorData
    {
        public ErrorType ErrorType; // The type of error encountered.
        public string ErrorText; // Human-readable error text.
        public object? UserData; // Transparently passed through from the error handler.
    }

    // A wrapper struct around Clay's error handler function.
    public struct ErrorHandler
    {
        public ErrorHandlerFunction? ErrorHandlerFunction; // A user provided function called when Clay encounters an error.
        public object? UserData; // Transparently passed through to the error handler when it is called.
    }

    // -----------------------------------------
    // CALLBACK DELEGATES ----------------------
    // -----------------------------------------

    public delegate Dimensions MeasureTextFunction(StringSegment text, TextElementConfig config, object? userData);
    public delegate void ErrorHandlerFunction(ErrorData errorData);
    public delegate void OnHoverFunction(ElementId elementId, PointerData pointerData, object? userData);
    public delegate Vector2 QueryScrollOffsetFunction(uint elementId, object? userData);
    public delegate bool TransitionHandler(TransitionCallbackArguments arguments);
    public delegate TransitionData TransitionSetStateFunction(TransitionData state, TransitionProperty properties);

    // -----------------------------------------
    // INTERNAL TYPES --------------------------
    // -----------------------------------------

    // One-shot "already warned" flags per error class.
    internal struct BooleanWarnings
    {
        public bool MaxElementsExceeded;
        public bool MaxRenderCommandsExceeded;
        public bool MaxTextMeasureCacheExceeded;
        public bool TextMeasurementFunctionNotSet;
        public bool HashMapCapacityExceeded;
    }

    // A single warning entry for the debug view's warnings pane. In Clay v0.14 nothing ever adds
    // warnings, so this array stays empty; kept for parity with the C context layout.
    internal struct Warning
    {
        public string BaseMessage;
        public string DynamicMessage;
    }

    // A single wrapped line of a text element.
    internal struct WrappedTextLine
    {
        public Dimensions Dimensions;
        public StringSegment Line; // A slice of the source text (Buffer = full text, Offset = start, Length = line length).
    }

    // Non-owning view over a region of a shared array. Mirrors the C `Array##Slice` structs.
    internal struct ArraySlice<T>
    {
        public int Length;
        public T[] InternalArray;
        public int Offset;

        private static T _sDefault = default!;

        public ref T Get(int index)
        {
            if (__Array_RangeCheck(index, Length)) return ref InternalArray[Offset + index];
            return ref _sDefault;
        }
    }

    // Layout element data for text elements (the "other half" of LayoutElement's C union).
    internal struct TextElementData
    {
        public string Text;
        public Dimensions PreferredDimensions;
        public ArraySlice<WrappedTextLine> WrappedLines;
    }

    // In C this holds an `int32_t *elements` pointer into Context.layoutElementChildren.
    internal struct LayoutElementChildren
    {
        public LayoutElement[] Elements; // The shared layoutElementChildren backing array (element references, not indices).
        public int Offset;     // Start offset within that array.
        public ushort Length;  // Number of children.
    }

    // Mutable reference type (the C implementation takes it by pointer everywhere).
    internal sealed class LayoutElement
    {
        public LayoutElementChildren Children;
        public Dimensions Dimensions;
        public Dimensions MinDimensions;

        // The C union of `ElementDeclaration config` vs `{ textConfig, textElementData }` becomes two
        // coexisting fields, gated by `isTextElement`.
        public ElementDeclaration Config;
        public TextElementConfig TextConfig;
        public TextElementData TextElementData;

        public uint Id;
        public ushort FloatingChildrenCount;
        public bool IsTextElement;
        public bool Exiting; // True if the element is in an exit transition ("synthetic" element).

        // Index of this element in Context.layoutElements — replaces C pointer arithmetic
        // (`element - context->layoutElements.internalArray`).
        public int Index;

        // Shallow clone: copies value-type fields and shares reference fields (children array, text string),
        // matching C's bitwise struct copy semantics for cloned exiting subtrees.
        internal LayoutElement Clone() => (LayoutElement)MemberwiseClone();
    }

    // Internal state of a scrolling container.
    internal struct ScrollContainerDataInternal
    {
        public LayoutElement LayoutElement;
        public BoundingBox BoundingBox;
        public Dimensions ContentSize;
        public Vector2 ScrollOrigin;
        public Vector2 PointerOrigin;
        public Vector2 ScrollMomentum;
        public Vector2 ScrollPosition;
        public Vector2 PreviousDelta;
        public float MomentumTime;
        public uint ElementId;
        public bool OpenThisFrame;
        public bool PointerScrollActive;
    }

    // Internal state of a transition element.
    internal struct TransitionDataInternal
    {
        public TransitionData InitialState;
        public TransitionData CurrentState;
        public TransitionData TargetState;
        public LayoutElement ElementThisFrame;
        public Vector2 OldParentRelativePosition;
        public uint ElementId;
        public uint ParentId;
        public uint SiblingIndex;
        public float ElapsedTime;
        public TransitionState State;
        public bool TransitionOut;
        public bool Reparented;
        public TransitionProperty ActiveProperties;
    }

    // Hash map item for element ID -> element lookups.
    internal struct LayoutElementHashMapItem
    {
        public BoundingBox BoundingBox;
        public ElementId ElementId;
        public LayoutElement LayoutElement;
        public int LayoutElementIndex; // Index into Context.layoutElements (replaces C pointer arithmetic).
        public OnHoverFunction? OnHoverFunction;
        public object? HoverFunctionUserData;
        public int NextIndex;
        public uint Generation;
        public bool AppearedThisFrame;
        public DebugDataType DebugData;

        internal struct DebugDataType
        {
            public bool Collision;
            public bool Collapsed;
        }
    }

    // A measured "word" in the text measurement cache, linked via `next`.
    internal struct MeasuredWord
    {
        public int StartOffset;
        public int Length;
        public float Width;
        public int Next;
    }

    // Hash map item for the text measurement cache.
    internal struct MeasureTextCacheItem
    {
        public Dimensions UnwrappedDimensions;
        public int MeasuredWordsStartIndex;
        public float MinWidth;
        public bool ContainsNewlines;
        public uint Id;
        public int NextIndex;
        public uint Generation;
    }

    // A node used by the DFS layout passes.
    internal struct LayoutElementTreeNode
    {
        public LayoutElement LayoutElement;
        public Vector2 Position;
        public Vector2 NextChildOffset;
        public bool ParentMovedThisFramed; // Used to relativise transitions.
    }

    // The root of a layout tree (the main tree plus each floating subtree).
    internal struct LayoutElementTreeRoot
    {
        public int LayoutElementIndex;
        public uint ParentId; // 0 in the case of the root layout tree.
        public uint ClipElementId; // 0 if there is no clip element.
        public short ZIndex;
        public Vector2 PointerOffset; // Only used when scroll containers are managed externally.
    }

    // The entire per-context state, mirroring the C `struct Context`.
    // A class (mutable reference) because it is frequently taken as a reference.
    public sealed class Context
    {
        internal int MaxElementCount;
        internal int MaxMeasureTextCacheWordCount;
        internal int ExitingElementsLength;
        internal int ExitingElementsChildrenLength;
        internal bool RootResizedLastFrame;
        internal ErrorHandler ErrorHandler;
        internal BooleanWarnings BooleanWarnings;

        internal PointerData PointerInfo;
        internal Dimensions LayoutDimensions;
        internal ElementId DynamicElementIndexBaseHash;
        internal uint DynamicElementIndex;
        internal bool DebugModeEnabled;
        internal bool DisableCulling;
        internal bool ExternalScrollHandlingEnabled;
        internal bool WarningsEnabled;
        internal uint DebugSelectedElementId;
        internal uint Generation;
        internal object? MeasureTextUserData;
        internal object? QueryScrollOffsetUserData;

        // Layout Elements / Render Commands
        internal Array<LayoutElement> LayoutElements;
        internal Array<RenderCommand> RenderCommands;
        internal Array<int> OpenLayoutElementStack;
        internal Array<LayoutElement> LayoutElementChildren;
        internal Array<int> LayoutElementChildrenBuffer;
        internal Array<int> ReusableElementIndexBuffer;
        internal Array<int> LayoutElementClipElementIds;

        // Misc Data Structures
        internal Array<WrappedTextLine> WrappedTextLines;
        internal Array<LayoutElementTreeNode> LayoutElementTreeNodeArray1;
        internal Array<LayoutElementTreeRoot> LayoutElementTreeRoots;
        internal Array<LayoutElementHashMapItem> LayoutElementsHashMapInternal;
        internal Array<int> LayoutElementsHashMap;
        internal Array<int> LayoutElementsHashMapFreeList;
        internal Array<MeasureTextCacheItem> MeasureTextHashMapInternal;
        internal Array<int> MeasureTextHashMapInternalFreeList;
        internal Array<int> MeasureTextHashMap;
        internal Array<MeasuredWord> MeasuredWords;
        internal Array<int> MeasuredWordsFreeList;
        internal Array<int> OpenClipElementStack;
        internal Array<ElementId> PointerOverIds;
        internal Array<ScrollContainerDataInternal> ScrollContainerDatas;
        internal Array<TransitionDataInternal> TransitionDatas;
        internal Array<bool> TreeNodeVisited;
        internal Array<Warning> Warnings;

        // Reports an error through the configured error handler (mirrors the C `context->errorHandler.errorHandlerFunction(...)` calls).
        internal void Error(ErrorType errorType, string errorText)
        {
            ErrorHandler.ErrorHandlerFunction?.Invoke(new ErrorData
            {
                ErrorType = errorType,
                ErrorText = errorText,
                UserData = ErrorHandler.UserData,
            });
        }

        // Persistent memory — initialized once and not reset between frames.
        internal void InitializePersistentMemory()
        {
            ScrollContainerDatas = new Array<ScrollContainerDataInternal>(100);
            TransitionDatas = new Array<TransitionDataInternal>(200);
            LayoutElementsHashMapInternal = new Array<LayoutElementHashMapItem>(MaxElementCount);
            LayoutElementsHashMap = new Array<int>(MaxElementCount);
            LayoutElementsHashMapFreeList = new Array<int>(MaxElementCount);
            MeasureTextHashMapInternal = new Array<MeasureTextCacheItem>(MaxElementCount);
            MeasureTextHashMapInternalFreeList = new Array<int>(MaxElementCount);
            MeasuredWordsFreeList = new Array<int>(MaxMeasureTextCacheWordCount);
            MeasureTextHashMap = new Array<int>(MaxElementCount);
            MeasuredWords = new Array<MeasuredWord>(MaxMeasureTextCacheWordCount);
            PointerOverIds = new Array<ElementId>(MaxElementCount);
        }

        // Ephemeral memory — reset every frame.
        internal void InitializeEphemeralMemory()
        {
            LayoutElementChildrenBuffer = new Array<int>(MaxElementCount);
            LayoutElements = new Array<LayoutElement>(MaxElementCount);
            WrappedTextLines = new Array<WrappedTextLine>(MaxElementCount);
            LayoutElementTreeNodeArray1 = new Array<LayoutElementTreeNode>(MaxElementCount);
            LayoutElementTreeRoots = new Array<LayoutElementTreeRoot>(MaxElementCount);
            LayoutElementChildren = new Array<LayoutElement>(MaxElementCount);
            OpenLayoutElementStack = new Array<int>(MaxElementCount);
            RenderCommands = new Array<RenderCommand>(MaxElementCount);
            TreeNodeVisited = new Array<bool>(MaxElementCount);
            TreeNodeVisited.Length = TreeNodeVisited.Capacity; // Accessed directly rather than behaving as a list.
            OpenClipElementStack = new Array<int>(MaxElementCount);
            ReusableElementIndexBuffer = new Array<int>(MaxElementCount);
            LayoutElementClipElementIds = new Array<int>(MaxElementCount);
            Warnings = new Array<Warning>(100);
        }
    }

    // Generic fixed-capacity array, a managed replacement for the C `_ARRAY_DEFINE` macro families.
    // `ref` returns replace the C `&array->internalArray[i]` pointer returns.
    internal struct Array<T>(int capacity)
    {
        public readonly int Capacity = capacity;
        public int Length = 0;
        public T[] InternalArray = new T[capacity];

        public readonly ref T Get(int index)
        {
            if (__Array_RangeCheck(index, Length)) return ref InternalArray[index];
            return ref Unsafe.NullRef<T>();
        }

        public readonly T GetValue(int index)
        {
            if (__Array_RangeCheck(index, Length)) return InternalArray[index];
            return default!;
        }

        public readonly ref T GetCheckCapacity(int index)
        {
            if (__Array_RangeCheck(index, Capacity)) return ref InternalArray[index];
            return ref Unsafe.NullRef<T>();
        }

        public ref T Add(T item)
        {
            if (__Array_AddCapacityCheck(Length, Capacity))
            {
                InternalArray[Length++] = item;
                return ref InternalArray[Length - 1];
            }
            return ref Unsafe.NullRef<T>();
        }

        public T RemoveSwapback(int index)
        {
            if (__Array_RangeCheck(index, Length))
            {
                Length--;
                T removed = InternalArray[index];
                InternalArray[index] = InternalArray[Length];
                return removed;
            }
            return default!;
        }

        public ref T Set(int index, T value)
        {
            if (__Array_RangeCheck(index, Capacity))
            {
                InternalArray[index] = value;
                Length = index < Length ? Length : index + 1;
                return ref InternalArray[index];
            }
            return ref Unsafe.NullRef<T>();
        }

        public readonly ref T Set_DontTouchLength(int index, T value)
        {
            if (__Array_RangeCheck(index, Capacity))
            {
                InternalArray[index] = value;
                return ref InternalArray[index];
            }
            return ref Unsafe.NullRef<T>();
        }
    }

    // -----------------------------------------
    // ENGINE — the static facade + internals ----
    // -----------------------------------------

    private const float MaxFloat = 3.40282346638528859812e+38f;
    private const float Epsilon = 0.01f;

    internal static Context? SCurrentContext;
    internal static int SDefaultMaxElementCount = 8192;
    internal static int SDefaultMaxMeasureTextWordCacheCount = 16384;

    // Default layout config (matches the C `extern LayoutConfig LAYOUT_DEFAULT`).
    public static readonly LayoutConfig LayoutDefault = default;

    // Debug view globals (the inspector itself lives in Clay.DebugView.cs).
    public static uint DebugViewWidth = 400;
    public static Color DebugViewHighlightColor = new Color(168, 66, 28, 100);

    // Function-pointer globals (mirrors the C `_MeasureText` / `_QueryScrollOffset`).
    internal static MeasureTextFunction? SMeasureText;
    internal static QueryScrollOffsetFunction? SQueryScrollOffset;

    public static Context? GetCurrentContext() => SCurrentContext;
    public static void SetCurrentContext(Context? context) => SCurrentContext = context;

    // -------------------------------------
    // Error helpers ------------------------
    // -------------------------------------

    internal static bool __Array_RangeCheck(int index, int length)
    {
        if (index < length && index >= 0) return true;
        GetCurrentContext()?.Error(ErrorType.InternalError,
            "Clay attempted to make an out of bounds array access. This is an internal error and is likely a bug.");
        return false;
    }

    internal static bool __Array_AddCapacityCheck(int length, int capacity)
    {
        if (length < capacity) return true;
        GetCurrentContext()?.Error(ErrorType.InternalError,
            "Clay attempted to make an out of bounds array access. This is an internal error and is likely a bug.");
        return false;
    }

    // -------------------------------------
    // Hashing ------------------------------
    // -------------------------------------

    internal static ElementId __HashNumber(uint offset, uint seed)
    {
        var hash = new HashCode();
        hash.Add(seed);
        hash.Add(offset + 48);
        uint id = unchecked((uint)hash.ToHashCode());
        return new ElementId { Id = id + 1, Offset = offset, BaseId = seed, StringId = string.Empty }; // Reserve the hash result of zero as "null id".
    }

    internal static ElementId __HashString(string key, uint seed)
    {
        var hash = new HashCode();
        hash.Add(seed);
        hash.Add(key);
        uint id = unchecked((uint)hash.ToHashCode());
        return new ElementId { Id = id + 1, Offset = 0, BaseId = id + 1, StringId = key }; // Reserve the hash result of zero as "null id".
    }

    internal static ElementId __HashStringWithOffset(string key, uint offset, uint seed)
    {
        var baseHash = new HashCode();
        baseHash.Add(seed);
        baseHash.Add(key);
        uint baseId = unchecked((uint)baseHash.ToHashCode());

        var hash = new HashCode();
        hash.Add(baseId);
        hash.Add(offset);
        uint id = unchecked((uint)hash.ToHashCode());

        return new ElementId { Id = id + 1, Offset = offset, BaseId = baseId + 1, StringId = key }; // Reserve the hash result of zero as "null id".
    }

    internal static uint __HashStringContentsWithConfig(string text, TextElementConfig config)
    {
        var hash = new HashCode();
        hash.Add(text);
        hash.Add(config.FontId);
        hash.Add(config.FontSize);
        hash.Add(config.LetterSpacing);
        return unchecked((uint)hash.ToHashCode()) + 1; // Reserve the hash result of zero as "null id".
    }

    // -------------------------------------
    // Element access helpers ---------------
    // -------------------------------------

    internal static LayoutElement __GetOpenLayoutElement()
    {
        var context = GetCurrentContext()!;
        return context.LayoutElements.InternalArray[context.OpenLayoutElementStack.InternalArray[context.OpenLayoutElementStack.Length - 1]];
    }

    internal static LayoutElement __GetParentElement()
    {
        var context = GetCurrentContext()!;
        return context.LayoutElements.InternalArray[context.OpenLayoutElementStack.GetValue(context.OpenLayoutElementStack.Length - 2)];
    }

    internal static uint __GetParentElementId() => __GetParentElement().Id;

    internal static bool __BorderHasAnyWidth(in BorderElementConfig borderConfig)
    {
        return borderConfig.Width.BetweenChildren > 0 || borderConfig.Width.Left > 0 || borderConfig.Width.Right > 0
               || borderConfig.Width.Top > 0 || borderConfig.Width.Bottom > 0;
    }

    internal static void __UpdateAspectRatioBox(LayoutElement layoutElement)
    {
        if (layoutElement.Config.AspectRatio.AspectRatio != 0)
        {
            if (layoutElement.Dimensions.Width == 0 && layoutElement.Dimensions.Height != 0)
            {
                layoutElement.Dimensions.Width = layoutElement.Dimensions.Height * layoutElement.Config.AspectRatio.AspectRatio;
            }
            else if (layoutElement.Dimensions.Width != 0 && layoutElement.Dimensions.Height == 0)
            {
                layoutElement.Dimensions.Height = layoutElement.Dimensions.Width * (1 / layoutElement.Config.AspectRatio.AspectRatio);
            }
        }
    }

    internal static bool __PointIsInsideRect(Vector2 point, BoundingBox rect)
    {
        return point.X >= rect.X && point.X <= rect.X + rect.Width && point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
    }

    internal static bool __FloatEqual(float left, float right)
    {
        float subtracted = left - right;
        return subtracted < Epsilon && subtracted > -Epsilon;
    }

    // Equality helpers replacing the C _MemCmp usage in the non-debug engine.
    internal static bool __ColorEqual(in Color a, in Color b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    internal static bool __BorderWidthEqual(in BorderWidth a, in BorderWidth b)
        => a.Left == b.Left && a.Right == b.Right && a.Top == b.Top && a.Bottom == b.Bottom && a.BetweenChildren == b.BetweenChildren;

    // -------------------------------------
    // Element ID hash map ------------------
    // -------------------------------------

    internal static ref LayoutElementHashMapItem __AddHashMapItem(ElementId elementId, LayoutElement layoutElement, int layoutElementIndex)
    {
        var context = GetCurrentContext()!;
        if (context.LayoutElementsHashMapInternal.Length == context.LayoutElementsHashMapInternal.Capacity - 1)
        {
            if (!context.BooleanWarnings.HashMapCapacityExceeded)
            {
                context.BooleanWarnings.HashMapCapacityExceeded = true;
                context.Error(ErrorType.HashMapCapacityExceeded,
                    "Clay has run out of space in it's internal element ID hashmap.  Try using SetMaxElementCount() with a higher value.");
            }
            return ref Unsafe.NullRef<LayoutElementHashMapItem>();
        }

        var item = new LayoutElementHashMapItem
        {
            ElementId = elementId,
            LayoutElement = layoutElement,
            LayoutElementIndex = layoutElementIndex,
            NextIndex = -1,
            Generation = context.Generation + 1,
            AppearedThisFrame = true,
        };

        int hashBucket = (int)(elementId.Id % (uint)context.LayoutElementsHashMap.Capacity);
        int hashItemPrevious = -1;
        int hashItemIndex = context.LayoutElementsHashMap.InternalArray[hashBucket];
        while (hashItemIndex != -1) // Just replace collision, not a big deal - leave it up to the end user.
        {
            ref var hashItem = ref context.LayoutElementsHashMapInternal.InternalArray[hashItemIndex];
            if (hashItem.ElementId.Id == elementId.Id) // Collision - resolve based on generation.
            {
                item.NextIndex = hashItem.NextIndex;
                if (hashItem.Generation <= context.Generation) // First collision - assume this is the "same" element.
                {
                    hashItem.AppearedThisFrame = hashItem.Generation < context.Generation;
                    hashItem.ElementId = elementId; // If the stringId reference has changed, update the hash item to use the new one.
                    hashItem.Generation = context.Generation + 1;
                    hashItem.LayoutElement = layoutElement;
                    hashItem.LayoutElementIndex = layoutElementIndex;
                    hashItem.DebugData.Collision = false;
                    hashItem.OnHoverFunction = null;
                    hashItem.HoverFunctionUserData = null;
                }
                else // Multiple collisions this frame - two elements have the same ID.
                {
                    context.Error(ErrorType.DuplicateId,
                        "An element with this ID was already previously declared during this layout.");
                    if (context.DebugModeEnabled) hashItem.DebugData.Collision = true;
                }
                return ref hashItem;
            }
            hashItemPrevious = hashItemIndex;
            hashItemIndex = hashItem.NextIndex;
        }

        int indexToUse;
        if (context.LayoutElementsHashMapFreeList.Length > 0)
        {
            indexToUse = context.LayoutElementsHashMapFreeList.InternalArray[context.LayoutElementsHashMapFreeList.Length - 1];
            context.LayoutElementsHashMapFreeList.Length--;
        }
        else
        {
            indexToUse = context.LayoutElementsHashMapInternal.Length;
        }
        context.LayoutElementsHashMapInternal.Set(indexToUse, item);
        if (hashItemPrevious != -1)
        {
            context.LayoutElementsHashMapInternal.InternalArray[hashItemPrevious].NextIndex = indexToUse;
        }
        else
        {
            context.LayoutElementsHashMap.InternalArray[hashBucket] = indexToUse;
        }
        return ref context.LayoutElementsHashMapInternal.InternalArray[indexToUse];
    }

    internal static ref LayoutElementHashMapItem __GetHashMapItem(uint id)
    {
        var context = GetCurrentContext();
        if (context == null) return ref Unsafe.NullRef<LayoutElementHashMapItem>();
        int hashBucket = (int)(id % (uint)context.LayoutElementsHashMap.Capacity);
        int elementIndex = context.LayoutElementsHashMap.InternalArray[hashBucket];
        while (elementIndex != -1)
        {
            ref var hashEntry = ref context.LayoutElementsHashMapInternal.InternalArray[elementIndex];
            if (hashEntry.ElementId.Id == id) return ref hashEntry;
            elementIndex = hashEntry.NextIndex;
        }
        return ref Unsafe.NullRef<LayoutElementHashMapItem>();
    }

    // -------------------------------------
    // Text measurement cache ---------------
    // -------------------------------------

    internal static ref MeasuredWord __AddMeasuredWord(MeasuredWord word, ref MeasuredWord previousWord)
    {
        var context = GetCurrentContext()!;
        if (context.MeasuredWordsFreeList.Length > 0)
        {
            int newItemIndex = context.MeasuredWordsFreeList.InternalArray[context.MeasuredWordsFreeList.Length - 1];
            context.MeasuredWordsFreeList.Length--;
            context.MeasuredWords.InternalArray[newItemIndex] = word;
            previousWord.Next = newItemIndex;
            return ref context.MeasuredWords.InternalArray[newItemIndex];
        }
        else
        {
            previousWord.Next = context.MeasuredWords.Length;
            return ref context.MeasuredWords.Add(word);
        }
    }

    internal static MeasureTextCacheItem __MeasureTextCached(string text, TextElementConfig config)
    {
        var context = GetCurrentContext()!;
        if (SMeasureText == null)
        {
            if (!context.BooleanWarnings.TextMeasurementFunctionNotSet)
            {
                context.BooleanWarnings.TextMeasurementFunctionNotSet = true;
                context.Error(ErrorType.TextMeasurementFunctionNotProvided,
                    "Clay's internal MeasureText function is null. You may have forgotten to call SetMeasureTextFunction(), or passed a NULL function pointer by mistake.");
            }
            return default;
        }

        uint id = __HashStringContentsWithConfig(text, config);
        int hashBucket = (int)(id % (uint)(context.MaxMeasureTextCacheWordCount / 32));
        int elementIndexPrevious = 0;
        int elementIndex = context.MeasureTextHashMap.InternalArray[hashBucket];
        while (elementIndex != 0)
        {
            var hashEntry = context.MeasureTextHashMapInternal.InternalArray[elementIndex];
            if (hashEntry.Id == id)
            {
                hashEntry.Generation = context.Generation;
                context.MeasureTextHashMapInternal.InternalArray[elementIndex] = hashEntry;
                return hashEntry;
            }

            // This element hasn't been seen in a few frames, delete the hash map item.
            if (context.Generation - hashEntry.Generation > 2)
            {
                // Add all the measured words that were included in this measurement to the freelist.
                int nextWordIndex = hashEntry.MeasuredWordsStartIndex;
                while (nextWordIndex != -1)
                {
                    var measuredWord = context.MeasuredWords.InternalArray[nextWordIndex];
                    context.MeasuredWordsFreeList.Add(nextWordIndex);
                    nextWordIndex = measuredWord.Next;
                }

                int nextIndex = hashEntry.NextIndex;
                context.MeasureTextHashMapInternal.InternalArray[elementIndex] = new MeasureTextCacheItem { MeasuredWordsStartIndex = -1 };
                context.MeasureTextHashMapInternalFreeList.Add(elementIndex);
                if (elementIndexPrevious == 0)
                {
                    context.MeasureTextHashMap.InternalArray[hashBucket] = nextIndex;
                }
                else
                {
                    var previousHashEntry = context.MeasureTextHashMapInternal.InternalArray[elementIndexPrevious];
                    previousHashEntry.NextIndex = nextIndex;
                    context.MeasureTextHashMapInternal.InternalArray[elementIndexPrevious] = previousHashEntry;
                }
                elementIndex = nextIndex;
            }
            else
            {
                elementIndexPrevious = elementIndex;
                elementIndex = hashEntry.NextIndex;
            }
        }

        int newItemIndex;
        var measured = new MeasureTextCacheItem { MeasuredWordsStartIndex = -1, Id = id, Generation = context.Generation };
        if (context.MeasureTextHashMapInternalFreeList.Length > 0)
        {
            newItemIndex = context.MeasureTextHashMapInternalFreeList.InternalArray[context.MeasureTextHashMapInternalFreeList.Length - 1];
            context.MeasureTextHashMapInternalFreeList.Length--;
            context.MeasureTextHashMapInternal.InternalArray[newItemIndex] = measured;
        }
        else
        {
            if (context.MeasureTextHashMapInternal.Length == context.MeasureTextHashMapInternal.Capacity - 1)
            {
                if (!context.BooleanWarnings.MaxTextMeasureCacheExceeded)
                {
                    context.BooleanWarnings.MaxTextMeasureCacheExceeded = true;
                    context.Error(ErrorType.ElementsCapacityExceeded,
                        "Clay ran out of capacity while attempting to measure text elements. Try using SetMaxElementCount() with a higher value.");
                }
                return default;
            }
            newItemIndex = context.MeasureTextHashMapInternal.Length;
            context.MeasureTextHashMapInternal.Add(measured);
        }

        int start = 0;
        int end = 0;
        float lineWidth = 0;
        float measuredWidth = 0;
        float measuredHeight = 0;
        float spaceWidth = SMeasureText(new StringSegment(" "), config, context.MeasureTextUserData).Width;

        MeasuredWord tempWord = default;
        tempWord.Next = -1;
        ref MeasuredWord previousWord = ref tempWord;

        while (end < text.Length)
        {
            if (context.MeasuredWords.Length == context.MeasuredWords.Capacity - 1)
            {
                if (!context.BooleanWarnings.MaxTextMeasureCacheExceeded)
                {
                    context.BooleanWarnings.MaxTextMeasureCacheExceeded = true;
                    context.Error(ErrorType.TextMeasurementCapacityExceeded,
                        "Clay has run out of space in it's internal text measurement cache. Try using SetMaxMeasureTextCacheWordCount() (default 16384, with 1 unit storing 1 measured word).");
                }
                return default;
            }

            char current = text[end];
            if (current == ' ' || current == '\n')
            {
                int length = end - start;
                Dimensions dimensions = default;
                if (length > 0)
                {
                    dimensions = SMeasureText(new StringSegment(text, start, length), config, context.MeasureTextUserData);
                }
                measured.MinWidth = MathF.Max(dimensions.Width, measured.MinWidth);
                measuredHeight = MathF.Max(measuredHeight, dimensions.Height);
                if (current == ' ')
                {
                    dimensions.Width += spaceWidth;
                    previousWord = ref __AddMeasuredWord(new MeasuredWord { StartOffset = start, Length = length + 1, Width = dimensions.Width, Next = -1 }, ref previousWord);
                    lineWidth += dimensions.Width;
                }
                if (current == '\n')
                {
                    if (length > 0)
                    {
                        previousWord = ref __AddMeasuredWord(new MeasuredWord { StartOffset = start, Length = length, Width = dimensions.Width, Next = -1 }, ref previousWord);
                    }
                    previousWord = ref __AddMeasuredWord(new MeasuredWord { StartOffset = end + 1, Length = 0, Width = 0, Next = -1 }, ref previousWord);
                    lineWidth += dimensions.Width;
                    measuredWidth = MathF.Max(lineWidth, measuredWidth);
                    measured.ContainsNewlines = true;
                    lineWidth = 0;
                }
                start = end + 1;
            }
            end++;
        }

        if (end - start > 0)
        {
            Dimensions dimensions = SMeasureText(new StringSegment(text, start, end - start), config, context.MeasureTextUserData);
            __AddMeasuredWord(new MeasuredWord { StartOffset = start, Length = end - start, Width = dimensions.Width, Next = -1 }, ref previousWord);
            lineWidth += dimensions.Width;
            measuredHeight = MathF.Max(measuredHeight, dimensions.Height);
            measured.MinWidth = MathF.Max(dimensions.Width, measured.MinWidth);
        }

        measuredWidth = MathF.Max(lineWidth, measuredWidth) - config.LetterSpacing;

        measured.MeasuredWordsStartIndex = tempWord.Next;
        measured.UnwrappedDimensions.Width = measuredWidth;
        measured.UnwrappedDimensions.Height = measuredHeight;

        // In C the `measured` pointer aliases the array slot; write the computed values back.
        context.MeasureTextHashMapInternal.InternalArray[newItemIndex] = measured;

        if (elementIndexPrevious != 0)
        {
            var previousHashEntry = context.MeasureTextHashMapInternal.InternalArray[elementIndexPrevious];
            previousHashEntry.NextIndex = newItemIndex;
            context.MeasureTextHashMapInternal.InternalArray[elementIndexPrevious] = previousHashEntry;
        }
        else
        {
            context.MeasureTextHashMap.InternalArray[hashBucket] = newItemIndex;
        }
        return measured;
    }

    // -------------------------------------
    // Element declaration ------------------
    // -------------------------------------

    internal static SizingAxis __GetElementSizing(LayoutElement element, bool xAxis)
    {
        if (element.IsTextElement) return default;
        return xAxis ? element.Config.Layout.Sizing.Width : element.Config.Layout.Sizing.Height;
    }

    internal static void __OpenElement()
    {
        var context = GetCurrentContext()!;
        if (context.LayoutElements.Length == context.LayoutElements.Capacity - 1 || context.BooleanWarnings.MaxElementsExceeded)
        {
            context.BooleanWarnings.MaxElementsExceeded = true;
            return;
        }

        var openLayoutElement = new LayoutElement();
        context.LayoutElements.Add(openLayoutElement);
        openLayoutElement.Index = context.LayoutElements.Length - 1;
        context.OpenLayoutElementStack.Add(context.LayoutElements.Length - 1);

        // Generate an ID.
        LayoutElement parentElement = context.LayoutElements.InternalArray[context.OpenLayoutElementStack.GetValue(context.OpenLayoutElementStack.Length - 2)];
        uint offset = (uint)(parentElement.Children.Length + parentElement.FloatingChildrenCount);
        ElementId elementId = __HashNumber(offset, parentElement.Id);
        openLayoutElement.Id = elementId.Id;
        __AddHashMapItem(elementId, openLayoutElement, openLayoutElement.Index);

        if (context.OpenClipElementStack.Length > 0)
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, context.OpenClipElementStack.GetValue(context.OpenClipElementStack.Length - 1));
        }
        else
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, 0);
        }
    }

    internal static void __OpenElementWithId(ElementId elementId)
    {
        var context = GetCurrentContext()!;
        if (context.LayoutElements.Length == context.LayoutElements.Capacity - 1 || context.BooleanWarnings.MaxElementsExceeded)
        {
            context.BooleanWarnings.MaxElementsExceeded = true;
            return;
        }

        var openLayoutElement = new LayoutElement { Id = elementId.Id };
        context.LayoutElements.Add(openLayoutElement);
        openLayoutElement.Index = context.LayoutElements.Length - 1;
        context.OpenLayoutElementStack.Add(context.LayoutElements.Length - 1);
        __AddHashMapItem(elementId, openLayoutElement, openLayoutElement.Index);

        if (context.OpenClipElementStack.Length > 0)
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, context.OpenClipElementStack.GetValue(context.OpenClipElementStack.Length - 1));
        }
        else
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, 0);
        }
    }

    internal static void __OpenTextElement(string text, TextElementConfig textConfig)
    {
        var context = GetCurrentContext()!;
        if (context.LayoutElements.Length == context.LayoutElements.Capacity - 1 || context.BooleanWarnings.MaxElementsExceeded)
        {
            context.BooleanWarnings.MaxElementsExceeded = true;
            return;
        }

        LayoutElement parentElement = __GetOpenLayoutElement();

        var textElement = new LayoutElement { TextConfig = textConfig, IsTextElement = true };
        context.LayoutElements.Add(textElement);
        textElement.Index = context.LayoutElements.Length - 1;

        if (context.OpenClipElementStack.Length > 0)
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, context.OpenClipElementStack.GetValue(context.OpenClipElementStack.Length - 1));
        }
        else
        {
            context.LayoutElementClipElementIds.Set(context.LayoutElements.Length - 1, 0);
        }

        context.LayoutElementChildrenBuffer.Add(context.LayoutElements.Length - 1);

        MeasureTextCacheItem textMeasured = __MeasureTextCached(text, textConfig);
        ElementId elementId = __HashNumber((uint)(parentElement.Children.Length + parentElement.FloatingChildrenCount), parentElement.Id);
        textElement.Id = elementId.Id;
        __AddHashMapItem(elementId, textElement, textElement.Index);

        Dimensions textDimensions = new Dimensions
        {
            Width = textMeasured.UnwrappedDimensions.Width,
            Height = textConfig.LineHeight > 0 ? textConfig.LineHeight : textMeasured.UnwrappedDimensions.Height,
        };
        textElement.Dimensions = textDimensions;
        textElement.MinDimensions = new Dimensions { Width = textMeasured.MinWidth, Height = textDimensions.Height };
        textElement.TextElementData = new TextElementData { Text = text, PreferredDimensions = textMeasured.UnwrappedDimensions };
        parentElement.Children.Length++;
    }

    internal static void __ConfigureOpenElementPtr(in ElementDeclaration declaration)
    {
        var context = GetCurrentContext()!;
        LayoutElement openLayoutElement = __GetOpenLayoutElement();
        openLayoutElement.Config = declaration;

        if ((declaration.Layout.Sizing.Width.Type == SizingType.Percent && declaration.Layout.Sizing.Width.Percent > 1)
            || (declaration.Layout.Sizing.Height.Type == SizingType.Percent && declaration.Layout.Sizing.Height.Percent > 1))
        {
            context.Error(ErrorType.PercentageOver1,
                "An element was configured with SIZING_PERCENT, but the provided percentage value was over 1.0. Clay expects a value between 0 and 1, i.e. 20% is 0.2.");
        }

        if (declaration.Floating.AttachTo != FloatingAttachToElement.None)
        {
            ref FloatingElementConfig floatingConfig = ref openLayoutElement.Config.Floating;
            // The depth of the tree will always be at least 2 here (auto generated root element).
            LayoutElement hierarchicalParent = context.LayoutElements.InternalArray[context.OpenLayoutElementStack.GetValue(context.OpenLayoutElementStack.Length - 2)];
            if (hierarchicalParent != null)
            {
                int clipElementId = 0;
                if (declaration.Floating.AttachTo == FloatingAttachToElement.Parent)
                {
                    // Attach to the element's direct hierarchical parent.
                    floatingConfig.ParentId = hierarchicalParent.Id;
                    if (context.OpenClipElementStack.Length > 0)
                    {
                        clipElementId = context.OpenClipElementStack.GetValue(context.OpenClipElementStack.Length - 1);
                    }
                }
                else if (declaration.Floating.AttachTo == FloatingAttachToElement.ElementWithId)
                {
                    ref LayoutElementHashMapItem parentItem = ref __GetHashMapItem(floatingConfig.ParentId);
                    if (Unsafe.IsNullRef(in parentItem))
                    {
                        context.Error(ErrorType.FloatingContainerParentNotFound,
                            "A floating element was declared with a parentId, but no element with that ID was found.");
                    }
                    else
                    {
                        clipElementId = context.LayoutElementClipElementIds.GetValue(parentItem.LayoutElementIndex);
                    }
                }
                else if (declaration.Floating.AttachTo == FloatingAttachToElement.Root)
                {
                    floatingConfig.ParentId = __HashString("_RootContainer", 0).Id;
                }

                if (declaration.Floating.ClipTo == FloatingClipToElement.None)
                {
                    clipElementId = 0;
                }

                int currentElementIndex = context.OpenLayoutElementStack.GetValue(context.OpenLayoutElementStack.Length - 1);
                context.LayoutElementClipElementIds.Set(currentElementIndex, clipElementId);
                context.OpenClipElementStack.Add(clipElementId);
                context.LayoutElementTreeRoots.Add(new LayoutElementTreeRoot
                {
                    LayoutElementIndex = context.OpenLayoutElementStack.GetValue(context.OpenLayoutElementStack.Length - 1),
                    ParentId = floatingConfig.ParentId,
                    ClipElementId = (uint)clipElementId,
                    ZIndex = floatingConfig.ZIndex,
                });
            }
        }

        if (declaration.Clip.Horizontal || declaration.Clip.Vertical)
        {
            context.OpenClipElementStack.Add((int)openLayoutElement.Id);
            // Retrieve or create cached data to track scroll position across frames.
            ref ScrollContainerDataInternal scrollOffset = ref Unsafe.NullRef<ScrollContainerDataInternal>();
            for (int i = 0; i < context.ScrollContainerDatas.Length; i++)
            {
                ref ScrollContainerDataInternal mapping = ref context.ScrollContainerDatas.InternalArray[i];
                if (openLayoutElement.Id == mapping.ElementId)
                {
                    scrollOffset = ref mapping;
                    scrollOffset.LayoutElement = openLayoutElement;
                    scrollOffset.OpenThisFrame = true;
                }
            }
            if (Unsafe.IsNullRef(in scrollOffset))
            {
                scrollOffset = ref context.ScrollContainerDatas.Add(new ScrollContainerDataInternal
                {
                    LayoutElement = openLayoutElement,
                    ScrollOrigin = new Vector2(-1, -1),
                    ElementId = openLayoutElement.Id,
                    OpenThisFrame = true,
                });
            }
            if (context.ExternalScrollHandlingEnabled)
            {
                scrollOffset.ScrollPosition = SQueryScrollOffset!(scrollOffset.ElementId, context.QueryScrollOffsetUserData);
            }
        }

        // Setup data to track transitions across frames.
        if (declaration.Transition.Handler != null)
        {
            ref TransitionDataInternal transitionData = ref Unsafe.NullRef<TransitionDataInternal>();
            LayoutElement parentElement = __GetParentElement();
            for (int i = 0; i < context.TransitionDatas.Length; i++)
            {
                ref TransitionDataInternal existingData = ref context.TransitionDatas.InternalArray[i];
                if (openLayoutElement.Id == existingData.ElementId)
                {
                    if (existingData.State == TransitionState.Exiting)
                    {
                        existingData.State = TransitionState.Idle;
                        ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(openLayoutElement.Id);
                        if (!Unsafe.IsNullRef(in hashMapItem)) hashMapItem.AppearedThisFrame = false;
                    }
                    transitionData = ref existingData;
                    transitionData.ElementThisFrame = openLayoutElement;
                    if (transitionData.ParentId != parentElement.Id)
                    {
                        transitionData.Reparented = true;
                    }
                    transitionData.ParentId = parentElement.Id;
                    transitionData.SiblingIndex = parentElement.Children.Length;
                    transitionData.TransitionOut = declaration.Transition.Exit.SetFinalState != null;
                }
            }
            if (Unsafe.IsNullRef(in transitionData))
            {
                transitionData = ref context.TransitionDatas.Add(new TransitionDataInternal
                {
                    ElementThisFrame = openLayoutElement,
                    ElementId = openLayoutElement.Id,
                    ParentId = parentElement.Id,
                    SiblingIndex = parentElement.Children.Length,
                    TransitionOut = declaration.Transition.Exit.SetFinalState != null,
                });
            }
        }
    }

    internal static void __ConfigureOpenElement(ElementDeclaration declaration) => __ConfigureOpenElementPtr(in declaration);

    internal static void __CloseElement()
    {
        var context = GetCurrentContext()!;
        if (context.BooleanWarnings.MaxElementsExceeded) return;

        LayoutElement openLayoutElement = __GetOpenLayoutElement();
        ref LayoutConfig layoutConfig = ref openLayoutElement.Config.Layout;
        bool elementHasClipHorizontal = openLayoutElement.Config.Clip.Horizontal;
        bool elementHasClipVertical = openLayoutElement.Config.Clip.Vertical;
        if (elementHasClipHorizontal || elementHasClipVertical || openLayoutElement.Config.Floating.AttachTo != FloatingAttachToElement.None)
        {
            context.OpenClipElementStack.Length--;
        }

        float leftRightPadding = layoutConfig.Padding.Left + layoutConfig.Padding.Right;
        float topBottomPadding = layoutConfig.Padding.Top + layoutConfig.Padding.Bottom;

        // Attach children to the current open element.
        openLayoutElement.Children.Elements = context.LayoutElementChildren.InternalArray;
        openLayoutElement.Children.Offset = context.LayoutElementChildren.Length;

        if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
        {
            openLayoutElement.Dimensions.Width = leftRightPadding;
            openLayoutElement.MinDimensions.Width = leftRightPadding;
            for (int i = 0; i < openLayoutElement.Children.Length; i++)
            {
                int childIndex = context.LayoutElementChildrenBuffer.GetValue(context.LayoutElementChildrenBuffer.Length - openLayoutElement.Children.Length + i);
                LayoutElement child = context.LayoutElements.InternalArray[childIndex];
                openLayoutElement.Dimensions.Width += child.Dimensions.Width;
                openLayoutElement.Dimensions.Height = MathF.Max(openLayoutElement.Dimensions.Height, child.Dimensions.Height + topBottomPadding);
                // Minimum size of child elements doesn't matter to clip containers as they can shrink and hide their contents.
                if (!elementHasClipHorizontal)
                {
                    openLayoutElement.MinDimensions.Width += child.MinDimensions.Width;
                }
                if (!elementHasClipVertical)
                {
                    openLayoutElement.MinDimensions.Height = MathF.Max(openLayoutElement.MinDimensions.Height, child.MinDimensions.Height + topBottomPadding);
                }
                context.LayoutElementChildren.Add(child);
            }
            float childGap = MathF.Max(openLayoutElement.Children.Length - 1, 0) * layoutConfig.ChildGap;
            openLayoutElement.Dimensions.Width += childGap;
            if (!elementHasClipHorizontal)
            {
                openLayoutElement.MinDimensions.Width += childGap;
            }
        }
        else if (layoutConfig.LayoutDirection == LayoutDirection.TopToBottom)
        {
            openLayoutElement.Dimensions.Height = topBottomPadding;
            openLayoutElement.MinDimensions.Height = topBottomPadding;
            for (int i = 0; i < openLayoutElement.Children.Length; i++)
            {
                int childIndex = context.LayoutElementChildrenBuffer.GetValue(context.LayoutElementChildrenBuffer.Length - openLayoutElement.Children.Length + i);
                LayoutElement child = context.LayoutElements.InternalArray[childIndex];
                openLayoutElement.Dimensions.Height += child.Dimensions.Height;
                openLayoutElement.Dimensions.Width = MathF.Max(openLayoutElement.Dimensions.Width, child.Dimensions.Width + leftRightPadding);
                if (!elementHasClipVertical)
                {
                    openLayoutElement.MinDimensions.Height += child.MinDimensions.Height;
                }
                if (!elementHasClipHorizontal)
                {
                    openLayoutElement.MinDimensions.Width = MathF.Max(openLayoutElement.MinDimensions.Width, child.MinDimensions.Width + leftRightPadding);
                }
                context.LayoutElementChildren.Add(child);
            }
            float childGap = MathF.Max(openLayoutElement.Children.Length - 1, 0) * layoutConfig.ChildGap;
            openLayoutElement.Dimensions.Height += childGap;
            if (!elementHasClipVertical)
            {
                openLayoutElement.MinDimensions.Height += childGap;
            }
        }

        context.LayoutElementChildrenBuffer.Length -= openLayoutElement.Children.Length;

        // Clamp element min and max width to the values configured in the layout.
        if (layoutConfig.Sizing.Width.Type != SizingType.Percent)
        {
            if (layoutConfig.Sizing.Width.MinMax.Max <= 0) layoutConfig.Sizing.Width.MinMax.Max = MaxFloat;
            openLayoutElement.Dimensions.Width = MathF.Min(MathF.Max(openLayoutElement.Dimensions.Width, layoutConfig.Sizing.Width.MinMax.Min), layoutConfig.Sizing.Width.MinMax.Max);
            openLayoutElement.MinDimensions.Width = MathF.Min(MathF.Max(openLayoutElement.MinDimensions.Width, layoutConfig.Sizing.Width.MinMax.Min), layoutConfig.Sizing.Width.MinMax.Max);
        }
        else
        {
            openLayoutElement.Dimensions.Width = 0;
        }

        // Clamp element min and max height to the values configured in the layout.
        if (layoutConfig.Sizing.Height.Type != SizingType.Percent)
        {
            if (layoutConfig.Sizing.Height.MinMax.Max <= 0) layoutConfig.Sizing.Height.MinMax.Max = MaxFloat;
            openLayoutElement.Dimensions.Height = MathF.Min(MathF.Max(openLayoutElement.Dimensions.Height, layoutConfig.Sizing.Height.MinMax.Min), layoutConfig.Sizing.Height.MinMax.Max);
            openLayoutElement.MinDimensions.Height = MathF.Min(MathF.Max(openLayoutElement.MinDimensions.Height, layoutConfig.Sizing.Height.MinMax.Min), layoutConfig.Sizing.Height.MinMax.Max);
        }
        else
        {
            openLayoutElement.Dimensions.Height = 0;
        }

        __UpdateAspectRatioBox(openLayoutElement);

        bool elementIsFloating = openLayoutElement.Config.Floating.AttachTo != FloatingAttachToElement.None;

        // Close the currently open element.
        int closingElementIndex = context.OpenLayoutElementStack.RemoveSwapback(context.OpenLayoutElementStack.Length - 1);

        // Get the currently open parent.
        openLayoutElement = __GetOpenLayoutElement();

        if (context.OpenLayoutElementStack.Length > 1)
        {
            if (elementIsFloating)
            {
                openLayoutElement.FloatingChildrenCount++;
                return;
            }
            openLayoutElement.Children.Length++;
            context.LayoutElementChildrenBuffer.Add(closingElementIndex);
        }
    }

    // -------------------------------------
    // Layout engine ------------------------
    // -------------------------------------

    internal static void __SizeContainersAlongAxis(bool xAxis, bool collectElements, ref Array<int> textElementsOut, ref Array<int> aspectRatioElementsOut)
    {
        var context = GetCurrentContext()!;
        Array<int> bfsBuffer = context.LayoutElementChildrenBuffer;
        Array<int> resizableContainerBuffer = context.OpenLayoutElementStack;

        for (int rootIndex = 0; rootIndex < context.LayoutElementTreeRoots.Length; ++rootIndex)
        {
            bfsBuffer.Length = 0;
            LayoutElementTreeRoot root = context.LayoutElementTreeRoots.InternalArray[rootIndex];
            LayoutElement rootElement = context.LayoutElements.InternalArray[root.LayoutElementIndex];
            bfsBuffer.Add(root.LayoutElementIndex);

            // Size floating containers to their parents.
            if (rootElement.Config.Floating.AttachTo != FloatingAttachToElement.None)
            {
                ref FloatingElementConfig floatingElementConfig = ref rootElement.Config.Floating;
                ref LayoutElementHashMapItem parentItem = ref __GetHashMapItem(floatingElementConfig.ParentId);
                if (!Unsafe.IsNullRef(in parentItem))
                {
                    LayoutElement parentLayoutElement = parentItem.LayoutElement;
                    switch (rootElement.Config.Layout.Sizing.Width.Type)
                    {
                        case SizingType.Grow:
                            rootElement.Dimensions.Width = parentLayoutElement.Dimensions.Width;
                            break;
                        case SizingType.Percent:
                            rootElement.Dimensions.Width = parentLayoutElement.Dimensions.Width * rootElement.Config.Layout.Sizing.Width.Percent;
                            break;
                        default: break;
                    }
                    switch (rootElement.Config.Layout.Sizing.Height.Type)
                    {
                        case SizingType.Grow:
                            rootElement.Dimensions.Height = parentLayoutElement.Dimensions.Height;
                            break;
                        case SizingType.Percent:
                            rootElement.Dimensions.Height = parentLayoutElement.Dimensions.Height * rootElement.Config.Layout.Sizing.Height.Percent;
                            break;
                        default: break;
                    }
                }
            }

            if (rootElement.Config.Layout.Sizing.Width.Type != SizingType.Percent)
            {
                rootElement.Dimensions.Width = MathF.Min(MathF.Max(rootElement.Dimensions.Width, rootElement.Config.Layout.Sizing.Width.MinMax.Min), rootElement.Config.Layout.Sizing.Width.MinMax.Max);
            }
            if (rootElement.Config.Layout.Sizing.Height.Type != SizingType.Percent)
            {
                rootElement.Dimensions.Height = MathF.Min(MathF.Max(rootElement.Dimensions.Height, rootElement.Config.Layout.Sizing.Height.MinMax.Min), rootElement.Config.Layout.Sizing.Height.MinMax.Max);
            }

            for (int i = 0; i < bfsBuffer.Length; ++i)
            {
                int parentIndex = bfsBuffer.InternalArray[i];
                LayoutElement parent = context.LayoutElements.InternalArray[parentIndex];
                ref LayoutConfig parentLayoutConfig = ref parent.Config.Layout;
                int growContainerCount = 0;
                float parentSize = xAxis ? parent.Dimensions.Width : parent.Dimensions.Height;
                float parentPadding = xAxis
                    ? parentLayoutConfig.Padding.Left + parentLayoutConfig.Padding.Right
                    : parentLayoutConfig.Padding.Top + parentLayoutConfig.Padding.Bottom;
                float innerContentSize = 0;
                float totalPaddingAndChildGaps = parentPadding;
                bool sizingAlongAxis = (xAxis && parentLayoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
                                       || (!xAxis && parentLayoutConfig.LayoutDirection == LayoutDirection.TopToBottom);
                resizableContainerBuffer.Length = 0;
                float parentChildGap = parentLayoutConfig.ChildGap;
                bool isFirstChild = true;

                for (int childOffset = 0; childOffset < parent.Children.Length; childOffset++)
                {
                    LayoutElement childElement = parent.Children.Elements[parent.Children.Offset + childOffset];
                    int childElementIndex = childElement.Index;
                    SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                    float childSize = xAxis ? childElement.Dimensions.Width : childElement.Dimensions.Height;

                    if (collectElements && childElement.IsTextElement)
                    {
                        textElementsOut.Add(childElementIndex);
                    }
                    else if (childElement.Children.Length > 0)
                    {
                        bfsBuffer.Add(childElementIndex);
                    }

                    if (!childElement.IsTextElement && collectElements && childElement.Config.AspectRatio.AspectRatio != 0)
                    {
                        aspectRatioElementsOut.Add(childElementIndex);
                    }

                    // Note: setting isFirstChild = false is skipped here.
                    if (childElement.Exiting)
                    {
                        continue;
                    }

                    if (childSizing.Type != SizingType.Percent
                        && childSizing.Type != SizingType.Fixed
                        && (!childElement.IsTextElement || childElement.TextConfig.WrapMode == TextElementConfigWrapMode.Words))
                    {
                        resizableContainerBuffer.Add(childElementIndex);
                    }

                    if (sizingAlongAxis)
                    {
                        innerContentSize += (childSizing.Type == SizingType.Percent ? 0 : childSize);
                        if (childSizing.Type == SizingType.Grow)
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
                for (int childOffset = 0; childOffset < parent.Children.Length; childOffset++)
                {
                    LayoutElement childElement = parent.Children.Elements[parent.Children.Offset + childOffset];
                    SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                    if (childSizing.Type == SizingType.Percent)
                    {
                        float percentSize = (parentSize - totalPaddingAndChildGaps) * childSizing.Percent;
                        if (xAxis) childElement.Dimensions.Width = percentSize;
                        else childElement.Dimensions.Height = percentSize;
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
                        if ((xAxis && parent.Config.Clip.Horizontal) || (!xAxis && parent.Config.Clip.Vertical))
                        {
                            continue;
                        }
                        // Scrolling containers preferentially compress before others.
                        while (sizeToDistribute < -Epsilon && resizableContainerBuffer.Length > 0)
                        {
                            float largest = 0;
                            float secondLargest = 0;
                            float widthToAdd = sizeToDistribute;
                            for (int childIndex = 0; childIndex < resizableContainerBuffer.Length; childIndex++)
                            {
                                LayoutElement child = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childIndex]];
                                float childSize = xAxis ? child.Dimensions.Width : child.Dimensions.Height;
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

                            widthToAdd = MathF.Max(widthToAdd, sizeToDistribute / resizableContainerBuffer.Length);

                            for (int childIndex = 0; childIndex < resizableContainerBuffer.Length; childIndex++)
                            {
                                LayoutElement child = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childIndex]];
                                float minSize = xAxis ? child.MinDimensions.Width : child.MinDimensions.Height;
                                float previousWidth = xAxis ? child.Dimensions.Width : child.Dimensions.Height;
                                if (__FloatEqual(previousWidth, largest))
                                {
                                    float newSize = previousWidth + widthToAdd;
                                    if (newSize <= minSize)
                                    {
                                        newSize = minSize;
                                        resizableContainerBuffer.RemoveSwapback(childIndex--);
                                    }
                                    if (xAxis) child.Dimensions.Width = newSize;
                                    else child.Dimensions.Height = newSize;
                                    sizeToDistribute -= (newSize - previousWidth);
                                }
                            }
                        }
                    }
                    // The content is too small, allow SIZING_GROW containers to expand.
                    else if (sizeToDistribute > 0 && growContainerCount > 0)
                    {
                        for (int childIndex = 0; childIndex < resizableContainerBuffer.Length; childIndex++)
                        {
                            LayoutElement child = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childIndex]];
                            if (__GetElementSizing(child, xAxis).Type != SizingType.Grow)
                            {
                                resizableContainerBuffer.RemoveSwapback(childIndex--);
                            }
                        }
                        while (sizeToDistribute > Epsilon && resizableContainerBuffer.Length > 0)
                        {
                            float smallest = MaxFloat;
                            float secondSmallest = MaxFloat;
                            float widthToAdd = sizeToDistribute;
                            for (int childIndex = 0; childIndex < resizableContainerBuffer.Length; childIndex++)
                            {
                                LayoutElement child = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childIndex]];
                                float childSize = xAxis ? child.Dimensions.Width : child.Dimensions.Height;
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

                            widthToAdd = MathF.Min(widthToAdd, sizeToDistribute / resizableContainerBuffer.Length);

                            for (int childIndex = 0; childIndex < resizableContainerBuffer.Length; childIndex++)
                            {
                                LayoutElement child = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childIndex]];
                                SizingAxis childSizing = __GetElementSizing(child, xAxis);
                                float maxSize = childSizing.MinMax.Max;
                                float previousWidth = xAxis ? child.Dimensions.Width : child.Dimensions.Height;
                                if (__FloatEqual(previousWidth, smallest))
                                {
                                    float newSize = previousWidth + widthToAdd;
                                    if (newSize >= maxSize)
                                    {
                                        newSize = maxSize;
                                        resizableContainerBuffer.RemoveSwapback(childIndex--);
                                    }
                                    if (xAxis) child.Dimensions.Width = newSize;
                                    else child.Dimensions.Height = newSize;
                                    sizeToDistribute -= (newSize - previousWidth);
                                }
                            }
                        }
                    }
                }
                // Sizing along the non layout axis ("off axis").
                else
                {
                    for (int childOffset = 0; childOffset < resizableContainerBuffer.Length; childOffset++)
                    {
                        LayoutElement childElement = context.LayoutElements.InternalArray[resizableContainerBuffer.InternalArray[childOffset]];
                        SizingAxis childSizing = __GetElementSizing(childElement, xAxis);
                        float minSize = xAxis ? childElement.MinDimensions.Width : childElement.MinDimensions.Height;
                        float maxSize = parentSize - parentPadding;
                        // If we're laying out the children of a scroll panel, grow containers expand to the size of the inner content.
                        if ((xAxis && parent.Config.Clip.Horizontal) || (!xAxis && parent.Config.Clip.Vertical))
                        {
                            maxSize = MathF.Max(maxSize, innerContentSize);
                        }
                        if (childSizing.Type == SizingType.Grow)
                        {
                            float growSize = MathF.Min(maxSize, childSizing.MinMax.Max);
                            if (xAxis) childElement.Dimensions.Width = growSize;
                            else childElement.Dimensions.Height = growSize;
                        }
                        float clamped = MathF.Max(minSize, MathF.Min(xAxis ? childElement.Dimensions.Width : childElement.Dimensions.Height, maxSize));
                        if (xAxis) childElement.Dimensions.Width = clamped;
                        else childElement.Dimensions.Height = clamped;
                    }
                }
            }
        }
    }

    internal static void __AddRenderCommand(RenderCommand renderCommand)
    {
        var context = GetCurrentContext()!;
        if (context.RenderCommands.Length < context.RenderCommands.Capacity - 1)
        {
            context.RenderCommands.Add(renderCommand);
        }
        else
        {
            if (!context.BooleanWarnings.MaxRenderCommandsExceeded)
            {
                context.BooleanWarnings.MaxRenderCommandsExceeded = true;
                context.Error(ErrorType.ElementsCapacityExceeded,
                    "Clay ran out of capacity while attempting to create render commands. This is usually caused by a large amount of wrapping text elements while close to the max element capacity. Try using SetMaxElementCount() with a higher value.");
            }
        }
    }

    internal static bool __ElementIsOffscreen(in BoundingBox boundingBox)
    {
        var context = GetCurrentContext()!;
        if (context.DisableCulling) return false;

        return (boundingBox.X > context.LayoutDimensions.Width)
               || (boundingBox.Y > context.LayoutDimensions.Height)
               || (boundingBox.X + boundingBox.Width < 0)
               || (boundingBox.Y + boundingBox.Height < 0);
    }

    internal static void __CalculateFinalLayout(float deltaTime, bool useStoredBoundingBoxes, bool generateRenderCommands)
    {
        var context = GetCurrentContext()!;

        // Calculate sizing along the X axis.
        Array<int> textElements = context.OpenClipElementStack;
        textElements.Length = 0;
        Array<int> aspectRatioElements = context.ReusableElementIndexBuffer;
        aspectRatioElements.Length = 0;
        __SizeContainersAlongAxis(true, true, ref textElements, ref aspectRatioElements);

        // Wrap text.
        for (int textElementIndex = 0; textElementIndex < textElements.Length; ++textElementIndex)
        {
            LayoutElement element = context.LayoutElements.InternalArray[textElements.InternalArray[textElementIndex]];
            ref TextElementData textElementData = ref element.TextElementData;
            textElementData.WrappedLines = new ArraySlice<WrappedTextLine>
            {
                Length = 0,
                InternalArray = context.WrappedTextLines.InternalArray,
                Offset = context.WrappedTextLines.Length,
            };

            MeasureTextCacheItem measureTextCacheItem = __MeasureTextCached(textElementData.Text, element.TextConfig);
            float lineWidth = 0;
            float lineHeight = element.TextConfig.LineHeight > 0 ? element.TextConfig.LineHeight : textElementData.PreferredDimensions.Height;
            int lineLengthChars = 0;
            int lineStartOffset = 0;

            if (!measureTextCacheItem.ContainsNewlines && textElementData.PreferredDimensions.Width <= element.Dimensions.Width)
            {
                context.WrappedTextLines.Add(new WrappedTextLine
                {
                    Dimensions = element.Dimensions,
                    Line = new StringSegment(textElementData.Text),
                });
                textElementData.WrappedLines.Length++;
                continue;
            }

            float spaceWidth = SMeasureText!(new StringSegment(" "), element.TextConfig, context.MeasureTextUserData).Width;
            int wordIndex = measureTextCacheItem.MeasuredWordsStartIndex;
            while (wordIndex != -1)
            {
                if (context.WrappedTextLines.Length > context.WrappedTextLines.Capacity - 1) break;

                MeasuredWord measuredWord = context.MeasuredWords.InternalArray[wordIndex];
                // Only word on the line is too large, just render it anyway.
                if (lineLengthChars == 0 && lineWidth + measuredWord.Width > element.Dimensions.Width)
                {
                    context.WrappedTextLines.Add(new WrappedTextLine
                    {
                        Dimensions = new Dimensions { Width = measuredWord.Width, Height = lineHeight },
                        Line = new StringSegment(textElementData.Text, measuredWord.StartOffset, measuredWord.Length),
                    });
                    textElementData.WrappedLines.Length++;
                    wordIndex = measuredWord.Next;
                    lineStartOffset = measuredWord.StartOffset + measuredWord.Length;
                }
                // measuredWord.length == 0 means a newline character.
                else if (measuredWord.Length == 0 || lineWidth + measuredWord.Width > element.Dimensions.Width)
                {
                    bool finalCharIsSpace = textElementData.Text[Math.Max(lineStartOffset + lineLengthChars - 1, 0)] == ' ';
                    // Clamp to 0 to avoid a negative-length StringSegment in a pathological case.
                    int lineLength = Math.Max(lineLengthChars + (finalCharIsSpace ? -1 : 0), 0);
                    context.WrappedTextLines.Add(new WrappedTextLine
                    {
                        Dimensions = new Dimensions { Width = lineWidth + (finalCharIsSpace ? -spaceWidth : 0), Height = lineHeight },
                        Line = new StringSegment(textElementData.Text, lineStartOffset, lineLength),
                    });
                    textElementData.WrappedLines.Length++;
                    if (lineLengthChars == 0 || measuredWord.Length == 0)
                    {
                        wordIndex = measuredWord.Next;
                    }
                    lineWidth = 0;
                    lineLengthChars = 0;
                    lineStartOffset = measuredWord.StartOffset;
                }
                else
                {
                    lineWidth += measuredWord.Width + element.TextConfig.LetterSpacing;
                    lineLengthChars += measuredWord.Length;
                    wordIndex = measuredWord.Next;
                }
            }

            if (lineLengthChars > 0)
            {
                context.WrappedTextLines.Add(new WrappedTextLine
                {
                    Dimensions = new Dimensions { Width = lineWidth - element.TextConfig.LetterSpacing, Height = lineHeight },
                    Line = new StringSegment(textElementData.Text, lineStartOffset, lineLengthChars),
                });
                textElementData.WrappedLines.Length++;
            }
            element.Dimensions.Height = lineHeight * textElementData.WrappedLines.Length;
        }

        // Scale vertical heights according to aspect ratio.
        for (int i = 0; i < aspectRatioElements.Length; ++i)
        {
            LayoutElement aspectElement = context.LayoutElements.InternalArray[aspectRatioElements.InternalArray[i]];
            aspectElement.Dimensions.Height = (1 / aspectElement.Config.AspectRatio.AspectRatio) * aspectElement.Dimensions.Width;
            aspectElement.Config.Layout.Sizing.Height.MinMax.Max = aspectElement.Dimensions.Height;
        }

        // Propagate the effect of text wrapping / aspect scaling on the height of parents.
        Array<LayoutElementTreeNode> dfsBuffer = context.LayoutElementTreeNodeArray1;
        dfsBuffer.Length = 0;
        for (int i = 0; i < context.LayoutElementTreeRoots.Length; ++i)
        {
            LayoutElementTreeRoot root = context.LayoutElementTreeRoots.InternalArray[i];
            context.TreeNodeVisited.InternalArray[dfsBuffer.Length] = false;
            dfsBuffer.Add(new LayoutElementTreeNode { LayoutElement = context.LayoutElements.InternalArray[root.LayoutElementIndex] });
        }
        while (dfsBuffer.Length > 0)
        {
            LayoutElementTreeNode currentElementTreeNode = dfsBuffer.InternalArray[dfsBuffer.Length - 1];
            LayoutElement currentElement = currentElementTreeNode.LayoutElement;
            if (!context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1])
            {
                context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = true;
                // If the element has no children or is a text element, don't bother inspecting it.
                if (currentElement.IsTextElement || currentElement.Children.Length == 0)
                {
                    dfsBuffer.Length--;
                    continue;
                }
                // Add the children to the DFS buffer.
                for (int i = 0; i < currentElement.Children.Length; i++)
                {
                    context.TreeNodeVisited.InternalArray[dfsBuffer.Length] = false;
                    dfsBuffer.Add(new LayoutElementTreeNode
                    {
                        LayoutElement = currentElement.Children.Elements[currentElement.Children.Offset + i],
                    });
                }
                continue;
            }
            dfsBuffer.Length--;

            // DFS node has been visited, this is on the way back up to the root.
            ref LayoutConfig layoutConfig = ref currentElement.Config.Layout;
            if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
            {
                // Resize any parent containers that have grown in height along their non layout axis.
                for (int j = 0; j < currentElement.Children.Length; ++j)
                {
                    LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + j];
                    float childHeightWithPadding = MathF.Max(childElement.Dimensions.Height + layoutConfig.Padding.Top + layoutConfig.Padding.Bottom, currentElement.Dimensions.Height);
                    currentElement.Dimensions.Height = MathF.Min(MathF.Max(childHeightWithPadding, layoutConfig.Sizing.Height.MinMax.Min), layoutConfig.Sizing.Height.MinMax.Max);
                }
            }
            else if (layoutConfig.LayoutDirection == LayoutDirection.TopToBottom)
            {
                // Resizing along the layout axis.
                float contentHeight = layoutConfig.Padding.Top + layoutConfig.Padding.Bottom;
                for (int j = 0; j < currentElement.Children.Length; ++j)
                {
                    LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + j];
                    contentHeight += childElement.Dimensions.Height;
                }
                contentHeight += MathF.Max(currentElement.Children.Length - 1, 0) * layoutConfig.ChildGap;
                currentElement.Dimensions.Height = MathF.Min(MathF.Max(contentHeight, layoutConfig.Sizing.Height.MinMax.Min), layoutConfig.Sizing.Height.MinMax.Max);
            }
        }

        // Calculate sizing along the Y axis.
        Array<int> noTextElements = default;
        Array<int> noAspectElements = default;
        __SizeContainersAlongAxis(false, false, ref noTextElements, ref noAspectElements);

        // Scale horizontal widths according to aspect ratio.
        for (int i = 0; i < aspectRatioElements.Length; ++i)
        {
            LayoutElement aspectElement = context.LayoutElements.InternalArray[aspectRatioElements.InternalArray[i]];
            aspectElement.Dimensions.Width = aspectElement.Config.AspectRatio.AspectRatio * aspectElement.Dimensions.Height;
        }

        // Sort tree roots by z-index.
        int sortMax = context.LayoutElementTreeRoots.Length - 1;
        while (sortMax > 0) // todo dumb bubble sort.
        {
            for (int i = 0; i < sortMax; ++i)
            {
                LayoutElementTreeRoot current = context.LayoutElementTreeRoots.InternalArray[i];
                LayoutElementTreeRoot next = context.LayoutElementTreeRoots.InternalArray[i + 1];
                if (next.ZIndex < current.ZIndex)
                {
                    context.LayoutElementTreeRoots.InternalArray[i] = next;
                    context.LayoutElementTreeRoots.InternalArray[i + 1] = current;
                }
            }
            sortMax--;
        }

        // Calculate final positions and generate render commands.
        context.RenderCommands.Length = 0;
        dfsBuffer.Length = 0;

        for (int rootIndex = 0; rootIndex < context.LayoutElementTreeRoots.Length; ++rootIndex)
        {
            dfsBuffer.Length = 0;
            LayoutElementTreeRoot root = context.LayoutElementTreeRoots.InternalArray[rootIndex];
            LayoutElement rootElement = context.LayoutElements.InternalArray[root.LayoutElementIndex];
            Vector2 rootPosition = default;
            ref LayoutElementHashMapItem parentHashMapItem = ref __GetHashMapItem(root.ParentId);

            // Position root floating containers.
            if (rootElement.Config.Floating.AttachTo != FloatingAttachToElement.None && !Unsafe.IsNullRef(in parentHashMapItem))
            {
                ref FloatingElementConfig config = ref rootElement.Config.Floating;
                Dimensions rootDimensions = rootElement.Dimensions;
                BoundingBox parentBoundingBox = parentHashMapItem.BoundingBox;
                Vector2 targetAttachPosition = default;

                switch (config.AttachPoints.Parent)
                {
                    case FloatingAttachPointType.LeftTop:
                    case FloatingAttachPointType.LeftCenter:
                    case FloatingAttachPointType.LeftBottom:
                        targetAttachPosition.X = parentBoundingBox.X; break;
                    case FloatingAttachPointType.CenterTop:
                    case FloatingAttachPointType.CenterCenter:
                    case FloatingAttachPointType.CenterBottom:
                        targetAttachPosition.X = parentBoundingBox.X + parentBoundingBox.Width / 2; break;
                    case FloatingAttachPointType.RightTop:
                    case FloatingAttachPointType.RightCenter:
                    case FloatingAttachPointType.RightBottom:
                        targetAttachPosition.X = parentBoundingBox.X + parentBoundingBox.Width; break;
                }
                switch (config.AttachPoints.Element)
                {
                    case FloatingAttachPointType.LeftTop:
                    case FloatingAttachPointType.LeftCenter:
                    case FloatingAttachPointType.LeftBottom: break;
                    case FloatingAttachPointType.CenterTop:
                    case FloatingAttachPointType.CenterCenter:
                    case FloatingAttachPointType.CenterBottom:
                        targetAttachPosition.X -= rootDimensions.Width / 2; break;
                    case FloatingAttachPointType.RightTop:
                    case FloatingAttachPointType.RightCenter:
                    case FloatingAttachPointType.RightBottom:
                        targetAttachPosition.X -= rootDimensions.Width; break;
                }
                switch (config.AttachPoints.Parent)
                {
                    case FloatingAttachPointType.LeftTop:
                    case FloatingAttachPointType.RightTop:
                    case FloatingAttachPointType.CenterTop:
                        targetAttachPosition.Y = parentBoundingBox.Y; break;
                    case FloatingAttachPointType.LeftCenter:
                    case FloatingAttachPointType.CenterCenter:
                    case FloatingAttachPointType.RightCenter:
                        targetAttachPosition.Y = parentBoundingBox.Y + parentBoundingBox.Height / 2; break;
                    case FloatingAttachPointType.LeftBottom:
                    case FloatingAttachPointType.CenterBottom:
                    case FloatingAttachPointType.RightBottom:
                        targetAttachPosition.Y = parentBoundingBox.Y + parentBoundingBox.Height; break;
                }
                switch (config.AttachPoints.Element)
                {
                    case FloatingAttachPointType.LeftTop:
                    case FloatingAttachPointType.RightTop:
                    case FloatingAttachPointType.CenterTop: break;
                    case FloatingAttachPointType.LeftCenter:
                    case FloatingAttachPointType.CenterCenter:
                    case FloatingAttachPointType.RightCenter:
                        targetAttachPosition.Y -= rootDimensions.Height / 2; break;
                    case FloatingAttachPointType.LeftBottom:
                    case FloatingAttachPointType.CenterBottom:
                    case FloatingAttachPointType.RightBottom:
                        targetAttachPosition.Y -= rootDimensions.Height; break;
                }
                targetAttachPosition.X += config.Offset.X;
                targetAttachPosition.Y += config.Offset.Y;
                rootPosition = targetAttachPosition;
            }

            if (root.ClipElementId != 0)
            {
                ref LayoutElementHashMapItem clipHashMapItem = ref __GetHashMapItem(root.ClipElementId);
                if (!Unsafe.IsNullRef(in clipHashMapItem) && !__ElementIsOffscreen(in clipHashMapItem.BoundingBox))
                {
                    // Floating elements attached to scrolling contents won't be correctly positioned if external scroll handling is enabled; fix here.
                    if (context.ExternalScrollHandlingEnabled)
                    {
                        if (clipHashMapItem.LayoutElement.Config.Clip.Horizontal)
                        {
                            rootPosition.X += clipHashMapItem.LayoutElement.Config.Clip.ChildOffset.X;
                        }
                        if (clipHashMapItem.LayoutElement.Config.Clip.Vertical)
                        {
                            rootPosition.Y += clipHashMapItem.LayoutElement.Config.Clip.ChildOffset.Y;
                        }
                    }
                    if (generateRenderCommands)
                    {
                        __AddRenderCommand(new RenderCommand
                        {
                            BoundingBox = clipHashMapItem.BoundingBox,
                            UserData = null,
                            Id = __HashNumber(rootElement.Id, (uint)(rootElement.Children.Length + 10)).Id, // TODO need a better strategy for managing derived ids.
                            ZIndex = root.ZIndex,
                            CommandType = RenderCommandType.ScissorStart,
                        });
                    }
                }
            }

            dfsBuffer.Add(new LayoutElementTreeNode
            {
                LayoutElement = rootElement,
                Position = rootPosition,
                NextChildOffset = new Vector2(rootElement.Config.Layout.Padding.Left, rootElement.Config.Layout.Padding.Top),
            });

            context.TreeNodeVisited.InternalArray[0] = false;
            while (dfsBuffer.Length > 0)
            {
                ref LayoutElementTreeNode currentElementTreeNode = ref dfsBuffer.InternalArray[dfsBuffer.Length - 1];
                LayoutElement currentElement = currentElementTreeNode.LayoutElement;
                LayoutConfig layoutConfig = currentElement.IsTextElement ? LayoutDefault : currentElement.Config.Layout;
                Vector2 scrollOffset = default;

                // DFS is returning back upwards.
                if (context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1])
                {
                    if (currentElement.IsTextElement)
                    {
                        dfsBuffer.Length--;
                        continue;
                    }
                    ref LayoutElementHashMapItem currentElementData = ref __GetHashMapItem(currentElement.Id);
                    if (generateRenderCommands && !Unsafe.IsNullRef(in currentElementData) && !__ElementIsOffscreen(in currentElementData.BoundingBox))
                    {
                        bool closeClipElement = false;
                        if (currentElement.Config.Clip.Horizontal || currentElement.Config.Clip.Vertical)
                        {
                            closeClipElement = true;
                            for (int i = 0; i < context.ScrollContainerDatas.Length; i++)
                            {
                                ScrollContainerDataInternal mapping = context.ScrollContainerDatas.InternalArray[i];
                                if (mapping.LayoutElement == currentElement)
                                {
                                    scrollOffset = currentElement.Config.Clip.ChildOffset;
                                    if (context.ExternalScrollHandlingEnabled)
                                    {
                                        scrollOffset = default;
                                    }
                                    break;
                                }
                            }
                        }

                        if (__BorderHasAnyWidth(in currentElement.Config.Border))
                        {
                            BoundingBox borderBoundingBox = currentElementData.BoundingBox;
                            ref BorderElementConfig borderConfig = ref currentElement.Config.Border;
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = borderBoundingBox,
                                RenderData = new RenderData
                                {
                                    Border = new BorderRenderData
                                    {
                                        Color = borderConfig.Color,
                                        CornerRadius = currentElement.Config.CornerRadius,
                                        Width = borderConfig.Width,
                                    },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = __HashNumber(currentElement.Id, currentElement.Children.Length).Id,
                                CommandType = RenderCommandType.Border,
                            });

                            if (borderConfig.Width.BetweenChildren > 0 && borderConfig.Color.A > 0)
                            {
                                float halfGap = layoutConfig.ChildGap / 2;
                                float halfWidth = borderConfig.Width.BetweenChildren / 2;
                                Vector2 borderOffset = new Vector2(layoutConfig.Padding.Left - halfGap, layoutConfig.Padding.Top - halfGap);
                                if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
                                {
                                    for (int i = 0; i < currentElement.Children.Length; ++i)
                                    {
                                        LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + i];
                                        if (i > 0)
                                        {
                                            __AddRenderCommand(new RenderCommand
                                            {
                                                BoundingBox = new BoundingBox(
                                                    borderBoundingBox.X + borderOffset.X + scrollOffset.X - halfWidth,
                                                    borderBoundingBox.Y + scrollOffset.Y,
                                                    borderConfig.Width.BetweenChildren,
                                                    currentElement.Dimensions.Height),
                                                RenderData = new RenderData
                                                {
                                                    Rectangle = new RectangleRenderData { BackgroundColor = borderConfig.Color },
                                                },
                                                UserData = currentElement.Config.UserData,
                                                Id = __HashNumber(currentElement.Id, (uint)(currentElement.Children.Length + 1 + i)).Id,
                                                CommandType = RenderCommandType.Rectangle,
                                            });
                                        }
                                        borderOffset.X += childElement.Dimensions.Width + layoutConfig.ChildGap;
                                    }
                                }
                                else
                                {
                                    for (int i = 0; i < currentElement.Children.Length; ++i)
                                    {
                                        LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + i];
                                        if (i > 0)
                                        {
                                            __AddRenderCommand(new RenderCommand
                                            {
                                                BoundingBox = new BoundingBox(
                                                    borderBoundingBox.X + scrollOffset.X,
                                                    borderBoundingBox.Y + borderOffset.Y + scrollOffset.Y - halfWidth,
                                                    currentElement.Dimensions.Width,
                                                    borderConfig.Width.BetweenChildren),
                                                RenderData = new RenderData
                                                {
                                                    Rectangle = new RectangleRenderData { BackgroundColor = borderConfig.Color },
                                                },
                                                UserData = currentElement.Config.UserData,
                                                Id = __HashNumber(currentElement.Id, (uint)(currentElement.Children.Length + 1 + i)).Id,
                                                CommandType = RenderCommandType.Rectangle,
                                            });
                                        }
                                        borderOffset.Y += childElement.Dimensions.Height + layoutConfig.ChildGap;
                                    }
                                }
                            }
                        }

                        if (currentElement.Config.OverlayColor.A > 0)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.OverlayColorEnd,
                            });
                        }
                        // This exists because the scissor needs to end _after_ borders between elements.
                        if (closeClipElement)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                Id = __HashNumber(currentElement.Id, (uint)(rootElement.Children.Length + 11)).Id,
                                CommandType = RenderCommandType.ScissorEnd,
                            });
                        }
                    }

                    dfsBuffer.Length--;
                    continue;
                }

                // This will only be run a single time for each element in downwards DFS order.
                context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = true;
                BoundingBox currentElementBoundingBox = new BoundingBox(currentElementTreeNode.Position.X, currentElementTreeNode.Position.Y, currentElement.Dimensions.Width, currentElement.Dimensions.Height);
                ref ScrollContainerDataInternal scrollContainerData = ref Unsafe.NullRef<ScrollContainerDataInternal>();

                if (!currentElement.IsTextElement)
                {
                    if (useStoredBoundingBoxes && currentElement.Config.Transition.Handler != null)
                    {
                        bool found = false;
                        for (int j = 0; j < context.TransitionDatas.Length; ++j)
                        {
                            ref TransitionDataInternal transitionData = ref context.TransitionDatas.InternalArray[j];
                            if (transitionData.ElementId == currentElement.Id)
                            {
                                found = true;
                                if (transitionData.State != TransitionState.Idle)
                                {
                                    if ((transitionData.ActiveProperties & TransitionProperty.X) != 0) currentElementBoundingBox.X = transitionData.CurrentState.BoundingBox.X;
                                    if ((transitionData.ActiveProperties & TransitionProperty.Y) != 0) currentElementBoundingBox.Y = transitionData.CurrentState.BoundingBox.Y;
                                    if ((transitionData.ActiveProperties & TransitionProperty.Width) != 0) currentElementBoundingBox.Width = transitionData.CurrentState.BoundingBox.Width;
                                    if ((transitionData.ActiveProperties & TransitionProperty.Height) != 0) currentElementBoundingBox.Height = transitionData.CurrentState.BoundingBox.Height;
                                }
                                break;
                            }
                        }
                        // An exiting element that completed its transition this frame - skip tree.
                        if (!found && currentElement.Config.Transition.Exit.SetFinalState != null)
                        {
                            dfsBuffer.Length--;
                            continue;
                        }
                    }

                    if (currentElement.Config.Floating.AttachTo != FloatingAttachToElement.None)
                    {
                        ref FloatingElementConfig floatingElementConfig = ref currentElement.Config.Floating;
                        Dimensions expand = floatingElementConfig.Expand;
                        currentElementBoundingBox.X -= expand.Width;
                        currentElementBoundingBox.Width += expand.Width * 2;
                        currentElementBoundingBox.Y -= expand.Height;
                        currentElementBoundingBox.Height += expand.Height * 2;
                    }

                    // Apply scroll offsets to container.
                    if (currentElement.Config.Clip.Horizontal || currentElement.Config.Clip.Vertical)
                    {
                        // This linear scan could theoretically be slow under very strange conditions.
                        for (int i = 0; i < context.ScrollContainerDatas.Length; i++)
                        {
                            ref ScrollContainerDataInternal mapping = ref context.ScrollContainerDatas.InternalArray[i];
                            if (mapping.LayoutElement == currentElement)
                            {
                                scrollContainerData = ref mapping;
                                mapping.BoundingBox = currentElementBoundingBox;
                                scrollOffset = currentElement.Config.Clip.ChildOffset;
                                if (context.ExternalScrollHandlingEnabled)
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
                    if (currentElement.IsTextElement)
                    {
                        ref TextElementConfig textElementConfig = ref currentElement.TextConfig;
                        float naturalLineHeight = currentElement.TextElementData.PreferredDimensions.Height;
                        float finalLineHeight = textElementConfig.LineHeight > 0 ? textElementConfig.LineHeight : naturalLineHeight;
                        float lineHeightOffset = (finalLineHeight - naturalLineHeight) / 2;
                        float yPosition = lineHeightOffset;
                        for (int lineIndex = 0; lineIndex < currentElement.TextElementData.WrappedLines.Length; ++lineIndex)
                        {
                            WrappedTextLine wrappedLine = currentElement.TextElementData.WrappedLines.InternalArray[currentElement.TextElementData.WrappedLines.Offset + lineIndex];
                            if (wrappedLine.Line.Length == 0)
                            {
                                yPosition += finalLineHeight;
                                continue;
                            }
                            float offset = currentElementBoundingBox.Width - wrappedLine.Dimensions.Width;
                            if (textElementConfig.TextAlignment == TextAlignment.Left)
                            {
                                offset = 0;
                            }
                            if (textElementConfig.TextAlignment == TextAlignment.Center)
                            {
                                offset /= 2;
                            }
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = new BoundingBox(currentElementBoundingBox.X + offset, currentElementBoundingBox.Y + yPosition, wrappedLine.Dimensions.Width, wrappedLine.Dimensions.Height),
                                RenderData = new RenderData
                                {
                                    Text = new TextRenderData
                                    {
                                        StringContents = wrappedLine.Line,
                                        TextColor = textElementConfig.TextColor,
                                        FontId = textElementConfig.FontId,
                                        FontSize = textElementConfig.FontSize,
                                        LetterSpacing = textElementConfig.LetterSpacing,
                                        LineHeight = textElementConfig.LineHeight,
                                    },
                                },
                                UserData = textElementConfig.UserData,
                                Id = __HashNumber((uint)lineIndex, currentElement.Id).Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.Text,
                            });
                            yPosition += finalLineHeight;

                            if (!context.DisableCulling && currentElementBoundingBox.Y + yPosition > context.LayoutDimensions.Height)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        if (currentElement.Config.OverlayColor.A > 0)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                RenderData = new RenderData
                                {
                                    OverlayColor = new OverlayColorRenderData { Color = currentElement.Config.OverlayColor },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.OverlayColorStart,
                            });
                        }
                        if (currentElement.Config.Image.ImageData != null)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = currentElementBoundingBox,
                                RenderData = new RenderData
                                {
                                    Image = new ImageRenderData
                                    {
                                        BackgroundColor = currentElement.Config.BackgroundColor,
                                        CornerRadius = currentElement.Config.CornerRadius,
                                        ImageData = currentElement.Config.Image.ImageData,
                                    },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.Image,
                            });
                        }
                        if (currentElement.Config.Custom.CustomData != null)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = currentElementBoundingBox,
                                RenderData = new RenderData
                                {
                                    Custom = new CustomRenderData
                                    {
                                        BackgroundColor = currentElement.Config.BackgroundColor,
                                        CornerRadius = currentElement.Config.CornerRadius,
                                        CustomData = currentElement.Config.Custom.CustomData,
                                    },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.Custom,
                            });
                        }
                        if (currentElement.Config.Clip.Horizontal || currentElement.Config.Clip.Vertical)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = currentElementBoundingBox,
                                RenderData = new RenderData
                                {
                                    Clip = new ClipRenderData
                                    {
                                        Horizontal = currentElement.Config.Clip.Horizontal,
                                        Vertical = currentElement.Config.Clip.Vertical,
                                    },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.ScissorStart,
                            });
                        }
                        if (currentElement.Config.BackgroundColor.A > 0)
                        {
                            __AddRenderCommand(new RenderCommand
                            {
                                BoundingBox = currentElementBoundingBox,
                                RenderData = new RenderData
                                {
                                    Rectangle = new RectangleRenderData
                                    {
                                        BackgroundColor = currentElement.Config.BackgroundColor,
                                        CornerRadius = currentElement.Config.CornerRadius,
                                    },
                                },
                                UserData = currentElement.Config.UserData,
                                Id = currentElement.Id,
                                ZIndex = root.ZIndex,
                                CommandType = RenderCommandType.Rectangle,
                            });
                        }
                    }
                }

                ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(currentElement.Id);
                if (!Unsafe.IsNullRef(in hashMapItem)) hashMapItem.BoundingBox = currentElementBoundingBox;

                if (currentElement.IsTextElement) continue;

                // Setup positions for child elements and add to DFS buffer.

                // On-axis alignment.
                Dimensions contentSizeCurrent = default;
                if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
                {
                    for (int i = 0; i < currentElement.Children.Length; ++i)
                    {
                        LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + i];
                        if (childElement.Exiting) continue;
                        contentSizeCurrent.Width += childElement.Dimensions.Width;
                        contentSizeCurrent.Height = MathF.Max(contentSizeCurrent.Height, childElement.Dimensions.Height);
                    }
                    contentSizeCurrent.Width += MathF.Max(currentElement.Children.Length - 1, 0) * layoutConfig.ChildGap;
                    float extraSpace = currentElement.Dimensions.Width - (layoutConfig.Padding.Left + layoutConfig.Padding.Right) - contentSizeCurrent.Width;
                    switch (layoutConfig.ChildAlignment.X)
                    {
                        case LayoutAlignmentX.Left: extraSpace = 0; break;
                        case LayoutAlignmentX.Center: extraSpace /= 2; break;
                        default: break;
                    }
                    extraSpace = MathF.Max(0, extraSpace);
                    currentElementTreeNode.NextChildOffset.X += extraSpace;
                }
                else if (layoutConfig.LayoutDirection == LayoutDirection.TopToBottom)
                {
                    for (int i = 0; i < currentElement.Children.Length; ++i)
                    {
                        LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + i];
                        if (childElement.Exiting) continue;
                        contentSizeCurrent.Width = MathF.Max(contentSizeCurrent.Width, childElement.Dimensions.Width);
                        contentSizeCurrent.Height += childElement.Dimensions.Height;
                    }
                    contentSizeCurrent.Height += MathF.Max(currentElement.Children.Length - 1, 0) * layoutConfig.ChildGap;
                    float extraSpace = currentElement.Dimensions.Height - (layoutConfig.Padding.Top + layoutConfig.Padding.Bottom) - contentSizeCurrent.Height;
                    switch (layoutConfig.ChildAlignment.Y)
                    {
                        case LayoutAlignmentY.Top: extraSpace = 0; break;
                        case LayoutAlignmentY.Center: extraSpace /= 2; break;
                        default: break;
                    }
                    extraSpace = MathF.Max(0, extraSpace);
                    currentElementTreeNode.NextChildOffset.Y += extraSpace;
                }

                if (!Unsafe.IsNullRef(in scrollContainerData))
                {
                    scrollContainerData.ContentSize = new Dimensions
                    {
                        Width = contentSizeCurrent.Width + layoutConfig.Padding.Left + layoutConfig.Padding.Right,
                        Height = contentSizeCurrent.Height + layoutConfig.Padding.Top + layoutConfig.Padding.Bottom,
                    };
                }

                // Add children to the DFS buffer.
                dfsBuffer.Length += currentElement.Children.Length;
                for (int i = 0; i < currentElement.Children.Length; ++i)
                {
                    LayoutElement childElement = currentElement.Children.Elements[currentElement.Children.Offset + i];

                    // Alignment along non layout axis.
                    if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
                    {
                        currentElementTreeNode.NextChildOffset.Y = currentElement.Config.Layout.Padding.Top;
                        float whiteSpaceAroundChild = currentElement.Dimensions.Height - (layoutConfig.Padding.Top + layoutConfig.Padding.Bottom) - childElement.Dimensions.Height;
                        switch (layoutConfig.ChildAlignment.Y)
                        {
                            case LayoutAlignmentY.Top: break;
                            case LayoutAlignmentY.Center: currentElementTreeNode.NextChildOffset.Y += whiteSpaceAroundChild / 2; break;
                            case LayoutAlignmentY.Bottom: currentElementTreeNode.NextChildOffset.Y += whiteSpaceAroundChild; break;
                        }
                    }
                    else
                    {
                        currentElementTreeNode.NextChildOffset.X = currentElement.Config.Layout.Padding.Left;
                        float whiteSpaceAroundChild = currentElement.Dimensions.Width - (layoutConfig.Padding.Left + layoutConfig.Padding.Right) - childElement.Dimensions.Width;
                        switch (layoutConfig.ChildAlignment.X)
                        {
                            case LayoutAlignmentX.Left: break;
                            case LayoutAlignmentX.Center: currentElementTreeNode.NextChildOffset.X += whiteSpaceAroundChild / 2; break;
                            case LayoutAlignmentX.Right: currentElementTreeNode.NextChildOffset.X += whiteSpaceAroundChild; break;
                        }
                    }

                    Vector2 childPosition = new Vector2(
                        currentElementBoundingBox.X + currentElementTreeNode.NextChildOffset.X + scrollOffset.X,
                        currentElementBoundingBox.Y + currentElementTreeNode.NextChildOffset.Y + scrollOffset.Y);

                    // DFS buffer elements need to be added in reverse because stack traversal happens backwards.
                    int newNodeIndex = dfsBuffer.Length - 1 - i;
                    dfsBuffer.InternalArray[newNodeIndex] = new LayoutElementTreeNode
                    {
                        LayoutElement = childElement,
                        Position = childPosition,
                        NextChildOffset = new Vector2(childElement.Config.Layout.Padding.Left, childElement.Config.Layout.Padding.Top),
                    };
                    context.TreeNodeVisited.InternalArray[newNodeIndex] = false;

                    // Update parent offsets.
                    if (!childElement.Exiting)
                    {
                        if (layoutConfig.LayoutDirection == LayoutDirection.LeftToRight)
                        {
                            currentElementTreeNode.NextChildOffset.X += childElement.Dimensions.Width + layoutConfig.ChildGap;
                        }
                        else
                        {
                            currentElementTreeNode.NextChildOffset.Y += childElement.Dimensions.Height + layoutConfig.ChildGap;
                        }
                    }
                }
            }

            if (root.ClipElementId != 0)
            {
                ref LayoutElementHashMapItem clipHashMapItem = ref __GetHashMapItem(root.ClipElementId);
                if (!Unsafe.IsNullRef(in clipHashMapItem) && !__ElementIsOffscreen(in clipHashMapItem.BoundingBox))
                {
                    __AddRenderCommand(new RenderCommand
                    {
                        Id = __HashNumber(rootElement.Id, (uint)(rootElement.Children.Length + 11)).Id,
                        CommandType = RenderCommandType.ScissorEnd,
                    });
                }
            }
        }
    }

    // -------------------------------------
    // PUBLIC API ---------------------------
    // -------------------------------------

    private static float Lerp(float from, float to, float mix) => from + (to - from) * mix;

    public static Context Initialize(Dimensions layoutDimensions, ErrorHandler errorHandler)
    {
        int maxElementCount = SCurrentContext != null ? SCurrentContext.MaxElementCount : SDefaultMaxElementCount;
        int maxMeasureTextCacheWordCount = SCurrentContext != null ? SCurrentContext.MaxMeasureTextCacheWordCount : SDefaultMaxMeasureTextWordCacheCount;

        var context = new Context
        {
            MaxElementCount = maxElementCount,
            MaxMeasureTextCacheWordCount = maxMeasureTextCacheWordCount,
            ErrorHandler = errorHandler.ErrorHandlerFunction != null ? errorHandler : default,
            LayoutDimensions = layoutDimensions,
        };
        SetCurrentContext(context);
        context.InitializePersistentMemory();
        context.InitializeEphemeralMemory();

        for (int i = 0; i < context.LayoutElementsHashMap.Capacity; ++i)
        {
            context.LayoutElementsHashMap.InternalArray[i] = -1;
        }
        for (int i = 0; i < context.MeasureTextHashMap.Capacity; ++i)
        {
            context.MeasureTextHashMap.InternalArray[i] = 0;
        }
        context.MeasureTextHashMapInternal.Length = 1; // Reserve the 0 value to mean "no next element".
        context.LayoutDimensions = layoutDimensions;
        return context;
    }

    public static void SetMeasureTextFunction(MeasureTextFunction measureTextFunction, object? userData)
    {
        var context = GetCurrentContext()!;
        SMeasureText = measureTextFunction;
        context.MeasureTextUserData = userData;
    }

    public static void SetQueryScrollOffsetFunction(QueryScrollOffsetFunction queryScrollOffsetFunction, object? userData)
    {
        var context = GetCurrentContext()!;
        SQueryScrollOffset = queryScrollOffsetFunction;
        context.QueryScrollOffsetUserData = userData;
    }

    public static void SetLayoutDimensions(Dimensions dimensions)
    {
        var context = GetCurrentContext()!;
        context.RootResizedLastFrame = !__FloatEqual(context.LayoutDimensions.Width, dimensions.Width) || !__FloatEqual(context.LayoutDimensions.Height, dimensions.Height);
        context.LayoutDimensions = dimensions;
    }

    public static Dimensions GetLayoutDimensions() => GetCurrentContext()!.LayoutDimensions;

    public static void SetPointerState(Vector2 position, bool isPointerDown)
    {
        var context = GetCurrentContext()!;
        if (context.BooleanWarnings.MaxElementsExceeded) return;

        context.PointerInfo.Position = position;
        context.PointerOverIds.Length = 0;

        Array<int> dfsBuffer = context.LayoutElementChildrenBuffer;

        for (int rootIndex = context.LayoutElementTreeRoots.Length - 1; rootIndex >= 0; --rootIndex)
        {
            dfsBuffer.Length = 0;
            LayoutElementTreeRoot root = context.LayoutElementTreeRoots.InternalArray[rootIndex];
            dfsBuffer.Add(root.LayoutElementIndex);
            context.TreeNodeVisited.InternalArray[0] = false;
            bool found = false;
            bool skipTree = false;

            while (dfsBuffer.Length > 0)
            {
                if (context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1])
                {
                    dfsBuffer.Length--;
                    continue;
                }
                context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = true;

                int currentElementIndex = dfsBuffer.InternalArray[dfsBuffer.Length - 1];
                LayoutElement currentElement = context.LayoutElements.InternalArray[currentElementIndex];

                ref LayoutElementHashMapItem mapItem = ref __GetHashMapItem(currentElement.Id); // TODO think of a way around this.
                int clipElementId = context.LayoutElementClipElementIds.GetValue(currentElementIndex);
                ref LayoutElementHashMapItem clipItem = ref __GetHashMapItem((uint)clipElementId);

                // This check skips mouse interactions for elements that are currently "exit transitioning".
                if (!Unsafe.IsNullRef(in mapItem) && mapItem.Generation > context.Generation)
                {
                    // Conditionally skip mouse interactions on non-exit transitions, based on user config.
                    if (!currentElement.IsTextElement && currentElement.Config.Transition.Handler != null)
                    {
                        for (int i = 0; i < context.TransitionDatas.Length; ++i)
                        {
                            ref TransitionDataInternal data = ref context.TransitionDatas.InternalArray[i];
                            if (data.ElementId == currentElement.Id)
                            {
                                if (currentElement.Config.Transition.InteractionHandling == TransitionInteractionHandlingType.TransitionDisableInteractionsWhileTransitioningPosition)
                                {
                                    if (data.State == TransitionState.Exiting || data.State == TransitionState.Entering
                                        || ((data.ActiveProperties & TransitionProperty.Position) != 0 && data.State == TransitionState.Transitioning))
                                    {
                                        skipTree = true;
                                    }
                                }
                                else if (currentElement.Config.Transition.InteractionHandling == TransitionInteractionHandlingType.TransitionAllowInteractionsWhileTransitioningPosition)
                                {
                                    if (data.State == TransitionState.Exiting)
                                    {
                                        skipTree = true;
                                    }
                                }
                            }
                        }
                    }

                    if (skipTree)
                    {
                        dfsBuffer.Length--;
                        continue;
                    }

                    BoundingBox elementBox = mapItem.BoundingBox;
                    elementBox.X -= root.PointerOffset.X;
                    elementBox.Y -= root.PointerOffset.Y;
                    if (__PointIsInsideRect(position, elementBox)
                        && (clipElementId == 0 || (!Unsafe.IsNullRef(in clipItem) && __PointIsInsideRect(position, clipItem.BoundingBox)) || context.ExternalScrollHandlingEnabled))
                    {
                        mapItem.OnHoverFunction?.Invoke(mapItem.ElementId, context.PointerInfo, mapItem.HoverFunctionUserData);
                        context.PointerOverIds.Add(mapItem.ElementId);
                        found = true;
                    }

                    for (int i = currentElement.Children.Length - 1; i >= 0; --i)
                    {
                        dfsBuffer.Add(currentElement.Children.Elements[currentElement.Children.Offset + i].Index);
                        context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = false; // TODO needs to be ranged checked.
                    }
                }
                else
                {
                    dfsBuffer.Length--;
                }
            }

            LayoutElement rootElement = context.LayoutElements.InternalArray[root.LayoutElementIndex];
            if (found && rootElement.Config.Floating.AttachTo != FloatingAttachToElement.None
                      && rootElement.Config.Floating.PointerCaptureMode == PointerCaptureMode.Capture)
            {
                break;
            }
        }

        if (isPointerDown)
        {
            if (context.PointerInfo.State == PointerDataInteractionState.PressedThisFrame)
            {
                context.PointerInfo.State = PointerDataInteractionState.Pressed;
            }
            else if (context.PointerInfo.State != PointerDataInteractionState.Pressed)
            {
                context.PointerInfo.State = PointerDataInteractionState.PressedThisFrame;
            }
        }
        else
        {
            if (context.PointerInfo.State == PointerDataInteractionState.ReleasedThisFrame)
            {
                context.PointerInfo.State = PointerDataInteractionState.Released;
            }
            else if (context.PointerInfo.State != PointerDataInteractionState.Released)
            {
                context.PointerInfo.State = PointerDataInteractionState.ReleasedThisFrame;
            }
        }
    }

    public static PointerData GetPointerState() => GetCurrentContext()!.PointerInfo;

    public static Vector2 GetScrollOffset()
    {
        var context = GetCurrentContext()!;
        if (context.BooleanWarnings.MaxElementsExceeded) return default;
        LayoutElement openLayoutElement = __GetOpenLayoutElement();
        for (int i = 0; i < context.ScrollContainerDatas.Length; i++)
        {
            ScrollContainerDataInternal mapping = context.ScrollContainerDatas.InternalArray[i];
            if (mapping.ElementId == openLayoutElement.Id) return mapping.ScrollPosition;
        }
        return default;
    }

    public static void UpdateScrollContainers(bool enableDragScrolling, Vector2 scrollDelta, float deltaTime)
    {
        var context = GetCurrentContext()!;
        bool isPointerActive = enableDragScrolling && (context.PointerInfo.State == PointerDataInteractionState.Pressed
                                                       || context.PointerInfo.State == PointerDataInteractionState.PressedThisFrame);

        // Don't apply scroll events to ancestors of the inner element.
        int highestPriorityElementIndex = -1;
        ref ScrollContainerDataInternal highestPriorityScrollData = ref Unsafe.NullRef<ScrollContainerDataInternal>();

        for (int i = 0; i < context.ScrollContainerDatas.Length; i++)
        {
            ref ScrollContainerDataInternal scrollData = ref context.ScrollContainerDatas.InternalArray[i];
            if (!scrollData.OpenThisFrame)
            {
                context.ScrollContainerDatas.RemoveSwapback(i);
                continue;
            }
            scrollData.OpenThisFrame = false;
            ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(scrollData.ElementId);
            // Element isn't rendered this frame but scroll offset has been retained.
            if (Unsafe.IsNullRef(in hashMapItem))
            {
                context.ScrollContainerDatas.RemoveSwapback(i);
                continue;
            }

            // Touch / click is released.
            if (!isPointerActive && scrollData.PointerScrollActive)
            {
                float xDiff = scrollData.ScrollPosition.X - scrollData.ScrollOrigin.X;
                if (xDiff < -10 || xDiff > 10)
                {
                    scrollData.ScrollMomentum.X = (scrollData.ScrollPosition.X - scrollData.ScrollOrigin.X) / (scrollData.MomentumTime * 25);
                }
                float yDiff = scrollData.ScrollPosition.Y - scrollData.ScrollOrigin.Y;
                if (yDiff < -10 || yDiff > 10)
                {
                    scrollData.ScrollMomentum.Y = (scrollData.ScrollPosition.Y - scrollData.ScrollOrigin.Y) / (scrollData.MomentumTime * 25);
                }
                scrollData.PointerScrollActive = false;
                scrollData.PointerOrigin = default;
                scrollData.ScrollOrigin = default;
                scrollData.MomentumTime = 0;
            }

            // Apply existing momentum.
            scrollData.ScrollPosition.X += scrollData.ScrollMomentum.X;
            scrollData.ScrollMomentum.X *= 0.95f;
            bool scrollOccurred = scrollDelta.X != 0 || scrollDelta.Y != 0;
            if ((scrollData.ScrollMomentum.X > -0.1f && scrollData.ScrollMomentum.X < 0.1f) || scrollOccurred)
            {
                scrollData.ScrollMomentum.X = 0;
            }
            scrollData.ScrollPosition.X = MathF.Min(MathF.Max(scrollData.ScrollPosition.X, -MathF.Max(scrollData.ContentSize.Width - scrollData.LayoutElement.Dimensions.Width, 0)), 0);

            scrollData.ScrollPosition.Y += scrollData.ScrollMomentum.Y;
            scrollData.ScrollMomentum.Y *= 0.95f;
            if ((scrollData.ScrollMomentum.Y > -0.1f && scrollData.ScrollMomentum.Y < 0.1f) || scrollOccurred)
            {
                scrollData.ScrollMomentum.Y = 0;
            }
            scrollData.ScrollPosition.Y = MathF.Min(MathF.Max(scrollData.ScrollPosition.Y, -MathF.Max(scrollData.ContentSize.Height - scrollData.LayoutElement.Dimensions.Height, 0)), 0);

            for (int j = 0; j < context.PointerOverIds.Length; ++j) // TODO n & m are small here but n*m gives me the creeps.
            {
                if (scrollData.LayoutElement.Id == context.PointerOverIds.InternalArray[j].Id)
                {
                    highestPriorityElementIndex = j;
                    highestPriorityScrollData = ref scrollData;
                }
            }
        }

        if (highestPriorityElementIndex > -1 && !Unsafe.IsNullRef(in highestPriorityScrollData))
        {
            LayoutElement scrollElement = highestPriorityScrollData.LayoutElement;
            ref ClipElementConfig clipConfig = ref scrollElement.Config.Clip;
            bool canScrollVertically = clipConfig.Vertical && highestPriorityScrollData.ContentSize.Height > scrollElement.Dimensions.Height;
            bool canScrollHorizontally = clipConfig.Horizontal && highestPriorityScrollData.ContentSize.Width > scrollElement.Dimensions.Width;

            // Handle wheel scroll.
            if (canScrollVertically)
            {
                highestPriorityScrollData.ScrollPosition.Y = highestPriorityScrollData.ScrollPosition.Y + scrollDelta.Y * 10;
            }
            if (canScrollHorizontally)
            {
                highestPriorityScrollData.ScrollPosition.X = highestPriorityScrollData.ScrollPosition.X + scrollDelta.X * 10;
            }

            // Handle click / touch scroll.
            if (isPointerActive)
            {
                highestPriorityScrollData.ScrollMomentum = default;
                if (!highestPriorityScrollData.PointerScrollActive)
                {
                    highestPriorityScrollData.PointerOrigin = context.PointerInfo.Position;
                    highestPriorityScrollData.ScrollOrigin = highestPriorityScrollData.ScrollPosition;
                    highestPriorityScrollData.PointerScrollActive = true;
                }
                else
                {
                    float scrollDeltaX = 0, scrollDeltaY = 0;
                    if (canScrollHorizontally)
                    {
                        float oldXScrollPosition = highestPriorityScrollData.ScrollPosition.X;
                        highestPriorityScrollData.ScrollPosition.X = highestPriorityScrollData.ScrollOrigin.X + (context.PointerInfo.Position.X - highestPriorityScrollData.PointerOrigin.X);
                        highestPriorityScrollData.ScrollPosition.X = MathF.Max(MathF.Min(highestPriorityScrollData.ScrollPosition.X, 0), -(highestPriorityScrollData.ContentSize.Width - highestPriorityScrollData.BoundingBox.Width));
                        scrollDeltaX = highestPriorityScrollData.ScrollPosition.X - oldXScrollPosition;
                    }
                    if (canScrollVertically)
                    {
                        float oldYScrollPosition = highestPriorityScrollData.ScrollPosition.Y;
                        highestPriorityScrollData.ScrollPosition.Y = highestPriorityScrollData.ScrollOrigin.Y + (context.PointerInfo.Position.Y - highestPriorityScrollData.PointerOrigin.Y);
                        highestPriorityScrollData.ScrollPosition.Y = MathF.Max(MathF.Min(highestPriorityScrollData.ScrollPosition.Y, 0), -(highestPriorityScrollData.ContentSize.Height - highestPriorityScrollData.BoundingBox.Height));
                        scrollDeltaY = highestPriorityScrollData.ScrollPosition.Y - oldYScrollPosition;
                    }
                    if (scrollDeltaX > -0.1f && scrollDeltaX < 0.1f && scrollDeltaY > -0.1f && scrollDeltaY < 0.1f && highestPriorityScrollData.MomentumTime > 0.15f)
                    {
                        highestPriorityScrollData.MomentumTime = 0;
                        highestPriorityScrollData.PointerOrigin = context.PointerInfo.Position;
                        highestPriorityScrollData.ScrollOrigin = highestPriorityScrollData.ScrollPosition;
                    }
                    else
                    {
                        highestPriorityScrollData.MomentumTime += deltaTime;
                    }
                }
            }

            // Clamp any changes to scroll position to the maximum size of the contents.
            if (canScrollVertically)
            {
                highestPriorityScrollData.ScrollPosition.Y = MathF.Max(MathF.Min(highestPriorityScrollData.ScrollPosition.Y, 0), -(highestPriorityScrollData.ContentSize.Height - scrollElement.Dimensions.Height));
            }
            if (canScrollHorizontally)
            {
                highestPriorityScrollData.ScrollPosition.X = MathF.Max(MathF.Min(highestPriorityScrollData.ScrollPosition.X, 0), -(highestPriorityScrollData.ContentSize.Width - scrollElement.Dimensions.Width));
            }
        }
    }

    public static void BeginLayout()
    {
        var context = GetCurrentContext()!;
        context.InitializeEphemeralMemory();
        context.Generation++;
        context.DynamicElementIndex = 0;

        // Set up the root container that covers the entire window.
        Dimensions rootDimensions = new Dimensions { Width = context.LayoutDimensions.Width, Height = context.LayoutDimensions.Height };
        if (context.DebugModeEnabled)
        {
            // The debug inspector consumes the right-hand strip, so keep the root width reduction for parity with C.
            rootDimensions.Width -= DebugViewWidth;
        }
        context.BooleanWarnings = default;
        __OpenElementWithId(Id("_RootContainer"));
        __ConfigureOpenElement(new ElementDeclaration
        {
            Layout = new LayoutConfig
            {
                Sizing = new Sizing
                {
                    Width = SizingFixed(rootDimensions.Width),
                    Height = SizingFixed(rootDimensions.Height),
                },
            },
        });
        context.OpenLayoutElementStack.Add(0);
        context.LayoutElementTreeRoots.Add(new LayoutElementTreeRoot { LayoutElementIndex = 0 });
    }

    internal static void __ApplyTransitionedPropertiesToElement(LayoutElement currentElement, TransitionProperty properties, TransitionData currentTransitionData, ref BoundingBox boundingBox, bool reparented)
    {
        if ((properties & TransitionProperty.Width) != 0)
        {
            if (!reparented)
            {
                currentElement.Dimensions.Width = currentTransitionData.BoundingBox.Width;
                currentElement.Config.Layout.Sizing.Width = SizingFixed(currentTransitionData.BoundingBox.Width);
            }
            else
            {
                boundingBox.Width = currentTransitionData.BoundingBox.Width;
            }
        }
        if ((properties & TransitionProperty.Height) != 0)
        {
            if (!reparented)
            {
                currentElement.Dimensions.Height = currentTransitionData.BoundingBox.Height;
                currentElement.Config.Layout.Sizing.Height = SizingFixed(currentTransitionData.BoundingBox.Height);
            }
            else
            {
                boundingBox.Height = currentTransitionData.BoundingBox.Height;
            }
        }
        if ((properties & TransitionProperty.X) != 0)
        {
            boundingBox.X = currentTransitionData.BoundingBox.X;
        }
        if ((properties & TransitionProperty.Y) != 0)
        {
            boundingBox.Y = currentTransitionData.BoundingBox.Y;
        }
        if ((properties & TransitionProperty.OverlayColor) != 0)
        {
            currentElement.Config.OverlayColor = currentTransitionData.OverlayColor;
        }
        if ((properties & TransitionProperty.BackgroundColor) != 0)
        {
            currentElement.Config.BackgroundColor = currentTransitionData.BackgroundColor;
        }
        if ((properties & TransitionProperty.BorderColor) != 0)
        {
            currentElement.Config.Border.Color = currentTransitionData.BorderColor;
        }
        if ((properties & TransitionProperty.BorderWidth) != 0)
        {
            currentElement.Config.Border.Width = currentTransitionData.BorderWidth;
        }
    }

    public static RenderCommandArray EndLayout(float deltaTime)
    {
        var context = GetCurrentContext()!;
        __CloseElement();

        if (context.OpenLayoutElementStack.Length > 1)
        {
            context.Error(ErrorType.UnbalancedOpenClose,
                "There were still open layout elements when EndLayout was called. This results from an unequal number of calls to _OpenElement and _CloseElement.");
        }

        // Prune non exiting transitions.
        for (int i = 0; i < context.TransitionDatas.Length; ++i)
        {
            ref TransitionDataInternal data = ref context.TransitionDatas.InternalArray[i];
            ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(data.ElementId);
            // Transition element exited and doesn't have an exit handler defined,
            // or the user deleted the transition handler from one frame to the next.
            if (!data.TransitionOut
                && (Unsafe.IsNullRef(in hashMapItem) || hashMapItem.Generation <= context.Generation || hashMapItem.LayoutElement == null || hashMapItem.LayoutElement.Config.Transition.Handler == null))
            {
                context.TransitionDatas.RemoveSwapback(i);
                i--;
                continue;
            }
        }

        Array<int> elementIdsToRemoveTransitions = context.ReusableElementIndexBuffer;
        elementIdsToRemoveTransitions.Length = 0;

        for (int i = 0; i < context.TransitionDatas.Length; ++i)
        {
            ref TransitionDataInternal data = ref context.TransitionDatas.InternalArray[i];
            ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(data.ElementId);
            if (data.TransitionOut)
            {
                TransitionElementConfig config = data.ElementThisFrame.Config.Transition;
                // Element wasn't found this frame - either delete transition data or transition out.
                if (!Unsafe.IsNullRef(in hashMapItem) && hashMapItem.Generation <= context.Generation)
                {
                    ref LayoutElementHashMapItem parentHashMapItem = ref __GetHashMapItem(data.ParentId);
                    // Don't exit transition if the parent has also exited and SKIP_WHEN_PARENT_EXITS is used.
                    if (config.Exit.Trigger == TransitionExitTriggerType.TransitionExitTriggerWhenParentExits
                        || Unsafe.IsNullRef(in parentHashMapItem) || parentHashMapItem.Generation > context.Generation)
                    {
                        // This if only runs one single time when the element first starts exiting.
                        if (data.State != TransitionState.Exiting)
                        {
                            if (Unsafe.IsNullRef(in parentHashMapItem) || parentHashMapItem.Generation <= context.Generation)
                            {
                                data.ElementThisFrame.Config.Floating.AttachTo = FloatingAttachToElement.Root;
                                data.ElementThisFrame.Config.Floating.Offset = new Vector2(hashMapItem.BoundingBox.X, hashMapItem.BoundingBox.Y);
                                data.ElementThisFrame.Config.Floating.ParentId = __HashString("_RootContainer", 0).Id;
                            }
                            hashMapItem.AppearedThisFrame = false;
                            data.ElementThisFrame.Exiting = true;
                            data.ElementThisFrame.Config.Layout.Sizing.Width = SizingFixed(data.ElementThisFrame.Dimensions.Width);
                            data.ElementThisFrame.Config.Layout.Sizing.Height = SizingFixed(data.ElementThisFrame.Dimensions.Height);
                            data.State = TransitionState.Exiting;
                            data.ActiveProperties = config.Properties;
                            data.ElapsedTime = 0;
                            data.TargetState = config.Exit.SetFinalState!(data.TargetState, config.Properties);
                        }

                        // Below this line runs every frame while element is exiting.

                        // Clone the entire subtree back into the main UI layout tree.
                        Array<int> bfsBuffer = context.OpenLayoutElementStack;
                        bfsBuffer.Length = 0;
                        int oldElementIndex = data.ElementThisFrame.Index;
                        LayoutElement exitingElement = data.ElementThisFrame.Clone();
                        context.LayoutElements.Add(exitingElement);
                        int exitingElementIndex = context.LayoutElements.Length - 1;
                        exitingElement.Index = exitingElementIndex;
                        context.LayoutElementClipElementIds.Set(exitingElementIndex, context.LayoutElementClipElementIds.GetValue(oldElementIndex));
                        data.ElementThisFrame = exitingElement;
                        bfsBuffer.Add(exitingElementIndex);

                        int bufferIndex = 0;
                        while (bufferIndex < bfsBuffer.Length)
                        {
                            LayoutElement layoutElement = context.LayoutElements.InternalArray[bfsBuffer.InternalArray[bufferIndex]];
                            ref LayoutElementHashMapItem bfsMapItem = ref __GetHashMapItem(layoutElement.Id);
                            // Children of exiting elements may have been moved elsewhere in the layout; this prevents a duplicate ID error.
                            if (Unsafe.IsNullRef(in bfsMapItem) || bfsMapItem.Generation <= context.Generation)
                            {
                                __AddHashMapItem(new ElementId { Id = layoutElement.Id }, layoutElement, layoutElement.Index);
                                int firstChildSlot = context.LayoutElementChildren.Length;
                                ushort newChildrenLength = layoutElement.Children.Length;
                                for (int j = 0; j < layoutElement.Children.Length; ++j)
                                {
                                    LayoutElement childElement = layoutElement.Children.Elements[layoutElement.Children.Offset + j];
                                    ref LayoutElementHashMapItem childMapItem = ref __GetHashMapItem(childElement.Id);
                                    if (Unsafe.IsNullRef(in childMapItem) || childMapItem.Generation <= context.Generation)
                                    {
                                        // Remove any nested transitions inside exiting trees.
                                        if (!childElement.IsTextElement && childElement.Config.Transition.Handler != null)
                                        {
                                            elementIdsToRemoveTransitions.Add((int)childElement.Id);
                                        }
                                        int oldChildIndex = childElement.Index;
                                        LayoutElement newChildElement = childElement.Clone();
                                        context.LayoutElements.Add(newChildElement);
                                        int newChildIndex = context.LayoutElements.Length - 1;
                                        newChildElement.Index = newChildIndex;
                                        context.LayoutElementClipElementIds.Set(newChildIndex, context.LayoutElementClipElementIds.GetValue(oldChildIndex));
                                        bfsBuffer.Add(newChildIndex);
                                        if (newChildElement.IsTextElement)
                                        {
                                            newChildElement.TextElementData.WrappedLines.Length = 0;
                                        }
                                        context.LayoutElementChildren.Add(newChildElement);
                                    }
                                    else
                                    {
                                        newChildrenLength--;
                                    }
                                }
                                layoutElement.Children = new LayoutElementChildren
                                {
                                    Elements = context.LayoutElementChildren.InternalArray,
                                    Offset = firstChildSlot,
                                    Length = newChildrenLength,
                                };
                            }
                            bufferIndex++;
                        }
                        hashMapItem.LayoutElement = exitingElement;
                        hashMapItem.LayoutElementIndex = exitingElementIndex;

                        // Reattach the inserted subtree to its previous parent if it still exists and the exiting element is not floating.
                        FloatingElementConfig floatingConfig = hashMapItem.LayoutElement.Config.Floating;
                        if (!Unsafe.IsNullRef(in parentHashMapItem) && parentHashMapItem.Generation > context.Generation && floatingConfig.AttachTo == FloatingAttachToElement.None)
                        {
                            LayoutElement parentElement = parentHashMapItem.LayoutElement;
                            int newChildrenStartIndex = context.LayoutElementChildren.Length;
                            bool found = false;
                            if (config.Exit.SiblingOrdering == ExitTransitionSiblingOrdering.UnderneathSiblings)
                            {
                                context.LayoutElementChildren.Add(exitingElement);
                                found = true;
                            }
                            for (int j = 0; j < parentElement.Children.Length; ++j)
                            {
                                if (config.Exit.SiblingOrdering == ExitTransitionSiblingOrdering.NaturalOrder && j == data.SiblingIndex)
                                {
                                    context.LayoutElementChildren.Add(exitingElement);
                                    found = true;
                                }
                                context.LayoutElementChildren.Add(parentElement.Children.Elements[parentElement.Children.Offset + j]);
                            }
                            if (!found)
                            {
                                context.LayoutElementChildren.Add(exitingElement);
                            }
                            parentElement.Children.Length++;
                            parentElement.Children.Elements = context.LayoutElementChildren.InternalArray;
                            parentElement.Children.Offset = newChildrenStartIndex;
                        }
                        // Otherwise, create the tree root for the floating element (needs to be created every frame).
                        else
                        {
                            context.LayoutElementTreeRoots.Add(new LayoutElementTreeRoot
                            {
                                LayoutElementIndex = exitingElementIndex,
                                ParentId = floatingConfig.ParentId,
                                ZIndex = floatingConfig.ZIndex,
                            });
                        }
                    }
                    // Parent exited, just delete child without exit transition.
                    else
                    {
                        context.TransitionDatas.RemoveSwapback(i);
                        i--;
                        continue;
                    }
                }
            }
        }

        // Remove nested transitions.
        for (int i = 0; i < elementIdsToRemoveTransitions.Length; ++i)
        {
            for (int j = 0; j < context.TransitionDatas.Length; ++j)
            {
                if (context.TransitionDatas.InternalArray[j].ElementId == (uint)elementIdsToRemoveTransitions.InternalArray[i])
                {
                    context.TransitionDatas.RemoveSwapback(j);
                    break;
                }
            }
        }

        if (context.BooleanWarnings.MaxElementsExceeded)
        {
            const string message = "Clay Error: Layout elements exceeded _maxElementCount";
            __AddRenderCommand(new RenderCommand
            {
                BoundingBox = new BoundingBox(context.LayoutDimensions.Width / 2 - 59 * 4, context.LayoutDimensions.Height / 2, 0, 0),
                RenderData = new RenderData
                {
                    Text = new TextRenderData
                    {
                        StringContents = new StringSegment(message),
                        TextColor = new Color(255, 0, 0, 255),
                        FontSize = 16,
                    },
                },
                CommandType = RenderCommandType.Text,
            });
        }
        else
        {
            if (context.TransitionDatas.Length > 0)
            {
                __CalculateFinalLayout(deltaTime, false, false);

                for (int i = 0; i < context.TransitionDatas.Length; ++i)
                {
                    ref TransitionDataInternal transitionData = ref context.TransitionDatas.InternalArray[i];
                    LayoutElement currentElement = transitionData.ElementThisFrame;
                    ref LayoutElementHashMapItem mapItem = ref __GetHashMapItem(transitionData.ElementId);
                    if (Unsafe.IsNullRef(in mapItem)) continue;
                    ref LayoutElementHashMapItem parentMapItem = ref __GetHashMapItem(transitionData.ParentId);

                    TransitionData targetState = transitionData.TargetState;
                    if (transitionData.State != TransitionState.Exiting)
                    {
                        targetState = new TransitionData
                        {
                            BoundingBox = mapItem.BoundingBox,
                            BackgroundColor = currentElement.Config.BackgroundColor,
                            OverlayColor = currentElement.Config.OverlayColor,
                            BorderColor = currentElement.Config.Border.Color,
                            BorderWidth = currentElement.Config.Border.Width,
                        };
                    }
                    TransitionData oldTargetState = transitionData.TargetState;
                    transitionData.TargetState = targetState;

                    if (mapItem.AppearedThisFrame)
                    {
                        if (currentElement.Config.Transition.Enter.SetInitialState != null
                            && !(!Unsafe.IsNullRef(in parentMapItem) && parentMapItem.AppearedThisFrame && currentElement.Config.Transition.Enter.Trigger == TransitionEnterTriggerType.TransitionEnterSkipOnFirstParentFrame))
                        {
                            transitionData.State = TransitionState.Entering;
                            transitionData.InitialState = currentElement.Config.Transition.Enter.SetInitialState(transitionData.TargetState, currentElement.Config.Transition.Properties);
                            transitionData.CurrentState = transitionData.InitialState;
                            transitionData.ActiveProperties = currentElement.Config.Transition.Properties;
                            __ApplyTransitionedPropertiesToElement(currentElement, currentElement.Config.Transition.Properties, transitionData.InitialState, ref mapItem.BoundingBox, transitionData.Reparented);
                        }
                        else
                        {
                            transitionData.InitialState = targetState;
                            transitionData.CurrentState = targetState;
                            transitionData.ActiveProperties = TransitionProperty.None;
                        }
                    }
                    else
                    {
                        if (transitionData.State != TransitionState.Exiting)
                        {
                            Vector2 parentScrollOffset = !Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.LayoutElement.Config.Clip.ChildOffset : default;
                            Vector2 newRelativePosition = new Vector2(
                                mapItem.BoundingBox.X - (!Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.BoundingBox.X : 0) - parentScrollOffset.X,
                                mapItem.BoundingBox.Y - (!Unsafe.IsNullRef(in parentMapItem) ? parentMapItem.BoundingBox.Y : 0) - parentScrollOffset.Y);
                            Vector2 oldRelativePosition = transitionData.OldParentRelativePosition;
                            transitionData.OldParentRelativePosition = newRelativePosition;

                            TransitionProperty properties = currentElement.Config.Transition.Properties;
                            TransitionProperty newActiveProperties = TransitionProperty.None;
                            if ((properties & TransitionProperty.X) != 0)
                            {
                                if (!__FloatEqual(oldTargetState.BoundingBox.X, targetState.BoundingBox.X)
                                    && (!__FloatEqual(oldRelativePosition.X, newRelativePosition.X) || transitionData.Reparented)
                                    && !context.RootResizedLastFrame)
                                {
                                    newActiveProperties |= TransitionProperty.X;
                                }
                            }
                            if ((properties & TransitionProperty.Y) != 0)
                            {
                                if (!__FloatEqual(oldTargetState.BoundingBox.Y, targetState.BoundingBox.Y)
                                    && (!__FloatEqual(oldRelativePosition.Y, newRelativePosition.Y) || transitionData.Reparented)
                                    && !context.RootResizedLastFrame)
                                {
                                    newActiveProperties |= TransitionProperty.Y;
                                }
                            }
                            if ((properties & TransitionProperty.Width) != 0)
                            {
                                if (!__FloatEqual(oldTargetState.BoundingBox.Width, targetState.BoundingBox.Width) && !context.RootResizedLastFrame)
                                {
                                    newActiveProperties |= TransitionProperty.Width;
                                }
                            }
                            if ((properties & TransitionProperty.Height) != 0)
                            {
                                if (!__FloatEqual(oldTargetState.BoundingBox.Height, targetState.BoundingBox.Height) && !context.RootResizedLastFrame)
                                {
                                    newActiveProperties |= TransitionProperty.Height;
                                }
                            }
                            if ((properties & TransitionProperty.BackgroundColor) != 0)
                            {
                                if (!__ColorEqual(oldTargetState.BackgroundColor, targetState.BackgroundColor))
                                {
                                    newActiveProperties |= TransitionProperty.BackgroundColor;
                                }
                            }
                            if ((properties & TransitionProperty.OverlayColor) != 0)
                            {
                                if (!__ColorEqual(oldTargetState.OverlayColor, targetState.OverlayColor))
                                {
                                    newActiveProperties |= TransitionProperty.OverlayColor;
                                }
                            }
                            if ((properties & TransitionProperty.BorderColor) != 0)
                            {
                                if (!__ColorEqual(oldTargetState.BorderColor, targetState.BorderColor))
                                {
                                    newActiveProperties |= TransitionProperty.BorderColor;
                                }
                            }
                            if ((properties & TransitionProperty.BorderWidth) != 0)
                            {
                                if (!__BorderWidthEqual(oldTargetState.BorderWidth, targetState.BorderWidth))
                                {
                                    newActiveProperties |= TransitionProperty.BorderWidth;
                                }
                            }

                            if (newActiveProperties != 0)
                            {
                                transitionData.ElapsedTime = 0;
                                transitionData.InitialState = transitionData.CurrentState;
                                transitionData.State = TransitionState.Transitioning;
                                transitionData.ActiveProperties |= newActiveProperties;
                            }
                        }

                        if (transitionData.State == TransitionState.Idle)
                        {
                            transitionData.InitialState = targetState;
                            transitionData.CurrentState = targetState;
                            transitionData.TargetState = targetState;
                            transitionData.ActiveProperties = TransitionProperty.None;
                        }
                        else
                        {
                            bool transitionComplete = currentElement.Config.Transition.Handler!(new TransitionCallbackArguments
                            {
                                TransitionState = transitionData.State,
                                Initial = transitionData.InitialState,
                                Current = ref transitionData.CurrentState,
                                Target = targetState,
                                ElapsedTime = transitionData.ElapsedTime,
                                Duration = currentElement.Config.Transition.Duration,
                                Properties = transitionData.ActiveProperties,
                            });
                            __ApplyTransitionedPropertiesToElement(currentElement, transitionData.ActiveProperties, transitionData.CurrentState, ref mapItem.BoundingBox, transitionData.Reparented);
                            transitionData.ElapsedTime += deltaTime;

                            if (transitionComplete)
                            {
                                if (transitionData.State == TransitionState.Entering || transitionData.State == TransitionState.Transitioning)
                                {
                                    transitionData.State = TransitionState.Idle;
                                    transitionData.ElapsedTime = 0;
                                    transitionData.Reparented = false;
                                    transitionData.ActiveProperties = TransitionProperty.None;
                                }
                                else if (transitionData.State == TransitionState.Exiting)
                                {
                                    context.TransitionDatas.RemoveSwapback(i);
                                    i--;
                                }
                            }
                        }
                    }
                }

                if (context.DebugModeEnabled)
                {
                    context.WarningsEnabled = false;
                    __RenderDebugView();
                    context.WarningsEnabled = true;
                }

                if (context.BooleanWarnings.MaxElementsExceeded)
                {
                    __AddDebugViewElementsExceededError();
                }
                else
                {
                    __CalculateFinalLayout(deltaTime, true, true);
                }
                // Note: C calls _CloneElementsWithExitTransition() here to persist exiting subtrees in reused
                // arena memory. In C#, object references already keep `elementThisFrame` alive across frames.
            }
            else
            {
                if (context.DebugModeEnabled)
                {
                    context.WarningsEnabled = false;
                    __RenderDebugView();
                    context.WarningsEnabled = true;
                }

                if (context.BooleanWarnings.MaxElementsExceeded)
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
        for (int i = 0; i < context.LayoutElementsHashMap.Capacity; ++i)
        {
            int currentElementIndex = context.LayoutElementsHashMap.InternalArray[i];
            int previousElementIndex = -1;
            while (currentElementIndex != -1)
            {
                LayoutElementHashMapItem currentItem = context.LayoutElementsHashMapInternal.InternalArray[currentElementIndex];
                int nextIndex = currentItem.NextIndex;
                if (currentItem.Generation <= context.Generation)
                {
                    // Delete the underlying item and add it to the freelist.
                    context.LayoutElementsHashMapInternal.InternalArray[currentElementIndex] = new LayoutElementHashMapItem { NextIndex = -1 };
                    context.LayoutElementsHashMapFreeList.Add(currentElementIndex);
                    if (previousElementIndex == -1)
                    {
                        context.LayoutElementsHashMap.InternalArray[i] = nextIndex;
                        currentElementIndex = nextIndex;
                        previousElementIndex = -1;
                    }
                    else
                    {
                        LayoutElementHashMapItem previousItem = context.LayoutElementsHashMapInternal.InternalArray[previousElementIndex];
                        previousItem.NextIndex = nextIndex;
                        context.LayoutElementsHashMapInternal.InternalArray[previousElementIndex] = previousItem;
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

        return new RenderCommandArray(context.RenderCommands);
    }

    public static uint GetOpenElementId() => __GetOpenLayoutElement().Id;

    public static ElementId GetElementId(string idString) => __HashString(idString, 0);

    public static ElementId GetElementIdWithIndex(string idString, uint index) => __HashStringWithOffset(idString, index, 0);

    public static bool Hovered()
    {
        var context = GetCurrentContext()!;
        if (context.BooleanWarnings.MaxElementsExceeded) return false;
        LayoutElement openLayoutElement = __GetOpenLayoutElement();
        for (int i = 0; i < context.PointerOverIds.Length; ++i)
        {
            if (context.PointerOverIds.InternalArray[i].Id == openLayoutElement.Id) return true;
        }
        return false;
    }

    public static void OnHover(OnHoverFunction onHoverFunction, object? userData)
    {
        var context = GetCurrentContext()!;
        if (context.BooleanWarnings.MaxElementsExceeded) return;
        LayoutElement openLayoutElement = __GetOpenLayoutElement();
        ref LayoutElementHashMapItem hashMapItem = ref __GetHashMapItem(openLayoutElement.Id);
        if (!Unsafe.IsNullRef(in hashMapItem))
        {
            hashMapItem.OnHoverFunction = onHoverFunction;
            hashMapItem.HoverFunctionUserData = userData;
        }
    }

    public static bool PointerOver(ElementId elementId) // TODO return priority for separating multiple results.
    {
        var context = GetCurrentContext()!;
        for (int i = 0; i < context.PointerOverIds.Length; ++i)
        {
            if (context.PointerOverIds.InternalArray[i].Id == elementId.Id) return true;
        }
        return false;
    }

    public static ElementIdArray GetPointerOverIds() => new ElementIdArray(GetCurrentContext()!.PointerOverIds);

    public static ScrollContainerData GetScrollContainerData(ElementId id)
    {
        var context = GetCurrentContext()!;
        for (int i = 0; i < context.ScrollContainerDatas.Length; ++i)
        {
            ref ScrollContainerDataInternal scrollContainerData = ref context.ScrollContainerDatas.InternalArray[i];
            if (scrollContainerData.ElementId == id.Id)
            {
                if (scrollContainerData.LayoutElement == null)
                {
                    // This can happen on the first frame before a scroll container is declared.
                    return default;
                }
                return ScrollContainerData.Create(ref scrollContainerData);
            }
        }
        return default;
    }

    public static ElementData GetElementData(ElementId id)
    {
        ref LayoutElementHashMapItem item = ref __GetHashMapItem(id.Id);
        if (Unsafe.IsNullRef(in item)) return default;
        return new ElementData { BoundingBox = item.BoundingBox, Found = true };
    }

    public static void SetDebugModeEnabled(bool enabled) => GetCurrentContext()!.DebugModeEnabled = enabled;
    public static bool IsDebugModeEnabled() => GetCurrentContext()!.DebugModeEnabled;

    public static void SetCullingEnabled(bool enabled) => GetCurrentContext()!.DisableCulling = !enabled;

    public static void SetExternalScrollHandlingEnabled(bool enabled) => GetCurrentContext()!.ExternalScrollHandlingEnabled = enabled;

    public static int GetMaxElementCount() => GetCurrentContext()!.MaxElementCount;

    public static void SetMaxElementCount(int maxElementCount)
    {
        var context = GetCurrentContext();
        if (context != null)
        {
            context.MaxElementCount = maxElementCount;
        }
        else
        {
            SDefaultMaxElementCount = maxElementCount;
            SDefaultMaxMeasureTextWordCacheCount = maxElementCount * 2;
        }
    }

    public static int GetMaxMeasureTextCacheWordCount() => GetCurrentContext()!.MaxMeasureTextCacheWordCount;

    public static void SetMaxMeasureTextCacheWordCount(int maxMeasureTextCacheWordCount)
    {
        var context = GetCurrentContext();
        if (context != null)
        {
            context.MaxMeasureTextCacheWordCount = maxMeasureTextCacheWordCount;
        }
        else
        {
            SDefaultMaxMeasureTextWordCacheCount = maxMeasureTextCacheWordCount;
        }
    }

    public static void ResetMeasureTextCache()
    {
        var context = GetCurrentContext()!;
        context.MeasureTextHashMapInternal.Length = 0;
        context.MeasureTextHashMapInternalFreeList.Length = 0;
        context.MeasureTextHashMap.Length = 0;
        context.MeasuredWords.Length = 0;
        context.MeasuredWordsFreeList.Length = 0;

        for (int i = 0; i < context.MeasureTextHashMap.Capacity; ++i)
        {
            context.MeasureTextHashMap.InternalArray[i] = 0;
        }
        context.MeasureTextHashMapInternal.Length = 1; // Reserve the 0 value to mean "no next element".
    }

    public static bool EaseOut(TransitionCallbackArguments arguments)
    {
        float ratio = 1;
        if (arguments.Duration > 0)
        {
            ratio = MathF.Min(arguments.ElapsedTime / arguments.Duration, 1);
        }
        float inverse = 1f - ratio;
        float lerpAmount = 1f - (inverse * inverse * inverse);

        if ((arguments.Properties & TransitionProperty.X) != 0)
        {
            arguments.Current.BoundingBox.X = Lerp(arguments.Initial.BoundingBox.X, arguments.Target.BoundingBox.X, lerpAmount);
        }
        if ((arguments.Properties & TransitionProperty.Y) != 0)
        {
            arguments.Current.BoundingBox.Y = Lerp(arguments.Initial.BoundingBox.Y, arguments.Target.BoundingBox.Y, lerpAmount);
        }
        if ((arguments.Properties & TransitionProperty.Width) != 0)
        {
            arguments.Current.BoundingBox.Width = Lerp(arguments.Initial.BoundingBox.Width, arguments.Target.BoundingBox.Width, lerpAmount);
        }
        if ((arguments.Properties & TransitionProperty.Height) != 0)
        {
            arguments.Current.BoundingBox.Height = Lerp(arguments.Initial.BoundingBox.Height, arguments.Target.BoundingBox.Height, lerpAmount);
        }
        if ((arguments.Properties & TransitionProperty.BackgroundColor) != 0)
        {
            arguments.Current.BackgroundColor = new Color(
                Lerp(arguments.Initial.BackgroundColor.R, arguments.Target.BackgroundColor.R, lerpAmount),
                Lerp(arguments.Initial.BackgroundColor.G, arguments.Target.BackgroundColor.G, lerpAmount),
                Lerp(arguments.Initial.BackgroundColor.B, arguments.Target.BackgroundColor.B, lerpAmount),
                Lerp(arguments.Initial.BackgroundColor.A, arguments.Target.BackgroundColor.A, lerpAmount));
        }
        if ((arguments.Properties & TransitionProperty.OverlayColor) != 0)
        {
            arguments.Current.OverlayColor = new Color(
                Lerp(arguments.Initial.OverlayColor.R, arguments.Target.OverlayColor.R, lerpAmount),
                Lerp(arguments.Initial.OverlayColor.G, arguments.Target.OverlayColor.G, lerpAmount),
                Lerp(arguments.Initial.OverlayColor.B, arguments.Target.OverlayColor.B, lerpAmount),
                Lerp(arguments.Initial.OverlayColor.A, arguments.Target.OverlayColor.A, lerpAmount));
        }
        if ((arguments.Properties & TransitionProperty.BorderColor) != 0)
        {
            arguments.Current.BorderColor = new Color(
                Lerp(arguments.Initial.BorderColor.R, arguments.Target.BorderColor.R, lerpAmount),
                Lerp(arguments.Initial.BorderColor.G, arguments.Target.BorderColor.G, lerpAmount),
                Lerp(arguments.Initial.BorderColor.B, arguments.Target.BorderColor.B, lerpAmount),
                Lerp(arguments.Initial.BorderColor.A, arguments.Target.BorderColor.A, lerpAmount));
        }
        if ((arguments.Properties & TransitionProperty.BorderWidth) != 0)
        {
            arguments.Current.BorderWidth = new BorderWidth
            {
                Left = (ushort)Lerp(arguments.Initial.BorderWidth.Left, arguments.Target.BorderWidth.Left, lerpAmount),
                Right = (ushort)Lerp(arguments.Initial.BorderWidth.Right, arguments.Target.BorderWidth.Right, lerpAmount),
                Top = (ushort)Lerp(arguments.Initial.BorderWidth.Top, arguments.Target.BorderWidth.Top, lerpAmount),
                Bottom = (ushort)Lerp(arguments.Initial.BorderWidth.Bottom, arguments.Target.BorderWidth.Bottom, lerpAmount),
                BetweenChildren = (ushort)Lerp(arguments.Initial.BorderWidth.BetweenChildren, arguments.Target.BorderWidth.BetweenChildren, lerpAmount),
            };
        }
        return ratio >= 1;
    }

    // -------------------------------------
    // DSL (replaces the C macros) ---------
    // -------------------------------------

    public sealed class ElementScope : IDisposable
    {
        void IDisposable.Dispose() => __CloseElement();
            
        public void Close() => __CloseElement();
    }

    private static readonly ElementScope SElementScope = new ElementScope();

    // CLAY(id, ...) { ... }  →  using (Clay.Element(id, decl)) { ... }
    public static ElementScope Element(ElementId id, ElementDeclaration declaration)
    {
        __OpenElementWithId(id);
        __ConfigureOpenElement(declaration);
        return SElementScope;
    }

    // Overload that evaluates the declaration _after_ the element is opened, so expressions like
    // Clay.Hovered() or Clay.GetScrollOffset() inside the declaration observe the newly opened element
    // (matching the C macro's evaluation order).
    public static ElementScope Element(ElementId id, Func<ElementDeclaration> declaration)
    {
        __OpenElementWithId(id);
        __ConfigureOpenElement(declaration());
        return SElementScope;
    }

    // AUTO_ID(...) { ... }  →  using (Clay.AutoId(decl)) { ... }
    public static ElementScope AutoId(ElementDeclaration declaration) => AutoId(() => declaration);

    public static ElementScope AutoId(Func<ElementDeclaration> declaration)
    {
        __OpenElement();
        __ConfigureOpenElement(declaration());
        return SElementScope;
    }

    // TEXT(text, ...)  →  Clay.Text(text, config)
    public static void Text(string text, TextElementConfig textConfig) => __OpenTextElement(text, textConfig);

    // ID helpers (ID / SID / IDI / SIDI / ID_LOCAL / ...)
    public static ElementId Id(string label) => __HashString(label, 0);
    public static ElementId SId(string label) => __HashString(label, 0);
    public static ElementId Idi(string label, uint index) => __HashStringWithOffset(label, index, 0);
    public static ElementId SIdi(string label, uint index) => __HashStringWithOffset(label, index, 0);
    public static ElementId IdLocal(string label) => __HashString(label, GetOpenElementId());
    public static ElementId SIdLocal(string label) => __HashString(label, GetOpenElementId());
    public static ElementId IdiLocal(string label, uint index) => __HashStringWithOffset(label, index, GetOpenElementId());
    public static ElementId SIdiLocal(string label, uint index) => __HashStringWithOffset(label, index, GetOpenElementId());

    // Sizing / padding / corner / border helpers (SIZING_* / PADDING_ALL / ...).
    public static SizingAxis SizingFixed(float fixedSize) => new SizingAxis
    {
        MinMax = new SizingMinMax { Min = fixedSize, Max = fixedSize },
        Type = SizingType.Fixed,
    };

    public static SizingAxis SizingGrow() => new SizingAxis { MinMax = default, Type = SizingType.Grow };
    public static SizingAxis SizingGrow(float min) => new SizingAxis { MinMax = new SizingMinMax { Min = min, Max = 0 }, Type = SizingType.Grow };
    public static SizingAxis SizingGrow(float min, float max) => new SizingAxis { MinMax = new SizingMinMax { Min = min, Max = max }, Type = SizingType.Grow };

    public static SizingAxis SizingFit() => new SizingAxis { MinMax = default, Type = SizingType.Fit };
    public static SizingAxis SizingFit(float min) => new SizingAxis { MinMax = new SizingMinMax { Min = min, Max = 0 }, Type = SizingType.Fit };
    public static SizingAxis SizingFit(float min, float max) => new SizingAxis { MinMax = new SizingMinMax { Min = min, Max = max }, Type = SizingType.Fit };

    public static SizingAxis SizingPercent(float percentOfParent) => new SizingAxis { Percent = percentOfParent, Type = SizingType.Percent };

    public static Padding PaddingAll(ushort padding) => new Padding { Left = padding, Right = padding, Top = padding, Bottom = padding };
    public static CornerRadiusValues CornerRadius(float radius) => new CornerRadiusValues { TopLeft = radius, TopRight = radius, BottomLeft = radius, BottomRight = radius };
    public static BorderWidth BorderAll(ushort widthValue) => new BorderWidth { Left = widthValue, Right = widthValue, Top = widthValue, Bottom = widthValue, BetweenChildren = widthValue };
    public static BorderWidth BorderOutside(ushort widthValue) => new BorderWidth { Left = widthValue, Right = widthValue, Top = widthValue, Bottom = widthValue, BetweenChildren = 0 };
}