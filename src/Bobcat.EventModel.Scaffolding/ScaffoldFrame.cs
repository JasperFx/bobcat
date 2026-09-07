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

    /// <summary>
    /// The unfilled judgment point, written so that it <em>compiles</em> (issue #226).
    /// </summary>
    /// <remarks>
    /// A hole in expression position — <c>new AppointmentConfirmed(/* TODO */)</c> — is a compile
    /// error, and one unfilled slice therefore fails the whole application project. That defeats
    /// per-slice independence at the source: no slice's specs can build or run until every slice
    /// is filled, so nine agents handed nine slices are each blocked by a hole in somebody else's
    /// file, and the failure reads as "your slice is broken" when the cause is a sibling nobody
    /// has touched yet.
    ///
    /// So the hole belongs in the <em>behaviour</em>, not in the syntax. The intended shape still
    /// travels — as a comment, so the deterministic 80% is handed over intact — and the body
    /// throws. Every slice's specs then run on day one and fail on their own assertions, which is
    /// the correct spec-first state and names the slice that owns the failure.
    /// </remarks>
    protected static void writeUnfilledDecision(ISourceWriter writer, string intent, params string[] shape)
    {
        writer.WriteLine("// Fill this in and delete the throw — the shape is:");
        foreach (var line in shape)
        {
            writer.WriteLine($"//     {line}");
        }

        writer.WriteLine($"throw new NotImplementedException(\"TODO: {intent}\");");
    }
}
