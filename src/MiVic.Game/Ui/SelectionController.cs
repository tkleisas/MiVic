using MiVic.Core.Sim;

namespace MiVic.Game.Ui;

/// <summary>
/// The player's current selection.
/// <para>
/// Holds entity handles rather than slots, so a unit that dies and has its slot
/// recycled cannot be selected by accident. Stale handles are skipped wherever
/// the selection is used.
/// </para>
/// </summary>
public sealed class SelectionController
{
    private readonly List<EntityId> _selected = [];

    /// <summary>Selected entities, in the order they were added.</summary>
    public IReadOnlyList<EntityId> Selected => _selected;

    /// <summary>Number of selected entities.</summary>
    public int Count => _selected.Count;

    /// <summary>True when nothing is selected.</summary>
    public bool IsEmpty => _selected.Count == 0;

    /// <summary>True when <paramref name="id"/> is selected.</summary>
    public bool Contains(EntityId id)
    {
        foreach (EntityId selected in _selected)
        {
            if (selected == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces the selection with a single entity.</summary>
    public void Select(EntityId id)
    {
        _selected.Clear();
        _selected.Add(id);
    }

    /// <summary>Adds an entity unless it is already selected.</summary>
    public void Add(EntityId id)
    {
        if (!Contains(id))
        {
            _selected.Add(id);
        }
    }

    /// <summary>Removes an entity.</summary>
    public void Remove(EntityId id)
    {
        for (int i = 0; i < _selected.Count; i++)
        {
            if (_selected[i] == id)
            {
                _selected.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>Clears the selection.</summary>
    public void Clear() => _selected.Clear();

    /// <summary>Drops handles that no longer resolve to a live entity.</summary>
    public void PruneDead(SimWorld world)
    {
        for (int i = _selected.Count - 1; i >= 0; i--)
        {
            if (!world.IsValid(_selected[i]))
            {
                _selected.RemoveAt(i);
            }
        }
    }
}
