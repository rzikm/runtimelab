// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Quic.Implementations.Managed.Internal.Headers;
using System.Net.Quic.Implementations.Managed.Internal.Parsing;
using System.Net.Security;

namespace System.Net.Quic.Implementations.Managed.Internal.Crypto
{
    internal class CryptoSeal
    {
        private readonly CryptoSealAlgorithm _algorithm;
        private readonly byte[] _headerKey;
        private readonly byte[] _iv;
        private readonly byte[] _secret;

        public int TagLength => _algorithm.TagLength;

        public int PayloadSampleLength => _algorithm.SampleLength;

        private CryptoSeal(TlsCipherSuite alg, byte[] secret, byte[] headerKey)
        {
            _headerKey = headerKey;
            _secret = secret;
            _headerKey = headerKey;

            _iv = KeyDerivation.DeriveIv(secret);
            var key = KeyDerivation.DeriveKey(secret);

            _algorithm = CryptoSealAlgorithm.Create(alg, key, headerKey);
        }

        public static CryptoSeal Create(TlsCipherSuite alg, ReadOnlySpan<byte> secret)
        {
            return new CryptoSeal(alg, secret.ToArray(), KeyDerivation.DeriveHp(secret));
        }

        public static CryptoSeal UpdateSeal(CryptoSeal seal)
        {
            // the header key is not updated
            return new CryptoSeal(seal._algorithm.CipherSuite, KeyDerivation.UpdateSecret(seal._secret), seal._headerKey);
        }

        private static void SplitPacket(Span<byte> buffer, int headerLength, int tagLength, out Span<byte> header, out Span<byte> payload, out Span<byte> tag)
        {
            // split packet buffer into spans
            header = buffer.Slice(0, headerLength);
            payload = buffer.Slice(headerLength, buffer.Length - headerLength - tagLength);
            tag = buffer.Slice(buffer.Length - tagLength, tagLength);
        }

        public void ProtectPacket(Span<byte> buffer, int pnOffset, long packetNumber)
        {
            int pnLength = HeaderHelpers.GetPacketNumberLength(buffer[0]);

            Span<byte> nonce = stackalloc byte[_iv.Length];
            MakeNonce(_iv, packetNumber, nonce);

            SplitPacket(buffer, pnOffset + pnLength, _algorithm.TagLength, out var header, out var payload, out var tag);

            // encrypt payload in-place
            _algorithm.Protect(nonce, payload, tag, header);
        }

        public void ProtectHeader(Span<byte> buffer, int pnOffset)
        {
            int pnLength = HeaderHelpers.GetPacketNumberLength(buffer[0]);

            Span<byte> protectionMask = stackalloc byte[5];

            // sample as if pnLength == 4
            _algorithm.CreateHeaderProtectionMask(buffer.Slice(pnOffset + 4, _algorithm.SampleLength), protectionMask);

            ProtectHeader(buffer.Slice(0, pnOffset + pnLength), protectionMask, pnLength);
        }

        public static void ProtectHeader(Span<byte> header, Span<byte> mask, int pnLength)
        {
            Debug.Assert(mask.Length == 5);

            if (HeaderHelpers.IsLongHeader(header[0]))
            {
                header[0] ^= (byte)(mask[0] & 0x0f);
            }
            else
            {
                header[0] ^= (byte)(mask[0] & 0x1f);
            }

            int ii = 1;
            for (int i = header.Length - pnLength; i < header.Length; i++)
            {
                header[i] ^= mask[ii++ % mask.Length];
            }
        }

        public void UnprotectHeader(Span<byte> buffer, int pnOffset)
        {
            // remove header protection
            Span<byte> protectionMask = stackalloc byte[5];
            _algorithm.CreateHeaderProtectionMask(buffer.Slice(pnOffset + 4, _algorithm.SampleLength), protectionMask);

            // pass header span as if packet number had the maximum 4 bytes
            UnprotectHeader(buffer.Slice(0, pnOffset + 4), protectionMask);
        }

        public bool UnprotectPacket(Span<byte> buffer, int pnOffset, long expectedPn)
        {
            // we expect the header protection to be already removed.
            int pnLength = HeaderHelpers.GetPacketNumberLength(buffer[0]);

            // read the actual packet number
            long packetNumber = 0;
            for (int i = 0; i < pnLength; ++i)
            {
                // the packet number is stored in big endian
                packetNumber = (packetNumber << 8) | buffer[pnOffset + i];
            }
            packetNumber = QuicPrimitives.DecodePacketNumber(expectedPn, (int) packetNumber, pnLength);

            Span<byte> nonce = stackalloc byte[_iv.Length];
            MakeNonce(_iv, packetNumber, nonce);

            SplitPacket(buffer, pnOffset + pnLength, _algorithm.TagLength, out var header, out var ciphertext, out var tag);

            return _algorithm.Unprotect(nonce, ciphertext, tag, header);
        }

        public static int UnprotectHeader(Span<byte> header, Span<byte> mask)
        {
            Debug.Assert(mask.Length == 5);

            if (HeaderHelpers.IsLongHeader(header[0]))
            {
                header[0] ^= (byte)(mask[0] & 0x0f);
            }
            else
            {
                header[0] ^= (byte)(mask[0] & 0x1f);
            }

            // we can get pnLength only after unprotecting the first byte
            int pnLength = HeaderHelpers.GetPacketNumberLength(header[0]);

            int ii = 1;
            // header span is passed as if the packet number had full 4 bytes, so we need to
            // need to leave out a couple of bytes at the end
            for (int i = 0; i < pnLength; i++)
            {
                header[header.Length - 4 + i] ^= mask[ii++ % mask.Length];
            }

            return pnLength;
        }

        private static void MakeNonce(ReadOnlySpan<byte> iv, long packetNumber, Span<byte> nonce)
        {
            Debug.Assert(iv.Length == 12);
            Debug.Assert(nonce.Length == 12);

            // From RFC: The nonce, N, is formed by combining the packet
            // protection IV with the packet number.  The 62 bits of the
            // reconstructed QUIC packet number in network byte order are left-
            // padded with zeros to the size of the IV.  The exclusive OR of the
            // padded packet number and the IV forms the AEAD nonce.

            iv.CopyTo(nonce);
            for (int i = 0; i < 8; i++)
            {
                nonce[nonce.Length - 1 - i] ^= (byte) (packetNumber >> (i * 8));
            }
        }

    }
}
