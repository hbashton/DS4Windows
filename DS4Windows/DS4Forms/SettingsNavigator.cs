using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DS4WinWPF.DS4Forms;

internal static class SettingsSearchMatcher
{
    internal static bool HasQuery(string query) => Normalize(query).Length != 0;

    internal static bool Matches(string query, string text)
    {
        string compact = Normalize(text).Replace(" ", "");
        return Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => compact.Contains(word, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (char character in (value ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return result.ToString().Trim();
    }
}

internal sealed class SettingsNavigator
{
    private sealed class Section
    {
        internal RadioButton Navigation;
        internal StackPanel Page;
        internal FrameworkElement[] Cards;
        internal TextBlock SearchHeading;
    }

    private readonly Section[] sections;
    private readonly Dictionary<Expander, bool> expansionBeforeSearch = new();
    internal bool IsSearching { get; private set; }

    internal SettingsNavigator(Grid pages, IEnumerable<RadioButton> navigation)
    {
        // Search can reveal several sections at once; stack them in separate
        // auto-sized rows rather than overlaying the normal category pages.
        int row = 0;
        sections = navigation.Select(button =>
        {
            var page = pages.Children.OfType<StackPanel>().Single(panel => panel.Name == button.Name.Replace("Nav", "Page"));
            pages.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(page, row++);
            var heading = new TextBlock { Text = button.Content + " — search results", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            heading.SetResourceReference(FrameworkElement.StyleProperty, "BridgeSectionTitleStyle");
            var cards = page.Children.OfType<FrameworkElement>().Where(child => child is Border or Expander).ToArray();
            page.Children.Insert(0, heading);
            return new Section { Navigation = button, Page = page, Cards = cards, SearchHeading = heading };
        }).ToArray();
    }

    internal void RestoreSection(string id)
    {
        string normalized = DS4Windows.SettingsSectionInfo.Normalize(id);
        sections.Single(section => (string)section.Navigation.Tag == normalized).Navigation.IsChecked = true;
    }

    internal int SetSearch(string query)
    {
        bool searching = SettingsSearchMatcher.HasQuery(query);
        if (searching && !IsSearching)
        {
            foreach (Expander expander in sections.SelectMany(section => section.Cards).SelectMany(LogicalElements).OfType<Expander>())
                expansionBeforeSearch[expander] = expander.IsExpanded;
        }
        foreach (var entry in expansionBeforeSearch) entry.Key.IsExpanded = entry.Value;
        IsSearching = searching;
        int total = 0;
        foreach (Section section in sections)
        {
            int matching = 0;
            foreach (FrameworkElement card in section.Cards)
            {
                if (!searching)
                {
                    card.ClearValue(UIElement.VisibilityProperty);
                    continue;
                }
                string text = section.Navigation.Content + " " + CollectText(card);
                bool matches = SettingsSearchMatcher.Matches(query, text);
                card.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                if (!matches) continue;
                matching++;
                foreach (Expander expander in LogicalElements(card).OfType<Expander>()) expander.IsExpanded = true;
            }
            section.SearchHeading.Visibility = searching && matching > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (searching) section.Page.Visibility = matching > 0 ? Visibility.Visible : Visibility.Collapsed;
            else section.Page.ClearValue(UIElement.VisibilityProperty);
            total += matching;
        }
        if (!searching) expansionBeforeSearch.Clear();
        return total;
    }

    private static string CollectText(DependencyObject root)
    {
        var text = new StringBuilder();
        foreach (DependencyObject element in LogicalElements(root))
        {
            if (element is TextBlock block) text.Append(' ').Append(block.Text);
            if (element is ContentControl content && content.Content is string label) text.Append(' ').Append(label);
            if (element is HeaderedContentControl header && header.Header is string title) text.Append(' ').Append(title);
            if (element is FrameworkElement framework && framework.ToolTip is string tip) text.Append(' ').Append(tip);
            if (element is TextBox input) text.Append(' ').Append(input.Text);
        }
        return text.ToString();
    }

    private static IEnumerable<DependencyObject> LogicalElements(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject element in LogicalElements(dependency)) yield return element;
    }
}
