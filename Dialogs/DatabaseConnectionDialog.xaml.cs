using System.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
public partial class DatabaseConnectionDialog : Window
{
    private bool updatingFields;

    public string ConnectionString { get; private set; } = string.Empty;

    public DatabaseConnectionDialog(string solutionDirectory, string? currentConnectionString)
    {
        InitializeComponent();

        CmbDiscoveredConnections.ItemsSource = ConnectionStringDiscoveryService.Discover(solutionDirectory);
        CmbDatabase.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(CmbDatabase_TextChanged));
        SetAuthenticationMode("windows");
        ChkTrustServerCertificate.IsChecked = true;

        if (!string.IsNullOrWhiteSpace(currentConnectionString))
        {
            ApplyConnectionString(currentConnectionString);
        }
        else
        {
            RefreshAuthenticationFields();
            RefreshConnectionStringFromFields();
        }
    }

    private void CmbDiscoveredConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbDiscoveredConnections.SelectedItem is not DiscoveredConnectionString selected)
        {
            return;
        }

        LblConnectionSource.Text = selected.SourceFile;
        ApplyConnectionString(selected.Value);
    }

    private void CmbAuthentication_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAuthenticationFields();
        RefreshConnectionStringFromFields();
    }

    private void ConnectionField_Changed(object sender, RoutedEventArgs e)
        => RefreshConnectionStringFromFields();

    private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        => RefreshConnectionStringFromFields();

    private void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshConnectionStringFromFields();

    private void CmbDatabase_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshConnectionStringFromFields();

    private void CmbDatabase_DropDownOpened(object sender, EventArgs e)
        => LoadDatabaseNames();

    private void TxtConnectionString_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingFields)
        {
            return;
        }

        LblStatus.Text = string.Empty;
        ApplyConnectionString(TxtConnectionString.Text);
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TestButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            using var connection = new SqlConnection(TxtConnectionString.Text);
            connection.Open();
            LblStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            var message = $"Connection successful. Database: {connection.Database}.";
            LblStatus.Text = message;
            MessageBox.Show(this, message, "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LblStatus.Foreground = System.Windows.Media.Brushes.Orange;
            LblStatus.Text = ex.Message;
            MessageBox.Show(this, $"Connection failed:\n\n{ex.Message}", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            TestButton.IsEnabled = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtConnectionString.Text))
        {
            MessageBox.Show(this, "Connection string is required.", "sp2rdlGenExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectionString = TxtConnectionString.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyConnectionString(string connectionString)
    {
        try
        {
            updatingFields = true;
            var builder = new SqlConnectionStringBuilder(connectionString);
            TxtServer.Text = builder.DataSource;
            CmbDatabase.Text = builder.InitialCatalog;
            TxtUser.Text = builder.UserID;
            TxtPassword.Password = builder.Password;
            ChkTrustServerCertificate.IsChecked = builder.TrustServerCertificate;
            SetAuthenticationMode(UsesWindowsAuthentication(builder) ? "windows" : "sql");
            TxtConnectionString.Text = NormalizeConnectionString(builder);
            LblStatus.Text = string.Empty;
        }
        catch
        {
            TxtConnectionString.Text = connectionString;
        }
        finally
        {
            updatingFields = false;
            RefreshAuthenticationFields();
        }
    }

    private void RefreshAuthenticationFields()
    {
        var usesSqlAuthentication = string.Equals(GetAuthenticationMode(), "sql", StringComparison.OrdinalIgnoreCase);
        TxtUser.IsEnabled = usesSqlAuthentication;
        TxtPassword.IsEnabled = usesSqlAuthentication;
    }

    private void RefreshConnectionStringFromFields()
    {
        if (updatingFields)
        {
            return;
        }

        try
        {
            updatingFields = true;
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = TxtServer.Text.Trim(),
                InitialCatalog = CmbDatabase.Text.Trim(),
                TrustServerCertificate = ChkTrustServerCertificate.IsChecked == true
            };

            if (string.Equals(GetAuthenticationMode(), "sql", StringComparison.OrdinalIgnoreCase))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = TxtUser.Text.Trim();
                builder.Password = TxtPassword.Password;
            }
            else
            {
                builder.IntegratedSecurity = true;
                builder.Remove("User ID");
                builder.Remove("UID");
                builder.Remove("Password");
                builder.Remove("Pwd");
                builder.Remove("Authentication");
            }

            TxtConnectionString.Text = builder.ConnectionString;
            LblStatus.Text = string.Empty;
        }
        finally
        {
            updatingFields = false;
        }
    }

    private void LoadDatabaseNames()
    {
        if (updatingFields || string.IsNullOrWhiteSpace(TxtServer.Text))
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var currentDatabase = CmbDatabase.Text.Trim();
            var builder = BuildServerConnectionStringBuilder();
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "select [name] from sys.databases where [state] = 0 order by [name]";

            var databases = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                databases.Add(reader.GetString(0));
            }

            updatingFields = true;
            CmbDatabase.ItemsSource = databases;
            CmbDatabase.Text = currentDatabase;
            updatingFields = false;

            LblStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            LblStatus.Text = databases.Count == 0
                ? "No databases found on this SQL Server instance."
                : $"{databases.Count} database(s) loaded.";
        }
        catch (Exception ex)
        {
            LblStatus.Foreground = System.Windows.Media.Brushes.Orange;
            LblStatus.Text = $"Could not load databases: {ex.Message}";
        }
        finally
        {
            updatingFields = false;
            Mouse.OverrideCursor = null;
        }
    }

    private SqlConnectionStringBuilder BuildServerConnectionStringBuilder()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = TxtServer.Text.Trim(),
            InitialCatalog = "master",
            TrustServerCertificate = ChkTrustServerCertificate.IsChecked == true
        };

        if (string.Equals(GetAuthenticationMode(), "sql", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = TxtUser.Text.Trim();
            builder.Password = TxtPassword.Password;
        }
        else
        {
            builder.IntegratedSecurity = true;
            builder.Remove("User ID");
            builder.Remove("UID");
            builder.Remove("Password");
            builder.Remove("Pwd");
            builder.Remove("Authentication");
        }

        return builder;
    }

    private string GetAuthenticationMode()
        => (CmbAuthentication.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "windows";

    private void SetAuthenticationMode(string mode)
    {
        foreach (var item in CmbAuthentication.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                CmbAuthentication.SelectedItem = item;
                return;
            }
        }

        CmbAuthentication.SelectedIndex = 0;
    }

    private static bool UsesWindowsAuthentication(SqlConnectionStringBuilder builder)
    {
        if (builder.IntegratedSecurity)
        {
            return true;
        }

        return builder.TryGetValue("Authentication", out var authentication)
            && authentication is not null
            && authentication.ToString()?.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeConnectionString(SqlConnectionStringBuilder builder)
    {
        if (UsesWindowsAuthentication(builder))
        {
            builder.IntegratedSecurity = true;
            builder.Remove("User ID");
            builder.Remove("UID");
            builder.Remove("Password");
            builder.Remove("Pwd");
            builder.Remove("Authentication");
        }

        return builder.ConnectionString;
    }
}
#pragma warning restore CS0618
