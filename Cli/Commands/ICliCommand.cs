namespace Kirin_Tool.Cli.Commands;

/// <summary>Local file/parse failure mapped to exit code 5.</summary>
internal sealed class CliFileException : Exception
{
    public CliFileException(string message) : base(message) { }
}

/// <summary>What a command can do to the device, surfaced to MCP clients and automation.</summary>
internal enum CliWriteKind
{
    /// <summary>Never touches device state.</summary>
    ReadOnly,
    /// <summary>Builds and acknowledges a plan; even confirmed, nothing is written.</summary>
    PlanOnly,
    /// <summary>Confirmed invocation performs a real write to the device.</summary>
    RealWrite,
}

internal interface ICliCommand
{
    string Name { get; }
    string McpName { get; }
    string Summary { get; }
    string Usage { get; }

    /// <summary>Write classification used by the MCP server and automation; default is read-only.</summary>
    CliWriteKind WriteKind => CliWriteKind.ReadOnly;

    /// <summary>Compatibility shim kept for existing call sites; equivalent to WriteKind != ReadOnly.</summary>
    bool IsWrite => WriteKind != CliWriteKind.ReadOnly;

    IReadOnlyList<CliParam> Params { get; }
    Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken);
}

internal sealed record WritePlan(object Details, IReadOnlyList<string> Lines);
