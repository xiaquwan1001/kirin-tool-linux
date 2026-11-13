// Licensed under the repository's Business Source License 1.1. See LICENSE.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kirin_Tool.Services.Approval;

/// <summary>Human-facing text for a gate verdict (shared by the GUI dialogs and the CLI/MCP hints).</summary>
public static class ApprovalText
{
    public static string Describe(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Denied denial => "The write was refused: " + denial.Reason,
        ApprovalDecision.TimedOut timeout =>
            "The approval dialog was not answered within " + (int)timeout.Waited.TotalSeconds + " seconds; nothing was written.",
        ApprovalDecision.NotSupported notSupported =>
            "This entry point cannot obtain a human approval (" + notSupported.ErrorCode + "). " + notSupported.Instruction,
        _ => "The write was not approved.",
    };
}

/// <summary>
/// Avalonia implementation of the approval prompt: a modal window that shows the real argv, the
/// side effects, the redline/irreversibility warning and — for redline requests — an input box
/// that has to match the confirm phrase exactly before the approve buttons unlock.
/// </summary>
public sealed class AvaloniaApprovalPrompt : IApprovalPrompt
{
    private readonly DialogService _dialogService;

    public AvaloniaApprovalPrompt(DialogService dialogService) => _dialogService = dialogService;

    public async Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken)
    {
        ApprovalRequest request = model.Request;
        ApprovalPromptOutcome outcome = ApprovalPromptOutcome.Denied;

        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = request.TitleZh + "  (" + request.CapabilityId + ")",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = "requested by: " + request.Origin.ToString().ToLowerInvariant() + "   tool: " + request.Tool,
            FontSize = 12,
            Opacity = 0.75,
        });

        if (request.Summary.Count > 0)
        {
            var effects = new StackPanel { Spacing = 2 };
            effects.Children.Add(new TextBlock { Text = "side effects", FontWeight = FontWeight.SemiBold, FontSize = 12 });
            foreach (string line in request.Summary)
                effects.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            body.Children.Add(effects);
        }

        if (request.Argv.Count > 0)
        {
            var argv = new StackPanel { Spacing = 2 };
            argv.Children.Add(new TextBlock { Text = "commands to be executed", FontWeight = FontWeight.SemiBold, FontSize = 12 });
            argv.Children.Add(new TextBlock
            {
                Text = string.Join(" ", request.Argv),
                FontFamily = new FontFamily("monospace"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            body.Children.Add(argv);
        }

        if (request.Targets.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "targets: " + string.Join(", ", request.Targets),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        if (request.TouchesRedline)
        {
            body.Children.Add(new TextBlock
            {
                Text = "REDLINE: this write touches a segment the tool cannot restore (identity / boot chain).",
                Foreground = Brushes.OrangeRed,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        if (request.Reversibility == ApprovalReversibility.Irreversible)
        {
            body.Children.Add(new TextBlock
            {
                Text = "IRREVERSIBLE: there is no undo in this tool. A backup is the only way back.",
                Foreground = Brushes.Red,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        TextBox? confirmBox = null;
        Button approveOnce;
        Button approveSession;
        if (model.RequiresTypedConfirm)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Type exactly  " + model.ConfirmPhrase + "  to enable the approve buttons:",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            confirmBox = new TextBox { Watermark = model.ConfirmPhrase, Width = 320 };
            body.Children.Add(confirmBox);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var deny = new Button { Content = "Deny", MinWidth = 100, Height = 32 };
        approveSession = new Button { Content = "Approve for this session", MinWidth = 150, Height = 32 };
        approveOnce = new Button { Content = "Approve once", MinWidth = 110, Height = 32 };
        buttons.Children.Add(deny);
        buttons.Children.Add(approveSession);
        buttons.Children.Add(approveOnce);
        body.Children.Add(buttons);

        bool TypedOk() => confirmBox == null ||
            string.Equals((confirmBox.Text ?? string.Empty).Trim(), model.ConfirmPhrase, StringComparison.Ordinal);

        if (confirmBox != null)
        {
            approveOnce.IsEnabled = false;
            approveSession.IsEnabled = false;
            confirmBox.TextChanged += (_, _) =>
            {
                bool ok = TypedOk();
                approveOnce.IsEnabled = ok;
                approveSession.IsEnabled = ok;
            };
        }

        var dialog = new Window
        {
            Title = "Approve write: " + request.CapabilityId,
            SystemDecorations = SystemDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 420,
            MaxWidth = 620,
            CanResize = false,
            Content = new Border { Padding = new Thickness(20), Child = body },
        };

        deny.Click += (_, _) => { outcome = ApprovalPromptOutcome.Denied; dialog.Close(); };
        approveOnce.Click += (_, _) => { if (TypedOk()) { outcome = ApprovalPromptOutcome.Approved; dialog.Close(); } };
        approveSession.Click += (_, _) => { if (TypedOk()) { outcome = ApprovalPromptOutcome.ApprovedForSession; dialog.Close(); } };

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            // A cancelled/timeout gate must not leave a modal window behind.
            if (dialog.CheckAccess()) { outcome = ApprovalPromptOutcome.TimedOut; dialog.Close(); }
        });

        await _dialogService.ShowDialog(dialog);

        return new ApprovalPromptAnswer(outcome, confirmBox?.Text?.Trim(), outcome == ApprovalPromptOutcome.Denied ? "denied in the approval dialog" : null);
    }
}
