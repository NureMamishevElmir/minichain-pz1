namespace MiniChain.Core;

/// <summary>Один крок доказу Меркла: хеш сусіда і з якого боку він стоїть.</summary>
public sealed record MerkleStep(string Hash, bool SiblingIsLeft);

/// <summary>
/// Власна реалізація дерева Меркла. Листки — id транзакцій.
/// Батьківський вузол = SHA-256(лівий || правий) над сирими байтами хешів.
/// Якщо на рівні непарна кількість вузлів, останній дублюється.
/// </summary>
public static class Merkle
{
    public static readonly string EmptyRoot = new('0', 64);

    public static string Root(IReadOnlyList<string> leaves)
    {
        if (leaves.Count == 0) return EmptyRoot;
        var level = leaves.ToList();
        while (level.Count > 1)
        {
            if (level.Count % 2 == 1) level.Add(level[^1]); // непарна кількість: дублюємо останній
            var next = new List<string>(level.Count / 2);
            for (int i = 0; i < level.Count; i += 2)
                next.Add(Parent(level[i], level[i + 1]));
            level = next;
        }
        return level[0];
    }

    public static string Parent(string left, string right) =>
        Hash.Sha256Hex(Hash.FromHex(left).Concat(Hash.FromHex(right)).ToArray());

    /// <summary>Доказ включення: список хешів-сусідів від листка до кореня.</summary>
    public static List<MerkleStep> Proof(IReadOnlyList<string> leaves, int index)
    {
        if (index < 0 || index >= leaves.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var proof = new List<MerkleStep>();
        var level = leaves.ToList();
        while (level.Count > 1)
        {
            if (level.Count % 2 == 1) level.Add(level[^1]);
            int sibling = index % 2 == 0 ? index + 1 : index - 1;
            proof.Add(new MerkleStep(level[sibling], SiblingIsLeft: index % 2 == 1));
            var next = new List<string>(level.Count / 2);
            for (int i = 0; i < level.Count; i += 2) next.Add(Parent(level[i], level[i + 1]));
            level = next;
            index /= 2;
        }
        return proof;
    }

    /// <summary>Легкий клієнт перевіряє доказ, маючи лише корінь із заголовка блоку.</summary>
    public static bool Verify(string leaf, IEnumerable<MerkleStep> proof, string root)
    {
        var h = leaf;
        foreach (var step in proof)
            h = step.SiblingIsLeft ? Parent(step.Hash, h) : Parent(h, step.Hash);
        return h == root;
    }
}
