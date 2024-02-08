// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

using G1 = Bls.P1;
using G2 = Bls.P2;
using GT = Bls.PT;

[TestFixture]
public class BlsTests
{
    private BlsSigner.PrivateKey sk;

    [SetUp]
    public void Setup()
    {
        sk.Bytes = [0x2c, 0xd4, 0xba, 0x40, 0x6b, 0x52, 0x24, 0x59, 0xd5, 0x7a, 0x0b, 0xed, 0x51, 0xa3, 0x97, 0x43, 0x5c, 0x0b, 0xb1, 0x1d, 0xd5, 0xf3, 0xca, 0x11, 0x52, 0xb3, 0x69, 0x4b, 0xb9, 0x1d, 0x7c, 0x22];
    }

    [Test]
    public void Calculate_signature()
    {
        byte[] expected = [0x8e, 0x02, 0xb7, 0x95, 0x01, 0x98, 0xd3, 0x35, 0xc7, 0xb3, 0x52, 0xd1, 0x88, 0x80, 0xe2, 0xf6, 0xb4, 0xe7, 0xf6, 0x78, 0x02, 0x98, 0x87, 0x2b, 0x67, 0x84, 0x0d, 0xb1, 0xfa, 0xa0, 0x69, 0xf9, 0xa8, 0xbe, 0x48, 0x80, 0x0c, 0xe2, 0xee, 0x55, 0x65, 0xa8, 0x11, 0xd8, 0x23, 0x0d, 0x3f, 0x05];
        byte[] message = [0x50, 0x32, 0xec, 0x38, 0xbb, 0xc5, 0xda, 0x98, 0xee, 0x0c, 0x6f, 0x56, 0x8b, 0x87, 0x2a, 0x65, 0xa0, 0x8a, 0xbf, 0x25, 0x1d, 0xeb, 0x21, 0xbb, 0x4b, 0x56, 0xe5, 0xd8, 0x82, 0x1e, 0x68, 0xaa];
        BlsSigner.Signature s = BlsSigner.Sign(sk, message);
        s.Bytes.Should().Equal(expected);
    }

    [Test]
    public void Verify_signature()
    {
        byte[] message = [0x3e, 0x00, 0xef, 0x2f, 0x89, 0x5f, 0x40, 0xd6, 0x7f, 0x5b, 0xb8, 0xe8, 0x1f, 0x09, 0xa5, 0xa1, 0x2c, 0x84, 0x0e, 0xc3, 0xce, 0x9a, 0x7f, 0x3b, 0x18, 0x1b, 0xe1, 0x88, 0xef, 0x71, 0x1a, 0x1e];
        BlsSigner.Signature s = BlsSigner.Sign(sk, message);
        var tmp = BlsSigner.Verify(BlsSigner.GetPublicKey(sk), s, message);
        Assert.That(tmp);
    }

    [Test]
    public void Rejects_bad_signature()
    {
        byte[] message = [0x3e, 0x00, 0xef, 0x2f, 0x89, 0x5f, 0x40, 0xd6, 0x7f, 0x5b, 0xb8, 0xe8, 0x1f, 0x09, 0xa5, 0xa1, 0x2c, 0x84, 0x0e, 0xc3, 0xce, 0x9a, 0x7f, 0x3b, 0x18, 0x1b, 0xe1, 0x88, 0xef, 0x71, 0x1a, 0x1e];
        BlsSigner.Signature s = BlsSigner.Sign(sk, message);
        s.Bytes[34] += 1;
        Assert.That(!BlsSigner.Verify(BlsSigner.GetPublicKey(sk), s, message));
    }

    [Test]
    public void Public_key_from_private_key()
    {
        byte[] expected = [0xb4, 0x95, 0x3c, 0x4b, 0xa1, 0x0c, 0x4d, 0x41, 0x96, 0xf9, 0x01, 0x69, 0xe7, 0x6f, 0xaf, 0x15, 0x4c, 0x26, 0x0e, 0xd7, 0x3f, 0xc7, 0x7b, 0xb6, 0x5d, 0xc3, 0xbe, 0x31, 0xe0, 0xce, 0xc6, 0x14, 0xa7, 0x28, 0x7c, 0xda, 0x94, 0x19, 0x53, 0x43, 0x67, 0x6c, 0x2c, 0x57, 0x49, 0x4f, 0x0e, 0x65, 0x15, 0x27, 0xe6, 0x50, 0x4c, 0x98, 0x40, 0x8e, 0x59, 0x9a, 0x4e, 0xb9, 0x6f, 0x7c, 0x5a, 0x8c, 0xfb, 0x85, 0xd2, 0xfd, 0xc7, 0x72, 0xf2, 0x85, 0x04, 0x58, 0x00, 0x84, 0xef, 0x55, 0x9b, 0x9b, 0x62, 0x3b, 0xc8, 0x4c, 0xe3, 0x05, 0x62, 0xed, 0x32, 0x0f, 0x6b, 0x7f, 0x65, 0x24, 0x5a, 0xd4];
        Assert.That(BlsSigner.GetPublicKey(sk).Bytes, Is.EqualTo(expected));
    }

    [Test]
    public void PairingTest1()
    {
        // e((12+34)*56*g1, 78*g2) == e(78*g1, 12*56*g2) * e(78*g1, 34*56*g2)
        GT q1 = new(G1.generator().mult((12 + 34) * 56), G2.generator().mult(78));
        GT q2 = new(G1.generator().mult(78), G2.generator().mult(12 * 56));
        GT q3 = new(G1.generator().mult(78), G2.generator().mult(34 * 56));
        q2.mul(q3);
        Assert.That(GT.finalverify(q1, q2));
    }

    [Test]
    public void PairingTest2()
    {
        GT q1 = new(G1.generator().mult(2), G2.generator());
        GT q2 = new(G1.generator(), G2.generator().mult(2));
        Assert.That(GT.finalverify(q1, q2));
    }
}