// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.DbTuner;

public class SyncDbTuner
{
    private readonly ITunableDb? _stateDb;
    private readonly ITunableDb? _codeDb;
    private readonly ITunableDb? _blockDb;
    private readonly ITunableDb? _receiptBlocksDb;
    private readonly ITunableDb? _receiptTransactionsDb;

    private readonly ITunableDb.TuneType _tuneType;
    private readonly ITunableDb.TuneType _blocksDbTuneType;
    private readonly ITunableDb.TuneType _receiptsDbTuneType;

    public SyncDbTuner(
        ISyncConfig syncConfig,
        ISyncFeed<SnapSyncBatch>? snapSyncFeed,
        ISyncFeed<BodiesSyncBatch>? bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch>? receiptSyncFeed,
        ITunableDb? stateDb,
        ITunableDb? codeDb,
        ITunableDb? blockDb,
        ITunableDb? receiptBlocksDb,
        ITunableDb? receiptTransactionsDb
    )
    {
        // Only these three make sense as they are write heavy
        // Headers is used everywhere, so slowing read might slow the whole sync.
        // Statesync is read heavy, Forward sync is just plain too slow to saturate IO.
        if (snapSyncFeed is not null)
        {
            snapSyncFeed.StateChanged += SnapStateChanged;
        }

        if (bodiesSyncFeed is not null)
        {
            bodiesSyncFeed.StateChanged += BodiesStateChanged;
        }

        if (receiptSyncFeed is not null)
        {
            receiptSyncFeed.StateChanged += ReceiptsStateChanged;
        }

        _stateDb = stateDb;
        _codeDb = codeDb;
        _blockDb = blockDb;
        _receiptBlocksDb = receiptBlocksDb;
        _receiptTransactionsDb = receiptTransactionsDb;

        _tuneType = syncConfig.TuneDbMode;
        _blocksDbTuneType = syncConfig.BlocksDbTuneDbMode;
        _receiptsDbTuneType = syncConfig.ReceiptsDbTuneDbMode;
    }

    private void SnapStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _stateDb?.Tune(_tuneType);
            _codeDb?.Tune(_tuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _stateDb?.Tune(ITunableDb.TuneType.Default);
            _codeDb?.Tune(ITunableDb.TuneType.Default);
        }
    }

    private void BodiesStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _blockDb?.Tune(_blocksDbTuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _blockDb?.Tune(ITunableDb.TuneType.Default);
        }
    }

    private void ReceiptsStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _receiptBlocksDb?.Tune(_receiptsDbTuneType);
            _receiptTransactionsDb?.Tune(_tuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _receiptBlocksDb?.Tune(ITunableDb.TuneType.Default);
            _receiptTransactionsDb?.Tune(ITunableDb.TuneType.Default);
        }
    }
}
