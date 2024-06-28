using System;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko;

public class BlockInvalidTxExecutor(IWorldState worldState, ITransactionProcessorAdapter txProcessor) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly IWorldState _worldState = worldState;
    private readonly ITransactionProcessorAdapter _txProcessor = txProcessor;

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
    {
        if (block.Transactions.Length == 0)
        {
            throw new ArgumentException("Block must contain at least the anchor transaction");
        }

        block.Transactions[0].IsAnchorTx = true;

        BlockExecutionContext blkCtx = new(block.Header);
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);
            if (!_txProcessor.Execute(tx, in blkCtx, receiptsTracer))
                continue;
            // only end the trace if the transaction was successful
            // so that we don't increment the receipt index for failed transactions
            receiptsTracer.EndTxTrace();
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, receiptsTracer.LastReceipt));
        }

        _worldState.Commit(spec, receiptsTracer);
        return [.. receiptsTracer.TxReceipts];
    }
}
