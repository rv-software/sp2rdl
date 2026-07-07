using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

public partial class SelectParameterDefinitionDialog : Window
{
    private readonly List<ReportingParameterDefinition> allDefinitions;
    private readonly ObservableCollection<ReportingParameterDefinition> visibleDefinitions = new();

    internal ReportingParameterDefinition? SelectedDefinition { get; private set; }

    internal SelectParameterDefinitionDialog(
        IEnumerable<ReportingParameterDefinition> definitions,
        string? currentDefinitionName,
        ResourceDictionary inheritedResources)
    {
        CopyResources(inheritedResources, Resources);
        this.allDefinitions = definitions
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        InitializeComponent();
        GridDefinitions.ItemsSource = this.visibleDefinitions;
        RefreshVisibleDefinitions();

        if (!string.IsNullOrWhiteSpace(currentDefinitionName))
        {
            GridDefinitions.SelectedItem = this.visibleDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Name, currentDefinitionName, StringComparison.OrdinalIgnoreCase));
            GridDefinitions.ScrollIntoView(GridDefinitions.SelectedItem);
        }
    }

    /// <summary>
    /// Copies the owner dialog resources so this picker follows the same extension color scheme.
    /// </summary>
    private static void CopyResources(ResourceDictionary source, ResourceDictionary target)
    {
        foreach (DictionaryEntry resource in source)
        {
            target[resource.Key] = resource.Value;
        }

        foreach (var mergedDictionary in source.MergedDictionaries)
        {
            target.MergedDictionaries.Add(mergedDictionary);
        }
    }

    /// <summary>
    /// Refreshes the visible definition list when the user changes the filter.
    /// </summary>
    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshVisibleDefinitions();

    /// <summary>
    /// Accepts the selected definition on double-click.
    /// </summary>
    private void DefinitionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => AcceptSelection();

    /// <summary>
    /// Accepts the selected definition and closes the dialog.
    /// </summary>
    private void OkButton_Click(object sender, RoutedEventArgs e)
        => AcceptSelection();

    /// <summary>
    /// Closes the dialog without selecting a definition.
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Rebuilds the filtered list from all loaded definitions.
    /// </summary>
    private void RefreshVisibleDefinitions()
    {
        var filter = TxtFilter?.Text?.Trim() ?? string.Empty;
        this.visibleDefinitions.Clear();

        foreach (var definition in this.allDefinitions.Where(definition => MatchesFilter(definition, filter)))
        {
            this.visibleDefinitions.Add(definition);
        }

        TxtStatus.Text = $"{this.visibleDefinitions.Count} of {this.allDefinitions.Count} definition(s)";
    }

    /// <summary>
    /// Returns true when a definition matches the current quick filter.
    /// </summary>
    private static bool MatchesFilter(ReportingParameterDefinition definition, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return Contains(definition.Name, filter)
            || Contains(definition.Label, filter)
            || Contains(definition.EntityKey, filter)
            || Contains(definition.DisplayFieldTemplate, filter);
    }

    /// <summary>
    /// Performs a case-insensitive contains check for optional text.
    /// </summary>
    private static bool Contains(string? value, string filter)
        => value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Stores the selected row as the dialog result.
    /// </summary>
    private void AcceptSelection()
    {
        if (GridDefinitions.SelectedItem is not ReportingParameterDefinition definition)
        {
            MessageBox.Show(this, "Select one parameter definition first.", "Select definition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedDefinition = definition;
        DialogResult = true;
        Close();
    }
}
