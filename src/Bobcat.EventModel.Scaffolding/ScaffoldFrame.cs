using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// Base of the scaffold-frame family: real JasperFx <see cref="SyncFrame"/>s carrying
/// <em>name-only</em> descriptor data, composed via <see cref="Frame.Next"/> exactly the way
/// Wolverine's runtime chains compose, and rendered through a throwaway
/// <see cref="GeneratedMethod"/> into a <see cref="SourceWriter"/>.
/// </summary>
/// <remarks>
/// Why a parallel family rather than reusing Wolverine's own frames: the chain machinery
/// (<c>Variable</c>, <c>GeneratedMethod</c> arguments) is <see cref="Type"/>-bound end to end,
/// and scaffolding happens <em>before the types exist</em>. The Frame contract itself —
/// <c>GenerateCode(GeneratedMethod, ISourceWriter)</c> plus <c>Next</c> — needs none of that, so
/// these frames mirror the runtime frames' shapes one-to-one while working from strings. The
/// deterministic 80% of a slice costs zero tokens; every judgment point is a marked TODO for the
/// AI or human layer.
/// </remarks>
public abstract class ScaffoldFrame : SyncFrame
{
    /// <summary>Render a chain of scaffold frames to source, the way a generated method renders its frames.</summary>
    public static string Render(params ScaffoldFrame[] frames)
    {
        if (frames.Length == 0) return string.Empty;

        for (var i = 0; i < frames.Length - 1; i++)
        {
            frames[i].Next = frames[i + 1];
        }

        var writer = new SourceWriter();

        // The GeneratedMethod here is a rendering harness only — no arguments, no variables.
        // It is the one concession this family makes to the Type-based model.
        var method = new GeneratedMethod("Scaffold", typeof(void));
        frames[0].GenerateCode(method, writer);
        return writer.Code();
    }
}
