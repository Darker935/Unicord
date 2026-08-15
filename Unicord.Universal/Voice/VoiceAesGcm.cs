using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace Unicord.Universal.Voice
{
    internal sealed class VoiceAesGcm : IDisposable
    {
        public const int NonceSize = 12;
        public const int TagSize = 16;
        public const int SuffixSize = 4;

        private readonly CryptographicKey _key;

        public VoiceAesGcm(byte[] key)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));

            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesGcm);
            _key = provider.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(key));
        }

        public byte[] Encrypt(byte[] plaintext, byte[] nonce, byte[] aad)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException("AES-GCM nonce must be 12 bytes.", nameof(nonce));

            var result = CryptographicEngine.EncryptAndAuthenticate(
                _key,
                CryptographicBuffer.CreateFromByteArray(plaintext ?? Array.Empty<byte>()),
                CryptographicBuffer.CreateFromByteArray(nonce),
                CryptographicBuffer.CreateFromByteArray(aad ?? Array.Empty<byte>()));

            CryptographicBuffer.CopyToByteArray(result.EncryptedData, out var cipher);
            CryptographicBuffer.CopyToByteArray(result.AuthenticationTag, out var tag);

            var output = new byte[cipher.Length + tag.Length];
            System.Buffer.BlockCopy(cipher, 0, output, 0, cipher.Length);
            System.Buffer.BlockCopy(tag, 0, output, cipher.Length, tag.Length);
            return output;
        }

        public byte[] Decrypt(byte[] cipherAndTag, byte[] nonce, byte[] aad)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException("AES-GCM nonce must be 12 bytes.", nameof(nonce));
            if (cipherAndTag == null || cipherAndTag.Length < TagSize)
                throw new ArgumentException("Ciphertext is too short.", nameof(cipherAndTag));

            var cipherLength = cipherAndTag.Length - TagSize;
            var cipher = new byte[cipherLength];
            var tag = new byte[TagSize];
            System.Buffer.BlockCopy(cipherAndTag, 0, cipher, 0, cipherLength);
            System.Buffer.BlockCopy(cipherAndTag, cipherLength, tag, 0, TagSize);

            var decrypted = CryptographicEngine.DecryptAndAuthenticate(
                _key,
                CryptographicBuffer.CreateFromByteArray(cipher),
                CryptographicBuffer.CreateFromByteArray(nonce),
                CryptographicBuffer.CreateFromByteArray(tag),
                CryptographicBuffer.CreateFromByteArray(aad ?? Array.Empty<byte>()));

            CryptographicBuffer.CopyToByteArray(decrypted, out var plaintext);
            return plaintext;
        }

        public void Dispose()
        {
        }
    }
}
