// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;
using System;
using System.Collections.Generic;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using Nethermind.Crypto;

namespace Nethermind.Evm;
public class AuthorizedCodeInfoRepository : ICodeInfoRepository
{
    public IEnumerable<Address> AuthorizedAddresses => _authorizedCode.Keys;
    private readonly Dictionary<Address, CodeInfo> _authorizedCode = new();
    private readonly AuthorizationTupleDecoder _authorizationTupleDecoder = new();
    private readonly EthereumEcdsa _ethereumEcdsa;
    private readonly ICodeInfoRepository _codeInfoRepository;
    private readonly ulong _chainId;
    private readonly ILogger _logger;
    private readonly byte[] _internalBuffer = new byte[128];

    public AuthorizedCodeInfoRepository(ICodeInfoRepository codeInfoRepository, ulong chainId, ILogger? logger = null)
    {
        _codeInfoRepository = codeInfoRepository;
        _chainId = chainId;
        _ethereumEcdsa = new EthereumEcdsa(_chainId, NullLogManager.Instance);
        _logger = logger ?? NullLogger.Instance;
        _internalBuffer[0] = Eip7702Constants.Magic;
    }
    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _authorizedCode.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : _codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);

    public CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode) => _codeInfoRepository.GetOrAdd(codeHash, initCode);

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        throw new NotSupportedException($"Use {nameof(CopyCodeAndOverwrite)}() for code authorizations.");

    /// <summary>
    /// Copy code from <paramref name="codeSource"/> and set it to override <paramref name="target"/>.
    /// Main use for this is for https://eips.ethereum.org/EIPS/eip-7702
    /// </summary>
    public void CopyCodeAndOverwrite(
        IWorldState worldState,
        Address codeSource,
        Address target,
        IReleaseSpec vmSpec)
    {
        if (!_authorizedCode.ContainsKey(target))
        {
            _authorizedCode.Add(target, GetCachedCodeInfo(worldState, codeSource, vmSpec));
        }
    }

    /// <summary>
    /// Build a code cache from transaction authorization_list authorized by signature.
    /// eip-7702
    /// </summary>
    public void InsertFromAuthorizations(
        IWorldState worldState,
        AuthorizationTuple?[] authorizations,
        IReleaseSpec spec)
    {
        _authorizedCode.Clear();

        //TODO optimize
        foreach (AuthorizationTuple? authTuple in authorizations)
        {
            if (authTuple is null)
            {
                continue;
            }
            if (authTuple.ChainId != 0 && _chainId != authTuple.ChainId)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping tuple in authorization_list because chain id ({authTuple.ChainId}) does not match.");
                continue;
            }

            Address authority = authTuple.Authority ?? RecoverAuthority(authTuple);
            CodeInfo authorityCodeInfo = _codeInfoRepository.GetCachedCodeInfo(worldState, authority, spec);
            if (authorityCodeInfo.MachineCode.Length > 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping tuple in authorization_list because authority ({authority}) has code deployed.");
                continue;
            }
            if (authTuple.Nonce is not null && worldState.GetNonce(authority) != authTuple.Nonce)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping tuple in authorization_list because authority ({authority}) nonce ({authTuple.Nonce}) does not match.");
                continue;
            }

            //TODO should we do insert if code is empty?
            CopyCodeAndOverwrite(worldState, authTuple.CodeAddress, authority, spec);
        }
    }

    private Address RecoverAuthority(AuthorizationTuple authTuple)
    {
        Span<byte> encoded = _internalBuffer.AsSpan();
        RlpStream stream = _authorizationTupleDecoder.EncodeWithoutSignature(authTuple.ChainId, authTuple.CodeAddress, authTuple.Nonce);
        stream.Data.AsSpan().CopyTo(encoded.Slice(1));
        return _ethereumEcdsa.RecoverAddress(authTuple.AuthoritySignature, Keccak.Compute(encoded.Slice(0, stream.Data.Length + 1)));
    }

    public void ClearAuthorizations()
    {
        _authorizedCode.Clear();
    }
}
