using System.Security.Cryptography;
using System.Text;

namespace ScipDotnet;

/// <summary>
/// One vector input produced by <see cref="MethodChunker"/> before it is attached to a
/// symbol: the aspect, the source span, the text to embed and the structural facts.
/// </summary>
/// <param name="Aspect">"signature", "body" or "block".</param>
/// <param name="LineStart">1-based first line of the span.</param>
/// <param name="LineEnd">1-based last line of the span.</param>
/// <param name="NormalizedText">Comment-free, whitespace-collapsed text.</param>
/// <param name="Ordinal">Block index within the method; 0 for signature and body.</param>
/// <param name="BodyLines">Body line count (body aspect only, else 0).</param>
/// <param name="MaxNestingDepth">Deepest statement nesting (body aspect only, else 0).</param>
/// <param name="CyclomaticComplexity">1 + branching nodes (body aspect only, else 0).</param>
public sealed record ChunkDraft(
    string Aspect,
    int LineStart,
    int LineEnd,
    string NormalizedText,
    int Ordinal,
    int BodyLines,
    int MaxNestingDepth,
    int CyclomaticComplexity)
{
    /// <summary>
    /// Gets the content key: lowercase sha256 hex of <see cref="NormalizedText"/>. Two
    /// spans with the same normalized text share the key, hence the same vector.
    /// </summary>
    public string TextHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedText))).ToLowerInvariant();
}
