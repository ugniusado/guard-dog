using GuardDog.Core.Models;

namespace GuardDog.Core.Diagram;

/// <summary>
/// Produces a Mermaid <c>erDiagram</c> string from a <see cref="SchemaSnapshot"/>.
/// The generated diagram can be embedded in a Markdown file and rendered natively
/// by GitHub, GitLab, and the Mermaid Live Editor.
/// </summary>
public interface IMermaidGenerator
{
    /// <summary>
    /// Generates the full Mermaid erDiagram block (including the markdown code fence)
    /// suitable for writing directly into a <c>.md</c> file.
    /// </summary>
    string Generate(SchemaSnapshot snapshot);
}
