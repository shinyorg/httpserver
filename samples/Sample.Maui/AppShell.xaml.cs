namespace Sample.Maui;

public partial class AppShell : Shell
{
    // The page comes from the container rather than being newed up in XAML: it needs the view
    // model, which needs the server and the tunnel.
    public AppShell(MainPage page)
    {
        this.InitializeComponent();

        this.Items.Add(new ShellContent { Content = page, Title = "Share" });
    }
}
