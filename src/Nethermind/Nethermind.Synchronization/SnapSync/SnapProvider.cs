// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider : ISnapProvider
    {
        //private readonly ObjectPool<ITrieStore> _trieStorePool;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        private readonly ProgressTracker _progressTracker;

        public SnapProvider(ProgressTracker progressTracker, IDbProvider dbProvider, ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
            //_trieStorePool = new DefaultObjectPool<ITrieStore>(new TrieStorePoolPolicy(_dbProvider.StateDb, logManager));

            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<SnapProvider>();
        }

        public bool CanSync() => _progressTracker.CanSync();

        public bool IsFinished(out SnapSyncBatch? nextBatch) => _progressTracker.IsFinished(out nextBatch);

        public AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response)
        {
            AddRangeResult result;

            if (response.PathAndAccounts.Count == 0 && response.Proofs.Count == 0)
            {
                _logger.Trace($"SNAP - GetAccountRange - requested expired RootHash:{request.RootHash}");

                result = AddRangeResult.ExpiredRootHash;
            }
            else
            {
                result = AddAccountRange(request.BlockNumber.Value, request.RootHash, request.StartingHash, response.PathAndAccounts, response.Proofs, hashLimit: request.LimitHash);

                if (result == AddRangeResult.OK)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedAccounts, response.PathAndAccounts.Count);
                }
            }

            _progressTracker.ReportAccountRangePartitionFinished(request.LimitHash.Value);
            response.Dispose();

            return result;
        }

        public AddRangeResult AddAccountRange(long blockNumber, in ValueHash256 expectedRootHash, in ValueHash256 startingHash, IReadOnlyList<PathWithAccount> accounts, IReadOnlyList<byte[]> proofs = null, in ValueHash256? hashLimit = null!)
        {
            //ITrieStore store = _trieStorePool.Get();
            ITrieStore store = NullTrieStore.Instance;
            //_progressTracker.AquireRawStateLock();
            try
            {
                StateTree tree = new(store, _logManager);

                ValueHash256 effectiveHashLimit = hashLimit.HasValue ? hashLimit.Value : ValueKeccak.MaxValue;

                using IRawState rawState = _progressTracker.GetNewRawState();

                (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> accountsWithStorage, List<ValueHash256> codeHashes) =
                    SnapProviderHelper.AddAccountRange(rawState, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs);

                if (result == AddRangeResult.OK)
                {
                    foreach (PathWithAccount item in CollectionsMarshal.AsSpan(accountsWithStorage))
                    {
                        _progressTracker.EnqueueAccountStorage(item);
                    }

                    _progressTracker.EnqueueCodeHashes(CollectionsMarshal.AsSpan(codeHashes));
                    _progressTracker.UpdateAccountRangePartitionProgress(effectiveHashLimit, accounts[^1].Path, moreChildrenToRight);
                }
                else if (result == AddRangeResult.MissingRootHashInProofs)
                {
                    _logger.Info($"SNAP - AddAccountRange failed, missing root hash {tree.RootHash} in the proofs, startingHash:{startingHash}");
                }
                else if (result == AddRangeResult.DifferentRootHash)
                {
                    _logger.Info($"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");
                }

                return result;
            }
            finally
            {
                //_trieStorePool.Return(store);
                //_progressTracker.ReleaseRawStateLock();
            }
        }

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response)
        {
            AddRangeResult result = AddRangeResult.OK;

            IReadOnlyList<PathWithStorageSlot[]> responses = response.PathsAndSlots;
            if (responses.Count == 0 && response.Proofs.Count == 0)
            {
                _logger.Trace($"SNAP - GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Count}), {request.StartingHash}");

                _progressTracker.ReportStorageRangeRequestFinished(request.Copy());

                return AddRangeResult.ExpiredRootHash;
            }
            else
            {
                int slotCount = 0;

                int requestLength = request.Accounts.Count;

                for (int i = 0; i < responses.Count; i++)
                {
                    // only the last can have proofs
                    IReadOnlyList<byte[]> proofs = null;
                    if (i == responses.Count - 1)
                    {
                        proofs = response.Proofs;
                    }

                    PathWithAccount account = request.Accounts[i];
                    result = AddStorageRange(request.BlockNumber.Value, account, account.Account.StorageRoot, request.StartingHash, responses[i], proofs);

                    slotCount += responses[i].Length;
                }

                if (requestLength > responses.Count)
                {
                    _progressTracker.ReportFullStorageRequestFinished(request.Accounts.Skip(responses.Count));
                }
                else
                {
                    _progressTracker.ReportFullStorageRequestFinished();
                }

                if (result == AddRangeResult.OK && slotCount > 0)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedStorageSlots, slotCount);
                }
            }

            response.Dispose();
            return result;
        }

        public AddRangeResult AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, in ValueHash256 expectedRootHash, in ValueHash256? startingHash, IReadOnlyList<PathWithStorageSlot> slots, IReadOnlyList<byte[]>? proofs = null)
        {
            //ITrieStore store = _trieStorePool.Get();
            ITrieStore store = NullTrieStore.Instance;
            StorageTree tree = new(store, _logManager);
            //_progressTracker.AquireRawStateLock();
            try
            {
                using IRawState rawState = _progressTracker.GetNewRawState();
                (AddRangeResult result, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(rawState, pathWithAccount, blockNumber, startingHash, slots, expectedRootHash, proofs);

                if (result == AddRangeResult.OK)
                {
                    if (moreChildrenToRight)
                    {
                        StorageRange range = new()
                        {
                            Accounts = new ArrayPoolList<PathWithAccount>(1) { pathWithAccount },
                            StartingHash = slots[^1].Path
                        };

                        _progressTracker.EnqueueStorageRange(range);
                    }
                }
                else if (result == AddRangeResult.MissingRootHashInProofs)
                {
                    _logger.Info($"SNAP - AddStorageRange failed, missing root hash {expectedRootHash} in the proofs, startingHash:{startingHash}");

                    _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
                }
                else if (result == AddRangeResult.DifferentRootHash)
                {
                    _logger.Info($"SNAP - AddStorageRange failed, expected storage root hash:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");

                    _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
                }

                return result;
            }
            finally
            {
                //_trieStorePool.Return(store);
                //_progressTracker.ReleaseRawStateLock();
            }
        }

        public void RefreshAccounts(AccountsToRefreshRequest request, IOwnedReadOnlyList<byte[]> response)
        {
            int respLength = response.Count;
            //ITrieStore store = _trieStorePool.Get();
            ITrieStore store = NullTrieStore.Instance;
            try
            {
                for (int reqi = 0; reqi < request.Paths.Count; reqi++)
                {
                    var requestedPath = request.Paths[reqi];

                    if (reqi < respLength)
                    {
                        byte[] nodeData = response[reqi];

                        if (nodeData.Length == 0)
                        {
                            RetryAccountRefresh(requestedPath);
                            _logger.Trace($"SNAP - Empty Account Refresh: {requestedPath.PathAndAccount.Path}");
                            continue;
                        }

                        try
                        {
                            TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
                            node.ResolveNode(store);
                            node.ResolveKey(store, true);

                            requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(node.Keccak);

                            if (requestedPath.StorageStartingHash > ValueKeccak.Zero)
                            {
                                StorageRange range = new()
                                {
                                    Accounts = new ArrayPoolList<PathWithAccount>(1) { requestedPath.PathAndAccount },
                                    StartingHash = requestedPath.StorageStartingHash
                                };

                                _progressTracker.EnqueueStorageRange(range);
                            }
                            else
                            {
                                _progressTracker.EnqueueAccountStorage(requestedPath.PathAndAccount);
                            }
                        }
                        catch (Exception exc)
                        {
                            RetryAccountRefresh(requestedPath);
                            _logger.Warn($"SNAP - {exc.Message}:{requestedPath.PathAndAccount.Path}:{Bytes.ToHexString(nodeData)}");
                        }
                    }
                    else
                    {
                        RetryAccountRefresh(requestedPath);
                    }
                }

                _progressTracker.ReportAccountRefreshFinished();
            }
            finally
            {
                response.Dispose();
                //_trieStorePool.Return(store);
            }
        }

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath)
        {
            _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash);
        }

        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IOwnedReadOnlyList<byte[]> codes)
        {
            HashSet<ValueHash256> set = requestedHashes.ToHashSet();

            using (IWriteBatch writeBatch = _dbProvider.CodeDb.StartWriteBatch())
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    byte[] code = codes[i];
                    ValueHash256 codeHash = ValueKeccak.Compute(code);

                    if (set.Remove(codeHash))
                    {
                        Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                        writeBatch[codeHash.Bytes] = code;
                    }
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codes.Count);
            codes.Dispose();
            _progressTracker.ReportCodeRequestFinished(set.ToArray());
        }

        public void RetryRequest(SnapSyncBatch batch)
        {
            if (batch.AccountRangeRequest is not null)
            {
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash.Value);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                _progressTracker.ReportStorageRangeRequestFinished(batch.StorageRangeRequest.Copy());
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(batch.CodesRequest.AsSpan());
            }
            else if (batch.AccountsToRefreshRequest is not null)
            {
                _progressTracker.ReportAccountRefreshFinished(batch.AccountsToRefreshRequest);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void UpdatePivot()
        {
            _progressTracker.UpdatePivot();
        }

        private class TrieStorePoolPolicy : IPooledObjectPolicy<ITrieStore>
        {
            private readonly IKeyValueStoreWithBatching _stateDb;
            private readonly ILogManager _logManager;

            public TrieStorePoolPolicy(IKeyValueStoreWithBatching stateDb, ILogManager logManager)
            {
                _stateDb = stateDb;
                _logManager = logManager;
            }

            public ITrieStore Create()
            {
                return new TrieStore(
                    _stateDb,
                    Nethermind.Trie.Pruning.No.Pruning,
                    Persist.EveryBlock,
                    _logManager);
            }

            public bool Return(ITrieStore obj)
            {
                return true;
            }
        }
    }
}
