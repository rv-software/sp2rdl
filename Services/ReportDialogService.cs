using System.Windows.Interop;
using sp2rdlGenExtension.Dialogs;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal sealed class ReportDialogService
{
    private readonly SqlIntrospector sqlIntrospector;

    public ReportDialogService(SqlIntrospector sqlIntrospector)
    {
        this.sqlIntrospector = sqlIntrospector;
    }

    public ReportGenerationRequest? ShowSetupDialog(string solutionDirectory, IntPtr ownerHwnd)
    {
        ReportGenerationRequest? request = null;
        Exception? failure = null;
        using var waitHandle = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new ReportSetupDialog(solutionDirectory, this.sqlIntrospector);
                DialogThemeService.Apply(dialog, ownerHwnd);
                if (ownerHwnd != IntPtr.Zero)
                {
                    new WindowInteropHelper(dialog).Owner = ownerHwnd;
                }

                dialog.ShowActivated = true;
                dialog.SourceInitialized += (_, _) =>
                {
                    dialog.Activate();
                    dialog.Topmost = true;
                    dialog.Topmost = false;
                    dialog.Focus();
                };

                if (dialog.ShowDialog() == true)
                {
                    request = dialog.Request;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                waitHandle.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        waitHandle.Wait();

        if (failure is not null)
        {
            throw failure;
        }

        return request;
    }

    public string? ShowConnectionDialog(
        string solutionDirectory,
        string? currentConnectionString,
        IntPtr ownerHwnd)
    {
        string? connectionString = null;
        Exception? failure = null;
        using var waitHandle = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new DatabaseConnectionDialog(solutionDirectory, currentConnectionString);
                DialogThemeService.Apply(dialog, ownerHwnd);
                if (ownerHwnd != IntPtr.Zero)
                {
                    new WindowInteropHelper(dialog).Owner = ownerHwnd;
                }

                dialog.ShowActivated = true;
                dialog.SourceInitialized += (_, _) =>
                {
                    dialog.Activate();
                    dialog.Topmost = true;
                    dialog.Topmost = false;
                    dialog.Focus();
                };

                if (dialog.ShowDialog() == true)
                {
                    connectionString = dialog.ConnectionString;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                waitHandle.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        waitHandle.Wait();

        if (failure is not null)
        {
            throw failure;
        }

        return connectionString;
    }
}
