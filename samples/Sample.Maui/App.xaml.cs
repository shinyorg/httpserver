namespace Sample.Maui;

public partial class App : Application
{
    readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        this.InitializeComponent();
        this.services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(this.services.GetRequiredService<AppShell>());
}
