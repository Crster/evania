using System;
using System.Collections.Generic;
using System.Linq;
using Evania.Controls.MarkdownEditor;
using Evania.Services;
using Microsoft.UI;
using FontStyle = Windows.UI.Text.FontStyle;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Evania.Views;

public static class NoteListTextRenderer
{
    public static readonly DependencyProperty SearchQueryProperty = DependencyProperty.RegisterAttached(
        "SearchQuery",
        typeof(string),
        typeof(NoteListTextRenderer),
        new PropertyMetadata(string.Empty, OnSearchQueryChanged));

    private static readonly DependencyProperty HighlightTokenProperty = DependencyProperty.RegisterAttached(
        "HighlightToken",
        typeof(long),
        typeof(NoteListTextRenderer),
        new PropertyMetadata(0L));

    private static readonly SolidColorBrush HighlightBrush = new(Colors.Gold);

    public static string GetSearchQuery(DependencyObject obj) => (string)obj.GetValue(SearchQueryProperty);

    public static void SetSearchQuery(DependencyObject obj, string value) => obj.SetValue(SearchQueryProperty, value);

    private static void OnSearchQueryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock textBlock)
        {
            return;
        }

        AttachTextChangedListener(textBlock);
        ApplyHighlight(textBlock, (string)args.NewValue);
    }

    private static void AttachTextChangedListener(TextBlock textBlock)
    {
        if ((long)textBlock.GetValue(HighlightTokenProperty) != 0)
        {
            return;
        }

        long token = textBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            ApplyHighlight(textBlock, GetSearchQuery(textBlock));
        });

        textBlock.SetValue(HighlightTokenProperty, token);
    }

    private static void ApplyHighlight(TextBlock textBlock, string? query)
    {
        textBlock.TextHighlighters.Clear();

        if (string.IsNullOrWhiteSpace(textBlock.Text) || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        foreach ((int start, int length) in NoteService.FindWordPrefixMatchRanges(textBlock.Text, query))
        {
            var range = new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = start, Length = length };
            var highlighter = new TextHighlighter { Background = HighlightBrush };
            highlighter.Ranges.Add(range);
            textBlock.TextHighlighters.Add(highlighter);
        }
    }
}
