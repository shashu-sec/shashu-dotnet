using Shashu.Internal;

namespace Shashu.Tests;

/// <summary>
/// The internal constants the contract fences read, in one place.
/// </summary>
/// <remarks>
/// A shim rather than an assertion helper: it exists so the schema tests can
/// name the SDK's own values without every test file taking a dependency on the
/// internal namespace layout.
/// </remarks>
internal static class SdkSurface
{
    internal static string[] Sources => [.. RagContextBuilder.Sources];

    internal const int MaxEvents = RetrievalBus.MaxEvents;
    internal const int MaxDocs = RetrievalBus.MaxDocs;
    internal const int MaxPreviewChars = RetrievalBus.MaxPreviewChars;
    internal const int MaxCorpusChars = RetrievalBus.MaxCorpusChars;
    internal const int MaxQueryChars = RetrievalBus.MaxQueryChars;
}
