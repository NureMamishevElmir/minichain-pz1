namespace MiniChain.Core;

/// <summary>Параметри консенсусу. Мають бути однаковими на всіх вузлах мережі.</summary>
public sealed record ChainParams
{
    public const string ChainId = "minichain-pz1";

    public int InitialDifficulty { get; init; } = 16;     // біт; 16 біт = 4 hex-нулі
    public int MinDifficulty { get; init; } = 4;
    public int MaxDifficulty { get; init; } = 48;
    public int AdjustInterval { get; init; } = 10;        // K: коригування кожні 10 блоків
    public long TargetBlockMs { get; init; } = 10_000;    // бажаний середній час блоку — 10 с
    public int MaxAdjustStep { get; init; } = 2;          // не більше ніж ±2 біти (x4) за раз
    public long BlockReward { get; init; } = 50;          // фіксована винагорода майнеру
    public int MaxBlockBytes { get; init; } = 4096;       // ліміт розміру транзакцій у блоці (дод. завдання)
    public long MaxFutureDriftMs { get; init; } = 2 * 60_000;
    public long GenesisTimestamp { get; init; } =
        new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static ChainParams Default { get; } = new();
}
