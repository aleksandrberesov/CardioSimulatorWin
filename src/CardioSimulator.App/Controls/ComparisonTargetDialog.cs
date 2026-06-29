using System.Collections.Generic;
using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Dialog for choosing one comparison pane's target: a pathology (left list) and a lead
/// (right grid). Port of the Android <c>ComparisonTargetDialog</c>. Returns the chosen
/// <see cref="ComparisonTarget"/>, or null if cancelled.
/// </summary>
public static class ComparisonTargetDialog
{
    public static async Task<ComparisonTarget?> ShowAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<PathologyEntry> rhythms,
        Language language,
        string? initialPathologyId,
        Lead? initialLead)
    {
        string? selectedId = initialPathologyId;
        Lead? selectedLead = initialLead;

        // Left column: pathology list.
        var pathologyList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 280,
            MaxHeight = 360,
        };
        foreach (var r in rhythms)
        {
            var label = language == Language.RU ? (r.NameRu ?? r.TitleEn) : r.TitleEn;
            var item = new ListViewItem { Content = label, Tag = r.Id };
            pathologyList.Items.Add(item);
            if (r.Id == initialPathologyId) pathologyList.SelectedItem = item;
        }

        // Right column: lead picker. The 12 standard leads are laid out as a fixed 2-column × 6-row
        // grid of toggle buttons so they all fit the dialog without overflowing into a single row.
        const int leadColumns = 2;
        var leadButtons = new List<ToggleButton>();
        var leadGrid = new Grid { ColumnSpacing = 6, RowSpacing = 6, MinWidth = 160 };
        leadGrid.ColumnDefinitions.Add(new ColumnDefinition());
        leadGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var leadRows = (Leads.All.Count + leadColumns - 1) / leadColumns;
        for (var i = 0; i < leadRows; i++)
            leadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < Leads.All.Count; i++)
        {
            var lead = Leads.All[i];
            var button = new ToggleButton
            {
                Content = lead.ToString(),
                Tag = lead,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsChecked = initialLead == lead,
            };
            Grid.SetRow(button, i / leadColumns);
            Grid.SetColumn(button, i % leadColumns);
            leadButtons.Add(button);
            leadGrid.Children.Add(button);
        }

        var leftColumn = new StackPanel { Spacing = 8 };
        leftColumn.Children.Add(new TextBlock
        {
            Text = AppStrings.EditorRhythmsTitle,
            FontWeight = FontWeights.SemiBold,
        });
        leftColumn.Children.Add(pathologyList);

        var rightColumn = new StackPanel { Spacing = 8 };
        rightColumn.Children.Add(new TextBlock
        {
            Text = AppStrings.CompareLeadLabel,
            FontWeight = FontWeights.SemiBold,
        });
        rightColumn.Children.Add(leadGrid);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        content.Children.Add(leftColumn);
        content.Children.Add(rightColumn);

        var dialog = new ContentDialog
        {
            Title = AppStrings.CompareTargetTitle,
            Content = content,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = xamlRoot,
            IsPrimaryButtonEnabled = initialPathologyId is not null && initialLead is not null,
        };

        pathologyList.SelectionChanged += (_, _) =>
        {
            selectedId = (pathologyList.SelectedItem as ListViewItem)?.Tag as string;
            dialog.IsPrimaryButtonEnabled = selectedId is not null && selectedLead is not null;
        };
        foreach (var button in leadButtons)
        {
            var captured = button;
            captured.Click += (_, _) =>
            {
                selectedLead = captured.Tag as Lead?;
                // Single-select: keep the clicked lead checked and clear the rest.
                foreach (var other in leadButtons) other.IsChecked = ReferenceEquals(other, captured);
                dialog.IsPrimaryButtonEnabled = selectedId is not null && selectedLead is not null;
            };
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if (selectedId is null || selectedLead is null) return null;
        return new ComparisonTarget(selectedId, selectedLead.Value);
    }
}
