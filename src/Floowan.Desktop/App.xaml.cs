using System.Windows;

namespace Floowan.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Localization.Loc.Initialize(Localization.Loc.CreateDefault());
        base.OnStartup(e);
    }
}
