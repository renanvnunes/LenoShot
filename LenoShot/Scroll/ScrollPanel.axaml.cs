using Avalonia.Input;

namespace LenoShot.Scroll;

public partial class ScrollPanel : FloatingWindow
{
    public event EventHandler? DoneRequested;
    public event EventHandler? CancelRequested;

    public ScrollPanel()
    {
        InitializeComponent();
        DoneButton.Click += (_, _) => DoneRequested?.Invoke(this, EventArgs.Empty);
        CancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetStatus(string status, string detail, int percent = 0)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        PercentText.Text = $"{percent}%";
        Progress.Value = percent;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            CancelRequested?.Invoke(this, EventArgs.Empty);
        else if (e.Key == Key.Enter)
            DoneRequested?.Invoke(this, EventArgs.Empty);
    }
}
