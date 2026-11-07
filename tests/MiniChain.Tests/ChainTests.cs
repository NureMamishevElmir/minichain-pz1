using MiniChain.Core;

namespace MiniChain.Tests;

/// <summary>Спільні помічники: низька складність, щоб тести йшли за мілісекунди.</summary>
internal static class T
{
    public static readonly ChainParams P = ChainParams.Default with
    {
        InitialDifficulty = 6, MinDifficulty = 1, MaxDifficulty = 20, AdjustInterval = 5, TargetBlockMs = 1000
    };

    /// <summary>Намайнити блок з транзакціями з мемпулу (або заданими) і додати в ланцюг.</summary>
    public static Block Mine(Blockchain chain, string miner, IEnumerable<Transaction>? txs = null, long? ts = null)
    {
        var block = chain.CreateCandidate(miner, txs ?? Array.Empty<Transaction>(),
            ts ?? chain.Tip.Header.Timestamp + chain.Params.TargetBlockMs);
        ProofOfWork.Mine(block.Header);
        block.Hash = block.Header.ComputeHash();
        var err = chain.TryAddBlock(block);
        Assert.Null(err);
        return block;
    }

    /// <summary>Ланцюг, де alice намайнила 2 блоки (баланс 100) і переслала bob 30 (fee 2).</summary>
    public static (Blockchain chain, Wallet alice, Wallet bob) Funded()
    {
        var chain = new Blockchain(P);
        var alice = Wallet.Create();
        var bob = Wallet.Create();
        Mine(chain, alice.Address);
        Mine(chain, alice.Address);
        Mine(chain, alice.Address, new[] { Transaction.Create(alice, bob.Address, 30, 2, 0) });
        return (chain, alice, bob);
    }
}

public class CryptoAndTransactionTests
{
    [Fact]
    public void Signature_IsValidForOwner_AndAddressIsHashOfPublicKey()
    {
        var w = Wallet.Create();
        var tx = Transaction.Create(w, Wallet.Create().Address, 10, 1, 0);
        Assert.Null(tx.CheckSignature());
        Assert.Equal(Hash.Sha256Hex(Hash.FromHex(w.PublicKey))[..40], w.Address);
        Assert.Equal(40, w.Address.Length);
    }

    [Fact]
    public void Serialization_IsDeterministic_WithSortedKeys()
    {
        var w = Wallet.Create();
        var a = Transaction.Create(w, "abc", 5, 1, 3);
        var b = new Transaction { PublicKey = w.PublicKey, Nonce = 3, Fee = 1, Amount = 5, To = "abc", From = w.Address };
        Assert.Equal(a.SigningBytes(), b.SigningBytes());           // ті самі дані — ті самі байти
        Assert.Equal(a.Id, b.Id);
        var json = System.Text.Encoding.UTF8.GetString(a.SigningBytes());
        Assert.StartsWith("{\"amount\":5,\"chainId\":", json);       // ключі відсортовані
        var h = new BlockHeader { Index = 1, Timestamp = 2, PrevHash = "aa", MerkleRoot = "bb", Difficulty = 3, Nonce = 4 };
        Assert.Equal(h.ComputeHash(), h.Clone().ComputeHash());
    }

    [Fact]
    public void ChangingAnyField_BreaksSignature()
    {
        var w = Wallet.Create();
        var tx = Transaction.Create(w, Wallet.Create().Address, 10, 1, 0);
        var changed = tx.Clone(); changed.Amount = 1000;
        Assert.Equal("недійсний підпис", changed.CheckSignature());
        var moved = tx.Clone(); moved.To = Wallet.Create().Address;   // «переклеїти» підпис на іншого отримувача
        Assert.NotNull(moved.CheckSignature());
    }
}

public class MerkleTests
{
    private static readonly string[] L = Enumerable.Range(0, 5).Select(i => Hash.Sha256Hex($"tx{i}")).ToArray();

    [Fact]
    public void Root_EvenCount_IsPairwiseHash()
    {
        var expected = Merkle.Parent(Merkle.Parent(L[0], L[1]), Merkle.Parent(L[2], L[3]));
        Assert.Equal(expected, Merkle.Root(L[..4]));
    }

    [Fact]
    public void Root_OddCount_DuplicatesLastHash()
    {
        // 3 листки: на першому рівні L2 дублюється
        var expected3 = Merkle.Parent(Merkle.Parent(L[0], L[1]), Merkle.Parent(L[2], L[2]));
        Assert.Equal(expected3, Merkle.Root(L[..3]));
        // 5 листків: дублювання відбувається на кількох рівнях
        var p01 = Merkle.Parent(L[0], L[1]); var p23 = Merkle.Parent(L[2], L[3]); var p44 = Merkle.Parent(L[4], L[4]);
        var expected5 = Merkle.Parent(Merkle.Parent(p01, p23), Merkle.Parent(p44, p44));
        Assert.Equal(expected5, Merkle.Root(L));
        Assert.Equal(L[0], Merkle.Root(L[..1]));   // один листок — він і є коренем
    }

    [Fact]
    public void Proof_VerifiesEveryLeaf_AndRejectsForeignLeaf()
    {
        var root = Merkle.Root(L);
        for (int i = 0; i < L.Length; i++)
            Assert.True(Merkle.Verify(L[i], Merkle.Proof(L, i), root));
        Assert.False(Merkle.Verify(Hash.Sha256Hex("fake"), Merkle.Proof(L, 2), root));
    }
}

public class MiningAndChainTests
{
    [Fact]
    public void MinedBlock_MeetsDifficulty_AndHashIsSha256OfHeader()
    {
        var chain = new Blockchain(T.P);
        var b = T.Mine(chain, Wallet.Create().Address);
        Assert.True(ProofOfWork.Meets(b.Hash, b.Header.Difficulty));
        Assert.True(ProofOfWork.LeadingZeroBits(Hash.FromHex(b.Hash)) >= T.P.InitialDifficulty);
        Assert.Equal(Hash.Sha256Hex(b.Header.Serialize()), b.Hash);
    }

    [Fact]
    public void Coinbase_IsRewardPlusFees_AndOverstatedRewardIsRejected()
    {
        var (chain, alice, bob) = T.Funded();
        Assert.Equal(T.P.BlockReward + 2, chain.Tip.Transactions[0].Amount);
        Assert.Equal(3 * 50 + 2 - 30 - 2, chain.Balance(alice.Address));
        Assert.Equal(30, chain.Balance(bob.Address));
        Assert.Equal(1, chain.NextNonce(alice.Address));

        var greedy = chain.CreateCandidate(bob.Address, Array.Empty<Transaction>(), chain.Tip.Header.Timestamp + 1000);
        greedy.Transactions[0].Amount = 1_000_000;   // майнер завищує собі винагороду
        greedy.Header.MerkleRoot = greedy.ComputeMerkleRoot();
        ProofOfWork.Mine(greedy.Header);
        greedy.Hash = greedy.Header.ComputeHash();
        Assert.Contains("завищена", chain.TryAddBlock(greedy));
    }

    [Theory]
    [InlineData(100, 2)]     // блоки в 10 разів швидші за ціль → складність +2 (обмеження кроку)
    [InlineData(500, 1)]     // у 2 рази швидші → +1 біт
    [InlineData(1000, 0)]    // точно в ціль → без змін
    [InlineData(4000, -2)]   // у 4 рази повільніші → −2 біти
    public void Difficulty_IsAdjustedEveryKBlocks(long blockMs, int expectedDelta)
    {
        var chain = new Blockchain(T.P);
        var miner = Wallet.Create().Address;
        long ts = T.P.GenesisTimestamp;
        for (int i = 1; i < 2 * T.P.AdjustInterval; i++) T.Mine(chain, miner, ts: ts += blockMs);
        int before = chain.Tip.Header.Difficulty;
        Assert.Equal(T.P.InitialDifficulty, before);
        Assert.Equal(before + expectedDelta, chain.NextDifficulty());   // блок #10 — точка коригування
    }

    [Fact]
    public void Chain_IsSavedToFile_AndRestoredWithFullValidation()
    {
        var (chain, alice, _) = T.Funded();
        var path = Path.Combine(Path.GetTempPath(), $"mc-test-{Guid.NewGuid()}.json");
        try
        {
            chain.Save(path);
            var restored = Blockchain.FromBlocks(Blockchain.LoadBlocks(path), T.P, out var report);
            Assert.True(report.IsValid, report.ToString());
            Assert.Equal(chain.Tip.Hash, restored!.Tip.Hash);
            Assert.Equal(chain.Balance(alice.Address), restored.Balance(alice.Address));
        }
        finally { File.Delete(path); }
    }
}

public class AttackTests
{
    [Fact]
    public void Attack1_ChangingAmountInOldBlock_IsDetected()
    {
        var (chain, _, _) = T.Funded();
        T.Mine(chain, Wallet.Create().Address);
        Assert.True(Blockchain.Validate(chain.Blocks, T.P).IsValid);
        chain.Blocks[3].Transactions[1].Amount = 99;   // було 30
        var report = Blockchain.Validate(chain.Blocks, T.P);
        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.StartsWith("Блок #3") && e.Contains("Меркла"));
    }

    [Fact]
    public void Attack2_ChangeWithRehash_IsDetectedByBrokenLinkToNextBlock()
    {
        var (chain, _, _) = T.Funded();
        T.Mine(chain, Wallet.Create().Address);
        var thief = Wallet.Create().Address;
        var block = chain.Blocks[2];
        block.Transactions[0].To = thief;                 // крадемо винагороду (coinbase не підписується)
        block.Header.MerkleRoot = block.ComputeMerkleRoot();
        ProofOfWork.Mine(block.Header);                   // перемайнили сам блок — він знову «правильний»
        block.Hash = block.Header.ComputeHash();
        var report = Blockchain.Validate(chain.Blocks, T.P);
        Assert.False(report.IsValid);
        Assert.DoesNotContain(report.Errors, e => e.StartsWith("Блок #2:"));   // сам блок формально коректний
        Assert.Contains(report.Errors, e => e.StartsWith("Блок #3") && e.Contains("попередній"));
    }

    [Fact]
    public void Attack3_ForgedSignature_IsRejected()
    {
        var (chain, alice, _) = T.Funded();
        var attacker = Wallet.Create();
        var tx = new Transaction { From = alice.Address, To = attacker.Address, Amount = 50, Nonce = 1, PublicKey = alice.PublicKey };
        tx.Signature = attacker.Sign(tx.SigningBytes());
        Assert.Equal("недійсний підпис", new Mempool().TryAdd(tx, chain));
    }

    [Fact]
    public void Attack4_DoubleSpend_SecondTransactionIsRejected()
    {
        var (chain, alice, bob) = T.Funded();
        var pool = new Mempool();
        long all = chain.Balance(alice.Address);
        Assert.Null(pool.TryAdd(Transaction.Create(alice, bob.Address, all - 1, 1, 1), chain));
        var second = pool.TryAdd(Transaction.Create(alice, Wallet.Create().Address, all - 1, 1, 2), chain);
        Assert.Contains("недостатньо коштів", second);
        // і той самий nonce (спроба замінити першу) теж не пройде
        Assert.Contains("nonce", pool.TryAdd(Transaction.Create(alice, Wallet.Create().Address, all - 1, 1, 1), chain));
    }

    [Fact]
    public void Attack5_ReplayOfConfirmedTransaction_IsRejectedByNonce()
    {
        var (chain, _, _) = T.Funded();
        var confirmed = chain.Tip.Transactions[1];
        var err = new Mempool().TryAdd(confirmed, chain);
        Assert.Contains("nonce 0 уже використано", err);
        // і навіть у блоці (в обхід мемпулу) повтор не пройде
        var block = chain.CreateCandidate(Wallet.Create().Address, new[] { confirmed }, chain.Tip.Header.Timestamp + 1000);
        ProofOfWork.Mine(block.Header); block.Hash = block.Header.ComputeHash();
        Assert.NotNull(chain.TryAddBlock(block));
    }
}

public class ForkAndMempoolTests
{
    [Fact]
    public void Fork_NodeSwitchesToHeavierChain_AndOrphanedTxReturnsToMempool()
    {
        var (baseChain, alice, bob) = T.Funded();
        var blocks = baseChain.Blocks.Select(b => b.Clone()).ToList();
        var nodeA = new NodeState(T.P, chain: Blockchain.FromBlocks(blocks, T.P, out _));
        var nodeB = new NodeState(T.P, chain: Blockchain.FromBlocks(blocks.Select(b => b.Clone()).ToList(), T.P, out _));

        // вузол A (від'єднаний) підтверджує транзакцію alice -> bob у своєму блоці
        Assert.Null(nodeA.Send(alice, bob.Address, 5, 1).error);
        var orphanTxId = nodeA.Mempool.All[0].Id;
        Assert.NotNull(nodeA.Mine("minerA").block);
        // вузол B тим часом знаходить два порожні блоки
        nodeB.Mine("minerB"); nodeB.Mine("minerB");

        var result = nodeA.TryAdoptChain(nodeB.Chain.Blocks.ToList());
        Assert.True(result.Adopted, result.Message);
        Assert.Equal(1, result.RolledBackBlocks);
        Assert.Equal(nodeB.Chain.Tip.Hash, nodeA.Chain.Tip.Hash);
        Assert.True(nodeA.Mempool.Contains(orphanTxId));   // транзакція не зникла
        Assert.Equal(0, nodeA.Chain.Balance("minerA"));     // винагорода за відкинутий блок зникла
        // B не переходить на легший ланцюг A
        Assert.False(nodeB.TryAdoptChain(nodeA.Chain.Blocks.ToList()).Adopted);
    }

    [Fact]
    public void ForkChoice_UsesTotalWork_NotLength()
    {
        // довгий, але «легкий» ланцюг (складність 1) проти короткого «важкого» (складність 8)
        var light = new Blockchain(T.P with { InitialDifficulty = 1 });
        for (int i = 0; i < 6; i++) T.Mine(light, "m");
        var heavyParams = T.P with { InitialDifficulty = 8 };
        var heavy = new Blockchain(heavyParams);
        for (int i = 0; i < 2; i++) T.Mine(heavy, "m");
        Assert.True(light.Height > heavy.Height);
        Assert.True(heavy.TotalWork > light.TotalWork);   // 2·2^8 = 512 > 6·2^1 = 12
    }

    [Fact]
    public void BlockSizeLimit_SelectsHighestFeeFirst_RespectingNonceOrder()
    {
        var (chain, alice, bob) = T.Funded();
        var carol = Wallet.Create();
        T.Mine(chain, carol.Address);
        var pool = new Mempool();
        Assert.Null(pool.TryAdd(Transaction.Create(alice, bob.Address, 1, 1, 1), chain));
        Assert.Null(pool.TryAdd(Transaction.Create(alice, bob.Address, 1, 9, 2), chain));   // висока комісія, але nonce 2
        Assert.Null(pool.TryAdd(Transaction.Create(carol, bob.Address, 1, 5, 0), chain));
        int oneTx = pool.All[0].Size;
        var picked = pool.SelectForBlock(chain, oneTx * 2);      // влазить лише дві транзакції
        Assert.Equal(2, picked.Count);
        Assert.Equal(new long[] { 5, 1 }, picked.Select(t => t.Fee));   // carol(5), потім alice nonce 1 — бо nonce 2 без nonce 1 неможливий
        Assert.Contains("неправильний nonce", pool.TryAdd(Transaction.Create(bob, alice.Address, 1, 1, 5), chain));
    }
}
