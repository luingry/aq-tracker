using System.Windows;
using System.Windows.Media.Animation;
using SwissTechKnife.Sdk;

namespace AqTracker.Module;

public partial class ModulePage
{
    private readonly HostedAppSupervisor _supervisor;
    private readonly IModuleContext _context;
    private HostedAppState? _shown;

    internal ModulePage(HostedAppSupervisor supervisor, IModuleContext context)
    {
        _supervisor = supervisor;
        _context = context;
        InitializeComponent();
        _supervisor.StateChanged += OnStateChanged;
        Apply(_supervisor.State);
    }

    private void OnStateChanged(HostedAppState state) =>
        _context.Dispatcher.BeginInvoke(new Action(() => Apply(_supervisor.State)));

    private void Apply(HostedAppState state)
    {
        if (_shown == state)
            return;
        var first = _shown is null;
        _shown = state;

        var (label, detail, soft, strong) = state switch
        {
            HostedAppState.Running => ("Em execução",
                "O AQ Tracker está acompanhando as cotas e os agentes do Codex e do Claude.",
                "Brush.Success.Soft", "Brush.Success"),
            HostedAppState.Starting => ("Iniciando",
                "O AQ Tracker está sendo iniciado.",
                "Brush.Info.Soft", "Brush.Info"),
            HostedAppState.RunningOutside => ("Aberto fora do SwissTechKnife",
                "Outro AQ Tracker já está aberto e continua acompanhando as cotas. O módulo assume assim que ele for fechado.",
                "Brush.Warning.Soft", "Brush.Warning"),
            HostedAppState.Failed => ("Parado",
                "O AQ Tracker fechou inesperadamente várias vezes. Mostre o widget para tentar de novo.",
                "Brush.Danger.Soft", "Brush.Danger"),
            _ => ("Parado",
                "O AQ Tracker não está em execução. Mostre o widget para iniciá-lo.",
                "Brush.Surface4", "Brush.Text.Secondary"),
        };

        StatusText.Text = label;
        StatusDetail.Text = detail;
        StatusChip.SetResourceReference(BackgroundProperty, soft);
        StatusText.SetResourceReference(ForegroundProperty, strong);

        if (!first)
            FadeIn(StatusChip, StatusDetail);
    }

    private void FadeIn(params UIElement[] elements)
    {
        var duration = TryFindResource("Motion.Normal") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(200));
        foreach (var element in elements)
        {
            var fade = new DoubleAnimation(0.4, 1, duration) { EasingFunction = TryFindResource("Ease.Out") as IEasingFunction };
            fade.Freeze();
            element.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void ShowButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.Send(TrackerCommand.Show);

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.Send(TrackerCommand.Settings);

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => _supervisor.Send(TrackerCommand.Refresh);
}
