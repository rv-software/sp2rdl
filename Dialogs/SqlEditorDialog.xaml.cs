using System.Collections;
using System.Windows;

namespace sp2rdlGenExtension.Dialogs;

public partial class SqlEditorDialog : Window
{
    private readonly Func<Task<string?>>? suggestSqlAsync;
    private readonly Func<string, Task<IEnumerable?>>? previewSqlAsync;

    public string SqlText { get; private set; } = string.Empty;

    public SqlEditorDialog(
        string title,
        string? sqlText,
        string hint,
        Func<Task<string?>>? suggestSqlAsync = null,
        Func<string, Task<IEnumerable?>>? previewSqlAsync = null)
    {
        this.suggestSqlAsync = suggestSqlAsync;
        this.previewSqlAsync = previewSqlAsync;
        InitializeComponent();
        TxtTitle.Text = title;
        TxtHint.Text = hint;
        TxtSql.Text = sqlText ?? string.Empty;
        SuggestButton.IsEnabled = suggestSqlAsync is not null;
        TestButton.IsEnabled = previewSqlAsync is not null;
        TxtSql.Focus();
        TxtSql.CaretIndex = TxtSql.Text.Length;
    }

    private void SuggestButton_Click(object sender, RoutedEventArgs e)
        => _ = SuggestSqlAsync();

    private async Task SuggestSqlAsync()
    {
        if (this.suggestSqlAsync is null)
        {
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            var suggestedSql = await this.suggestSqlAsync();
            if (string.IsNullOrWhiteSpace(suggestedSql))
            {
                MessageBox.Show(this, "No SQL suggestion was found.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtSql.Text = suggestedSql;
            TxtSql.Focus();
            TxtSql.CaretIndex = TxtSql.Text.Length;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not suggest SQL:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
        => _ = PreviewSqlAsync();

    private async Task PreviewSqlAsync()
    {
        if (this.previewSqlAsync is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtSql.Text))
        {
            MessageBox.Show(this, "SQL is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            GridPreview.ItemsSource = await this.previewSqlAsync(TxtSql.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not test SQL:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SqlText = TxtSql.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
