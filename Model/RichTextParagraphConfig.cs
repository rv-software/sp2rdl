namespace sp2rdlGenExtension.Model;

internal sealed class RichTextParagraphConfig
{
    public string Text { get; set; } = string.Empty;

    public string TextAlign { get; set; } = "Left";

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public double FontSizeInPoints { get; set; } = 9.0d;

    public string ListStyle { get; set; } = "None";
}
