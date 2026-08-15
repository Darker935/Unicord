// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DSharpPlus;
using DSharpPlus.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using Unicord.Universal.Controls.Emoji;
using Unicord.Universal.Converters;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.Emoji;
using Unicord.Universal.Parsers.Markdown;
using Unicord.Universal.Parsers.Markdown.Inlines;
using Unicord.Universal.Parsers.Markdown.Render;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Controls.Markdown.Render
{
    /// <summary>
    /// Inline UI Methods for UWP UI Creation.
    /// </summary>
    public partial class MarkdownRenderer
    {

        // every unicode sequence Discord itself treats as one emoji symbol, indexed for a
        // longest-first scan of ordinary text
        private static HashSet<char> _emojiStarts;
        private static int _emojiLongest;

        /// <summary>
        /// Renders emoji element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderEmoji(EmojiInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            localContext.InlineCollection.Add(CreateEmojiInline(new EmojiViewModel(element.Text)));
        }

        /// <summary>
        /// Builds the inline every emoji uses, whatever its origin: an <see cref="EmojiControl"/>
        /// box the size of the artwork a glyph of this font draws, dropped by as much as that
        /// artwork hangs below the baseline.
        ///
        /// A unicode emoji and a custom one used to be laid out by two different mechanisms - a
        /// text <see cref="Run"/> and an <see cref="InlineUIContainer"/> holding an image - and no
        /// amount of matching font metrics made them agree, because the text engine and the
        /// inline-container rule place their contents differently. One box, one rule, both kinds.
        /// </summary>
        private InlineUIContainer CreateEmojiInline(EmojiViewModel emoji, string tooltip = null)
        {
            var control = new EmojiControl()
            {
                Emoji = emoji,
                Size = Math.Round(EmojiSize > 0 ? EmojiSize : FontSize),
                FontFamily = EmojiFontFamily ?? DefaultEmojiFont,
                // An InlineUIContainer puts its child's bottom edge on the baseline, which leaves an
                // emoji floating above the text it sits in. The official client's rule is
                // vertical-align: bottom - bottom edge on the bottom of the line - so the box drops
                // by the message font's descent, which Segoe UI states as 514/2048 em.
                //
                // A RenderTransform and not a Margin, because a margin is layout and layout is what
                // the container has already used to place the child. RenderCodeRun below does the
                // same thing for the same reason.
                RenderTransform = new TranslateTransform() { Y = Math.Round(EmojiControl.LineDescent * FontSize) }
            };

            if (tooltip != null)
                ToolTipService.SetToolTip(control, tooltip);

            return new InlineUIContainer() { Child = control };
        }

        /// <summary>
        /// Splits a stretch of text into its emoji and non-emoji parts.
        ///
        /// Literal emoji arrive as ordinary text: <see cref="EmojiInline"/> only ever fires on a
        /// <c>:shortcode:</c>, and Discord stores what was actually typed, so a message practically
        /// never carries one. Picking them out here is what lets every emoji reach the one renderer
        /// - without it a unicode emoji stays a glyph the text engine draws and can never line up
        /// with the image beside it.
        ///
        /// Matched longest-first against the table Discord itself uses, so skin tones, keycaps,
        /// flags and ZWJ sequences come out as the single symbols they draw as.
        /// </summary>
        private static List<(bool IsEmoji, string Text)> SplitEmoji(string text)
        {
            if (_emojiStarts == null)
            {
                var starts = new HashSet<char>();
                var longest = 0;

                foreach (var sequence in DiscordEmoji.DiscordNameLookup.Keys)
                {
                    if (string.IsNullOrEmpty(sequence))
                        continue;

                    starts.Add(sequence[0]);
                    if (sequence.Length > longest)
                        longest = sequence.Length;
                }

                _emojiLongest = longest;
                _emojiStarts = starts;
            }

            var segments = new List<(bool, string)>();
            var plain = 0;

            for (var i = 0; i < text.Length;)
            {
                if (!_emojiStarts.Contains(text[i]))
                {
                    i++;
                    continue;
                }

                var length = 0;
                for (var candidate = Math.Min(_emojiLongest, text.Length - i); candidate > 0; candidate--)
                {
                    if (DiscordEmoji.DiscordNameLookup.ContainsKey(text.Substring(i, candidate)))
                    {
                        length = candidate;
                        break;
                    }
                }

                if (length == 0)
                {
                    i++;
                    continue;
                }

                if (i > plain)
                    segments.Add((false, text.Substring(plain, i - plain)));

                segments.Add((true, text.Substring(i, length)));
                i += length;
                plain = i;
            }

            if (plain < text.Length)
                segments.Add((false, text.Substring(plain)));

            return segments;
        }

        /// <summary>
        /// Renders a text run element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderTextRun(TextRunInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            var text = CollapseWhitespace(context, element.Text);

            // a Hyperlink's inlines only take Runs, so link text keeps plain glyphs
            if (localContext.WithinHyperlink || localContext.Parent is Hyperlink)
            {
                localContext.InlineCollection.Add(new Run { Text = text });
                return;
            }

            foreach (var segment in SplitEmoji(text))
            {
                if (segment.IsEmoji)
                    localContext.InlineCollection.Add(CreateEmojiInline(new EmojiViewModel(segment.Text)));
                else
                    localContext.InlineCollection.Add(new Run { Text = segment.Text });
            }
        }

        private Run InternalRenderTextRun(TextRunInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            var inlineCollection = localContext.InlineCollection;

            // Create the text run
            var textRun = new Run
            {
                Text = CollapseWhitespace(context, element.Text)
            };

            // Add it
            inlineCollection.Add(textRun);
            return textRun;
        }

        /// <summary>
        /// Renders a bold run element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderBoldRun(BoldTextInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            // Create the text run
            var boldSpan = new Span
            {
                FontWeight = FontWeights.Bold
            };

            var childContext = new InlineRenderContext(boldSpan.Inlines, context)
            {
                Parent = boldSpan,
                WithinBold = true
            };

            // Render the children into the bold inline.
            RenderInlineChildren(element.Inlines, childContext);

            // Add it to the current inlines
            localContext.InlineCollection.Add(boldSpan);
        }

        /// <summary>
        /// Renders an underlined run element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderUnderlineRun(UnderlineTextInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            // Create the text run
            var boldSpan = new Span
            {
                TextDecorations = TextDecorations.Underline
            };

            var childContext = new InlineRenderContext(boldSpan.Inlines, context)
            {
                Parent = boldSpan,
                WithinUnderline = true
            };

            // Render the children into the bold inline.
            RenderInlineChildren(element.Inlines, childContext);

            // Add it to the current inlines
            localContext.InlineCollection.Add(boldSpan);
        }


        /// <summary>
        /// Renders a link element
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderMarkdownLink(MarkdownLinkInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            // Regular ol' hyperlink.
            var link = new Hyperlink();

            // Register the link
            LinkRegister.RegisterNewHyperLink(link, element.Url);

            // Render the children into the link inline.
            var childContext = new InlineRenderContext(link.Inlines, context)
            {
                Parent = link,
                WithinHyperlink = true
            };

            if (localContext.OverrideForeground)
            {
                link.Foreground = localContext.Foreground;
            }
            else if (LinkForeground != null)
            {
                link.Foreground = LinkForeground;
            }

            RenderInlineChildren(element.Inlines, childContext);
            context.TrimLeadingWhitespace = childContext.TrimLeadingWhitespace;

            ToolTipService.SetToolTip(link, element.Tooltip ?? element.Url);

            // Add it to the current inlines
            localContext.InlineCollection.Add(link);
        }

        /// <summary>
        /// Renders a raw link element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderHyperlink(HyperlinkInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            var link = new Hyperlink();

            // Register the link
            LinkRegister.RegisterNewHyperLink(link, element.Url);

            var brush = localContext.Foreground;
            if (LinkForeground != null && !localContext.OverrideForeground)
            {
                brush = LinkForeground;
            }

            // Make a text block for the link
            var linkText = new Run
            {
                Text = CollapseWhitespace(context, element.Text),
                Foreground = brush
            };

            link.Inlines.Add(linkText);

            // Add it to the current inlines
            localContext.InlineCollection.Add(link);
        }

        /// <summary>
        /// Renders an image element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override async void RenderImage(ImageInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            var inlineCollection = localContext.InlineCollection;

            var placeholder = InternalRenderTextRun(new TextRunInline { Text = element.Text, Type = MarkdownInlineType.TextRun }, context);
            var resolvedImage = await ImageResolver.ResolveImageAsync(element.RenderUrl, element.Tooltip);

            // if image can not be resolved we have to return
            if (resolvedImage == null)
            {
                return;
            }

            var image = new Image
            {
                Source = resolvedImage,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Stretch = ImageStretch
            };

            var hyperlinkButton = new HyperlinkButton()
            {
                Content = image
            };

            var viewbox = new Viewbox
            {
                Child = hyperlinkButton,
                StretchDirection = StretchDirection.DownOnly
            };

            viewbox.PointerWheelChanged += Preventative_PointerWheelChanged;

            var scrollViewer = new ScrollViewer
            {
                Content = viewbox,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var imageContainer = new InlineUIContainer() { Child = scrollViewer };

            var ishyperlink = false;
            if (element.RenderUrl != element.Url)
            {
                ishyperlink = true;
            }

            LinkRegister.RegisterNewHyperLink(image, element.Url, ishyperlink);

            if (ImageMaxHeight > 0)
            {
                viewbox.MaxHeight = ImageMaxHeight;
            }

            if (ImageMaxWidth > 0)
            {
                viewbox.MaxWidth = ImageMaxWidth;
            }

            if (element.ImageWidth > 0)
            {
                image.Width = element.ImageWidth;
                image.Stretch = Stretch.UniformToFill;
            }

            if (element.ImageHeight > 0)
            {
                if (element.ImageWidth == 0)
                {
                    image.Width = element.ImageHeight;
                }

                image.Height = element.ImageHeight;
                image.Stretch = Stretch.UniformToFill;
            }

            if (element.ImageHeight > 0 && element.ImageWidth > 0)
            {
                image.Stretch = Stretch.Fill;
            }

            // If image size is given then scroll to view overflown part
            if (element.ImageHeight > 0 || element.ImageWidth > 0)
            {
                scrollViewer.HorizontalScrollMode = ScrollMode.Auto;
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }

            // Else resize the image
            else
            {
                scrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }

            ToolTipService.SetToolTip(image, element.Tooltip);

            // Try to add it to the current inlines
            // Could fail because some containers like Hyperlink cannot have inlined images
            try
            {
                var placeholderIndex = inlineCollection.IndexOf(placeholder);
                inlineCollection.Remove(placeholder);
                inlineCollection.Insert(placeholderIndex, imageContainer);
            }
            catch
            {
                // Ignore error
            }
        }

        /// <summary>
        /// Renders a text run element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderItalicRun(ItalicTextInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            // Create the text run
            var italicSpan = new Span
            {
                FontStyle = FontStyle.Italic
            };

            var childContext = new InlineRenderContext(italicSpan.Inlines, context)
            {
                Parent = italicSpan,
                WithinItalics = true
            };

            // Render the children into the italic inline.
            RenderInlineChildren(element.Inlines, childContext);

            // Add it to the current inlines
            localContext.InlineCollection.Add(italicSpan);
        }

        /// <summary>
        /// Renders a strikethrough element.
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderStrikethroughRun(StrikethroughTextInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            var span = new Span();

            if (TextDecorationsSupported)
            {
                span.TextDecorations = TextDecorations.Strikethrough;
            }
            else
            {
                span.FontFamily = new FontFamily("Consolas");
            }

            var childContext = new InlineRenderContext(span.Inlines, context)
            {
                Parent = span
            };

            // Render the children into the inline.
            RenderInlineChildren(element.Inlines, childContext);

            if (!TextDecorationsSupported)
            {
                AlterChildRuns(span, (parentSpan, run) =>
                {
                    var text = run.Text;
                    var builder = new StringBuilder(text.Length * 2);
                    foreach (var c in text)
                    {
                        builder.Append((char)0x0336);
                        builder.Append(c);
                    }
                    run.Text = builder.ToString();
                });
            }

            // Add it to the current inlines
            localContext.InlineCollection.Add(span);
        }

        /// <summary>
        /// Renders a code element
        /// </summary>
        /// <param name="element"> The parsed inline element to render. </param>
        /// <param name="context"> Persistent state. </param>
        protected override void RenderCodeRun(CodeInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            if (localContext.Parent is Hyperlink)
            {
                // In case of Hyperlink, break glass (or add a run).

                var text = new Run { Text = CollapseWhitespace(context, element.Text) };

                if (localContext.WithinItalics)
                {
                    text.FontStyle = FontStyle.Italic;
                }

                if (localContext.WithinBold)
                {
                    text.FontWeight = FontWeights.Bold;
                }

                if (localContext.WithinUnderline)
                {
                    text.TextDecorations = TextDecorations.Underline;
                }

                localContext.InlineCollection.Add(text);
            }
            else
            {
                var text = CreateTextBlock(localContext);
                text.Text = CollapseWhitespace(context, element.Text);
                text.FontFamily = InlineCodeFontFamily ?? FontFamily;

                if (localContext.WithinItalics)
                {
                    text.FontStyle = FontStyle.Italic;
                }

                if (localContext.WithinBold)
                {
                    text.FontWeight = FontWeights.Bold;
                }

                if (localContext.WithinUnderline)
                {
                    text.TextDecorations = TextDecorations.Underline;
                }

                var borderthickness = InlineCodeBorderThickness;
                var padding = InlineCodePadding;
                var spacingoffset = -(borderthickness.Bottom + padding.Bottom);

                var margin = new Thickness(0, spacingoffset, 0, spacingoffset);

                var border = new Border
                {
                    BorderThickness = borderthickness,
                    BorderBrush = InlineCodeBorderBrush,
                    Background = InlineCodeBackground,
                    CornerRadius = InlineCodeCornerRadius,
                    Child = text,
                    Padding = padding,
                    Margin = margin
                };

                // Aligns content in InlineUI, see https://social.msdn.microsoft.com/Forums/silverlight/en-US/48b5e91e-efc5-4768-8eaf-f897849fcf0b/richtextbox-inlineuicontainer-vertical-alignment-issue?forum=silverlightarchieve
                border.RenderTransform = new TranslateTransform
                {
                    Y = 4
                };

                var inlineUIContainer = new InlineUIContainer
                {
                    Child = border,
                };

                RootElement.Margin = new Thickness(0, 0, 0, 4);
                // Add it to the current inlines
                localContext.InlineCollection.Add(inlineUIContainer);
            }
        }

        protected override void RenderSpoiler(SpoilerTextInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            // TODO (maybe): make this shit actually work?

            if (localContext.Parent is Hyperlink)
            {
                // In case of Hyperlink, break glass (or add a run).

                var span = new Span();
                var childContext = new InlineRenderContext(span.Inlines, context)
                {
                    Parent = span
                };

                // Render the children into the inline.
                RenderInlineChildren(element.Inlines, childContext);

                // Add it to the current inlines
                localContext.InlineCollection.Add(span);
            }
            else
            {
                var text = new RichTextBlock
                {
                    CharacterSpacing = CharacterSpacing,
                    FontFamily = FontFamily,
                    FontSize = FontSize,
                    FontStretch = FontStretch,
                    FontStyle = FontStyle,
                    FontWeight = FontWeight,
                    Foreground = localContext.Foreground,
                    IsTextSelectionEnabled = IsTextSelectionEnabled,
                    TextWrapping = TextWrapping
                };

                var paragraph = new Paragraph();
                var childContext = new InlineRenderContext(paragraph.Inlines, context)
                {
                    Parent = text
                };

                RenderInlineChildren(element.Inlines, childContext);

                if (localContext.WithinItalics)
                {
                    text.FontStyle = FontStyle.Italic;
                }

                if (localContext.WithinBold)
                {
                    text.FontWeight = FontWeights.Bold;
                }

                if (localContext.WithinUnderline)
                {
                    text.TextDecorations = TextDecorations.Underline;
                }

                text.Blocks.Add(paragraph);

                var borderthickness = InlineCodeBorderThickness;
                var padding = InlineCodePadding;
                var spacingoffset = -(borderthickness.Bottom + padding.Bottom);
                var margin = new Thickness(0, spacingoffset, 0, spacingoffset);

                var grid = new Grid
                {
                    BorderThickness = borderthickness,
                    BorderBrush = InlineCodeBorderBrush,
                    Background = InlineCodeBackground,
                    Padding = padding,
                    Margin = margin
                };

                // Aligns content in InlineUI, see https://social.msdn.microsoft.com/Forums/silverlight/en-US/48b5e91e-efc5-4768-8eaf-f897849fcf0b/richtextbox-inlineuicontainer-vertical-alignment-issue?forum=silverlightarchieve
                grid.RenderTransform = new TranslateTransform
                {
                    Y = 4
                };

                grid.Children.Add(text);

                if (App.RoamingSettings.Read(Constants.ENABLE_SPOILERS, true))
                {
                    var border = new Border()
                    {
                        Background = InlineCodeBackground,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };

                    border.Tapped += (o, e) => { border.Opacity = 0; };

                    grid.Children.Add(border);
                }

                var inlineUIContainer = new InlineUIContainer
                {
                    Child = grid,
                };

                RootElement.Margin = new Thickness(0, 0, 0, 4);

                // Add it to the current inlines
                localContext.InlineCollection.Add(inlineUIContainer);
            }
        }

        private Brush GetDiscordBrush(DiscordColor color)
        {
            return color.Value == 0 ? Foreground : (Brush)ColourBrushConverter.Convert(color, typeof(Brush), null, "");
        }

        protected override void RenderDiscord(DiscordInline element, IRenderContext context)
        {
            if (context is not InlineRenderContext localContext)
            {
                throw new RenderContextIncorrectException();
            }

            if (Channel != null)
            {
                var client = Channel.Discord as DiscordClient;
                var guild = Channel.Guild;

                if (element.DiscordType != DiscordInline.MentionType.Emote)
                {
                    var run = new Run() { FontWeight = FontWeights.Bold };

                    if (element.DiscordType == DiscordInline.MentionType.User)
                    {
                        var user = (DiscordUser)guild?.Members.GetValueOrDefault(element.Id);
                        if (user == null)
                        {
                            client.TryGetCachedUser(element.Id, out user);
                        }

                        if (user != null)
                        {
                            run.Text = IsSystemMessage ? user.DisplayName : $"@{user.DisplayName}";
                            if (user is DiscordMember member)
                                run.Foreground = GetDiscordBrush(member.Color);
                        }
                        else
                        {
                            run.Text = $"@deleted-user";
                        }
                    }
                    else if (element.DiscordType == DiscordInline.MentionType.Role)
                    {
                        if (guild?.Roles.TryGetValue(element.Id, out var role) == true)
                        {
                            run.Text = $"@{role.Name}";
                            run.Foreground = GetDiscordBrush(role.Color);
                        }
                        else
                        {
                            run.Text = $"@deleted-role";
                        }
                    }
                    else if (element.DiscordType == DiscordInline.MentionType.Channel)
                    {
                        if (client.TryGetCachedChannel(element.Id, out var channel) && channel is not DiscordDmChannel)
                        {
                            run.Text = $"#{channel.Name}";
                        }
                        else
                        {
                            run.Text = $"#deleted-channel";
                        }
                    }


                    localContext.InlineCollection.Add(run);
                }
                else
                {
                    // Requested from the CDN well above the drawn size, so display scaling has
                    // pixels to work with instead of upscaling a small source.
                    var requestSize = EmojiSize > 32 ? 256 : 128;
                    var emoji = new EmojiViewModel(element.Id, element.Text, element.IsAnimated, requestSize);

                    // exactly the same box a unicode emoji gets
                    localContext.InlineCollection.Add(CreateEmojiInline(emoji, element.Text));
                }
            }
        }
    }
}