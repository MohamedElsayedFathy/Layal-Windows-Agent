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
}
