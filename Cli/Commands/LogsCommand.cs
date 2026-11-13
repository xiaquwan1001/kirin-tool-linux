using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// Read-only inspection of the merged operation logs (docs/log-merge.md).
/// One operation = one session directory = one <c>session.jsonl</c>; the pre-2.4.15 per-operation
/// files are still listed (with <c>legacy: true</c>) and still readable through the same command,
/// which is what makes the migration a no-op for anyone with old logs on disk.
///
/// Never touches the device, never writes: no approval, no <c>confirm</c> over MCP.
/// </summary>
internal sealed class LogsCommand : ICliCommand
{
    public string Name => "logs";
    public string McpName => "logs";
    public string Summary => "List/show the unified operation logs (sessions and legacy files); read-only JSON.";
    public string Usage =>
        "logs list [--json]\n" +
        "logs show <session|latest> [--trace] [--json]\n" +
        "logs path <session|latest>\n" +
        "  <session> is a session id, a unique id prefix, 'latest', or a path to a session file/dir\n" +
        "  legacy vcom-*.log and <category>/*.jsonl files are listed as kind=legacy-* and readable here too\n" +
        "  every mode prints one JSON document on stdout; diagnostics go to stderr";
    public CliWriteKind WriteKind => CliWriteKind.ReadOnly;

    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("verb", "string", "list | show | path (default: list)", Positional: true),
        new CliParam("session", "string", "session id / unique prefix / 'latest' / path", Positional: true),
        new CliParam("trace", "boolean", "include the human VCOM transcript lines in 'show' output"),
        new CliParam("root", "string", "log root override (default: $KIRIN_LOG_DIR or ~/.local/state/kirin-tool)"),
    };

    public Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        string verb = (args.Positionals.Count > 0 ? args.Positionals[0] : "list").Trim().ToLowerInvariant();
        string? selector = args.Positionals.Count > 1 ? args.Positionals[1] : null;
        string? root = args.Value("root");
        bool indented = !context.Json;

        switch (verb)
        {
            case "list":
            case "ls":
            {
                IReadOnlyList<LogSessionDescriptor> sessions = LogSessionStore.List(root);
                context.Output.Write(LogSessionStore.ListJson(sessions, root, indented));
                if (sessions.Count == 0)
                    context.Diag("note: no logs found under " + (string.IsNullOrWhiteSpace(root) ? OperationLogger.SessionsRoot : root));
                return Task.FromResult(ExitCodes.Ok);
            }

            case "show":
            {
                LogSessionDescriptor? session = LogSessionStore.Resolve(selector, root);
                if (session == null)
                {
                    context.Diag("error: no session matches '" + (selector ?? LogSessionStore.Latest) + "'");
                    return Task.FromResult(ExitCodes.FileOrVerify);
                }
                context.Output.Write(LogSessionStore.Show(session, args.HasFlag("trace"), indented));
                return Task.FromResult(ExitCodes.Ok);
            }

            case "path":
            {
                LogSessionDescriptor? session = LogSessionStore.Resolve(selector, root);
                if (session == null)
                {
                    context.Diag("error: no session matches '" + (selector ?? LogSessionStore.Latest) + "'");
                    return Task.FromResult(ExitCodes.FileOrVerify);
                }
                context.Output.Write(LogSessionStore.Paths(session, indented));
                return Task.FromResult(ExitCodes.Ok);
            }

            default:
                throw new CliUsageException("unknown logs verb '" + verb + "'; expected list, show or path");
        }
    }
}
