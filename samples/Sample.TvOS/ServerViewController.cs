using UIKit;

namespace Sample.TvOS;

/// <summary>
/// The screen. A TV has no browser and no address bar, so the one thing this has to do well is say
/// what to type on the device that does.
/// </summary>
public class ServerViewController : UIViewController
{
    readonly UILabel status = Label(58, UIColor.White);
    readonly UILabel detail = Label(34, UIColor.LightGray);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        this.View!.BackgroundColor = UIColor.FromRGB(18, 18, 20);

        var stack = new UIStackView([this.status, this.detail])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 28,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        this.View.AddSubview(stack);

        NSLayoutConstraint.ActivateConstraints([
            stack.CenterXAnchor.ConstraintEqualTo(this.View.CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(this.View.CenterYAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(this.View.WidthAnchor, 0.8f)
        ]);

        this.Update(false, [], null);
    }

    public void Update(bool running, IReadOnlyList<string> urls, Exception? error)
    {
        if (error is not null)
        {
            this.status.Text = "Could not start";
            this.status.TextColor = UIColor.FromRGB(255, 105, 97);
            this.detail.Text = error.Message;
            return;
        }

        this.status.Text = running ? "Serving" : "Stopped";
        this.status.TextColor = running ? UIColor.FromRGB(120, 220, 140) : UIColor.LightGray;

        this.detail.Text = running
            ? urls.Count > 0
                ? "Open on another device:\n" + String.Join("\n", urls)
                // Bound and listening, but the addresses came back empty. Worth saying plainly:
                // this is what a missing NSLocalNetworkUsageDescription tends to look like from
                // inside the app, which is to say almost exactly like everything working.
                : "Listening, but this device reports no LAN address.\nCheck NSLocalNetworkUsageDescription in Info.plist."
            : "The app is in the background. It starts again on resume.";
    }

    static UILabel Label(int size, UIColor color)
        => new()
        {
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            TextColor = color,
            Font = UIFont.SystemFontOfSize(size, UIFontWeight.Medium)!
        };
}
