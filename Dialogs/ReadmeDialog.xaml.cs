using System.Windows;

namespace sp2rdlGenExtension.Dialogs;

public partial class ReadmeDialog : Window
{
    public ReadmeDialog(string title, string documentText)
    {
        InitializeComponent();
        Title = title;
        TxtDocument.Text = documentText;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
