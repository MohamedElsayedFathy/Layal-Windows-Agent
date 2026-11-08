namespace Layal.Models;

public sealed class SkillRecord
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string SkillMarkdownEntry { get; set; } = "SKILL.md";
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public string ValidationStatus { get; set; } = "Validated metadata; scripts not executed";
    public string PackageSha256 { get; set; } = string.Empty;
    public string SourceType { get; set; } = "local";
    public string? SourceRepository { get; set; }
    public string? SourceRef { get; set; }
    public string? SourceCommitSha { get; set; }
    public string? SourceArchiveSha256 { get; set; }
    public string? SourceSkillPath { get; set; }
    public DateTimeOffset? SourceResolvedAtUtc { get; set; }
}
