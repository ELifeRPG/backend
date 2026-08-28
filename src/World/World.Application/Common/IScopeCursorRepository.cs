using ELifeRPG.World.Domain.Snapshots;

namespace ELifeRPG.World.Application.Common;

/// <summary>Backs the <see cref="ScopeCursor"/> gate on a <c>Full</c>-mode snapshot batch — see that type's own doc comment for why it is keyed per-scope.</summary>
public interface IScopeCursorRepository
{
    ValueTask<ScopeCursor?> FindAsync(string scopeKey, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the scope's cursor whole — see <see cref="ScopeCursor"/>'s own doc comment for why a
    /// full <c>Store()</c> is correct here rather than a targeted patch. Joins the caller's own
    /// <c>IWorldSession</c>; nothing reaches Postgres until the caller's own <c>SaveChangesAsync</c>
    /// runs, and that call is what actually surfaces a lost race — see
    /// <see cref="ELifeRPG.World.Domain.Exceptions.ScopeCursorConflictException"/>.
    ///
    /// <b>Callers MUST call <see cref="FindAsync"/> for this exact <paramref name="scopeKey"/> earlier
    /// in the very same session before calling this method</b> — every real caller
    /// (<c>ApplySnapshotHandler</c>'s sequence gate, both scope kinds) already does, since deciding
    /// whether to advance at all requires reading the current cursor first. This is a real precondition,
    /// not a formality: the implementation deliberately does <b>not</b> defend against a caller that
    /// skips it by re-reading internally — fix round 1 did exactly that, and fix round 2's review proved
    /// it re-opened the very race this type exists to close (a stale sequence decision silently
    /// overwriting a fresher committed one). See <c>MartenScopeCursorRepository.AdvanceAsync</c>'s own
    /// doc comment for the empirically-verified mechanics behind both rounds' findings.
    /// </summary>
    ValueTask AdvanceAsync(string scopeKey, long sequence, DateTimeOffset now, CancellationToken cancellationToken);
}
