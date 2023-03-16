// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
//
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncFeed : ActivatedSyncFeed<ReceiptsSyncBatch?>
    {
        private const int MinReceiptBlock = 1;
        private int _requestSize = GethSyncLimits.MaxReceiptFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly IBlockProcessingQueue _blockProcessingQueue;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPeerPool _syncPeerPool;

        private SyncStatusList _syncStatusList;
        private readonly long _pivotNumber;
        private readonly long _barrier;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || AllReceiptsDownloaded;
        private bool AllReceiptsDownloaded => _receiptStorage.LowestInsertedReceiptBlockNumber <= _barrier;

        public ReceiptsSyncFeed(
            ISyncModeSelector syncModeSelector,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            IBlockProcessingQueue blockProcessingQueue,
            ILogManager logManager)
            : base(syncModeSelector)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockProcessingQueue = blockProcessingQueue;
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _barrier = _syncConfig.AncientReceiptsBarrierCalc;

            if (_logger.IsInfo) _logger.Info($"Using pivot {_pivotNumber} and barrier {_barrier} in receipts sync");

            ResetSyncStatusList();
        }

        private void ResetSyncStatusList()
        {
            _syncStatusList = new SyncStatusList(
                _blockTree,
                _pivotNumber,
                _receiptStorage.LowestInsertedReceiptBlockNumber);
        }

        protected override SyncMode ActivationSyncModes { get; }
            = SyncMode.FastReceipts & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Receipts;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadReceipts = _syncConfig.DownloadReceiptsInFastSync;
            bool allReceiptsDownloaded = AllReceiptsDownloaded;
            bool isGenesisDownloaded = _syncStatusList.LowestInsertWithoutGaps <= MinReceiptBlock;
            bool noBatchesLeft = !shouldDownloadReceipts
                                 || allReceiptsDownloaded
                                 || isGenesisDownloaded;

            if (noBatchesLeft)
            {
                if (ShouldFinish)
                {
                    ResetSyncStatusList();
                    Finish();
                    PostFinishCleanUp();
                }

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();
            _syncReport.ReceiptsInQueue.Update(0);
            _syncReport.ReceiptsInQueue.MarkEnd();
        }

        public override async ValueTask<ReceiptsSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            ReceiptsSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                await _blockProcessingQueue.Emptied();
                if (ShouldBuildANewBatch())
                {
                    BlockInfo?[] infos = new BlockInfo[_requestSize];
                    _syncStatusList.GetInfosForBatch(infos);
                    if (infos[0] is not null)
                    {
                        batch = new ReceiptsSyncBatch(infos) { MinNumber = infos[0].BlockNumber, Prioritized = true };
                    }

                    // Array.Reverse(infos);
                }
            }

            _receiptStorage.LowestInsertedReceiptBlockNumber = _syncStatusList.LowestInsertWithoutGaps;

            return batch;
        }

        public override async ValueTask<SyncResponseHandlingResult> HandleResponse(ReceiptsSyncBatch? batch, PeerInfo peer = null)
        {
            if (batch is null)
            {
                if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                return SyncResponseHandlingResult.InternalError;
            }

            await _blockProcessingQueue.Emptied();

            batch.MarkHandlingStart();
            try
            {
                int added = InsertReceipts(batch);
                return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
            }
            finally
            {
                batch.MarkHandlingEnd();
            }
        }

        private bool TryPrepareReceipts(BlockInfo blockInfo, TxReceipt[] receipts, out TxReceipt[]? preparedReceipts)
        {
            BlockHeader? header = _blockTree.FindHeader(blockInfo.BlockHash);
            if (header is null)
            {
                if (_logger.IsWarn) _logger.Warn("Could not find header for requested blockhash.");
                preparedReceipts = null;
            }
            else
            {
                if (header.ReceiptsRoot == Keccak.EmptyTreeHash)
                {
                    preparedReceipts = receipts.Length == 0 ? receipts : null;
                }
                else
                {
                    // BlockInfo has no timestamp
                    IReceiptSpec releaseSpec = _specProvider.GetReceiptSpec(blockInfo.BlockNumber);
                    preparedReceipts = receipts.GetReceiptsRoot(releaseSpec, header.ReceiptsRoot) != header.ReceiptsRoot
                        ? null
                        : receipts;
                }
            }

            return preparedReceipts is not null;
        }

        private int InsertReceipts(ReceiptsSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo? blockInfo = batch.Infos[i];
                TxReceipt[]? receipts = (batch.Response?.Length ?? 0) <= i
                    ? null
                    : (batch.Response![i] ?? Array.Empty<TxReceipt>());

                if (receipts is not null)
                {
                    TxReceipt[]? prepared = null;
                    // last batch
                    if (blockInfo is null)
                    {
                        break;
                    }

                    bool isValid = !hasBreachedProtocol && TryPrepareReceipts(blockInfo, receipts, out prepared);
                    if (isValid)
                    {
                        Block block = _blockTree.FindBlock(blockInfo.BlockHash);
                        if (block is null)
                        {
                            if (blockInfo.BlockNumber >= _barrier)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Could not find block {blockInfo.BlockHash}");
                            }

                            _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                        }
                        else
                        {
                            try
                            {
                                _receiptStorage.Insert(block, prepared);
                                _syncStatusList.MarkInserted(block.Number);
                                validResponsesCount++;
                            }
                            catch (InvalidDataException)
                            {
                                _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                            }
                        }
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - tx or uncles");

                        if (batch.ResponseSourcePeer is not null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, InitiateDisconnectReason.InvalidReceiptRoot, "invalid tx or uncles root");
                        }

                        _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
                else
                {
                    if (blockInfo is not null)
                    {
                        _syncStatusList.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
            }

            AdjustRequestSize(batch, validResponsesCount);
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            _syncReport.FastBlocksReceipts.Update(_pivotNumber - _syncStatusList.LowestInsertWithoutGaps);
            _syncReport.ReceiptsInQueue.Update(_syncStatusList.QueueSize);
            return validResponsesCount;
        }

        private void LogPostProcessingBatchInfo(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(ReceiptsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void AdjustRequestSize(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            lock (_syncStatusList)
            {
                if (validResponsesCount == batch.Infos.Length)
                {
                    _requestSize = Math.Min(256, _requestSize * 2);
                }

                if (validResponsesCount == 0)
                {
                    _requestSize = Math.Max(4, _requestSize / 2);
                }
            }
        }
    }
}
