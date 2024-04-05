using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using Nethermind.Blockchain;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Crypto;
using Multiformats.Address;
using Nethermind.Core;
using Google.Protobuf;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Abi;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Consensus.Processing;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysReceived;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogger _logger;
    private readonly Address KeyBroadcastContractAddress;
    private readonly Address KeyperSetManagerContractAddress;
    private readonly ulong InstanceID;
    private ulong _eon = 0;

    public ShutterP2P(Action<Dto.DecryptionKeys> OnDecryptionKeysReceived, IReadOnlyBlockTree readOnlyBlockTree, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ILogManager logManager)
    {
        _onDecryptionKeysReceived = OnDecryptionKeysReceived;
        _readOnlyBlockTree = readOnlyBlockTree;
        _readOnlyTxProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        _abiEncoder = abiEncoder;
        _logger = logManager.GetClassLogger();
        KeyBroadcastContractAddress = new(auraConfig.ShutterKeyBroadcastContractAddress);
        KeyperSetManagerContractAddress = new(auraConfig.ShutterKeyperSetManagerContractAddress);
        InstanceID = auraConfig.ShutterInstanceID;

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = auraConfig.ShutterP2PProtocolVersion,
                AgentVersion = auraConfig.ShutterP2PAgentVersion
            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {peer.Address}");
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        ConcurrentQueue<byte[]> msgQueue = new();

        long msgCount = 0;
        topic.OnMessage += (byte[] msg) =>
        {
            Interlocked.Increment(ref msgCount);
            msgQueue.Enqueue(msg);
        };

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);
        ConnectToPeers(proto, auraConfig.ShutterKeyperP2PAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long backoff = 10;
        Task.Run(() =>
        {
            for (; ; )
            {
                try
                {
                    Thread.Sleep(20);
                    long delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                    if (msgQueue.TryDequeue(out var msg))
                    {
                        ProcessP2PMessage(msg);
                        lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        backoff = 10;
                    }
                    else if (delta >= backoff)
                    {
                        if (_logger.IsWarn) _logger.Warn("Not receiving Shutter messages, reconnecting...");
                        ConnectToPeers(proto, auraConfig.ShutterKeyperP2PAddresses);
                        lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        backoff *= 2;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("Shutter processing thread exception: " + e.Message);
                }
            }
        });
    }

    internal class MyProto : IDiscoveryProtocol
    {
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            return Task.Delay(int.MaxValue);
        }
    }

    internal void ProcessP2PMessage(byte[] msg)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(_readOnlyBlockTree.Head!.StateRoot!);
        KeyBroadcastContract keyBroadcastContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyBroadcastContractAddress);
        KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, KeyperSetManagerContractAddress);

        Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
        if (!envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
        {
            if (_logger.IsWarn) _logger.Warn("Could not parse Shutter decryption keys...");
            return;
        }

        if (!GetEonInfo(keyBroadcastContract, keyperSetManagerContract!, out ulong eon, out Bls.P2 eonKey, out int threshold))
        {
            if (_logger.IsWarn) _logger.Warn("Could not get Shutter eon info...");
            return;
        }

        if (CheckDecryptionKeys(keyperSetManagerContract!, decryptionKeys, eon, eonKey, threshold))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");
            _onDecryptionKeysReceived(decryptionKeys);
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn("Invalid decryption keys received on P2P network.");
        }
    }

    internal bool CheckDecryptionKeys(IKeyperSetManagerContract keyperSetManagerContract, Dto.DecryptionKeys decryptionKeys, ulong eon, Bls.P2 eonKey, int threshold)
    {
        if (_logger.IsInfo) _logger.Info($"Checking decryption keys instanceId: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count()} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer}");

        if (decryptionKeys.InstanceID != InstanceID || decryptionKeys.Eon != eon)
        {
            return false;
        }

        // todo: enable when Shutter uses BLS
        // foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable())
        // {
        //    if (!ShutterCrypto.CheckDecryptionKey(new(key.Key_.ToArray()), eonKey, new(key.Identity.ToArray())))
        //     {
        //         return false;
        //     }
        // }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count() != signerIndicesCount)
        {
            return false;
        }

        if (signerIndicesCount != threshold)
        {
            return false;
        }

        // IEnumerable<Bls.P1> identities = decryptionKeys.Keys.Select((Dto.Key key) => new Bls.P1(key.Identity.ToArray()));

        // foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        // {
        //     Address keyperAddress = keyperSetManagerContract.GetKeyperSetAddress(_readOnlyBlockTree.Head!.Header, signerIndex).Item1;
        //     if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eon, slot, identities, signature.Span, keyperAddress))
        //     {
        //         return false;
        //     }
        // }

        return true;
    }

    internal void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        foreach (string addr in p2pAddresses)
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }

    internal bool GetEonInfo(IKeyBroadcastContract keyBroadcastContract, IKeyperSetManagerContract keyperSetManagerContract, out ulong eon, out Bls.P2 eonKey, out int threshold)
    {
        eon = keyperSetManagerContract.GetNumKeyperSets(_readOnlyBlockTree.Head!.Header) - 1;
        threshold = 0;
        byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);

        // todo: remove once shutter fixes
        if (!eonKeyBytes.Any())
        {
            // eonKeyBytes = Convert.FromHexString("2fdfb787563ac3aa9be365a581eae6684334cbb9ce11e95c486ea31820e0469a07a5e6e49caddee2b1891900848e7ed03749aac68d4d31d4f98f4a537b9050621a791a11c6c154ae972659a5a4ed7c55d2bf8772f1a4c05542436df59d0a2edc05ea7e70b72f27b4eb8a4fb5ed675cb35d67934a1ed75043ed3802ac6a8ed68c");
            eonKeyBytes = Convert.FromHexString("B068AD1BE382009AC2DCE123EC62DCA8337D6B93B909B3EE52E31CB9E4098D1B56D596BF3C08166C7B46CB3AA85C23381380055AB9F1A87786F2508F3E4CE5CAA5ABCDAE0A80141EE8CCC3626311E0A53BE5D873FA964FD85AD56771F2984579");
        }

        // todo: use key bytes when Shutter swaps to BLS
        // eonKey = new(eonKeyBytes);
        eonKey = new();

        if (_logger.IsInfo && _eon != eon)
        {
            _logger.Info($"Shutter eon: {eon} key: {Convert.ToHexString(eonKeyBytes)}");
            _eon = eon;
        }

        return true;
    }
}
