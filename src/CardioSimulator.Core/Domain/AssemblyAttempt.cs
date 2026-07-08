using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// One candidate tile in a block's palette: the underlying <see cref="EcgBlockPiece"/>, whether it is
/// the correct choice for its slot, and a stable <see cref="Key"/> used as the drag payload / lookup id
/// (unique within an <see cref="AssemblyAttempt"/>).
/// </summary>
public sealed record AssemblyPaletteItem(EcgBlock Block, EcgBlockPiece Piece, bool IsCorrect, int Key);

/// <summary>
/// The learner's in-progress attempt at one «Собери ЭКГ» question: per-block shuffled palettes and the
/// piece currently dropped into each slot. Grading is all-or-nothing (<see cref="AllCorrect"/>). The
/// shuffle is seeded so an attempt is reproducible (stable across re-renders, and testable) — the same
/// question always presents its palettes in the same order for a given seed.
/// </summary>
public sealed class AssemblyAttempt
{
    private readonly Dictionary<EcgBlock, List<AssemblyPaletteItem>> _palettes = new();
    private readonly Dictionary<EcgBlock, AssemblyPaletteItem?> _placed = new();

    public EcgAssembly Spec { get; }

    /// <summary>The blocks in strip order (P, QRS, T), as present in <see cref="Spec"/>.</summary>
    public IReadOnlyList<EcgBlock> Blocks { get; }

    public AssemblyAttempt(EcgAssembly spec, int seed)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        var rng = new Random(seed);
        var key = 0;
        var blocks = new List<EcgBlock>();

        foreach (var block in spec.BlockList)
        {
            blocks.Add(block.Block);
            var items = new List<AssemblyPaletteItem>
            {
                new(block.Block, block.Correct, true, key++),
            };
            foreach (var distractor in block.DistractorList)
                items.Add(new AssemblyPaletteItem(block.Block, distractor, false, key++));

            Shuffle(items, rng);
            _palettes[block.Block] = items;
            _placed[block.Block] = null;
        }
        Blocks = blocks;
    }

    /// <summary>The shuffled candidate tiles for a block (empty if the block is absent).</summary>
    public IReadOnlyList<AssemblyPaletteItem> Palette(EcgBlock block) =>
        _palettes.TryGetValue(block, out var list) ? list : Array.Empty<AssemblyPaletteItem>();

    /// <summary>The tiles for a block that are not yet placed (what the palette should still show).</summary>
    public IReadOnlyList<AssemblyPaletteItem> Available(EcgBlock block)
    {
        var placed = Placed(block);
        return Palette(block).Where(i => placed is null || i.Key != placed.Key).ToList();
    }

    /// <summary>The tile dropped into a block's slot, or null if the slot is empty.</summary>
    public AssemblyPaletteItem? Placed(EcgBlock block) =>
        _placed.TryGetValue(block, out var item) ? item : null;

    /// <summary>Looks a tile up by its drag-payload <see cref="AssemblyPaletteItem.Key"/>.</summary>
    public AssemblyPaletteItem? ItemByKey(int key) =>
        _palettes.Values.SelectMany(list => list).FirstOrDefault(i => i.Key == key);

    /// <summary>Drops a tile into its own block's slot.</summary>
    public void Place(AssemblyPaletteItem item)
    {
        if (item is null || !_placed.ContainsKey(item.Block)) return;
        _placed[item.Block] = item;
    }

    /// <summary>Clears a block's slot, returning its tile to the palette.</summary>
    public void Clear(EcgBlock block)
    {
        if (_placed.ContainsKey(block)) _placed[block] = null;
    }

    /// <summary>True once every block's slot holds a tile.</summary>
    public bool IsComplete => Blocks.Count > 0 && Blocks.All(b => Placed(b) is not null);

    /// <summary>All-or-nothing verdict: complete and every placed tile is the correct one.</summary>
    public bool AllCorrect => IsComplete && Blocks.All(b => Placed(b)!.IsCorrect);

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
