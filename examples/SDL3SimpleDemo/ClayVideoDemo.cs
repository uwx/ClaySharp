using ClaySharp;

namespace ClaySharp.Examples.SDL3;

// Port of clay/examples/shared-layouts/clay-video-demo.c. The C `frameArena` (used to
// allocate SidebarClickData with a pointer to the selected index) is unnecessary in C# —
// the OnHover callback closes over the data directly.
public sealed class ClayVideoDemo_Data
{
    public int selectedDocumentIndex;
    public float yOffset;
}

public static class ClayVideoDemo
{
    public const int FONT_ID_BODY_16 = 0;
    public static readonly Clay_Color COLOR_WHITE = new Clay_Color(255, 255, 255, 255);

    private struct Document
    {
        public string title;
        public string contents;
    }

    private static readonly Document[] documents = new Document[5];

    private static void RenderHeaderButton(string text)
    {
        using (Clay.AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 16, right = 16, top = 8, bottom = 8 } },
            backgroundColor = new Clay_Color(140, 140, 140, 255),
            cornerRadius = Clay.CornerRadius(5),
        }))
        {
            Clay.Text(text, new Clay_TextElementConfig
            {
                fontId = FONT_ID_BODY_16,
                fontSize = 16,
                textColor = new Clay_Color(255, 255, 255, 255),
            });
        }
    }

    private static void RenderDropdownMenuItem(string text)
    {
        using (Clay.AutoId(new Clay_ElementDeclaration
        {
            layout = new Clay_LayoutConfig { padding = Clay.PaddingAll(16) },
        }))
        {
            Clay.Text(text, new Clay_TextElementConfig
            {
                fontId = FONT_ID_BODY_16,
                fontSize = 16,
                textColor = new Clay_Color(255, 255, 255, 255),
            });
        }
    }

    public static ClayVideoDemo_Data Initialize()
    {
        documents[0] = new Document
        {
            title = "Squirrels",
            contents =
                "The Secret Life of Squirrels: Nature's Clever Acrobats\n" +
                "Squirrels are often overlooked creatures, dismissed as mere park inhabitants or backyard nuisances. Yet, beneath their fluffy tails and twitching noses lies an intricate world of cunning, agility, and survival tactics that are nothing short of fascinating. As one of the most common mammals in North America, squirrels have adapted to a wide range of environments from bustling urban centers to tranquil forests and have developed a variety of unique behaviors that continue to intrigue scientists and nature enthusiasts alike.\n" +
                "\n" +
                "Master Tree Climbers\n" +
                "At the heart of a squirrel's skill set is its impressive ability to navigate trees with ease. Whether they're darting from branch to branch or leaping across wide gaps, squirrels possess an innate talent for acrobatics. Their powerful hind legs, which are longer than their front legs, give them remarkable jumping power. With a tail that acts as a counterbalance, squirrels can leap distances of up to ten times the length of their body, making them some of the best aerial acrobats in the animal kingdom.\n" +
                "But it's not just their agility that makes them exceptional climbers. Squirrels' sharp, curved claws allow them to grip tree bark with precision, while the soft pads on their feet provide traction on slippery surfaces. Their ability to run at high speeds and scale vertical trunks with ease is a testament to the evolutionary adaptations that have made them so successful in their arboreal habitats.\n" +
                "\n" +
                "Food Hoarders Extraordinaire\n" +
                "Squirrels are often seen frantically gathering nuts, seeds, and even fungi in preparation for winter. While this behavior may seem like instinctual hoarding, it is actually a survival strategy that has been honed over millions of years. Known as \"scatter hoarding,\" squirrels store their food in a variety of hidden locations, often burying it deep in the soil or stashing it in hollowed-out tree trunks.\n" +
                "Interestingly, squirrels have an incredible memory for the locations of their caches. Research has shown that they can remember thousands of hiding spots, often returning to them months later when food is scarce. However, they don't always recover every stash some forgotten caches eventually sprout into new trees, contributing to forest regeneration. This unintentional role as forest gardeners highlights the ecological importance of squirrels in their ecosystems.\n" +
                "\n" +
                "The Great Squirrel Debate: Urban vs. Wild\n" +
                "While squirrels are most commonly associated with rural or wooded areas, their adaptability has allowed them to thrive in urban environments as well. In cities, squirrels have become adept at finding food sources in places like parks, streets, and even garbage cans. However, their urban counterparts face unique challenges, including traffic, predators, and the lack of natural shelters. Despite these obstacles, squirrels in urban areas are often observed using human infrastructure such as buildings, bridges, and power lines as highways for their acrobatic escapades.\n" +
                "There is, however, a growing concern regarding the impact of urban life on squirrel populations. Pollution, deforestation, and the loss of natural habitats are making it more difficult for squirrels to find adequate food and shelter. As a result, conservationists are focusing on creating squirrel-friendly spaces within cities, with the goal of ensuring these resourceful creatures continue to thrive in both rural and urban landscapes.\n" +
                "\n" +
                "A Symbol of Resilience\n" +
                "In many cultures, squirrels are symbols of resourcefulness, adaptability, and preparation. Their ability to thrive in a variety of environments while navigating challenges with agility and grace serves as a reminder of the resilience inherent in nature. Whether you encounter them in a quiet forest, a city park, or your own backyard, squirrels are creatures that never fail to amaze with their endless energy and ingenuity.\n" +
                "In the end, squirrels may be small, but they are mighty in their ability to survive and thrive in a world that is constantly changing. So next time you spot one hopping across a branch or darting across your lawn, take a moment to appreciate the remarkable acrobat at work a true marvel of the natural world.\n",
        };

        documents[1] = new Document
        {
            title = "Lorem Ipsum",
            contents = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.",
        };

        documents[2] = new Document
        {
            title = "Vacuum Instructions",
            contents =
                "Chapter 3: Getting Started - Unpacking and Setup\n" +
                "\n" +
                "Congratulations on your new SuperClean Pro 5000 vacuum cleaner! In this section, we will guide you through the simple steps to get your vacuum up and running. Before you begin, please ensure that you have all the components listed in the \"Package Contents\" section on page 2.\n" +
                "\n" +
                "1. Unboxing Your Vacuum\n" +
                "Carefully remove the vacuum cleaner from the box. Avoid using sharp objects that could damage the product. Once removed, place the unit on a flat, stable surface to proceed with the setup. Inside the box, you should find:\n" +
                "\n" +
                "    The main vacuum unit\n" +
                "    A telescoping extension wand\n" +
                "    A set of specialized cleaning tools (crevice tool, upholstery brush, etc.)\n" +
                "    A reusable dust bag (if applicable)\n" +
                "    A power cord with a 3-prong plug\n" +
                "    A set of quick-start instructions\n" +
                "\n" +
                "2. Assembling Your Vacuum\n" +
                "Begin by attaching the extension wand to the main body of the vacuum cleaner. Line up the connectors and twist the wand into place until you hear a click. Next, select the desired cleaning tool and firmly attach it to the wand's end, ensuring it is securely locked in.\n" +
                "\n" +
                "For models that require a dust bag, slide the bag into the compartment at the back of the vacuum, making sure it is properly aligned with the internal mechanism. If your vacuum uses a bagless system, ensure the dust container is correctly seated and locked in place before use.\n" +
                "\n" +
                "3. Powering On\n" +
                "To start the vacuum, plug the power cord into a grounded electrical outlet. Once plugged in, locate the power switch, usually positioned on the side of the handle or body of the unit, depending on your model. Press the switch to the \"On\" position, and you should hear the motor begin to hum. If the vacuum does not power on, check that the power cord is securely plugged in, and ensure there are no blockages in the power switch.\n" +
                "\n" +
                "Note: Before first use, ensure that the vacuum filter (if your model has one) is properly installed. If unsure, refer to \"Section 5: Maintenance\" for filter installation instructions.",
        };

        documents[3] = new Document { title = "Article 4", contents = "Article 4" };
        documents[4] = new Document { title = "Article 5", contents = "Article 5" };

        return new ClayVideoDemo_Data();
    }

    public static Clay_RenderCommandArray CreateLayout(ClayVideoDemo_Data data)
    {
        Clay.BeginLayout();

        Clay_Sizing layoutExpand = new Clay_Sizing { width = Clay.SizingGrow(0), height = Clay.SizingGrow(0) };
        Clay_Color contentBackgroundColor = new Clay_Color(90, 90, 90, 255);

        using (Clay.Element(Clay.Id("OuterContainer"), new Clay_ElementDeclaration
        {
            backgroundColor = new Clay_Color(43, 41, 51, 255),
            layout = new Clay_LayoutConfig
            {
                layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                sizing = layoutExpand,
                padding = Clay.PaddingAll(16),
                childGap = 16,
            },
        }))
        {
            // Header bar.
            using (Clay.Element(Clay.Id("HeaderBar"), new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig
                {
                    sizing = new Clay_Sizing { height = Clay.SizingFixed(60), width = Clay.SizingGrow(0) },
                    padding = new Clay_Padding { left = 16, right = 16, top = 0, bottom = 0 },
                    childGap = 16,
                    childAlignment = new Clay_ChildAlignment { y = Clay_LayoutAlignmentY.CLAY_ALIGN_Y_CENTER },
                },
                backgroundColor = contentBackgroundColor,
                cornerRadius = Clay.CornerRadius(8),
            }))
            {
                using (Clay.Element(Clay.Id("FileButton"), new Clay_ElementDeclaration
                {
                    layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 16, right = 16, top = 8, bottom = 8 } },
                    backgroundColor = new Clay_Color(140, 140, 140, 255),
                    cornerRadius = Clay.CornerRadius(5),
                }))
                {
                    Clay.Text("File", new Clay_TextElementConfig
                    {
                        fontId = FONT_ID_BODY_16,
                        fontSize = 16,
                        textColor = new Clay_Color(255, 255, 255, 255),
                    });

                    bool fileMenuVisible =
                        Clay.PointerOver(Clay.GetElementId("FileButton"))
                        || Clay.PointerOver(Clay.GetElementId("FileMenu"));

                    if (fileMenuVisible)
                    {
                        using (Clay.Element(Clay.Id("FileMenu"), new Clay_ElementDeclaration
                        {
                            floating = new Clay_FloatingElementConfig
                            {
                                attachTo = Clay_FloatingAttachToElement.CLAY_ATTACH_TO_PARENT,
                                attachPoints = new Clay_FloatingAttachPoints { parent = Clay_FloatingAttachPointType.CLAY_ATTACH_POINT_LEFT_BOTTOM },
                            },
                            layout = new Clay_LayoutConfig { padding = new Clay_Padding { left = 0, right = 0, top = 8, bottom = 8 } },
                        }))
                        {
                            using (Clay.AutoId(new Clay_ElementDeclaration
                            {
                                layout = new Clay_LayoutConfig
                                {
                                    layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                                    sizing = new Clay_Sizing { width = Clay.SizingFixed(200) },
                                },
                                backgroundColor = new Clay_Color(40, 40, 40, 255),
                                cornerRadius = Clay.CornerRadius(8),
                            }))
                            {
                                RenderDropdownMenuItem("New");
                                RenderDropdownMenuItem("Open");
                                RenderDropdownMenuItem("Close");
                            }
                        }
                    }
                }

                RenderHeaderButton("Edit");
                using (Clay.AutoId(new Clay_ElementDeclaration { layout = new Clay_LayoutConfig { sizing = new Clay_Sizing { width = Clay.SizingGrow(0) } } })) { }
                RenderHeaderButton("Upload");
                RenderHeaderButton("Media");
                RenderHeaderButton("Support");
            }

            // Lower content: sidebar + scrolling main content.
            using (Clay.Element(Clay.Id("LowerContent"), new Clay_ElementDeclaration
            {
                layout = new Clay_LayoutConfig { sizing = layoutExpand, childGap = 16 },
            }))
            {
                using (Clay.Element(Clay.Id("Sidebar"), new Clay_ElementDeclaration
                {
                    backgroundColor = contentBackgroundColor,
                    layout = new Clay_LayoutConfig
                    {
                        layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                        padding = Clay.PaddingAll(16),
                        childGap = 8,
                        sizing = new Clay_Sizing { width = Clay.SizingFixed(250), height = Clay.SizingGrow(0) },
                    },
                }))
                {
                    for (int i = 0; i < documents.Length; i++)
                    {
                        Document document = documents[i];
                        Clay_LayoutConfig sidebarButtonLayout = new Clay_LayoutConfig
                        {
                            sizing = new Clay_Sizing { width = Clay.SizingGrow(0) },
                            padding = Clay.PaddingAll(16),
                        };

                        if (i == data.selectedDocumentIndex)
                        {
                            using (Clay.AutoId(new Clay_ElementDeclaration
                            {
                                layout = sidebarButtonLayout,
                                backgroundColor = new Clay_Color(120, 120, 120, 255),
                                cornerRadius = Clay.CornerRadius(8),
                            }))
                            {
                                Clay.Text(document.title, new Clay_TextElementConfig
                                {
                                    fontId = FONT_ID_BODY_16,
                                    fontSize = 20,
                                    textColor = new Clay_Color(255, 255, 255, 255),
                                });
                            }
                        }
                        else
                        {
                            int requestedDocumentIndex = i;
                            using (Clay.AutoId(() => new Clay_ElementDeclaration
                            {
                                layout = sidebarButtonLayout,
                                backgroundColor = new Clay_Color(120, 120, 120, Clay.Hovered() ? 120 : 0),
                                cornerRadius = Clay.CornerRadius(8),
                            }))
                            {
                                Clay.OnHover((Clay_ElementId elementId, Clay_PointerData pointerData, object? userData) =>
                                {
                                    if (pointerData.state == Clay_PointerDataInteractionState.CLAY_POINTER_DATA_PRESSED_THIS_FRAME)
                                    {
                                        data.selectedDocumentIndex = requestedDocumentIndex;
                                    }
                                }, null);

                                Clay.Text(document.title, new Clay_TextElementConfig
                                {
                                    fontId = FONT_ID_BODY_16,
                                    fontSize = 20,
                                    textColor = new Clay_Color(255, 255, 255, 255),
                                });
                            }
                        }
                    }
                }

                using (Clay.Element(Clay.Id("MainContent"), () => new Clay_ElementDeclaration
                {
                    backgroundColor = contentBackgroundColor,
                    clip = new Clay_ClipElementConfig { vertical = true, childOffset = Clay.GetScrollOffset() },
                    layout = new Clay_LayoutConfig
                    {
                        layoutDirection = Clay_LayoutDirection.CLAY_TOP_TO_BOTTOM,
                        childGap = 16,
                        padding = Clay.PaddingAll(16),
                        sizing = layoutExpand,
                    },
                }))
                {
                    Document selectedDocument = documents[data.selectedDocumentIndex];
                    Clay.Text(selectedDocument.title, new Clay_TextElementConfig
                    {
                        fontId = FONT_ID_BODY_16,
                        fontSize = 24,
                        textColor = COLOR_WHITE,
                    });
                    Clay.Text(selectedDocument.contents, new Clay_TextElementConfig
                    {
                        fontId = FONT_ID_BODY_16,
                        fontSize = 24,
                        textColor = COLOR_WHITE,
                    });
                }
            }
        }

        Clay_RenderCommandArray renderCommands = Clay.EndLayout(0);
        for (int i = 0; i < renderCommands.length; i++)
        {
            renderCommands.Get(i).boundingBox.y += data.yOffset;
        }
        return renderCommands;
    }
}
