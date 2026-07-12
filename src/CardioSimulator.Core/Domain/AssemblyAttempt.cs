using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// One shuffled part in the palette: its <see cref="CorrectIndex"/> (the slot it belongs in), its
/// baseline-zeroed <see cref="Samples"/>, and a stable <see cref="Key"/> used as the drag payload /
/// lookup id (unique within an <see cref="AssemblyAttempt"/>).
/// </summary>
public sealed record AssemblyPaletteItem(int CorrectIndex, IReadOnlyList<int> Samples, int Key);

/// <summary>
/// The learner's in-progress attempt at one «Собери ЭКГ» question: a shuffled pool of the trace's parts
/// and which part is currently dropped into each ordered slot. Any part may be dragged into any slot;
/// grading is all-or-nothing (<see cref="AllCorrect"/> — every slot holds the part whose original index
/// equals the slot's index). The shuffle is seeded so an attempt is reproducible (stable across
/// re-renders, and testable).
/// </summary>
public sealed class AssemblyAttempt
{
    private readonly List<AssemblyPaletteItem> _palette = new();
    private readonly AssemblyPaletteItem?[] _slots;

    public EcgAssembly Spec { get; }

    /// <summary>Number of ordered slots on the tape (= part count).</summary>
    public int SlotCount => _slots.Length;

    public AssemblyAttempt(EcgAssembly spec, int seed)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        var parts = spec.PartList;
        _slots = new AssemblyPaletteItem?[parts.Count];

        for (var i = 0; i < parts.Count; i++)
            _palette.Add(new AssemblyPaletteItem(i, parts[i].SampleList, i));

        Shuffle(_palette, new Random(seed));
    }

    /// <summary>The shuffled parts in their (stable) palette-display order.</summary>
    public IReadOnlyList<AssemblyPaletteItem> Palette => _palette;

    /// <summary>The parts not yet dropped into any slot (what the palette should still show).</summary>
    public IReadOnlyList<AssemblyPaletteItem> Available => _palette.Where(it => SlotOf(it) < 0).ToList();

    /// <summary>The part dropped into a slot, or null if the slot is empty.</summary>
    public AssemblyPaletteItem? PlacedAt(int slot) =>
        slot >= 0 && slot < _slots.Length ? _slots[slot] : null;

    /// <summary>The slot a part currently sits in, or -1 if it is still in the pool.</summary>
    public int SlotOf(AssemblyPaletteItem item)
    {
        if (item is null) return -1;
        for (var i = 0; i < _slots.Length; i++)
            if (_slots[i]?.Key == item.Key) return i;
        return -1;
    }

    /// <summary>Looks a part up by its drag-payload <see cref="AssemblyPaletteItem.Key"/>.</summary>
    public AssemblyPaletteItem? ItemByKey(int key) => _palette.FirstOrDefault(i => i.Key == key);

    /// <summary>
    /// Drops a part into <paramref name="slot"/>. If the part was already in another slot it moves here;
    /// whatever occupied this slot is bumped back to the pool.
    /// </summary>
    public void Place(int slot, AssemblyPaletteItem item)
    {
        if (item is null || slot < 0 || slot >= _slots.Length) return;
        var prev = SlotOf(item);
        if (prev >= 0) _slots[prev] = null;
        _slots[slot] = item; // any previous occupant is overwritten → returns to the pool
    }

    /// <summary>Clears a slot, returning its part to the pool.</summary>
    public void Clear(int slot)
    {
        if (slot >= 0 && slot < _slots.Length) _slots[slot] = null;
    }

    /// <summary>True once every slot holds a part.</summary>
    public bool IsComplete => _slots.Length > 0 && _slots.All(s => s is not null);

    /// <summary>All-or-nothing verdict: complete and every part sits in its original slot.</summary>
    public bool AllCorrect =>
        IsComplete && Enumerable.Range(0, _slots.Length).All(i => _slots[i]!.CorrectIndex == i);

    /// <summary>Fisher–Yates using the seeded RNG so order is reproducible.</summary>
    private static void Shuffle(List<AssemblyPaletteItem> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
