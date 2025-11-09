namespace SqlMemDiag.Core.Analysis;

public sealed record DiagnosticFinding(string Id, string Title, string Description, double SeverityScore);
