using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
public partial class ImportReportParametersDialog : Window
{
    private readonly string connectionString;
    private readonly ReportingMetadataReader metadataReader;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly ObservableCollection<ReportingParameterImportCandidate> parameters = new();

    internal IReadOnlyList<ReportingParameterImportCandidate> SelectedParameters { get; private set; } = [];

    internal bool UpdateExistingParameters { get; private set; }

    internal ImportReportParametersDialog(string connectionString, ReportingMetadataReader metadataReader)
    {
        this.connectionString = connectionString;
        this.metadataReader = metadataReader;
        InitializeComponent();
        GridParameters.ItemsSource = this.parameters;
        Loaded += ImportReportParametersDialog_Loaded;
        Closed += ImportReportParametersDialog_Closed;
    }

    /// <summary>
    /// Loads available report versions when the dialog opens.
    /// </summary>
    private void ImportReportParametersDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _ = LoadReportVersionsAsync();
    }

    /// <summary>
    /// Cancels pending database reads when the dialog is closed.
    /// </summary>
    private void ImportReportParametersDialog_Closed(object? sender, EventArgs e)
    {
        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Reloads report versions from the Reporting metadata database.
    /// </summary>
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadReportVersionsAsync();
    }

    /// <summary>
    /// Loads parameters for the selected report version.
    /// </summary>
    private void ReportVersions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbReportVersions.SelectedValue is int versionId)
        {
            _ = LoadParametersAsync(versionId);
        }
    }

    /// <summary>
    /// Marks every displayed parameter for import.
    /// </summary>
    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var parameter in this.parameters)
        {
            parameter.IsSelected = true;
        }

        GridParameters.Items.Refresh();
    }

    /// <summary>
    /// Clears every displayed parameter selection.
    /// </summary>
    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var parameter in this.parameters)
        {
            parameter.IsSelected = false;
        }

        GridParameters.Items.Refresh();
    }

    /// <summary>
    /// Confirms the selected parameter rows and update mode.
    /// </summary>
    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        GridParameters.CommitEdit(DataGridEditingUnit.Cell, true);
        GridParameters.CommitEdit(DataGridEditingUnit.Row, true);

        SelectedParameters = this.parameters
            .Where(parameter => parameter.IsSelected)
            .OrderBy(parameter => parameter.CreationOrder <= 0 ? int.MaxValue : parameter.CreationOrder)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (SelectedParameters.Count == 0)
        {
            MessageBox.Show(this, "Select at least one parameter to import.", "Load params from report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateExistingParameters = ChkUpdateExisting.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Reads report version choices and selects the first available version.
    /// </summary>
    private async Task LoadReportVersionsAsync()
    {
        try
        {
            TxtStatus.Text = "Loading report versions...";
            CmbReportVersions.IsEnabled = false;
            var versions = await this.metadataReader.ListReportVersionsAsync(this.connectionString, cancellationTokenSource.Token);
            CmbReportVersions.ItemsSource = versions;
            CmbReportVersions.SelectedIndex = versions.Count > 0 ? 0 : -1;
            TxtStatus.Text = versions.Count == 0 ? "No active report versions found." : $"{versions.Count} active report version(s) found.";
        }
        catch (Exception ex) when (ex is System.Data.SqlClient.SqlException or InvalidOperationException)
        {
            TxtStatus.Text = "Could not load report versions.";
            MessageBox.Show(this, ex.Message, "Load params from report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CmbReportVersions.IsEnabled = true;
        }
    }

    /// <summary>
    /// Reads importable parameters for one report version.
    /// </summary>
    private async Task LoadParametersAsync(int versionId)
    {
        try
        {
            TxtStatus.Text = "Loading parameters...";
            this.parameters.Clear();
            var rows = await this.metadataReader.ReadParametersForVersionAsync(this.connectionString, versionId, cancellationTokenSource.Token);
            foreach (var row in rows)
            {
                row.IsSelected = true;
                this.parameters.Add(row);
            }

            TxtStatus.Text = $"{this.parameters.Count} parameter(s) loaded.";
        }
        catch (Exception ex) when (ex is System.Data.SqlClient.SqlException or InvalidOperationException)
        {
            TxtStatus.Text = "Could not load parameters.";
            MessageBox.Show(this, ex.Message, "Load params from report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
#pragma warning restore CS0618
