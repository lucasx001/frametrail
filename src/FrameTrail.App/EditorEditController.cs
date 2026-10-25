using FrameTrail.Domain;
using FrameTrail.Workspace;

namespace FrameTrail.App;

internal enum EditorEditOwner { None, Timeline, Canvas, Visual }

/// <summary>Owns the one active gesture, so unrelated controls cannot commit or cancel it.</summary>
internal sealed class EditorEditController(EditorSession session)
{
    internal EditorEditOwner Owner { get; private set; }

    internal void Begin(EditorEditOwner owner)
    {
        if (owner == EditorEditOwner.None) throw new ArgumentOutOfRangeException(nameof(owner));
        if (Owner != EditorEditOwner.None) throw new InvalidOperationException("Finish the active editor gesture first.");
        session.BeginTransaction();
        Owner = owner;
    }

    internal ProjectSnapshot Preview(EditorEditOwner owner, object command)
    {
        RequireOwner(owner);
        return session.PreviewCommand(command);
    }

    internal async Task CommitAsync(EditorEditOwner owner)
    {
        RequireOwner(owner);
        try { await session.CommitTransactionAsync(); }
        catch { session.CancelTransaction(); throw; }
        finally { Owner = EditorEditOwner.None; }
    }

    internal void Cancel(EditorEditOwner owner)
    {
        RequireOwner(owner);
        session.CancelTransaction();
        Owner = EditorEditOwner.None;
    }

    internal void CancelCurrent()
    {
        if (Owner != EditorEditOwner.None) Cancel(Owner);
    }

    private void RequireOwner(EditorEditOwner owner)
    {
        if (owner == EditorEditOwner.None || Owner != owner)
            throw new InvalidOperationException("The gesture belongs to a different editor control.");
    }
}
