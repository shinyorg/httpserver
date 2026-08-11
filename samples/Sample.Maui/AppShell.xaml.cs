namespace Sample.Maui;

public partial class AppShell : ShinyShell
{
    // No constructor injection any more: pages come from the container when the tab is realised,
    // and Shiny Shell attaches the view model registered against each one.
    public AppShell() => this.InitializeComponent();
}
