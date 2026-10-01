using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using AqTracker.Core;
using Forms = System.Windows.Forms;

namespace AqTracker;

public partial class MainWindow
{
    private readonly ClaudeSessionActivityService _claudeActivity = new();
    private ClaudeUsageClient _claudeClient = null!;
    private bool _wasClaudeEngaged;
    private string _pendingClaudeAccentColor = "#D97757";
    private CancellationTokenSource? _claudeLoginCancellation;
    private ClaudeOAuthAttempt? _manualClaudeAttempt;
    private DateTimeOffset _manualClaudeStartedAt;

    private void InitializeClaude()
    {
        _pendingClaudeAccentColor = _settings.ClaudeAccentColor;
        _claudeClient = new ClaudeUsageClient(new ClaudeTokenStore(Path.Combine(_store.DirectoryPath, "claude-tokens.dat")),
            Path.Combine(_store.DirectoryPath, "claude-quota.json"), CurrentVersion);
        _viewModel.SetClaudeEnabled(_settings.ClaudeProfileEnabled);
        _viewModel.ApplyClaude(_claudeClient.Snapshot, _claudeClient.State, true);
    }

    private IReadOnlyList<CompletedAgentWork> VisibleUnreadWorks() => _unreadAgentWorks
        .Where(work => work.Provider == AgentProvider.Codex || _settings.ClaudeProfileEnabled).ToArray();

    private async Task RefreshClaudeAsync(bool onlyIfStale = false)
    {
        if (_demo || !_settings.ClaudeProfileEnabled || _shutdown.IsCancellationRequested) return;
        try
        {
            await _claudeClient.RefreshAsync(_shutdown.Token, onlyIfStale);
            if (!_shutdown.IsCancellationRequested)
                _viewModel.ApplyClaude(_claudeClient.Snapshot, _claudeClient.State, _claudeClient.IsStale);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { SanitizedLogger.Write("Claude poll error: " + error.GetType().Name); }
    }

    private void ApplyProfileTheme(string theme, string codex, string claude)
    {
        ThemeManager.Apply(theme, codex, claude);
        _viewModel.SetProfileColors(theme, codex, claude);
    }

    private async void ConnectClaude(object sender, RoutedEventArgs e)
    {
        if (!System.Net.HttpListener.IsSupported)
        {
            ConnectClaudeWithCode(sender, e);
            ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeManualFallback");
            return;
        }
        CancelClaudeLogin();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _claudeLoginCancellation = cancellation;
        ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeWaitingBrowser");
        SetClaudeLoginBusy(true);
        var useManualFallback = false;
        try
        {
            using var callback = new ClaudeOAuthCallback();
            Process.Start(new ProcessStartInfo(callback.Attempt.AuthorizeUri.AbsoluteUri) { UseShellExecute = true });
            var code = await callback.WaitForCodeAsync(LocalizationManager.Text("ClaudeBrowserDone"), LocalizationManager.Text("ClaudeInvalidCallback"), cancellation.Token);
            await _claudeClient.ExchangeCodeAsync(callback.Attempt, code, cancellation.Token);
            await RefreshClaudeAsync();
            ClaudeLoginFeedback.Text = "";
        }
        catch (OperationCanceledException) { if (!_shutdown.IsCancellationRequested) ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginCancelled"); }
        catch (Exception error) when (error is System.Net.HttpListenerException or PlatformNotSupportedException)
        { useManualFallback = true; }
        catch (Exception error)
        {
            ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginFailed");
            SanitizedLogger.Write("Claude login error: " + error.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_claudeLoginCancellation, cancellation))
            {
                _claudeLoginCancellation = null;
                SetClaudeLoginBusy(false);
                _viewModel.ApplyClaude(_claudeClient.Snapshot, _claudeClient.State, _claudeClient.IsStale);
            }
            cancellation.Dispose();
        }
        if (useManualFallback && !_shutdown.IsCancellationRequested && SettingsPanel.Visibility == Visibility.Visible)
        {
            ConnectClaudeWithCode(sender, e);
            ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeManualFallback");
        }
    }

    private void ConnectClaudeWithCode(object sender, RoutedEventArgs e)
    {
        CancelClaudeLogin();
        _manualClaudeAttempt = new ClaudeOAuthAttempt(ClaudeOAuthConstants.ManualRedirect);
        _manualClaudeStartedAt = DateTimeOffset.UtcNow;
        _viewModel.IsClaudeManualEntryOpen = true;
        ClaudeCodeBox.Clear();
        ClaudeLoginFeedback.Text = "";
        try { Process.Start(new ProcessStartInfo(_manualClaudeAttempt.AuthorizeUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { _manualClaudeAttempt = null; _viewModel.IsClaudeManualEntryOpen = false; ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginFailed"); SanitizedLogger.Write("Claude browser error: " + error.GetType().Name); }
    }

    private async void SubmitClaudeCode(object sender, RoutedEventArgs e)
    {
        if (_manualClaudeAttempt is not { } attempt || DateTimeOffset.UtcNow - _manualClaudeStartedAt >= TimeSpan.FromMinutes(5) ||
            !attempt.TryParseManualCode(ClaudeCodeBox.Text, out var code))
        {
            ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeInvalidCode");
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _claudeLoginCancellation = cancellation;
        SetClaudeLoginBusy(true);
        ClaudeCodeBox.Clear();
        try
        {
            await _claudeClient.ExchangeCodeAsync(attempt, code, cancellation.Token);
            await RefreshClaudeAsync();
            _manualClaudeAttempt = null;
            _viewModel.IsClaudeManualEntryOpen = false;
            ClaudeLoginFeedback.Text = "";
        }
        catch (OperationCanceledException) { ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginCancelled"); }
        catch (Exception error) { ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginFailed"); SanitizedLogger.Write("Claude code exchange error: " + error.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_claudeLoginCancellation, cancellation)) { _claudeLoginCancellation = null; SetClaudeLoginBusy(false); }
            cancellation.Dispose();
            _viewModel.ApplyClaude(_claudeClient.Snapshot, _claudeClient.State, _claudeClient.IsStale);
        }
    }

    private async void DisconnectClaude(object sender, RoutedEventArgs e)
    {
        CancelClaudeLogin();
        try
        {
            await _claudeClient.DisconnectAsync(_shutdown.Token);
            _viewModel.ApplyClaude(null, _claudeClient.State, true);
            ClaudeLoginFeedback.Text = "";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ClaudeLoginFeedback.Text = LocalizationManager.Text("ClaudeLoginFailed"); SanitizedLogger.Write("Claude disconnect error: " + error.GetType().Name); }
    }

    private void CancelClaudeConnection(object sender, RoutedEventArgs e) => CancelClaudeLogin();
    private void CancelClaudeLogin()
    {
        _claudeLoginCancellation?.Cancel();
        _manualClaudeAttempt = null;
        ClaudeCodeBox.Clear();
        _viewModel.IsClaudeManualEntryOpen = false;
    }
    private void SetClaudeLoginBusy(bool busy)
    {
        ClaudeSubmitButton.IsEnabled = !busy;
        _viewModel.IsClaudeLoginBusy = busy;
    }
    private void ChooseClaudeAccentColor(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog { AllowFullOpen = true, AnyColor = true, FullOpen = true, SolidColorOnly = true,
            Color = System.Drawing.ColorTranslator.FromHtml(AccentPalette.Normalize(_pendingClaudeAccentColor)) };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _pendingClaudeAccentColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateClaudeAccentPreview();
        PreviewTheme(sender, e);
    }
    private void UpdateClaudeAccentPreview()
    {
        ClaudeAccentValue.Text = AccentPalette.Normalize(_pendingClaudeAccentColor);
        ClaudeAccentSwatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ClaudeAccentValue.Text));
    }
}
