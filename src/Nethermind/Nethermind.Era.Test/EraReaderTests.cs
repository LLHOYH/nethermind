// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Era1.Test;
internal class EraReaderTests
{
    [Test]
    public async Task ReadAccumulator_DoesNotThrow()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        await builder.Add(
            Keccak.Zero,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0,
            0,
            0);
        await builder.Finalize();
        EraReader sut = await EraReader.Create(stream);

        Assert.That(async () => await sut.ReadAccumulator(), Throws.Nothing);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task GetBlockByNumber_DifferentNumber_ReturnsBlockWithCorrectNumber(int number)
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        Block block0 = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await builder.Add(block0, Array.Empty<TxReceipt>());
        await builder.Add(block1, Array.Empty<TxReceipt>());
        await builder.Add(block2, Array.Empty<TxReceipt>());
        await builder.Finalize();
        EraReader sut = await EraReader.Create(stream);

        (Block result,_,_) = await sut.GetBlockByNumber(number);

        Assert.That(result.Number, Is.EqualTo(number));
    }

    [Test]
    public async Task GetAsyncEnumerator_EnumerateAll_ReadsAllAddedBlocks()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        Block block0 = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await builder.Add(block0, Array.Empty<TxReceipt>());
        await builder.Add(block1, Array.Empty<TxReceipt>());
        await builder.Add(block2, Array.Empty<TxReceipt>());
        await builder.Finalize();
        EraReader sut = await EraReader.Create(stream);

        IAsyncEnumerator<(Block, TxReceipt[], UInt256)> enumerator = sut.GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (Block block, _, UInt256 td) = enumerator.Current;
        block.Header.TotalDifficulty = td;
        block.Should().BeEquivalentTo(block0);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (block, _, td) = enumerator.Current;
        block.Header.TotalDifficulty = td;
        block.Should().BeEquivalentTo(block1);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (block, _, td) = enumerator.Current;
        block.Header.TotalDifficulty = td;
        block.Should().BeEquivalentTo(block2);
    }

    [Test]
    public async Task GetAsyncEnumerator_EnumerateAll_ReadsAllAddedReceipts()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        Block block0 = Build.A.Block.WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        TxReceipt[] receipt0 = new[] { Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject };
        TxReceipt[] receipt1 = new[] { Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject };
        TxReceipt[] receipt2 = new[] { Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject };
        await builder.Add(block0, receipt0);
        await builder.Add(block0, receipt1);
        await builder.Add(block0, receipt2);
        await builder.Finalize();
        EraReader sut = await EraReader.Create(stream);

        IAsyncEnumerator<(Block, TxReceipt[], UInt256)> enumerator = sut.GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (_, TxReceipt[] receipts, _) = enumerator.Current;
        receipts.Should().BeEquivalentTo(receipt0);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (_, receipts, _) = enumerator.Current;
        receipts.Should().BeEquivalentTo(receipt1);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        (_, receipts, _) = enumerator.Current;
        receipts.Should().BeEquivalentTo(receipt2);
    }

    [Test]
    public async Task GetAsyncEnumerator_EnumerateAll_EnumeratesCorrectAmountOfBlocks()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        Block block0 = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await builder.Add(block0, Array.Empty<TxReceipt>());
        await builder.Add(block1, Array.Empty<TxReceipt>());
        await builder.Add(block2, Array.Empty<TxReceipt>());
        await builder.Finalize();

        EraReader sut = await EraReader.Create(stream);
        int result = 0;
        await foreach (var item in sut)
        {
            result++;
        }

        Assert.That(result, Is.EqualTo(3));
    }

    [Test]
    public async Task Dispose_Disposed_InnerStreamIsDisposed()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        await builder.Add(
            Keccak.Zero,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0,
            0,
            0);
        await builder.Finalize();
        EraReader sut = await EraReader.Create(stream);

        sut.Dispose();

        Assert.That(() => stream.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
    }

}