using System.Numerics;
using Microsoft.Extensions.Primitives;

namespace ClaySharp;

// Self-hosted debug inspector, ported from clay.h's DebugTools region (Clay__RenderDebugView and helpers).
// This is a partial class companion to Clay.cs.
public static partial class Clay
{
    // -------------------------------------
    // Debug view constants + helpers ------
    // -------------------------------------

    private static readonly Clay_Color Clay__DEBUGVIEW_COLOR_1 = new(58, 56, 52, 255);
    private static readonly Clay_Color Clay__DEBUGVIEW_COLOR_2 = new(62, 60, 58, 255);
    private static readonly Clay_Color Clay__DEBUGVIEW_COLOR_3 = new(141, 133, 135, 255);
    private static readonly Clay_Color Clay__DEBUGVIEW_COLOR_4 = new(238, 226, 231, 255);
    private static readonly Clay_Color Clay__DEBUGVIEW_COLOR_SELECTED_ROW = new(102, 80, 78, 255);
    private const int CLAY__DEBUGVIEW_ROW_HEIGHT = 30;
    private const int CLAY__DEBUGVIEW_OUTER_PADDING = 10;
    private const int CLAY__DEBUGVIEW_INDENT_WIDTH = 16;

    private static readonly Clay_TextElementConfig Clay__DebugView_TextNameConfig = new()
    {
        textColor = new Clay_Color(238, 226, 231, 255),
        fontSize = 16,
        wrapMode = Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NONE,
    };

    private static readonly Clay_LayoutConfig Clay__DebugView_ScrollViewItemLayoutConfig = new()
    {
        sizing = new Clay_Sizing { height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
        childGap = 6,
        childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
    };

    private enum Clay__DebugElementConfigType
    {
        CLAY__ELEMENT_CONFIG_TYPE_BACKGROUND_COLOR,
        CLAY__ELEMENT_CONFIG_TYPE_OVERLAY_COLOR,
        CLAY__ELEMENT_CONFIG_TYPE_CORNER_RADIUS,
        CLAY__ELEMENT_CONFIG_TYPE_TEXT,
        CLAY__ELEMENT_CONFIG_TYPE_ASPECT,
        CLAY__ELEMENT_CONFIG_TYPE_IMAGE,
        CLAY__ELEMENT_CONFIG_TYPE_FLOATING,
        CLAY__ELEMENT_CONFIG_TYPE_CLIP,
        CLAY__ELEMENT_CONFIG_TYPE_BORDER,
        CLAY__ELEMENT_CONFIG_TYPE_CUSTOM,
    }

    private struct Clay__DebugElementConfigTypeLabelConfig
    {
        public string label;
        public Clay_Color color;
    }

    private struct Clay__RenderDebugLayoutData
    {
        public int rowCount;
        public int selectedElementRowIndex;
    }

    private static Clay__DebugElementConfigTypeLabelConfig __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType type)
    {
        switch (type)
        {
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BACKGROUND_COLOR: return new() { label = "Background", color = new(243, 134, 48, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_OVERLAY_COLOR: return new() { label = "Overlay", color = new(142, 129, 206, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CORNER_RADIUS: return new() { label = "Radius", color = new(239, 148, 157, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_TEXT: return new() { label = "Text", color = new(105, 210, 231, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_ASPECT: return new() { label = "Aspect", color = new(101, 149, 194, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_IMAGE: return new() { label = "Image", color = new(121, 189, 154, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_FLOATING: return new() { label = "Floating", color = new(250, 105, 0, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CLIP: return new() { label = "Scroll", color = new(242, 196, 90, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BORDER: return new() { label = "Border", color = new(108, 91, 123, 255) };
            case Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CUSTOM: return new() { label = "Custom", color = new(11, 72, 107, 255) };
            default: break;
        }
        return new() { label = "Error", color = new(0, 0, 0, 255) };
    }

    // Replaces the C Clay__IntToString dynamic-string buffer. The C function takes an int32_t but every
    // caller passes a float, so C implicitly truncates; we reproduce that truncation here.
    private static string __IntToString(float value) => ((int)value).ToString();

    private static bool __CornerRadiusIsZero(in Clay_CornerRadius cornerRadius)
        => cornerRadius.topLeft == 0 && cornerRadius.topRight == 0 && cornerRadius.bottomLeft == 0 && cornerRadius.bottomRight == 0;

    private static void __RenderElementConfigTypeLabel(string label, Clay_Color color, bool offscreen)
    {
        Clay_Color backgroundColor = color;
        backgroundColor.a = 90;
        using (AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
            backgroundColor = backgroundColor,
            cornerRadius = CornerRadius(4),
            border = new Clay_BorderElementConfig { color = color, width = BorderOutside(1) },
        }))
        {
            Text(label, new Clay_TextElementConfig
            {
                textColor = offscreen ? Clay__DEBUGVIEW_COLOR_3 : Clay__DEBUGVIEW_COLOR_4,
                fontSize = 16,
            });
        }
    }

    private static void __RenderDebugLayoutSizing(Clay_SizingAxis sizing, Clay_TextElementConfig infoTextConfig)
    {
        string sizingLabel = "GROW";
        if (sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_FIT)
        {
            sizingLabel = "FIT";
        }
        else if (sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
        {
            sizingLabel = "PERCENT";
        }
        else if (sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_FIXED)
        {
            sizingLabel = "FIXED";
        }
        Text(sizingLabel, infoTextConfig);
        if (sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_GROW || sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_FIT || sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_FIXED)
        {
            Text("(", infoTextConfig);
            if (sizing.minMax.min != 0)
            {
                Text("min: ", infoTextConfig);
                Text(__IntToString(sizing.minMax.min), infoTextConfig);
                if (sizing.minMax.max != CLAY__MAXFLOAT)
                {
                    Text(", ", infoTextConfig);
                }
            }
            if (sizing.minMax.max != CLAY__MAXFLOAT)
            {
                Text("max: ", infoTextConfig);
                Text(__IntToString(sizing.minMax.max), infoTextConfig);
            }
            Text(")", infoTextConfig);
        }
        else if (sizing.type == Clay__SizingType.CLAY__SIZING_TYPE_PERCENT)
        {
            Text("(", infoTextConfig);
            Text(__IntToString(sizing.percent * 100), infoTextConfig);
            Text("%)", infoTextConfig);
        }
    }

    private static void __DebugViewRenderElementConfigHeader(string elementId, Clay__DebugElementConfigType type)
    {
        Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(type);
        Clay_Color backgroundColor = config.color;
        backgroundColor.a = 90;
        using (AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
            backgroundColor = backgroundColor,
            cornerRadius = CornerRadius(4),
            border = new Clay_BorderElementConfig { color = config.color, width = BorderOutside(1) },
        }))
        {
            Text(config.label, new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16 });
        }
    }

    private static void __RenderDebugViewColor(Clay_Color color, Clay_TextElementConfig textConfig)
    {
        using (AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER } },
        }))
        {
            Text("{ r: ", textConfig);
            Text(__IntToString(color.r), textConfig);
            Text(", g: ", textConfig);
            Text(__IntToString(color.g), textConfig);
            Text(", b: ", textConfig);
            Text(__IntToString(color.b), textConfig);
            Text(", a: ", textConfig);
            Text(__IntToString(color.a), textConfig);
            Text(" }", textConfig);
            using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingFixed(10) } } })) { }
            using (AutoId(new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig
                {
                    sizing = new Clay_Sizing { width = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT - 8), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT - 8) },
                },
                backgroundColor = color,
                cornerRadius = CornerRadius(4),
                border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_4, width = BorderOutside(1) },
            })) { }
        }
    }

    private static void __RenderDebugViewCornerRadius(Clay_CornerRadius cornerRadius, Clay_TextElementConfig textConfig)
    {
        using (AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER } },
        }))
        {
            Text("{ topLeft: ", textConfig);
            Text(__IntToString(cornerRadius.topLeft), textConfig);
            Text(", topRight: ", textConfig);
            Text(__IntToString(cornerRadius.topRight), textConfig);
            Text(", bottomLeft: ", textConfig);
            Text(__IntToString(cornerRadius.bottomLeft), textConfig);
            Text(", bottomRight: ", textConfig);
            Text(__IntToString(cornerRadius.bottomRight), textConfig);
            Text(" }", textConfig);
        }
    }

    private static void HandleDebugViewCloseButtonInteraction(Clay_ElementId elementId, Clay_PointerData pointerInfo, object? userData)
    {
        Clay_Context context = GetCurrentContext()!;
        if (pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
        {
            context.debugModeEnabled = false;
        }
    }

    // -------------------------------------
    // Element list (left-hand tree) -------
    // -------------------------------------

    private static Clay__RenderDebugLayoutData __RenderDebugLayoutElementsList(int initialRootsLength, int highlightedRowIndex)
    {
        Clay_Context context = GetCurrentContext()!;
        ClayArray<int> dfsBuffer = context.reusableElementIndexBuffer;
        Clay__RenderDebugLayoutData layoutData = default;

        uint highlightedElementId = 0;

        for (int rootIndex = 0; rootIndex < initialRootsLength; ++rootIndex)
        {
            dfsBuffer.length = 0;
            Clay__LayoutElementTreeRoot root = context.layoutElementTreeRoots.internalArray[rootIndex];
            dfsBuffer.Add(root.layoutElementIndex);
            context.treeNodeVisited.internalArray[0] = false;
            if (rootIndex > 0)
            {
                using (Element(Idi("Clay__DebugView_EmptyRowOuter", (uint)rootIndex), new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig
                    {
                        sizing = new Clay_Sizing { width = SizingGrow() },
                        padding = new Clay_Padding { left = CLAY__DEBUGVIEW_INDENT_WIDTH / 2, right = 0, top = 0, bottom = 0 },
                    },
                }))
                {
                    using (Element(Idi("Clay__DebugView_EmptyRow", (uint)rootIndex), new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig
                        {
                            sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
                        },
                        border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = new Clay_BorderWidth { top = 1 } },
                    })) { }
                }
                layoutData.rowCount++;
            }

            while (dfsBuffer.length > 0)
            {
                int currentElementIndex = dfsBuffer.internalArray[dfsBuffer.length - 1];
                Clay_LayoutElement currentElement = context.layoutElements.internalArray[currentElementIndex];
                if (context.treeNodeVisited.internalArray[dfsBuffer.length - 1])
                {
                    if (!currentElement.isTextElement && currentElement.children.length > 0)
                    {
                        __CloseElement();
                        __CloseElement();
                        __CloseElement();
                    }
                    dfsBuffer.length--;
                    continue;
                }

                if (currentElement.exiting) // TODO there is a duplicate ID problem with exiting elements
                {
                    dfsBuffer.length--;
                    continue;
                }

                if (highlightedRowIndex == layoutData.rowCount)
                {
                    if (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
                    {
                        context.debugSelectedElementId = currentElement.id;
                    }
                    highlightedElementId = currentElement.id;
                }

                context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = true;
                Clay_LayoutElementHashMapItem? currentElementData = __GetHashMapItem(currentElement.id);
                bool offscreen = currentElementData != null && __ElementIsOffscreen(in currentElementData.boundingBox);
                if (context.debugSelectedElementId == currentElement.id)
                {
                    layoutData.selectedElementRowIndex = layoutData.rowCount;
                }

                using (Element(Idi("Clay__DebugView_ElementOuter", currentElement.id), new Clay_ElementDeclaration { layout = Clay__DebugView_ScrollViewItemLayoutConfig }))
                {
                    // Collapse icon / button
                    if (!(currentElement.isTextElement || currentElement.children.length == 0))
                    {
                        using (Element(Idi("Clay__DebugView_CollapseElement", currentElement.id), new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig
                            {
                                sizing = new Clay_Sizing { width = SizingFixed(16), height = SizingFixed(16) },
                                childAlignment = new Clay_ChildAlignment { x = Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER, y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                            },
                            cornerRadius = CornerRadius(4),
                            border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = BorderOutside(1) },
                        }))
                        {
                            Text((currentElementData != null && currentElementData.debugData.collapsed) ? "+" : "-",
                                new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16 });
                        }
                    }
                    else // Square dot for empty containers
                    {
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig
                            {
                                sizing = new Clay_Sizing { width = SizingFixed(16), height = SizingFixed(16) },
                                childAlignment = new Clay_ChildAlignment { x = Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER, y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                            },
                        }))
                        {
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingFixed(8), height = SizingFixed(8) } },
                                backgroundColor = Clay__DEBUGVIEW_COLOR_3,
                                cornerRadius = CornerRadius(2),
                            })) { }
                        }
                    }
                    // Collisions and offscreen info
                    if (currentElementData != null)
                    {
                        if (currentElementData.debugData.collision)
                        {
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
                                border = new Clay_BorderElementConfig { color = new Clay_Color(177, 147, 8, 255), width = BorderOutside(1) },
                            }))
                            {
                                Text("Duplicate ID", new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_3, fontSize = 16 });
                            }
                        }
                        if (offscreen)
                        {
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
                                border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = BorderOutside(1) },
                            }))
                            {
                                Text("Offscreen", new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_3, fontSize = 16 });
                            }
                        }
                    }
                    if (currentElementData != null && currentElementData.elementId.stringId.Length > 0)
                    {
                        using (AutoId(new Clay_ElementDeclaration { }))
                        {
                            Clay_TextElementConfig textConfig = offscreen
                                ? new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_3, fontSize = 16 }
                                : Clay__DebugView_TextNameConfig;
                            Text(currentElementData.elementId.stringId, textConfig);
                            if (currentElementData.elementId.offset != 0)
                            {
                                Text(" (", textConfig);
                                Text(__IntToString(currentElementData.elementId.offset), textConfig);
                                Text(")", textConfig);
                            }
                        }
                    }
                    if (currentElement.isTextElement)
                    {
                        __RenderElementConfigTypeLabel("Text", new Clay_Color(105, 210, 231, 255), offscreen);
                    }
                    else
                    {
                        if (currentElement.config.backgroundColor.a > 0)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BACKGROUND_COLOR);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.overlayColor.a > 0)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_OVERLAY_COLOR);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (!__CornerRadiusIsZero(in currentElement.config.cornerRadius))
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CORNER_RADIUS);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.aspectRatio.aspectRatio != 0)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_ASPECT);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.image.imageData != null)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_IMAGE);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_FLOATING);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.clip.horizontal || currentElement.config.clip.vertical)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CLIP);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (__BorderHasAnyWidth(in currentElement.config.border))
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BORDER);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                        if (currentElement.config.custom.customData != null)
                        {
                            Clay__DebugElementConfigTypeLabelConfig config = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CUSTOM);
                            __RenderElementConfigTypeLabel(config.label, config.color, offscreen);
                        }
                    }
                }

                // Render the text contents below the element as a non-interactive row
                if (currentElement.isTextElement)
                {
                    layoutData.rowCount++;
                    Clay__TextElementData textElementData = currentElement.textElementData;
                    Clay_TextElementConfig rawTextConfig = offscreen
                        ? new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_3, fontSize = 16 }
                        : Clay__DebugView_TextNameConfig;
                    using (AutoId(new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig
                        {
                            sizing = new Clay_Sizing { height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
                            childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                        },
                    }))
                    {
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingFixed(CLAY__DEBUGVIEW_INDENT_WIDTH + 16) } },
                        })) { }
                        Text("\"", rawTextConfig);
                        Text(textElementData.text.Length > 40 ? textElementData.text.Substring(0, 40) : textElementData.text, rawTextConfig);
                        if (textElementData.text.Length > 40)
                        {
                            Text("...", rawTextConfig);
                        }
                        Text("\"", rawTextConfig);
                    }
                }
                else if (currentElement.children.length > 0)
                {
                    __OpenElement();
                    __ConfigureOpenElement(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8 } } });
                    __OpenElement();
                    __ConfigureOpenElement(new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = CLAY__DEBUGVIEW_INDENT_WIDTH } },
                        border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = new Clay_BorderWidth { left = 1 } },
                    });
                    __OpenElement();
                    __ConfigureOpenElement(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM } });
                }

                layoutData.rowCount++;
                if (!(currentElement.isTextElement || (currentElementData != null && currentElementData.debugData.collapsed)))
                {
                    for (int i = currentElement.children.length - 1; i >= 0; --i)
                    {
                        dfsBuffer.Add(currentElement.children.elements[currentElement.children.offset + i].index);
                        context.treeNodeVisited.internalArray[dfsBuffer.length - 1] = false; // TODO needs to be ranged checked
                    }
                }
            }
        }

        if (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
        {
            Clay_ElementId collapseButtonId = Id("Clay__DebugView_CollapseElement");
            for (int i = context.pointerOverIds.length - 1; i >= 0; i--)
            {
                Clay_ElementId elementId = context.pointerOverIds.internalArray[i];
                if (elementId.baseId == collapseButtonId.baseId)
                {
                    Clay_LayoutElementHashMapItem? highlightedItem = __GetHashMapItem(elementId.offset);
                    if (highlightedItem != null) highlightedItem.debugData.collapsed = !highlightedItem.debugData.collapsed;
                    break;
                }
            }
        }

        if (highlightedElementId != 0)
        {
            using (Element(Id("Clay__DebugView_ElementHighlight"), new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() } },
                floating = new Clay_FloatingElementConfig
                {
                    parentId = highlightedElementId,
                    zIndex = 32767,
                    pointerCaptureMode = Clay_PointerCaptureMode.CLAY_POINTER_CAPTURE_MODE_PASSTHROUGH,
                    attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ELEMENT_WITH_ID,
                },
            }))
            {
                using (Element(Id("Clay__DebugView_ElementHighlightRectangle"), new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() } },
                    backgroundColor = __debugViewHighlightColor,
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
        Clay_Context context = GetCurrentContext()!;
        Clay_ElementId closeButtonId = Id("Clay__DebugViewTopHeaderCloseButtonOuter");
        if (context.pointerInfo.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
        {
            for (int i = 0; i < context.pointerOverIds.length; ++i)
            {
                Clay_ElementId elementId = context.pointerOverIds.internalArray[i];
                if (elementId.id == closeButtonId.id)
                {
                    context.debugModeEnabled = false;
                    return;
                }
            }
        }

        int initialRootsLength = context.layoutElementTreeRoots.length;
        int initialElementsLength = context.layoutElements.length;
        Clay_TextElementConfig infoTextConfig = new()
        {
            textColor = Clay__DEBUGVIEW_COLOR_4,
            fontSize = 16,
            wrapMode = Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NONE,
        };
        Clay_TextElementConfig infoTitleConfig = new()
        {
            textColor = Clay__DEBUGVIEW_COLOR_3,
            fontSize = 16,
            wrapMode = Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NONE,
        };
        Clay_ElementId scrollId = Id("Clay__DebugViewOuterScrollPane");
        float scrollYOffset = 0;
        bool pointerInDebugView = context.pointerInfo.position.Y < context.layoutDimensions.height - 300;
        for (int i = 0; i < context.scrollContainerDatas.length; ++i)
        {
            Clay__ScrollContainerDataInternal scrollContainerData = context.scrollContainerDatas.internalArray[i];
            if (scrollContainerData.elementId == scrollId.id)
            {
                if (!context.externalScrollHandlingEnabled)
                {
                    scrollYOffset = scrollContainerData.scrollPosition.Y;
                }
                else
                {
                    pointerInDebugView = context.pointerInfo.position.Y + scrollContainerData.scrollPosition.Y < context.layoutDimensions.height - 300;
                }
                break;
            }
        }
        int highlightedRow = pointerInDebugView
            ? (int)((context.pointerInfo.position.Y - scrollYOffset) / CLAY__DEBUGVIEW_ROW_HEIGHT) - 1
            : -1;
        if (context.pointerInfo.position.X < context.layoutDimensions.width - __debugViewWidth)
        {
            highlightedRow = -1;
        }

        Clay__RenderDebugLayoutData layoutData = default;

        using (Element(Id("Clay__DebugView"), new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig
            {
                sizing = new Clay_Sizing { width = SizingFixed(__debugViewWidth), height = SizingFixed(context.layoutDimensions.height) },
                layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
            },
            floating = new Clay_FloatingElementConfig
            {
                zIndex = 32765,
                attachPoints = new Clay_FloatingAttachPoints
                {
                    element = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER,
                    parent = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER,
                },
                attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ROOT,
                clipTo = Clay_FloatingClipToElement.CLAY_CLIP_TO_ATTACHED_PARENT,
            },
            border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = new Clay_BorderWidth { bottom = 1 } },
        }))
        {
            // Header bar
            using (AutoId(new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig
                {
                    sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
                    padding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 0, bottom = 0 },
                    childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                },
                backgroundColor = Clay__DEBUGVIEW_COLOR_2,
            }))
            {
                Text("Clay Debug Tools", infoTextConfig);
                using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow() } } })) { }
                // Close button
                using (AutoId(new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig
                    {
                        sizing = new Clay_Sizing { width = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT - 10), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT - 10) },
                        childAlignment = new Clay_ChildAlignment { x = Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER, y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                    },
                    backgroundColor = new Clay_Color(217, 91, 67, 80),
                    cornerRadius = CornerRadius(4),
                    border = new Clay_BorderElementConfig { color = new Clay_Color(217, 91, 67, 255), width = BorderOutside(1) },
                }))
                {
                    OnHover(HandleDebugViewCloseButtonInteraction, null);
                    Text("x", new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16 });
                }
            }

            using (AutoId(new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(1) } },
                backgroundColor = Clay__DEBUGVIEW_COLOR_3,
            })) { }

            // Scroll pane containing the element list
            using (Element(scrollId, () => new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() } },
                clip = new Clay_ClipElementConfig { horizontal = true, vertical = true, childOffset = GetScrollOffset() },
            }))
            {
                using (AutoId(new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() }, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                    backgroundColor = ((initialElementsLength + initialRootsLength) & 1) == 0 ? Clay__DEBUGVIEW_COLOR_2 : Clay__DEBUGVIEW_COLOR_1,
                }))
                {
                    Clay_ElementId panelContentsId = Id("Clay__DebugViewPaneOuter");
                    // Element list
                    using (Element(panelContentsId, new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() } },
                        floating = new Clay_FloatingElementConfig
                        {
                            zIndex = 32766,
                            pointerCaptureMode = Clay_PointerCaptureMode.CLAY_POINTER_CAPTURE_MODE_PASSTHROUGH,
                            attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT,
                            clipTo = Clay_FloatingClipToElement.CLAY_CLIP_TO_ATTACHED_PARENT,
                        },
                    }))
                    {
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig
                            {
                                sizing = new Clay_Sizing { width = SizingGrow(), height = SizingGrow() },
                                padding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 0, bottom = 0 },
                                layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                            },
                        }))
                        {
                            layoutData = __RenderDebugLayoutElementsList(initialRootsLength, highlightedRow);
                        }
                    }
                    float contentWidth = __GetHashMapItem(panelContentsId.id)?.layoutElement.dimensions.width ?? 0;
                    using (AutoId(new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingFixed(contentWidth) }, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                    })) { }
                    // Row striping behind the (floating) element list
                    for (int i = 0; i < layoutData.rowCount; i++)
                    {
                        Clay_Color rowColor = (i & 1) == 0 ? Clay__DEBUGVIEW_COLOR_2 : Clay__DEBUGVIEW_COLOR_1;
                        if (i == layoutData.selectedElementRowIndex)
                        {
                            rowColor = Clay__DEBUGVIEW_COLOR_SELECTED_ROW;
                        }
                        if (i == highlightedRow)
                        {
                            rowColor.r *= 1.25f;
                            rowColor.g *= 1.25f;
                            rowColor.b *= 1.25f;
                        }
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) }, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            backgroundColor = rowColor,
                        })) { }
                    }
                }
            }

            using (AutoId(new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(1) } },
                backgroundColor = Clay__DEBUGVIEW_COLOR_3,
            })) { }

            Clay_LayoutElementHashMapItem? selectedItem = __GetHashMapItem(context.debugSelectedElementId);
            if (selectedItem != null && selectedItem.layoutElement != null)
            {
                using (AutoId(() => new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(300) }, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                    backgroundColor = Clay__DEBUGVIEW_COLOR_2,
                    clip = new Clay_ClipElementConfig { vertical = true, childOffset = GetScrollOffset() },
                    border = new Clay_BorderElementConfig { color = Clay__DEBUGVIEW_COLOR_3, width = new Clay_BorderWidth { betweenChildren = 1 } },
                }))
                {
                    using (AutoId(new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig
                        {
                            sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT + 8) },
                            padding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 0, bottom = 0 },
                            childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                        },
                    }))
                    {
                        Text("Element Configuration", infoTextConfig);
                        using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow() } } })) { }
                        if (selectedItem.elementId.stringId.Length != 0)
                        {
                            Text(selectedItem.elementId.stringId, infoTitleConfig);
                            if (selectedItem.elementId.offset != 0)
                            {
                                Text(" (", infoTitleConfig);
                                Text(__IntToString(selectedItem.elementId.offset), infoTitleConfig);
                                Text(")", infoTitleConfig);
                            }
                        }
                    }

                    Clay_Padding attributeConfigPadding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 8, bottom = 8 };

                    // Clay_LayoutConfig debug info
                    using (AutoId(new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                    }))
                    {
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
                            backgroundColor = new Clay_Color(200, 200, 200, 120),
                            cornerRadius = CornerRadius(4),
                            border = new Clay_BorderElementConfig { color = new Clay_Color(200, 200, 200, 255), width = BorderOutside(1) },
                        }))
                        {
                            Text("Layout", new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16 });
                        }
                        // .boundingBox
                        Text("Bounding Box", infoTitleConfig);
                        using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                        {
                            Text("{ x: ", infoTextConfig);
                            Text(__IntToString(selectedItem.boundingBox.x), infoTextConfig);
                            Text(", y: ", infoTextConfig);
                            Text(__IntToString(selectedItem.boundingBox.y), infoTextConfig);
                            Text(", width: ", infoTextConfig);
                            Text(__IntToString(selectedItem.boundingBox.width), infoTextConfig);
                            Text(", height: ", infoTextConfig);
                            Text(__IntToString(selectedItem.boundingBox.height), infoTextConfig);
                            Text(" }", infoTextConfig);
                        }
                        if (!selectedItem.layoutElement.isTextElement)
                        {
                            // .layoutDirection
                            Text("Layout Direction", infoTitleConfig);
                            Clay_LayoutConfig layoutConfig = selectedItem.layoutElement.config.layout;
                            Text(layoutConfig.layoutDirection == Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM ? "TOP_TO_BOTTOM" : "LEFT_TO_RIGHT", infoTextConfig);
                            // .sizing
                            Text("Sizing", infoTitleConfig);
                            using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                            {
                                Text("width: ", infoTextConfig);
                                __RenderDebugLayoutSizing(layoutConfig.sizing.width, infoTextConfig);
                            }
                            using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                            {
                                Text("height: ", infoTextConfig);
                                __RenderDebugLayoutSizing(layoutConfig.sizing.height, infoTextConfig);
                            }
                            // .padding
                            Text("Padding", infoTitleConfig);
                            using (Element(Id("Clay__DebugViewElementInfoPadding"), new Clay_ElementDeclaration { }))
                            {
                                Text("{ left: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.padding.left), infoTextConfig);
                                Text(", right: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.padding.right), infoTextConfig);
                                Text(", top: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.padding.top), infoTextConfig);
                                Text(", bottom: ", infoTextConfig);
                                Text(__IntToString(layoutConfig.padding.bottom), infoTextConfig);
                                Text(" }", infoTextConfig);
                            }
                            // .childGap
                            Text("Child Gap", infoTitleConfig);
                            Text(__IntToString(layoutConfig.childGap), infoTextConfig);
                            // .childAlignment
                            Text("Child Alignment", infoTitleConfig);
                            using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                            {
                                Text("{ x: ", infoTextConfig);
                                string alignX = "LEFT";
                                if (layoutConfig.childAlignment.x == Clay_LayoutAlignmentX.CLAY_ALIGN_X_CENTER)
                                {
                                    alignX = "CENTER";
                                }
                                else if (layoutConfig.childAlignment.x == Clay_LayoutAlignmentX.CLAY_ALIGN_X_RIGHT)
                                {
                                    alignX = "RIGHT";
                                }
                                Text(alignX, infoTextConfig);
                                Text(", y: ", infoTextConfig);
                                string alignY = "TOP";
                                if (layoutConfig.childAlignment.y == Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER)
                                {
                                    alignY = "CENTER";
                                }
                                else if (layoutConfig.childAlignment.y == Clay_LayoutAlignmentY.CLAY_ALIGN_Y_BOTTOM)
                                {
                                    alignY = "BOTTOM";
                                }
                                Text(alignY, infoTextConfig);
                                Text(" }", infoTextConfig);
                            }
                        }
                    }

                    if (selectedItem.layoutElement.isTextElement)
                    {
                        Clay_TextElementConfig textConfig = selectedItem.layoutElement.textConfig;
                        using (AutoId(new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                        }))
                        {
                            __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_TEXT);
                            // .fontSize
                            Text("Font Size", infoTitleConfig);
                            Text(__IntToString(textConfig.fontSize), infoTextConfig);
                            // .fontId
                            Text("Font ID", infoTitleConfig);
                            Text(__IntToString(textConfig.fontId), infoTextConfig);
                            // .lineHeight
                            Text("Line Height", infoTitleConfig);
                            Text(textConfig.lineHeight == 0 ? "auto" : __IntToString(textConfig.lineHeight), infoTextConfig);
                            // .letterSpacing
                            Text("Letter Spacing", infoTitleConfig);
                            Text(__IntToString(textConfig.letterSpacing), infoTextConfig);
                            // .wrapMode
                            Text("Wrap Mode", infoTitleConfig);
                            string wrapMode = "WORDS";
                            if (textConfig.wrapMode == Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NONE)
                            {
                                wrapMode = "NONE";
                            }
                            else if (textConfig.wrapMode == Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NEWLINES)
                            {
                                wrapMode = "NEWLINES";
                            }
                            Text(wrapMode, infoTextConfig);
                            // .textAlignment
                            Text("Text Alignment", infoTitleConfig);
                            string textAlignment = "LEFT";
                            if (textConfig.textAlignment == Clay_TextAlignment.CLAY_TEXT_ALIGN_CENTER)
                            {
                                textAlignment = "CENTER";
                            }
                            else if (textConfig.textAlignment == Clay_TextAlignment.CLAY_TEXT_ALIGN_RIGHT)
                            {
                                textAlignment = "RIGHT";
                            }
                            Text(textAlignment, infoTextConfig);
                            // .textColor
                            Text("Text Color", infoTitleConfig);
                            __RenderDebugViewColor(textConfig.textColor, infoTextConfig);
                        }
                    }
                    else
                    {
                        using (Element(Id("Clay__DebugViewElementInfoSharedBody"), new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                        }))
                        {
                            Clay__DebugElementConfigTypeLabelConfig labelConfig = __DebugGetElementConfigTypeLabel(Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BACKGROUND_COLOR);
                            Clay_Color backgroundColor = labelConfig.color;
                            backgroundColor.a = 90;
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 8, right = 8, top = 2, bottom = 2 } },
                                backgroundColor = backgroundColor,
                                cornerRadius = CornerRadius(4),
                                border = new Clay_BorderElementConfig { color = labelConfig.color, width = BorderOutside(1) },
                            }))
                            {
                                Text("Color & Radius", new Clay_TextElementConfig { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16 });
                            }
                            // .backgroundColor
                            if (selectedItem.layoutElement.config.backgroundColor.a > 0)
                            {
                                Text("Background Color", infoTitleConfig);
                                __RenderDebugViewColor(selectedItem.layoutElement.config.backgroundColor, infoTextConfig);
                            }
                            // .cornerRadius
                            if (!__CornerRadiusIsZero(in selectedItem.layoutElement.config.cornerRadius))
                            {
                                Text("Corner Radius", infoTitleConfig);
                                __RenderDebugViewCornerRadius(selectedItem.layoutElement.config.cornerRadius, infoTextConfig);
                            }
                            // .overlayColor
                            if (selectedItem.layoutElement.config.overlayColor.a > 0)
                            {
                                Text("Overlay Color", infoTitleConfig);
                                __RenderDebugViewColor(selectedItem.layoutElement.config.overlayColor, infoTextConfig);
                            }
                        }

                        if (selectedItem.layoutElement.config.aspectRatio.aspectRatio > 0)
                        {
                            Clay_AspectRatioElementConfig aspectRatioConfig = selectedItem.layoutElement.config.aspectRatio;
                            using (Element(Id("Clay__DebugViewElementInfoAspectRatioBody"), new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_ASPECT);
                                Text("Aspect Ratio", infoTitleConfig);
                                using (Element(Id("Clay__DebugViewElementInfoAspectRatio"), new Clay_ElementDeclaration { }))
                                {
                                    Text(__IntToString(aspectRatioConfig.aspectRatio), infoTextConfig);
                                    Text(".", infoTextConfig);
                                    float frac = aspectRatioConfig.aspectRatio - (int)aspectRatioConfig.aspectRatio;
                                    frac *= 100;
                                    if ((int)frac < 10)
                                    {
                                        Text("0", infoTextConfig);
                                    }
                                    Text(__IntToString(frac), infoTextConfig);
                                }
                            }
                        }

                        if (selectedItem.layoutElement.config.image.imageData != null)
                        {
                            Clay_ImageElementConfig imageConfig = selectedItem.layoutElement.config.image;
                            Clay_AspectRatioElementConfig aspectConfig = new() { aspectRatio = 1 };
                            if (selectedItem.layoutElement.config.aspectRatio.aspectRatio > 0)
                            {
                                aspectConfig = selectedItem.layoutElement.config.aspectRatio;
                            }
                            using (Element(Id("Clay__DebugViewElementInfoImageBody"), new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_IMAGE);
                                // Image Preview
                                Text("Preview", infoTitleConfig);
                                using (AutoId(new Clay_ElementDeclaration
                                {
                                    layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(64, 128), height = SizingGrow(64, 128) } },
                                    aspectRatio = aspectConfig,
                                    image = imageConfig,
                                })) { }
                            }
                        }

                        if (selectedItem.layoutElement.config.floating.attachTo != Clay_FloatingAttachToElement.CLAY_ATTACH_TO_NONE)
                        {
                            Clay_FloatingElementConfig floatingConfig = selectedItem.layoutElement.config.floating;
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_FLOATING);
                                // .offset
                                Text("Offset", infoTitleConfig);
                                using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                                {
                                    Text("{ x: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.offset.X), infoTextConfig);
                                    Text(", y: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.offset.Y), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .expand
                                Text("Expand", infoTitleConfig);
                                using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                                {
                                    Text("{ width: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.expand.width), infoTextConfig);
                                    Text(", height: ", infoTextConfig);
                                    Text(__IntToString(floatingConfig.expand.height), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .zIndex
                                Text("z-index", infoTitleConfig);
                                Text(__IntToString(floatingConfig.zIndex), infoTextConfig);
                                // .parentId
                                Text("Parent", infoTitleConfig);
                                Clay_LayoutElementHashMapItem? hashItem = __GetHashMapItem(floatingConfig.parentId);
                                Text(hashItem?.elementId.stringId ?? "", infoTextConfig);
                                // .attachPoints
                                Text("Attach Points", infoTitleConfig);
                                using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                                {
                                    Text("{ element: ", infoTextConfig);
                                    string attachPointElement = "LEFT_TOP";
                                    if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER) attachPointElement = "LEFT_CENTER";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM) attachPointElement = "LEFT_BOTTOM";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP) attachPointElement = "CENTER_TOP";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER) attachPointElement = "CENTER_CENTER";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM) attachPointElement = "CENTER_BOTTOM";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP) attachPointElement = "RIGHT_TOP";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER) attachPointElement = "RIGHT_CENTER";
                                    else if (floatingConfig.attachPoints.element == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM) attachPointElement = "RIGHT_BOTTOM";
                                    Text(attachPointElement, infoTextConfig);
                                    string attachPointParent = "LEFT_TOP";
                                    if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_CENTER) attachPointParent = "LEFT_CENTER";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM) attachPointParent = "LEFT_BOTTOM";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_TOP) attachPointParent = "CENTER_TOP";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_CENTER) attachPointParent = "CENTER_CENTER";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_CENTER_BOTTOM) attachPointParent = "CENTER_BOTTOM";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_TOP) attachPointParent = "RIGHT_TOP";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_CENTER) attachPointParent = "RIGHT_CENTER";
                                    else if (floatingConfig.attachPoints.parent == Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_RIGHT_BOTTOM) attachPointParent = "RIGHT_BOTTOM";
                                    Text(", parent: ", infoTextConfig);
                                    Text(attachPointParent, infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .pointerCaptureMode
                                Text("Pointer Capture Mode", infoTitleConfig);
                                string pointerCaptureMode = "NONE";
                                if (floatingConfig.pointerCaptureMode == Clay_PointerCaptureMode.CLAY_POINTER_CAPTURE_MODE_PASSTHROUGH)
                                {
                                    pointerCaptureMode = "PASSTHROUGH";
                                }
                                Text(pointerCaptureMode, infoTextConfig);
                                // .attachTo
                                Text("Attach To", infoTitleConfig);
                                string attachTo = "NONE";
                                if (floatingConfig.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT) attachTo = "PARENT";
                                else if (floatingConfig.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ELEMENT_WITH_ID) attachTo = "ELEMENT_WITH_ID";
                                else if (floatingConfig.attachTo == Clay_FloatingAttachToElement.CLAY_ATTACH_TO_ROOT) attachTo = "ROOT";
                                Text(attachTo, infoTextConfig);
                                // .clipTo
                                Text("Clip To", infoTitleConfig);
                                string clipTo = "ATTACHED_PARENT";
                                if (floatingConfig.clipTo == Clay_FloatingClipToElement.CLAY_CLIP_TO_NONE)
                                {
                                    clipTo = "NONE";
                                }
                                Text(clipTo, infoTextConfig);
                            }
                        }

                        Clay_ClipElementConfig clipConfig = selectedItem.layoutElement.config.clip;
                        if (clipConfig.horizontal || clipConfig.vertical)
                        {
                            using (AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_CLIP);
                                // .vertical
                                Text("Vertical", infoTitleConfig);
                                Text(clipConfig.vertical ? "true" : "false", infoTextConfig);
                                // .horizontal
                                Text("Horizontal", infoTitleConfig);
                                Text(clipConfig.horizontal ? "true" : "false", infoTextConfig);
                            }
                        }

                        Clay_BorderElementConfig borderConfig = selectedItem.layoutElement.config.border;
                        if (__BorderHasAnyWidth(in borderConfig))
                        {
                            using (Element(Id("Clay__DebugViewElementInfoBorderBody"), new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig { padding = attributeConfigPadding, childGap = 8, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                            }))
                            {
                                __DebugViewRenderElementConfigHeader(selectedItem.elementId.stringId, Clay__DebugElementConfigType.CLAY__ELEMENT_CONFIG_TYPE_BORDER);
                                Text("Border Widths", infoTitleConfig);
                                using (AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { layoutDirection = Clay_LayoutDirection.CLAY_LEFT_TO_RIGHT } }))
                                {
                                    Text("{ left: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.width.left), infoTextConfig);
                                    Text(", right: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.width.right), infoTextConfig);
                                    Text(", top: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.width.top), infoTextConfig);
                                    Text(", bottom: ", infoTextConfig);
                                    Text(__IntToString(borderConfig.width.bottom), infoTextConfig);
                                    Text(" }", infoTextConfig);
                                }
                                // .textColor (border color)
                                Text("Border Color", infoTitleConfig);
                                __RenderDebugViewColor(borderConfig.color, infoTextConfig);
                            }
                        }
                    }
                }
            }
            else
            {
                using (Element(Id("Clay__DebugViewWarningsScrollPane"), () => new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(300) }, childGap = 6, layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM },
                    backgroundColor = Clay__DEBUGVIEW_COLOR_2,
                    clip = new Clay_ClipElementConfig { horizontal = true, vertical = true, childOffset = GetScrollOffset() },
                }))
                {
                    Clay_TextElementConfig warningConfig = new() { textColor = Clay__DEBUGVIEW_COLOR_4, fontSize = 16, wrapMode = Clay_TextElementConfigWrapMode.CLAY_TEXT_WRAP_NONE };
                    using (Element(Id("Clay__DebugViewWarningItemHeader"), new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig
                        {
                            sizing = new Clay_Sizing { height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
                            padding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 0, bottom = 0 },
                            childGap = 8,
                            childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                        },
                    }))
                    {
                        Text("Warnings", warningConfig);
                    }
                    using (Element(Id("Clay__DebugViewWarningsTopBorder"), new Clay_ElementDeclaration
                    {
                        layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = SizingGrow(), height = SizingFixed(1) } },
                        backgroundColor = new Clay_Color(200, 200, 200, 255),
                    })) { }
                    int previousWarningsLength = context.warnings.length;
                    for (int i = 0; i < previousWarningsLength; i++)
                    {
                        Clay__Warning warning = context.warnings.internalArray[i];
                        using (Element(Idi("Clay__DebugViewWarningItem", (uint)i), new Clay_ElementDeclaration
                        {
                            layout = new Clay_LayoutConfig
                            {
                                sizing = new Clay_Sizing { height = SizingFixed(CLAY__DEBUGVIEW_ROW_HEIGHT) },
                                padding = new Clay_Padding { left = CLAY__DEBUGVIEW_OUTER_PADDING, right = CLAY__DEBUGVIEW_OUTER_PADDING, top = 0, bottom = 0 },
                                childGap = 8,
                                childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                            },
                        }))
                        {
                            Text(warning.baseMessage, warningConfig);
                            if (warning.dynamicMessage.Length > 0)
                            {
                                Text(warning.dynamicMessage, warningConfig);
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
    // Mirrors the C "Debug view caused layout element count to exceed Clay__maxElementCount" path.
    private static void __AddDebugViewElementsExceededError()
    {
        Clay_Context context = GetCurrentContext()!;
        const string message = "Clay Error: Debug view caused layout element count to exceed Clay__maxElementCount";
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
}
