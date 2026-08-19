using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Primitives;

namespace ClaySharp;

// Self-hosted debug inspector, ported from clay.h's DebugTools region (_RenderDebugView and helpers).
// This is a partial class companion to Clay.cs.
public static partial class Clay
{
    // -------------------------------------
    // Debug view constants + helpers ------
    // -------------------------------------

    private static readonly Color DebugViewColor1 = new(58, 56, 52, 255);
    private static readonly Color DebugViewColor2 = new(62, 60, 58, 255);
    private static readonly Color DebugViewColor3 = new(141, 133, 135, 255);
    private static readonly Color DebugViewColor4 = new(238, 226, 231, 255);
    private static readonly Color DebugViewColorSelectedRow = new(102, 80, 78, 255);
    private const int DebugViewRowHeight = 30;
    private const int DebugViewOuterPadding = 10;
    private const int DebugViewIndentWidth = 16;

    private static readonly TextElementConfig DebugViewTextNameConfig = new()
    {
        TextColor = new Color(238, 226, 231, 255),
        FontSize = 16,
        WrapMode = TextElementConfigWrapMode.None,
    };

    private static readonly LayoutConfig DebugViewScrollViewItemLayoutConfig = new()
    {
        Sizing = new Sizing { Height = SizingFixed(DebugViewRowHeight) },
        ChildGap = 6,
        ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
    };

    private enum DebugElementConfigType
    {
        BackgroundColor,
        OverlayColor,
        CornerRadius,
        Text,
        Aspect,
        Image,
        Floating,
        Clip,
        Border,
        Custom,
    }

    private struct DebugElementConfigTypeLabelConfig
    {
        public string Label;
        public Color Color;
    }

    private struct RenderDebugLayoutData
    {
        public int RowCount;
        public int SelectedElementRowIndex;
    }

    private static DebugElementConfigTypeLabelConfig __DebugGetElementConfigTypeLabel(DebugElementConfigType type)
    {
        switch (type)
        {
            case DebugElementConfigType.BackgroundColor: return new() { Label = "Background", Color = new(243, 134, 48, 255) };
            case DebugElementConfigType.OverlayColor: return new() { Label = "Overlay", Color = new(142, 129, 206, 255) };
            case DebugElementConfigType.CornerRadius: return new() { Label = "Radius", Color = new(239, 148, 157, 255) };
            case DebugElementConfigType.Text: return new() { Label = "Text", Color = new(105, 210, 231, 255) };
            case DebugElementConfigType.Aspect: return new() { Label = "Aspect", Color = new(101, 149, 194, 255) };
            case DebugElementConfigType.Image: return new() { Label = "Image", Color = new(121, 189, 154, 255) };
            case DebugElementConfigType.Floating: return new() { Label = "Floating", Color = new(250, 105, 0, 255) };
            case DebugElementConfigType.Clip: return new() { Label = "Scroll", Color = new(242, 196, 90, 255) };
            case DebugElementConfigType.Border: return new() { Label = "Border", Color = new(108, 91, 123, 255) };
            case DebugElementConfigType.Custom: return new() { Label = "Custom", Color = new(11, 72, 107, 255) };
            default: break;
        }
        return new() { Label = "Error", Color = new(0, 0, 0, 255) };
    }

    // Replaces the C _IntToString dynamic-string buffer. The C function takes an int32_t but every
    // caller passes a float, so C implicitly truncates; we reproduce that truncation here.
    private static string __IntToString(float value) => ((int)value).ToString();

    private static bool __CornerRadiusIsZero(in CornerRadiusValues cornerRadius)
        => cornerRadius.TopLeft == 0 && cornerRadius.TopRight == 0 && cornerRadius.BottomLeft == 0 && cornerRadius.BottomRight == 0;

    private static void __RenderElementConfigTypeLabel(string label, Color color, bool offscreen)
    {
        Color backgroundColor = color;
        backgroundColor.A = 90;
        using (AutoId(new ElementDeclaration
        {
            Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
            BackgroundColor = backgroundColor,
            CornerRadius = CornerRadius(4),
            Border = new BorderElementConfig { Color = color, Width = BorderOutside(1) },
        }))
        {
            Text(label, new TextElementConfig
            {
                TextColor = offscreen ? DebugViewColor3 : DebugViewColor4,
                FontSize = 16,
            });
        }
    }

    private static void __RenderDebugLayoutSizing(SizingAxis sizing, TextElementConfig infoTextConfig)
    {
        string sizingLabel = "GROW";
        if (sizing.Type == SizingType.Fit)
        {
            sizingLabel = "FIT";
        }
        else if (sizing.Type == SizingType.Percent)
        {
            sizingLabel = "PERCENT";
        }
        else if (sizing.Type == SizingType.Fixed)
        {
            sizingLabel = "FIXED";
        }
        Text(sizingLabel, infoTextConfig);
        if (sizing.Type == SizingType.Grow || sizing.Type == SizingType.Fit || sizing.Type == SizingType.Fixed)
        {
            Text("(", infoTextConfig);
            if (sizing.MinMax.Min != 0)
            {
                Text("min: ", infoTextConfig);
                Text(__IntToString(sizing.MinMax.Min), infoTextConfig);
                if (sizing.MinMax.Max != MaxFloat)
                {
                    Text(", ", infoTextConfig);
                }
            }
            if (sizing.MinMax.Max != MaxFloat)
            {
                Text("max: ", infoTextConfig);
                Text(__IntToString(sizing.MinMax.Max), infoTextConfig);
            }
            Text(")", infoTextConfig);
        }
        else if (sizing.Type == SizingType.Percent)
        {
            Text("(", infoTextConfig);
            Text(__IntToString(sizing.Percent * 100), infoTextConfig);
            Text("%)", infoTextConfig);
        }
    }

    private static void __DebugViewRenderElementConfigHeader(string elementId, DebugElementConfigType type)
    {
        DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(type);
        Color backgroundColor = config.Color;
        backgroundColor.A = 90;
        using (AutoId(new ElementDeclaration
        {
            Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
            BackgroundColor = backgroundColor,
            CornerRadius = CornerRadius(4),
            Border = new BorderElementConfig { Color = config.Color, Width = BorderOutside(1) },
        }))
        {
            Text(config.Label, new TextElementConfig { TextColor = DebugViewColor4, FontSize = 16 });
        }
    }

    private static void __RenderDebugViewColor(Color color, TextElementConfig textConfig)
    {
        using (AutoId(new ElementDeclaration
        {
            Layout = new LayoutConfig { ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center } },
        }))
        {
            Text("{ r: ", textConfig);
            Text(__IntToString(color.R), textConfig);
            Text(", g: ", textConfig);
            Text(__IntToString(color.G), textConfig);
            Text(", b: ", textConfig);
            Text(__IntToString(color.B), textConfig);
            Text(", a: ", textConfig);
            Text(__IntToString(color.A), textConfig);
            Text(" }", textConfig);
            using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingFixed(10) } } })) { }
            using (AutoId(new ElementDeclaration
            {
                Layout = new LayoutConfig
                {
                    Sizing = new Sizing { Width = SizingFixed(DebugViewRowHeight - 8), Height = SizingFixed(DebugViewRowHeight - 8) },
                },
                BackgroundColor = color,
                CornerRadius = CornerRadius(4),
                Border = new BorderElementConfig { Color = DebugViewColor4, Width = BorderOutside(1) },
            })) { }
        }
    }

    private static void __RenderDebugViewCornerRadius(CornerRadiusValues cornerRadius, TextElementConfig textConfig)
    {
        using (AutoId(new ElementDeclaration
        {
            Layout = new LayoutConfig { ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center } },
        }))
        {
            Text("{ topLeft: ", textConfig);
            Text(__IntToString(cornerRadius.TopLeft), textConfig);
            Text(", topRight: ", textConfig);
            Text(__IntToString(cornerRadius.TopRight), textConfig);
            Text(", bottomLeft: ", textConfig);
            Text(__IntToString(cornerRadius.BottomLeft), textConfig);
            Text(", bottomRight: ", textConfig);
            Text(__IntToString(cornerRadius.BottomRight), textConfig);
            Text(" }", textConfig);
        }
    }

    private static void HandleDebugViewCloseButtonInteraction(ElementId elementId, PointerData pointerInfo, object? userData)
    {
        Context context = GetCurrentContext()!;
        if (pointerInfo.State == PointerDataInteractionState.PressedThisFrame)
        {
            context.DebugModeEnabled = false;
        }
    }

    // -------------------------------------
    // Element list (left-hand tree) -------
    // -------------------------------------

    private static RenderDebugLayoutData __RenderDebugLayoutElementsList(int initialRootsLength, int highlightedRowIndex)
    {
        Context context = GetCurrentContext()!;
        Array<int> dfsBuffer = context.ReusableElementIndexBuffer;
        RenderDebugLayoutData layoutData = default;

        uint highlightedElementId = 0;

        for (int rootIndex = 0; rootIndex < initialRootsLength; ++rootIndex)
        {
            dfsBuffer.Length = 0;
            LayoutElementTreeRoot root = context.LayoutElementTreeRoots.InternalArray[rootIndex];
            dfsBuffer.Add(root.LayoutElementIndex);
            context.TreeNodeVisited.InternalArray[0] = false;
            if (rootIndex > 0)
            {
                using (Element(Idi("_DebugView_EmptyRowOuter", (uint)rootIndex), new ElementDeclaration
                {
                    Layout = new LayoutConfig
                    {
                        Sizing = new Sizing { Width = SizingGrow() },
                        Padding = new Padding { Left = DebugViewIndentWidth / 2, Right = 0, Top = 0, Bottom = 0 },
                    },
                }))
                {
                    using (Element(Idi("_DebugView_EmptyRow", (uint)rootIndex), new ElementDeclaration
                    {
                        Layout = new LayoutConfig
                        {
                            Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(DebugViewRowHeight) },
                        },
                        Border = new BorderElementConfig { Color = DebugViewColor3, Width = new BorderWidth { Top = 1 } },
                    })) { }
                }
                layoutData.RowCount++;
            }

            while (dfsBuffer.Length > 0)
            {
                int currentElementIndex = dfsBuffer.InternalArray[dfsBuffer.Length - 1];
                LayoutElement currentElement = context.LayoutElements.InternalArray[currentElementIndex];
                if (context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1])
                {
                    if (!currentElement.IsTextElement && currentElement.Children.Length > 0)
                    {
                        __CloseElement();
                        __CloseElement();
                        __CloseElement();
                    }
                    dfsBuffer.Length--;
                    continue;
                }

                if (currentElement.Exiting) // TODO there is a duplicate ID problem with exiting elements
                {
                    dfsBuffer.Length--;
                    continue;
                }

                if (highlightedRowIndex == layoutData.RowCount)
                {
                    if (context.PointerInfo.State == PointerDataInteractionState.PressedThisFrame)
                    {
                        context.DebugSelectedElementId = currentElement.Id;
                    }
                    highlightedElementId = currentElement.Id;
                }

                context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = true;
                ref LayoutElementHashMapItem currentElementData = ref __GetHashMapItem(currentElement.Id);
                bool offscreen = !Unsafe.IsNullRef(in currentElementData) && __ElementIsOffscreen(in currentElementData.BoundingBox);
                if (context.DebugSelectedElementId == currentElement.Id)
                {
                    layoutData.SelectedElementRowIndex = layoutData.RowCount;
                }

                using (Element(Idi("_DebugView_ElementOuter", currentElement.Id), new ElementDeclaration { Layout = DebugViewScrollViewItemLayoutConfig }))
                {
                    // Collapse icon / button
                    if (!(currentElement.IsTextElement || currentElement.Children.Length == 0))
                    {
                        using (Element(Idi("_DebugView_CollapseElement", currentElement.Id), new ElementDeclaration
                        {
                            Layout = new LayoutConfig
                            {
                                Sizing = new Sizing { Width = SizingFixed(16), Height = SizingFixed(16) },
                                ChildAlignment = new ChildAlignment { X = LayoutAlignmentX.Center, Y = LayoutAlignmentY.Center },
                            },
                            CornerRadius = CornerRadius(4),
                            Border = new BorderElementConfig { Color = DebugViewColor3, Width = BorderOutside(1) },
                        }))
                        {
                            Text((!Unsafe.IsNullRef(in currentElementData) && currentElementData.DebugData.Collapsed) ? "+" : "-",
                                new TextElementConfig { TextColor = DebugViewColor4, FontSize = 16 });
                        }
                    }
                    else // Square dot for empty containers
                    {
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig
                            {
                                Sizing = new Sizing { Width = SizingFixed(16), Height = SizingFixed(16) },
                                ChildAlignment = new ChildAlignment { X = LayoutAlignmentX.Center, Y = LayoutAlignmentY.Center },
                            },
                        }))
                        {
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingFixed(8), Height = SizingFixed(8) } },
                                BackgroundColor = DebugViewColor3,
                                CornerRadius = CornerRadius(2),
                            })) { }
                        }
                    }
                    // Collisions and offscreen info
                    if (!Unsafe.IsNullRef(in currentElementData))
                    {
                        if (currentElementData.DebugData.Collision)
                        {
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
                                Border = new BorderElementConfig { Color = new Color(177, 147, 8, 255), Width = BorderOutside(1) },
                            }))
                            {
                                Text("Duplicate ID", new TextElementConfig { TextColor = DebugViewColor3, FontSize = 16 });
                            }
                        }
                        if (offscreen)
                        {
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
                                Border = new BorderElementConfig { Color = DebugViewColor3, Width = BorderOutside(1) },
                            }))
                            {
                                Text("Offscreen", new TextElementConfig { TextColor = DebugViewColor3, FontSize = 16 });
                            }
                        }
                    }
                    if (!Unsafe.IsNullRef(in currentElementData) && currentElementData.ElementId.StringId.Length > 0)
                    {
                        using (AutoId(new ElementDeclaration { }))
                        {
                            TextElementConfig textConfig = offscreen
                                ? new TextElementConfig { TextColor = DebugViewColor3, FontSize = 16 }
                                : DebugViewTextNameConfig;
                            Text(currentElementData.ElementId.StringId, textConfig);
                            if (currentElementData.ElementId.Offset != 0)
                            {
                                Text(" (", textConfig);
                                Text(__IntToString(currentElementData.ElementId.Offset), textConfig);
                                Text(")", textConfig);
                            }
                        }
                    }
                    if (currentElement.IsTextElement)
                    {
                        __RenderElementConfigTypeLabel("Text", new Color(105, 210, 231, 255), offscreen);
                    }
                    else
                    {
                        if (currentElement.Config.BackgroundColor.A > 0)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.BackgroundColor);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.OverlayColor.A > 0)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.OverlayColor);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (!__CornerRadiusIsZero(in currentElement.Config.CornerRadius))
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.CornerRadius);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.AspectRatio.AspectRatio != 0)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Aspect);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.Image.ImageData != null)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Image);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.Floating.AttachTo != FloatingAttachToElement.None)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Floating);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.Clip.Horizontal || currentElement.Config.Clip.Vertical)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Clip);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (__BorderHasAnyWidth(in currentElement.Config.Border))
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Border);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                        if (currentElement.Config.Custom.CustomData != null)
                        {
                            DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(DebugElementConfigType.Custom);
                            __RenderElementConfigTypeLabel(config.Label, config.Color, offscreen);
                        }
                    }
                }

                // Render the text contents below the element as a non-interactive row
                if (currentElement.IsTextElement)
                {
                    layoutData.RowCount++;
                    TextElementData textElementData = currentElement.TextElementData;
                    TextElementConfig rawTextConfig = offscreen
                        ? new TextElementConfig { TextColor = DebugViewColor3, FontSize = 16 }
                        : DebugViewTextNameConfig;
                    using (AutoId(new ElementDeclaration
                    {
                        Layout = new LayoutConfig
                        {
                            Sizing = new Sizing { Height = SizingFixed(DebugViewRowHeight) },
                            ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
                        },
                    }))
                    {
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingFixed(DebugViewIndentWidth + 16) } },
                        })) { }
                        Text("\"", rawTextConfig);
                        Text(textElementData.Text.Length > 40 ? textElementData.Text.Substring(0, 40) : textElementData.Text, rawTextConfig);
                        if (textElementData.Text.Length > 40)
                        {
                            Text("...", rawTextConfig);
                        }
                        Text("\"", rawTextConfig);
                    }
                }
                else if (currentElement.Children.Length > 0)
                {
                    __OpenElement();
                    __ConfigureOpenElement(new ElementDeclaration { Layout = new LayoutConfig { Padding = new Padding { Left = 8 } } });
                    __OpenElement();
                    __ConfigureOpenElement(new ElementDeclaration
                    {
                        Layout = new LayoutConfig { Padding = new Padding { Left = DebugViewIndentWidth } },
                        Border = new BorderElementConfig { Color = DebugViewColor3, Width = new BorderWidth { Left = 1 } },
                    });
                    __OpenElement();
                    __ConfigureOpenElement(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.TopToBottom } });
                }

                layoutData.RowCount++;
                if (!(currentElement.IsTextElement || (!Unsafe.IsNullRef(in currentElementData) && currentElementData.DebugData.Collapsed)))
                {
                    for (int i = currentElement.Children.Length - 1; i >= 0; --i)
                    {
                        dfsBuffer.Add(currentElement.Children.Elements[currentElement.Children.Offset + i].Index);
                        context.TreeNodeVisited.InternalArray[dfsBuffer.Length - 1] = false; // TODO needs to be ranged checked
                    }
                }
            }
        }

        if (context.PointerInfo.State == PointerDataInteractionState.PressedThisFrame)
        {
            ElementId collapseButtonId = Id("_DebugView_CollapseElement");
            for (int i = context.PointerOverIds.Length - 1; i >= 0; i--)
            {
                ElementId elementId = context.PointerOverIds.InternalArray[i];
                if (elementId.BaseId == collapseButtonId.BaseId)
                {
                    ref LayoutElementHashMapItem highlightedItem = ref __GetHashMapItem(elementId.Offset);
                    if (!Unsafe.IsNullRef(in highlightedItem)) highlightedItem.DebugData.Collapsed = !highlightedItem.DebugData.Collapsed;
                    break;
                }
            }
        }

        if (highlightedElementId != 0)
        {
            using (Element(Id("_DebugView_ElementHighlight"), new ElementDeclaration
            {
                Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() } },
                Floating = new FloatingElementConfig
                {
                    ParentId = highlightedElementId,
                    ZIndex = 32767,
                    PointerCaptureMode = PointerCaptureMode.Passthrough,
                    AttachTo = FloatingAttachToElement.ElementWithId,
                },
            }))
            {
                using (Element(Id("_DebugView_ElementHighlightRectangle"), new ElementDeclaration
                {
                    Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() } },
                    BackgroundColor = DebugViewHighlightColor,
                })) { }
            }
        }
        return layoutData;
    }

    // -------------------------------------
    // Main debug view renderer ------------
    // -------------------------------------

    private static void __RenderDebugView()
    {
        Context context = GetCurrentContext()!;
        ElementId closeButtonId = Id("_DebugViewTopHeaderCloseButtonOuter");
        if (context.PointerInfo.State == PointerDataInteractionState.PressedThisFrame)
        {
            for (int i = 0; i < context.PointerOverIds.Length; ++i)
            {
                ElementId elementId = context.PointerOverIds.InternalArray[i];
                if (elementId.Id == closeButtonId.Id)
                {
                    context.DebugModeEnabled = false;
                    return;
                }
            }
        }

        int initialRootsLength = context.LayoutElementTreeRoots.Length;
        int initialElementsLength = context.LayoutElements.Length;
        TextElementConfig infoTextConfig = new()
        {
            TextColor = DebugViewColor4,
            FontSize = 16,
            WrapMode = TextElementConfigWrapMode.None,
        };
        TextElementConfig infoTitleConfig = new()
        {
            TextColor = DebugViewColor3,
            FontSize = 16,
            WrapMode = TextElementConfigWrapMode.None,
        };
        ElementId scrollId = Id("_DebugViewOuterScrollPane");
        float scrollYOffset = 0;
        bool pointerInDebugView = context.PointerInfo.Position.Y < context.LayoutDimensions.Height - 300;
        for (int i = 0; i < context.ScrollContainerDatas.Length; ++i)
        {
            ScrollContainerDataInternal scrollContainerData = context.ScrollContainerDatas.InternalArray[i];
            if (scrollContainerData.ElementId == scrollId.Id)
            {
                if (!context.ExternalScrollHandlingEnabled)
                {
                    scrollYOffset = scrollContainerData.ScrollPosition.Y;
                }
                else
                {
                    pointerInDebugView = context.PointerInfo.Position.Y + scrollContainerData.ScrollPosition.Y < context.LayoutDimensions.Height - 300;
                }
                break;
            }
        }
        int highlightedRow = pointerInDebugView
            ? (int)((context.PointerInfo.Position.Y - scrollYOffset) / DebugViewRowHeight) - 1
            : -1;
        if (context.PointerInfo.Position.X < context.LayoutDimensions.Width - DebugViewWidth)
        {
            highlightedRow = -1;
        }

        RenderDebugLayoutData layoutData = default;

        using (Element(Id("_DebugView"), new ElementDeclaration
        {
            Layout = new LayoutConfig
            {
                Sizing = new Sizing { Width = SizingFixed(DebugViewWidth), Height = SizingFixed(context.LayoutDimensions.Height) },
                LayoutDirection = LayoutDirection.TopToBottom,
            },
            Floating = new FloatingElementConfig
            {
                ZIndex = 32765,
                AttachPoints = new FloatingAttachPoints
                {
                    Element = FloatingAttachPointType.LeftCenter,
                    Parent = FloatingAttachPointType.RightCenter,
                },
                AttachTo = FloatingAttachToElement.Root,
                ClipTo = FloatingClipToElement.AttachedParent,
            },
            Border = new BorderElementConfig { Color = DebugViewColor3, Width = new BorderWidth { Bottom = 1 } },
        }))
        {
            // Header bar
            using (AutoId(new ElementDeclaration
            {
                Layout = new LayoutConfig
                {
                    Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(DebugViewRowHeight) },
                    Padding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 0, Bottom = 0 },
                    ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
                },
                BackgroundColor = DebugViewColor2,
            }))
            {
                Text("Clay Debug Tools", infoTextConfig);
                using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow() } } })) { }
                // Close button
                using (AutoId(new ElementDeclaration
                {
                    Layout = new LayoutConfig
                    {
                        Sizing = new Sizing { Width = SizingFixed(DebugViewRowHeight - 10), Height = SizingFixed(DebugViewRowHeight - 10) },
                        ChildAlignment = new ChildAlignment { X = LayoutAlignmentX.Center, Y = LayoutAlignmentY.Center },
                    },
                    BackgroundColor = new Color(217, 91, 67, 80),
                    CornerRadius = CornerRadius(4),
                    Border = new BorderElementConfig { Color = new Color(217, 91, 67, 255), Width = BorderOutside(1) },
                }))
                {
                    OnHover(HandleDebugViewCloseButtonInteraction, null);
                    Text("x", new TextElementConfig { TextColor = DebugViewColor4, FontSize = 16 });
                }
            }

            using (AutoId(new ElementDeclaration
            {
                Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(1) } },
                BackgroundColor = DebugViewColor3,
            })) { }

            // Scroll pane containing the element list
            using (Element(scrollId, () => new ElementDeclaration
            {
                Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() } },
                Clip = new ClipElementConfig { Horizontal = true, Vertical = true, ChildOffset = GetScrollOffset() },
            }))
            {
                using (AutoId(new ElementDeclaration
                {
                    Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() }, LayoutDirection = LayoutDirection.TopToBottom },
                    BackgroundColor = ((initialElementsLength + initialRootsLength) & 1) == 0 ? DebugViewColor2 : DebugViewColor1,
                }))
                {
                    ElementId panelContentsId = Id("_DebugViewPaneOuter");
                    // Element list
                    using (Element(panelContentsId, new ElementDeclaration
                    {
                        Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() } },
                        Floating = new FloatingElementConfig
                        {
                            ZIndex = 32766,
                            PointerCaptureMode = PointerCaptureMode.Passthrough,
                            AttachTo = FloatingAttachToElement.Parent,
                            ClipTo = FloatingClipToElement.AttachedParent,
                        },
                    }))
                    {
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig
                            {
                                Sizing = new Sizing { Width = SizingGrow(), Height = SizingGrow() },
                                Padding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 0, Bottom = 0 },
                                LayoutDirection = LayoutDirection.TopToBottom,
                            },
                        }))
                        {
                            layoutData = __RenderDebugLayoutElementsList(initialRootsLength, highlightedRow);
                        }
                    }

                    ref LayoutElementHashMapItem panelContents = ref __GetHashMapItem(panelContentsId.Id);
                    float contentWidth = !Unsafe.IsNullRef(in panelContents) ? panelContents.LayoutElement.Dimensions.Width : 0;
                    using (AutoId(new ElementDeclaration
                    {
                        Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingFixed(contentWidth) }, LayoutDirection = LayoutDirection.TopToBottom },
                    })) { }
                    // Row striping behind the (floating) element list
                    for (int i = 0; i < layoutData.RowCount; i++)
                    {
                        Color rowColor = (i & 1) == 0 ? DebugViewColor2 : DebugViewColor1;
                        if (i == layoutData.SelectedElementRowIndex)
                        {
                            rowColor = DebugViewColorSelectedRow;
                        }
                        if (i == highlightedRow)
                        {
                            rowColor.R *= 1.25f;
                            rowColor.G *= 1.25f;
                            rowColor.B *= 1.25f;
                        }
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(DebugViewRowHeight) }, LayoutDirection = LayoutDirection.TopToBottom },
                            BackgroundColor = rowColor,
                        })) { }
                    }
                }
            }

            using (AutoId(new ElementDeclaration
            {
                Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(1) } },
                BackgroundColor = DebugViewColor3,
            })) { }

            ref LayoutElementHashMapItem selectedItem = ref __GetHashMapItem(context.DebugSelectedElementId);
            if (!Unsafe.IsNullRef(in selectedItem) && selectedItem.LayoutElement != null)
            {
                using (AutoId(() => new ElementDeclaration
                {
                    Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(300) }, LayoutDirection = LayoutDirection.TopToBottom },
                    BackgroundColor = DebugViewColor2,
                    Clip = new ClipElementConfig { Vertical = true, ChildOffset = GetScrollOffset() },
                    Border = new BorderElementConfig { Color = DebugViewColor3, Width = new BorderWidth { BetweenChildren = 1 } },
                }))
                {
                    using (AutoId(new ElementDeclaration
                    {
                        Layout = new LayoutConfig
                        {
                            Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(DebugViewRowHeight + 8) },
                            Padding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 0, Bottom = 0 },
                            ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
                        },
                    }))
                    {
                        Text("Element Configuration", infoTextConfig);
                        using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow() } } })) { }
                        if (selectedItem.ElementId.StringId.Length != 0)
                        {
                            Text(selectedItem.ElementId.StringId, infoTitleConfig);
                            if (selectedItem.ElementId.Offset != 0)
                            {
                                Text(" (", infoTitleConfig);
                                Text(__IntToString(selectedItem.ElementId.Offset), infoTitleConfig);
                                Text(")", infoTitleConfig);
                            }
                        }
                    }

                    Padding attributeConfigPadding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 8, Bottom = 8 };

                    // LayoutConfig debug info
                    using (AutoId(new ElementDeclaration
                    {
                        Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                    }))
                    {
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
                            BackgroundColor = new Color(200, 200, 200, 120),
                            CornerRadius = CornerRadius(4),
                            Border = new BorderElementConfig { Color = new Color(200, 200, 200, 255), Width = BorderOutside(1) },
                        }))
                        {
                            Text("Layout", new TextElementConfig { TextColor = DebugViewColor4, FontSize = 16 });
                        }
                        // .boundingBox
                        Text("Bounding Box", infoTitleConfig);
                        using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                        {
                            Text("{ x: ", infoTextConfig);
                            Text(__IntToString(selectedItem.BoundingBox.X), infoTextConfig);
                            Text(", y: ", infoTextConfig);
                            Text(__IntToString(selectedItem.BoundingBox.Y), infoTextConfig);
                            Text(", width: ", infoTextConfig);
                            Text(__IntToString(selectedItem.BoundingBox.Width), infoTextConfig);
                            Text(", height: ", infoTextConfig);
                            Text(__IntToString(selectedItem.BoundingBox.Height), infoTextConfig);
                            Text(" }", infoTextConfig);
                        }
                        if (!selectedItem.LayoutElement.IsTextElement)
                        {
                            // .layoutDirection
                            Text("Layout Direction", infoTitleConfig);
                            LayoutConfig layoutConfig = selectedItem.LayoutElement.Config.Layout;
                            Text(layoutConfig.LayoutDirection == LayoutDirection.TopToBottom ? "TOP_TO_BOTTOM" : "LEFT_TO_RIGHT", infoTextConfig);
                            // .sizing
                            Text("Sizing", infoTitleConfig);
                            using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                            {
                                Text("width: ", infoTextConfig);
                                __RenderDebugLayoutSizing(layoutConfig.Sizing.Width, infoTextConfig);
                            }
                            using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                            {
                                Text("height: ", infoTextConfig);
                                __RenderDebugLayoutSizing(layoutConfig.Sizing.Height, infoTextConfig);
                            }
                            // .padding
                            Text("Padding", infoTitleConfig);
                            using (Element(Id("_DebugViewElementInfoPadding"), new ElementDeclaration { }))
                            {
                                Text("{ left: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.Padding.Left), infoTextConfig);
                                Text(", right: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.Padding.Right), infoTextConfig);
                                Text(", top: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.Padding.Top), infoTextConfig);
                                Text(", bottom: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.Padding.Bottom), infoTextConfig);
                                Text(" }", infoTextConfig);
                            }
                            // .childGap
                            Text("Child Gap", infoTitleConfig);
                            Text(__IntToString(layoutConfig.ChildGap), infoTextConfig);
                            // .childAlignment
                            Text("Child Alignment", infoTitleConfig);
                            using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                            {
                                Text("{ x: ", infoTextConfig);
                                string alignX = "LEFT";
                                if (layoutConfig.ChildAlignment.X == LayoutAlignmentX.Center)
                                {
                                    alignX = "CENTER";
                                }
                                else if (layoutConfig.ChildAlignment.X == LayoutAlignmentX.Right)
                                {
                                    alignX = "RIGHT";
                                }
                                Text(alignX, infoTextConfig);
                                Text(", y: ", infoTextConfig);
                                string alignY = "TOP";
                                if (layoutConfig.ChildAlignment.Y == LayoutAlignmentY.Center)
                                {
                                    alignY = "CENTER";
                                }
                                else if (layoutConfig.ChildAlignment.Y == LayoutAlignmentY.Bottom)
                                {
                                    alignY = "BOTTOM";
                                }
                                Text(alignY, infoTextConfig);
                                Text(" }", infoTextConfig);
                            }
                        }
                    }

                    if (selectedItem.LayoutElement.IsTextElement)
                    {
                        TextElementConfig textConfig = selectedItem.LayoutElement.TextConfig;
                        using (AutoId(new ElementDeclaration
                        {
                            Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                        }))
                        {
                            __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Text);
                            // .fontSize
                            Text("Font Size", infoTitleConfig);
                            Text(__IntToString(textConfig.FontSize), infoTextConfig);
                            // .fontId
                            Text("Font ID", infoTitleConfig);
                            Text(__IntToString(textConfig.FontId), infoTextConfig);
                            // .lineHeight
                            Text("Line Height", infoTitleConfig);
                            Text(textConfig.LineHeight == 0 ? "auto" : __IntToString(textConfig.LineHeight), infoTextConfig);
                            // .letterSpacing
                            Text("Letter Spacing", infoTitleConfig);
                            Text(__IntToString(textConfig.LetterSpacing), infoTextConfig);
                            // .wrapMode
                            Text("Wrap Mode", infoTitleConfig);
                            string wrapMode = "WORDS";
                            if (textConfig.WrapMode == TextElementConfigWrapMode.None)
                            {
                                wrapMode = "NONE";
                            }
                            else if (textConfig.WrapMode == TextElementConfigWrapMode.Newlines)
                            {
                                wrapMode = "NEWLINES";
                            }
                            Text(wrapMode, infoTextConfig);
                            // .textAlignment
                            Text("Text Alignment", infoTitleConfig);
                            string textAlignment = "LEFT";
                            if (textConfig.TextAlignment == TextAlignment.Center)
                            {
                                textAlignment = "CENTER";
                            }
                            else if (textConfig.TextAlignment == TextAlignment.Right)
                            {
                                textAlignment = "RIGHT";
                            }
                            Text(textAlignment, infoTextConfig);
                            // .textColor
                            Text("Text Color", infoTitleConfig);
                            __RenderDebugViewColor(textConfig.TextColor, infoTextConfig);
                        }
                    }
                    else
                    {
                        using (Element(Id("_DebugViewElementInfoSharedBody"), new ElementDeclaration
                        {
                            Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                        }))
                        {
                            DebugElementConfigTypeLabelConfig labelConfig = __DebugGetElementConfigTypeLabel(DebugElementConfigType.BackgroundColor);
                            Color backgroundColor = labelConfig.Color;
                            backgroundColor.A = 90;
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = new Padding { Left = 8, Right = 8, Top = 2, Bottom = 2 } },
                                BackgroundColor = backgroundColor,
                                CornerRadius = CornerRadius(4),
                                Border = new BorderElementConfig { Color = labelConfig.Color, Width = BorderOutside(1) },
                            }))
                            {
                                Text("Color & Radius", new TextElementConfig { TextColor = DebugViewColor4, FontSize = 16 });
                            }
                            // .backgroundColor
                            if (selectedItem.LayoutElement.Config.BackgroundColor.A > 0)
                            {
                                Text("Background Color", infoTitleConfig);
                                __RenderDebugViewColor(selectedItem.LayoutElement.Config.BackgroundColor, infoTextConfig);
                            }
                            // .cornerRadius
                            if (!__CornerRadiusIsZero(in selectedItem.LayoutElement.Config.CornerRadius))
                            {
                                Text("Corner Radius", infoTitleConfig);
                                __RenderDebugViewCornerRadius(selectedItem.LayoutElement.Config.CornerRadius, infoTextConfig);
                            }
                            // .overlayColor
                            if (selectedItem.LayoutElement.Config.OverlayColor.A > 0)
                            {
                                Text("Overlay Color", infoTitleConfig);
                                __RenderDebugViewColor(selectedItem.LayoutElement.Config.OverlayColor, infoTextConfig);
                            }
                        }

                        if (selectedItem.LayoutElement.Config.AspectRatio.AspectRatio > 0)
                        {
                            AspectRatioElementConfig aspectRatioConfig = selectedItem.LayoutElement.Config.AspectRatio;
                            using (Element(Id("_DebugViewElementInfoAspectRatioBody"), new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Aspect);
                                Text("Aspect Ratio", infoTitleConfig);
                                using (Element(Id("_DebugViewElementInfoAspectRatio"), new ElementDeclaration { }))
                                {
                                    Text(__IntToString(aspectRatioConfig.AspectRatio), infoTextConfig);
                                    Text(".", infoTextConfig);
                                    float frac = aspectRatioConfig.AspectRatio - (int)aspectRatioConfig.AspectRatio;
                                    frac *= 100;
                                    if ((int)frac < 10)
                                    {
                                        Text("0", infoTextConfig);
                                    }
                                    Text(__IntToString(frac), infoTextConfig);
                                }
                            }
                        }

                        if (selectedItem.LayoutElement.Config.Image.ImageData != null)
                        {
                            ImageElementConfig imageConfig = selectedItem.LayoutElement.Config.Image;
                            AspectRatioElementConfig aspectConfig = new() { AspectRatio = 1 };
                            if (selectedItem.LayoutElement.Config.AspectRatio.AspectRatio > 0)
                            {
                                aspectConfig = selectedItem.LayoutElement.Config.AspectRatio;
                            }
                            using (Element(Id("_DebugViewElementInfoImageBody"), new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Image);
                                // Image Preview
                                Text("Preview", infoTitleConfig);
                                using (AutoId(new ElementDeclaration
                                {
                                    Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(64, 128), Height = SizingGrow(64, 128) } },
                                    AspectRatio = aspectConfig,
                                    Image = imageConfig,
                                })) { }
                            }
                        }

                        if (selectedItem.LayoutElement.Config.Floating.AttachTo != FloatingAttachToElement.None)
                        {
                            FloatingElementConfig floatingConfig = selectedItem.LayoutElement.Config.Floating;
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Floating);
                                // .offset
                                Text("Offset", infoTitleConfig);
                                using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                                {
                                    Text("{ x: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.Offset.X), infoTextConfig);
                                    Text(", y: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.Offset.Y), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .expand
                                Text("Expand", infoTitleConfig);
                                using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                                {
                                    Text("{ width: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.Expand.Width), infoTextConfig);
                                    Text(", height: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.Expand.Height), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .zIndex
                                Text("z-index", infoTitleConfig);
                                Text(__IntToString(floatingConfig.ZIndex), infoTextConfig);
                                // .parentId
                                Text("Parent", infoTitleConfig);
                                ref LayoutElementHashMapItem hashItem = ref __GetHashMapItem(floatingConfig.ParentId);
                                Text(!Unsafe.IsNullRef(in hashItem) ? hashItem.ElementId.StringId : "", infoTextConfig);
                                // .attachPoints
                                Text("Attach Points", infoTitleConfig);
                                using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                                {
                                    Text("{ element: ", infoTextConfig);
                                    string attachPointElement = "LEFT_TOP";
                                    if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.LeftCenter) attachPointElement = "LEFT_CENTER";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.LeftBottom) attachPointElement = "LEFT_BOTTOM";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.CenterTop) attachPointElement = "CENTER_TOP";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.CenterCenter) attachPointElement = "CENTER_CENTER";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.CenterBottom) attachPointElement = "CENTER_BOTTOM";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.RightTop) attachPointElement = "RIGHT_TOP";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.RightCenter) attachPointElement = "RIGHT_CENTER";
                                    else if (floatingConfig.AttachPoints.Element == FloatingAttachPointType.RightBottom) attachPointElement = "RIGHT_BOTTOM";
                                    Text(attachPointElement, infoTextConfig);
                                    string attachPointParent = "LEFT_TOP";
                                    if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.LeftCenter) attachPointParent = "LEFT_CENTER";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.LeftBottom) attachPointParent = "LEFT_BOTTOM";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.CenterTop) attachPointParent = "CENTER_TOP";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.CenterCenter) attachPointParent = "CENTER_CENTER";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.CenterBottom) attachPointParent = "CENTER_BOTTOM";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.RightTop) attachPointParent = "RIGHT_TOP";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.RightCenter) attachPointParent = "RIGHT_CENTER";
                                    else if (floatingConfig.AttachPoints.Parent == FloatingAttachPointType.RightBottom) attachPointParent = "RIGHT_BOTTOM";
                                    Text(", parent: ", infoTextConfig);
                                    Text(attachPointParent, infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .pointerCaptureMode
                                Text("Pointer Capture Mode", infoTitleConfig);
                                string pointerCaptureMode = "NONE";
                                if (floatingConfig.PointerCaptureMode == PointerCaptureMode.Passthrough)
                                {
                                    pointerCaptureMode = "PASSTHROUGH";
                                }
                                Text(pointerCaptureMode, infoTextConfig);
                                // .attachTo
                                Text("Attach To", infoTitleConfig);
                                string attachTo = "NONE";
                                if (floatingConfig.AttachTo == FloatingAttachToElement.Parent) attachTo = "PARENT";
                                else if (floatingConfig.AttachTo == FloatingAttachToElement.ElementWithId) attachTo = "ELEMENT_WITH_ID";
                                else if (floatingConfig.AttachTo == FloatingAttachToElement.Root) attachTo = "ROOT";
                                Text(attachTo, infoTextConfig);
                                // .clipTo
                                Text("Clip To", infoTitleConfig);
                                string clipTo = "ATTACHED_PARENT";
                                if (floatingConfig.ClipTo == FloatingClipToElement.None)
                                {
                                    clipTo = "NONE";
                                }
                                Text(clipTo, infoTextConfig);
                            }
                        }

                        ClipElementConfig clipConfig = selectedItem.LayoutElement.Config.Clip;
                        if (clipConfig.Horizontal || clipConfig.Vertical)
                        {
                            using (AutoId(new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Clip);
                                // .vertical
                                Text("Vertical", infoTitleConfig);
                                Text(clipConfig.Vertical ? "true" : "false", infoTextConfig);
                                // .horizontal
                                Text("Horizontal", infoTitleConfig);
                                Text(clipConfig.Horizontal ? "true" : "false", infoTextConfig);
                            }
                        }

                        BorderElementConfig borderConfig = selectedItem.LayoutElement.Config.Border;
                        if (__BorderHasAnyWidth(in borderConfig))
                        {
                            using (Element(Id("_DebugViewElementInfoBorderBody"), new ElementDeclaration
                            {
                                Layout = new LayoutConfig { Padding = attributeConfigPadding, ChildGap = 8, LayoutDirection = LayoutDirection.TopToBottom },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.ElementId.StringId, DebugElementConfigType.Border);
                                Text("Border Widths", infoTitleConfig);
                                using (AutoId(new ElementDeclaration { Layout = new LayoutConfig { LayoutDirection = LayoutDirection.LeftToRight } }))
                                {
                                    Text("{ left: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.Width.Left), infoTextConfig);
                                    Text(", right: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.Width.Right), infoTextConfig);
                                    Text(", top: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.Width.Top), infoTextConfig);
                                    Text(", bottom: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.Width.Bottom), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .textColor (border color)
                                Text("Border Color", infoTitleConfig);
                                __RenderDebugViewColor(borderConfig.Color, infoTextConfig);
                            }
                        }
                    }
                }
            }
            else
            {
                using (Element(Id("_DebugViewWarningsScrollPane"), () => new ElementDeclaration
                {
                    Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(300) }, ChildGap = 6, LayoutDirection = LayoutDirection.TopToBottom },
                    BackgroundColor = DebugViewColor2,
                    Clip = new ClipElementConfig { Horizontal = true, Vertical = true, ChildOffset = GetScrollOffset() },
                }))
                {
                    TextElementConfig warningConfig = new() { TextColor = DebugViewColor4, FontSize = 16, WrapMode = TextElementConfigWrapMode.None };
                    using (Element(Id("_DebugViewWarningItemHeader"), new ElementDeclaration
                    {
                        Layout = new LayoutConfig
                        {
                            Sizing = new Sizing { Height = SizingFixed(DebugViewRowHeight) },
                            Padding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 0, Bottom = 0 },
                            ChildGap = 8,
                            ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
                        },
                    }))
                    {
                        Text("Warnings", warningConfig);
                    }
                    using (Element(Id("_DebugViewWarningsTopBorder"), new ElementDeclaration
                    {
                        Layout = new LayoutConfig { Sizing = new Sizing { Width = SizingGrow(), Height = SizingFixed(1) } },
                        BackgroundColor = new Color(200, 200, 200, 255),
                    })) { }
                    int previousWarningsLength = context.Warnings.Length;
                    for (int i = 0; i < previousWarningsLength; i++)
                    {
                        Warning warning = context.Warnings.InternalArray[i];
                        using (Element(Idi("_DebugViewWarningItem", (uint)i), new ElementDeclaration
                        {
                            Layout = new LayoutConfig
                            {
                                Sizing = new Sizing { Height = SizingFixed(DebugViewRowHeight) },
                                Padding = new Padding { Left = DebugViewOuterPadding, Right = DebugViewOuterPadding, Top = 0, Bottom = 0 },
                                ChildGap = 8,
                                ChildAlignment = new ChildAlignment { Y = LayoutAlignmentY.Center },
                            },
                        }))
                        {
                            Text(warning.BaseMessage, warningConfig);
                            if (warning.DynamicMessage.Length > 0)
                            {
                                Text(warning.DynamicMessage, warningConfig);
                            }
                        }
                    }
                }
            }
        }
    }

    // -------------------------------------
    // Error helper (EndLayout integration)
    // -------------------------------------

    // Adds the error text command shown when the debug view itself pushed the element count over capacity.
    // Mirrors the C "Debug view caused layout element count to exceed _maxElementCount" path.
    private static void __AddDebugViewElementsExceededError()
    {
        Context context = GetCurrentContext()!;
        const string message = "Clay Error: Debug view caused layout element count to exceed _maxElementCount";
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
}
